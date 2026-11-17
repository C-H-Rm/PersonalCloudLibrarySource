using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace PersonalCloudLibrarySource
{
    /// <summary>SDK-independent, rollback-capable policy for explicitly adopting legacy games.</summary>
    public sealed class LegacyLibraryMigration
    {
        private const string SettingsFile = "config.json";
        private const string ManifestFile = "mod-sync-manifest.json";
        private const string StateDirectoryName = "mod-sync";
        private const string JournalFile = "legacy-migration-journal.json";
        private readonly string sourceDirectory;
        private readonly string destinationDirectory;
        private readonly Func<IList<LegacyGameOwnership>> readGames;
        private readonly Action<string, Guid> setOwner;
        private readonly Action dataCopied;
        public bool HasPendingMigration => File.Exists(Path.Combine(destinationDirectory, JournalFile));

        public LegacyLibraryMigration(string sourceDirectory, string destinationDirectory,
            Func<IList<LegacyGameOwnership>> readGames, Action<string, Guid> setOwner, Action dataCopied = null)
        {
            this.sourceDirectory = Path.GetFullPath(sourceDirectory ?? throw new ArgumentNullException(nameof(sourceDirectory)));
            this.destinationDirectory = Path.GetFullPath(destinationDirectory ?? throw new ArgumentNullException(nameof(destinationDirectory)));
            this.readGames = readGames ?? throw new ArgumentNullException(nameof(readGames));
            this.setOwner = setOwner ?? throw new ArgumentNullException(nameof(setOwner));
            this.dataCopied = dataCopied;
            if (string.Equals(this.sourceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), this.destinationDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
                IsWithin(this.sourceDirectory, this.destinationDirectory) || IsWithin(this.destinationDirectory, this.sourceDirectory))
                throw new ArgumentException("Legacy source and standalone destination must be distinct, non-overlapping roots.");
        }

        public LegacyMigrationResult Execute(bool consent, bool legacyPluginEnabled, bool separateSetupChosen)
        {
            if (!consent) return LegacyMigrationResult.NoOp("Adoption was not selected.");
            if (legacyPluginEnabled) return LegacyMigrationResult.Refused("Disable or remove the legacy extension before adopting its games. Its files and settings will not be changed.");
            try
            {
                RecoverInterruptedMigration();
                if (separateSetupChosen) return LegacyMigrationResult.NoOp("Separate library setup was selected.");
                EnsureSafeDirectory(sourceDirectory, false);
                EnsureSafeDirectory(destinationDirectory, true);
                var copies = GetCopyPlan();
                var games = (readGames() ?? throw new InvalidOperationException("Could not inspect game ownership."))
                    .Where(g => g == null || g.PluginId == ForkIdentity.PluginId || g.PluginId == ForkIdentity.LegacyPluginId).ToList();
                if (games.Any(g => g == null || string.IsNullOrWhiteSpace(g.GameId)))
                    throw new InvalidOperationException("Game ownership data is incomplete; adoption was not attempted.");
                var legacy = games.Where(g => g.PluginId == ForkIdentity.LegacyPluginId).ToList();
                if (legacy.Any(g => g.IsRunning || g.IsInstalling || g.IsUninstalling))
                    throw new InvalidOperationException("Close running games and wait for installations to finish before adopting.");
                var duplicateLegacy = legacy.GroupBy(g => g.GameId, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() != 1);
                if (duplicateLegacy != null) throw new InvalidOperationException("Multiple legacy games share GameId '" + duplicateLegacy.Key + "'; resolve the duplicate before adoption.");
                var duplicateTarget = games.Where(g => g.PluginId == ForkIdentity.PluginId)
                    .GroupBy(g => g.GameId, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() != 1);
                if (duplicateTarget != null) throw new InvalidOperationException("Multiple standalone games share GameId '" + duplicateTarget.Key + "'; adoption was refused.");
                var ambiguous = games.GroupBy(g => g.GameId, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(group => group.Any(g => g.PluginId == ForkIdentity.LegacyPluginId) && group.Count() > 1);
                if (ambiguous != null) throw new InvalidOperationException("More than one library entry shares legacy GameId '" + ambiguous.Key + "'; resolve the duplicate before adoption.");
                var targetIds = new HashSet<string>(games.Where(g => g.PluginId == ForkIdentity.PluginId).Select(g => g.GameId), StringComparer.OrdinalIgnoreCase);
                var collision = legacy.FirstOrDefault(g => targetIds.Contains(g.GameId));
                if (collision != null) throw new InvalidOperationException("Both extensions own a game with GameId '" + collision.GameId + "'; choose a separate library or resolve the duplicate first.");

                EnsureSafeDirectory(destinationDirectory, true);
                var journalPath = Path.Combine(destinationDirectory, JournalFile);
                if (File.Exists(journalPath)) throw new InvalidOperationException("A migration journal remains; retry initialization to recover it before proceeding.");
                foreach (var item in copies)
                {
                    var target = Path.Combine(destinationDirectory, item);
                    if (File.Exists(target) || Directory.Exists(target))
                        throw new InvalidOperationException("Standalone destination already contains migration data ('" + item + "'); refusing to overwrite or merge it.");
                }

                var journal = new MigrationJournal
                {
                    GameIds = legacy.Select(g => g.GameId).ToList(),
                    Files = copies,
                    Source = sourceDirectory,
                    Destination = destinationDirectory,
                    Phase = "prepared"
                };
                WriteJournal(journalPath, journal);
                try
                {
                    foreach (var item in copies) CopySafe(item);
                    journal.Phase = "data-copied";
                    WriteJournal(journalPath, journal);
                    foreach (var gameId in journal.GameIds)
                    {
                        setOwner(gameId, ForkIdentity.PluginId);
                    }
                    if (copies.Contains(SettingsFile, StringComparer.Ordinal)) dataCopied?.Invoke();
                    journal.Phase = "complete";
                    WriteJournal(journalPath, journal);
                    File.Delete(journalPath);
                    return LegacyMigrationResult.Completed(journal.GameIds.Count, copies.Count > 0);
                }
                catch
                {
                    RollBack(journalPath, journal);
                    throw;
                }
            }
            catch (Exception ex)
            {
                return LegacyMigrationResult.Refused(ex.Message);
            }
        }

        /// <summary>Completes rollback of an interrupted transaction before imports are enabled.</summary>
        public void RecoverInterruptedMigration(bool legacyPluginEnabled = false)
        {
            if (!Directory.Exists(destinationDirectory)) return;
            RejectReparsePath(destinationDirectory);
            var path = Path.Combine(destinationDirectory, JournalFile);
            if (!File.Exists(path)) return;
            if (legacyPluginEnabled) throw new InvalidOperationException("Disable the legacy extension before recovering an interrupted adoption.");
            RejectReparsePath(path);
            var journal = ReadJournal(path);
            var allowedFiles = new HashSet<string>(new[] { SettingsFile, ManifestFile }, StringComparer.Ordinal);
            if (journal.GameIds == null || journal.Files == null || journal.Files.Any(file => !allowedFiles.Contains(file) && file != StateDirectoryName) ||
                journal.Source != sourceDirectory || journal.Destination != destinationDirectory ||
                (journal.Phase != "prepared" && journal.Phase != "data-copied" && journal.Phase != "complete") ||
                journal.GameIds.Any(string.IsNullOrWhiteSpace) || journal.GameIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.GameIds.Count)
                throw new InvalidOperationException("Migration journal contains unsupported paths or missing ownership data; it was retained for manual recovery.");
            if (journal.Phase == "complete")
            {
                if (journal.Files.Any(file => !File.Exists(Path.Combine(destinationDirectory, file)) && !Directory.Exists(Path.Combine(destinationDirectory, file))))
                    throw new InvalidOperationException("Completed migration data is missing; the journal was retained for review.");
                var owners = readGames() ?? new List<LegacyGameOwnership>();
                if (journal.GameIds.Any(id => owners.Count(g => g.GameId == id && g.PluginId == ForkIdentity.PluginId) != 1))
                    throw new InvalidOperationException("Completed migration journal does not match adopted ownership; evidence was retained for manual recovery.");
                File.Delete(path);
                return;
            }
            RollBack(path, journal);
        }

        private List<string> GetCopyPlan()
        {
            var files = new List<string>();
            AddIfExists(SettingsFile, files);
            AddIfExists(ManifestFile, files);
            var state = Path.Combine(sourceDirectory, StateDirectoryName);
            if (Directory.Exists(state))
            {
                EnsureSafeDirectory(state, false);
                RejectStateLinks(state);
                if (Directory.GetFileSystemEntries(state).Length != 0) files.Add(StateDirectoryName);
            }
            if (files.Count == 0) throw new InvalidOperationException("No legacy settings, cached mod manifest, or mod-sync state were found to adopt.");
            return files;
        }

        private void AddIfExists(string name, ICollection<string> files)
        {
            var path = Path.Combine(sourceDirectory, name);
            if (File.Exists(path))
            {
                RejectReparsePath(path);
                files.Add(name);
            }
        }

        private void CopySafe(string relative)
        {
            var source = Path.Combine(sourceDirectory, relative);
            var target = Path.Combine(destinationDirectory, relative);
            RejectReparsePath(source);
            if (Directory.Exists(source)) { CopyDirectorySafe(source, target); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                input.CopyTo(output);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
        }

        private static void RejectStateLinks(string root)
        {
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.Count != 0)
                foreach (var entry in Directory.GetFileSystemEntries(pending.Pop()))
                {
                    var attr = File.GetAttributes(entry);
                    if ((attr & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Legacy mod-sync state contains a link/reparse point; refusing to follow or copy it.");
                    if ((attr & FileAttributes.Directory) != 0) pending.Push(entry);
                }
        }

        private static void CopyDirectorySafe(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var entry in Directory.GetFileSystemEntries(source))
            {
                RejectReparsePath(entry);
                var dest = Path.Combine(target, Path.GetFileName(entry));
                if (Directory.Exists(entry)) CopyDirectorySafe(entry, dest);
                else
                {
                    using (var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                    File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(entry));
                }
            }
        }

        private void RollBack(string journalPath, MigrationJournal journal)
        {
            var errors = new List<Exception>();
            foreach (var gameId in journal.GameIds ?? new List<string>())
            {
                try
                {
                    var matching = (readGames() ?? new List<LegacyGameOwnership>())
                        .Where(g => (g.PluginId == ForkIdentity.PluginId || g.PluginId == ForkIdentity.LegacyPluginId) &&
                            string.Equals(g.GameId, gameId, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matching.Count == 1 && matching[0].PluginId == ForkIdentity.PluginId)
                        setOwner(gameId, ForkIdentity.LegacyPluginId);
                    else if (matching.Count > 1)
                        throw new InvalidOperationException("Cannot safely roll back duplicate GameId '" + gameId + "'.");
                }
                catch (Exception ex) { errors.Add(ex); }
            }
            if (errors.Count != 0) throw new AggregateException("Ownership rollback is incomplete; copied data and journal were retained for recovery.", errors);
            foreach (var relative in journal.Files ?? new List<string>())
            {
                try
                {
                    var target = Path.GetFullPath(Path.Combine(destinationDirectory, relative));
                    if (!IsWithin(destinationDirectory, target)) throw new InvalidOperationException("Invalid migration journal path.");
                    RejectExistingReparsePath(target);
                    if (File.Exists(target)) File.Delete(target);
                    else if (Directory.Exists(target)) Directory.Delete(target, true);
                }
                catch (Exception ex) { errors.Add(ex); }
            }
            if (errors.Count != 0) throw new AggregateException("Migration rollback is incomplete. The journal was retained; retry initialization after resolving the reported problem.", errors);
            if (File.Exists(journalPath)) File.Delete(journalPath);
        }

        private static void EnsureSafeDirectory(string path, bool create)
        {
            if (create) RejectReparsePath(path);
            if (create) Directory.CreateDirectory(path);
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Migration directory not found: " + path);
            RejectReparsePath(path);
        }

        private static void RejectReparsePath(string path)
        {
            var full = Path.GetFullPath(path);
            var current = full;
            while (!string.IsNullOrEmpty(current))
            {
                if (File.Exists(current) || Directory.Exists(current))
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidOperationException("Migration refuses link/reparse paths: " + current);
                }
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || parent == current) break;
                current = parent;
            }
        }

        private static void RejectExistingReparsePath(string path)
        {
            if (File.Exists(path) || Directory.Exists(path)) RejectReparsePath(path);
        }

        private static bool IsWithin(string root, string path)
        {
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static void WriteJournal(string path, MigrationJournal journal)
        {
            var serializer = new DataContractJsonSerializer(typeof(MigrationJournal));
            var temp = path + ".tmp";
            RejectExistingReparsePath(path);
            RejectExistingReparsePath(temp);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) serializer.WriteObject(stream, journal);
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }

        private static MigrationJournal ReadJournal(string path)
        {
            var serializer = new DataContractJsonSerializer(typeof(MigrationJournal));
            using (var stream = File.OpenRead(path))
                return serializer.ReadObject(stream) as MigrationJournal ?? throw new InvalidOperationException("Migration journal is invalid; it was retained for manual recovery.");
        }

        [DataContract]
        private sealed class MigrationJournal
        {
            [DataMember] public string Phase { get; set; }
            [DataMember] public List<string> GameIds { get; set; }
            [DataMember] public List<string> Files { get; set; }
            [DataMember] public string Source { get; set; }
            [DataMember] public string Destination { get; set; }
        }
    }

    public sealed class LegacyGameOwnership
    {
        public string GameId { get; set; }
        public Guid PluginId { get; set; }
        public bool IsRunning { get; set; }
        public bool IsInstalling { get; set; }
        public bool IsUninstalling { get; set; }
    }

    public sealed class LegacyMigrationResult
    {
        public bool Success { get; private set; }
        public bool Changed { get; private set; }
        public int GamesAdopted { get; private set; }
        public string Message { get; private set; }
        private LegacyMigrationResult(bool success, bool changed, int count, string message)
        { Success = success; Changed = changed; GamesAdopted = count; Message = message; }
        internal static LegacyMigrationResult Completed(int count, bool copiedData) => new LegacyMigrationResult(true, count > 0 || copiedData, count, "Adopted " + count + " legacy game(s)" + (copiedData ? " and copied legacy settings/manifest." : "."));
        internal static LegacyMigrationResult NoOp(string message) => new LegacyMigrationResult(true, false, 0, message);
        internal static LegacyMigrationResult Refused(string message) => new LegacyMigrationResult(false, false, 0, message);
    }
}
