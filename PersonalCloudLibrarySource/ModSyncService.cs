using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace PersonalCloudLibrarySource
{
    public sealed class ModSyncRequest
    {
        public string GameId { get; set; }
        public string LocalFolder { get; set; }
        public string RemoteFolder { get; set; }
        public string LaunchFile { get; set; }
        public string StateDirectory { get; set; }
        public string RcloneExecutable { get; set; } = "rclone";
        public int TimeoutSeconds { get; set; } = 1800;
        public CancellationToken CancellationToken { get; set; }
        public Action<ModSyncProgress> Progress { get; set; }
    }

    public sealed class ModSyncProgress
    {
        public string Phase { get; set; }
        public long Bytes { get; set; }
        public long TotalBytes { get; set; }
        public long Checks { get; set; }
        public long TotalChecks { get; set; }
        public double BytesPerSecond { get; set; }
        public double? Percentage { get; set; }
    }

    public sealed class ModSyncResult
    {
        public bool Success { get; set; }
        public bool HasConflicts { get; set; }
        public string Message { get; set; }
        public bool Cancelled { get; set; }
        public bool RequiresRelink { get; set; }
        public string DiagnosticPath { get; set; }
    }

    public sealed class ModSyncService
    {
        private readonly ModSyncProcess process = new ModSyncProcess();

        public ModSyncResult Synchronize(ModSyncRequest request, Func<string, bool> confirm)
        {
            string diagnostic = null;
            try
            {
                Validate(request);
                request.CancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(request.StateDirectory);
                diagnostic = DiagnosticPath(request);
                using (new FileStream(request.StateDirectory + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    return SynchronizeLocked(request, confirm ?? (_ => false));
                }
            }
            catch (Exception ex)
            {
                var cancelled = ex is OperationCanceledException || (request != null && request.CancellationToken.IsCancellationRequested);
                var relink = request != null && (File.Exists(Path.Combine(request.StateDirectory ?? "", "paused")) ||
                    (File.Exists(Path.Combine(request.StateDirectory ?? "", "pair.txt")) && !File.Exists(Path.Combine(request.StateDirectory ?? "", "ready"))));
                if (!string.IsNullOrEmpty(diagnostic)) AppendDiagnostic(diagnostic, "operation", ex.ToString());
                return new ModSyncResult { Success = false, Cancelled = cancelled, RequiresRelink = relink, DiagnosticPath = diagnostic,
                    Message = cancelled ? "Mod sync cancelled. No automatic resync was attempted." : ex.Message };
            }
        }

        private ModSyncResult SynchronizeLocked(ModSyncRequest r, Func<string, bool> confirm, bool restoreAfterInstall = false)
        {
            if (File.Exists(Path.Combine(r.StateDirectory, "paused")))
                throw new InvalidOperationException("Mod sync is paused after uninstall. Use Sync mods to review recovery, or Recover or relink mods, to explicitly establish a new baseline.");
            var pairFile = Path.Combine(r.StateDirectory, "pair.txt");
            var readyFile = Path.Combine(r.StateDirectory, "ready");
            var markerName = ".pcls-modsync-" + Key(r.GameId).Substring(0, 16);
            var localMarker = Path.Combine(r.LocalFolder, markerName);
            var workDirectory = Path.Combine(r.StateDirectory, "bisync");
            var identity = Path.GetFullPath(r.LocalFolder) + "\n" + r.RemoteFolder;
            var initialized = File.Exists(readyFile);

            var help = Require(r, "bisync", "--help").Output;
            if (!help.Contains("--recover") || !help.Contains("--conflict-resolve") || !help.Contains("--check-filename") || !help.Contains("--download-hash"))
                throw new InvalidOperationException("This rclone version lacks required bisync safety options. Update rclone before linking mods.");

            if (File.Exists(pairFile))
            {
                if (File.ReadAllText(pairFile) != identity)
                    throw new InvalidOperationException("Mod sync mapping changed. Restore the previous paths, or explicitly set up a new pairing after inspecting the retained state.");
                if (!initialized)
                    throw new InvalidOperationException("A previous initial mod sync did not finish. State is retained; inspect diagnostics and recover explicitly rather than automatically running --resync.");
            }
            else if (initialized)
                throw new InvalidOperationException("Mod sync identity is missing. Refusing to recreate its baseline automatically.");

            if (!Directory.Exists(r.LocalFolder))
                throw new InvalidOperationException("Local mod folder is missing. Sync is paused, not treated as deletion. Create the folder before first linkage; after reinstall, explicitly relink from the cloud.");
            RejectReparsePoints(r.LocalFolder);

            if (!initialized)
            {
                if (!restoreAfterInstall && !confirm("Link this mod folder for two-way sync, including deletions?\n\nLocal: " + r.LocalFolder + "\nCloud: " + r.RemoteFolder + "\n\nExisting files will be merged. Conflicting initial files must be resolved first."))
                    return Declined(r);
                Require(r, "mkdir", r.RemoteFolder);
                // Compare each overlapping object using a hash explicitly present on both objects.
                // If even one shared object lacks a compatible native hash, use byte-content checking.
                var check = CompareInitialContents(r, Path.Combine(r.StateDirectory, "initial-check.txt"));
                var report = File.Exists(Path.Combine(r.StateDirectory, "initial-check.txt"))
                    ? File.ReadAllLines(Path.Combine(r.StateDirectory, "initial-check.txt")) : new string[0];
                var caseCollision = FindWindowsCaseCollision(report.Where(line => line.Length > 2).Select(line => line.Substring(2)));
                if (caseCollision != null)
                    throw new InvalidOperationException("Initial mod names collide on a Windows destination. Rename the conflicting files before linking: " + caseCollision);
                if (report.Any(line => line.StartsWith("* ") || line.StartsWith("! ")) ||
                    (check.ExitCode != 0 && !report.Any(line => line.StartsWith("+ ") || line.StartsWith("- "))))
                    throw new InvalidOperationException("Initial mod folders contain conflicting or unreadable files. Neither copy was overwritten. Resolve the differences, then link again.\n" + check.Log);

                Directory.CreateDirectory(workDirectory);
                File.WriteAllText(pairFile, identity);
                File.WriteAllText(localMarker, r.GameId);
                Require(r, "copyto", localMarker, r.RemoteFolder.TrimEnd('/') + "/" + markerName);
            }
            else if (!File.Exists(localMarker) || File.ReadAllText(localMarker) != r.GameId)
                throw new InvalidOperationException("Local mod access marker is missing or changed. Sync is paused; removing/reinstalling a game must not delete cloud mods.");

            var remoteFiles = Require(r, "lsf", r.RemoteFolder, "--recursive", "--files-only").Output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (!remoteFiles.Contains(markerName, StringComparer.Ordinal))
                throw new InvalidOperationException("Cloud mod access marker is missing. Sync is paused; check the remote mapping and connectivity.");
            var localCount = Directory.GetFiles(r.LocalFolder, "*", SearchOption.AllDirectories)
                .Count(path => !string.Equals(Path.GetFullPath(path), Path.GetFullPath(localMarker), StringComparison.OrdinalIgnoreCase));
            var remoteCount = remoteFiles.Count(path => path != markerName);
            // Never trust a separately persisted count: a crash can leave it stale.
            // Conservatively confirm every non-initial empty-vs-populated pairing.
            if (initialized && ((localCount == 0 && remoteCount > 0) || (remoteCount == 0 && localCount > 0)))
            {
                if (!confirm("One tracked mod folder is now empty. Synchronization may remove all remaining mods on the other side. Continue?\n\nLocal: " + r.LocalFolder + "\nCloud: " + r.RemoteFolder))
                    return Declined(r);
            }

            var arguments = new List<string>
            {
                "bisync", r.LocalFolder, r.RemoteFolder,
                "--workdir", workDirectory, "--check-access", "--check-filename", markerName,
                "--compare", "size,modtime,checksum", "--download-hash",
                "--conflict-resolve", "none", "--conflict-loser", "num", "--conflict-suffix", "pcls-conflict",
                "--max-delete", "100", "--resilient", "--recover", "--verbose",
                "--use-json-log", "--stats=1s", "--stats-log-level", "NOTICE"
            };
            if (!initialized) arguments.Add("--resync");
            var sync = Run(r, arguments.ToArray());
            File.WriteAllText(Path.Combine(r.StateDirectory, "last-sync.log"), sync.Log);
            if (sync.ExitCode != 0)
                throw new InvalidOperationException("Mod sync failed. Baseline and diagnostics are retained; no automatic --resync was attempted.\n" + sync.Log);
            File.WriteAllText(readyFile, "ready");
            var conflicts = sync.Log.IndexOf("pcls-conflict", StringComparison.OrdinalIgnoreCase) >= 0 ||
                sync.Log.IndexOf("New or changed in both paths", StringComparison.OrdinalIgnoreCase) >= 0;
            return new ModSyncResult
            {
                Success = true, HasConflicts = conflicts,
                DiagnosticPath = DiagnosticPath(r),
                Message = conflicts ? "Mods synchronized; conflicting versions were kept with pcls-conflict suffixes. Review them before playing." : "Mods synchronized."
            };
        }

        [DataContract]
        private sealed class RcloneListedFile
        {
            [DataMember(Name = "Path")] public string Path { get; set; }
            [DataMember(Name = "Name")] public string Name { get; set; }
            [DataMember(Name = "Hashes")] public Dictionary<string, string> Hashes { get; set; }
        }

        private ModSyncCommandResult CompareInitialContents(ModSyncRequest r, string reportPath)
        {
            var local = RunPhase(r, "check", "lsjson", r.LocalFolder, "--recursive", "--files-only", "--hash");
            var remote = RunPhase(r, "check", "lsjson", r.RemoteFolder, "--recursive", "--files-only", "--hash");
            if (local.ExitCode == 0 && remote.ExitCode == 0)
            {
                try
                {
                    var localFiles = DeserializeListedFiles(local.Output);
                    var remoteFiles = DeserializeListedFiles(remote.Output);
                    var caseCollision = FindWindowsCaseCollision(localFiles.Concat(remoteFiles).Select(f => f.Path ?? f.Name));
                    if (caseCollision != null)
                    {
                        File.WriteAllLines(reportPath, new[] { "* " + caseCollision });
                        AppendDiagnostic(DiagnosticPath(r), "initial-check", "mode=refused-case-collision " + caseCollision);
                        return new ModSyncCommandResult { ExitCode = 1,
                            Error = "Initial mod names collide on a Windows destination. Rename them before linking: " + caseCollision };
                    }
                    var left = localFiles.ToDictionary(f => f.Path ?? f.Name, StringComparer.Ordinal);
                    var right = remoteFiles.ToDictionary(f => f.Path ?? f.Name, StringComparer.Ordinal);
                    var shared = left.Keys.Intersect(right.Keys, StringComparer.Ordinal).ToArray();
                    var comparisons = new List<Tuple<string, bool>>();
                    var allUsable = true;
                    foreach (var path in shared)
                    {
                        var localHashes = left[path].Hashes ?? new Dictionary<string, string>();
                        var remoteHashes = right[path].Hashes ?? new Dictionary<string, string>();
                        var compatible = new[] { "sha1", "md5" }.FirstOrDefault(type =>
                            localHashes.ContainsKey(type) && remoteHashes.ContainsKey(type) &&
                            !string.IsNullOrWhiteSpace(localHashes[type]) && !string.IsNullOrWhiteSpace(remoteHashes[type]));
                        if (compatible == null) { allUsable = false; break; }
                        comparisons.Add(Tuple.Create(path, string.Equals(localHashes[compatible], remoteHashes[compatible], StringComparison.OrdinalIgnoreCase)));
                    }
                    if (allUsable)
                    {
                        var lines = new List<string>();
                        foreach (var path in left.Keys.Except(right.Keys, StringComparer.Ordinal)) lines.Add("+ " + path);
                        foreach (var path in right.Keys.Except(left.Keys, StringComparer.Ordinal)) lines.Add("- " + path);
                        foreach (var item in comparisons.Where(item => !item.Item2)) lines.Add("* " + item.Item1);
                        File.WriteAllLines(reportPath, lines);
                        AppendDiagnostic(DiagnosticPath(r), "initial-check", "mode=native-per-object phase=verified-hashes shared=" + shared.Length + " differences=" + comparisons.Count(item => !item.Item2));
                        return new ModSyncCommandResult { ExitCode = comparisons.Any(item => !item.Item2) ? 1 : 0,
                            Output = string.Join("\n", lines), Error = "Initial comparison used explicit per-object SHA-1/MD5 hashes." };
                    }
                }
                catch (Exception ex)
                {
                    AppendDiagnostic(DiagnosticPath(r), "initial-check", "mode=download-fallback reason=native hash listing could not be verified: " + ex.Message);
                }
            }
            else AppendDiagnostic(DiagnosticPath(r), "initial-check", "mode=download-fallback reason=lsjson failed");

            AppendDiagnostic(DiagnosticPath(r), "initial-check", "mode=download-fallback phase=content-check");
            return Run(r, "check", r.LocalFolder, r.RemoteFolder, "--download", "--combined", reportPath,
                "--use-json-log", "--stats=1s", "--stats-log-level", "NOTICE");
        }

        private static string FindWindowsCaseCollision(IEnumerable<string> paths)
        {
            var spellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                if (spellings.TryGetValue(path, out var previous) && !string.Equals(previous, path, StringComparison.Ordinal))
                    return previous + " / " + path;
                spellings[path] = path;
            }
            return null;
        }

        private static List<RcloneListedFile> DeserializeListedFiles(string json)
        {
            var serializer = new DataContractJsonSerializer(typeof(List<RcloneListedFile>),
                new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json ?? "[]")))
                return (List<RcloneListedFile>)serializer.ReadObject(stream);
        }

        private ModSyncCommandResult Run(ModSyncRequest r, params string[] arguments)
        {
            var phase = arguments.Length == 0 ? "command" : arguments[0];
            if (phase == "bisync" && arguments.Contains("--help")) phase = "compatibility";
            return RunPhase(r, phase, arguments);
        }

        private ModSyncCommandResult RunPhase(ModSyncRequest r, string phase, params string[] arguments)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                var result = process.RunCore(r.RcloneExecutable, r.TimeoutSeconds, r.CancellationToken, r.Progress, phase, arguments);
                AppendDiagnostic(DiagnosticPath(r), phase, "elapsed_ms=" + timer.ElapsedMilliseconds + "\n" + result.Log + (result.Cancelled ? "\nCANCELLED" : ""));
                if (result.Cancelled) throw new OperationCanceledException(r.CancellationToken);
                return result;
            }
            catch (ModSyncTimeoutException ex) { AppendDiagnostic(DiagnosticPath(r), phase, "elapsed_ms=" + timer.ElapsedMilliseconds + "\n" + ex.Log + "\nTIMEOUT " + ex.Message); throw; }
            catch (ModSyncCallbackException ex) { AppendDiagnostic(DiagnosticPath(r), phase, "elapsed_ms=" + timer.ElapsedMilliseconds + "\n" + ex.Log + "\nCALLBACK ERROR " + ex.InnerException); throw; }
        }

        private static string DiagnosticPath(ModSyncRequest r) => r == null || string.IsNullOrWhiteSpace(r.StateDirectory)
            ? null : Path.Combine(r.StateDirectory, "diagnostics.log");

        private static void AppendDiagnostic(string path, string phase, string text)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                // Retain a bounded current log and one previous log, never pairing/baseline state.
                if (File.Exists(path) && new FileInfo(path).Length > 4 * 1024 * 1024)
                {
                    File.Copy(path, path + ".previous", true);
                    File.WriteAllText(path, "Previous diagnostics retained in diagnostics.log.previous\n");
                }
                text = text ?? "";
                if (text.Length > 262144) text = "[earlier command output omitted]\n" + text.Substring(text.Length - 262144);
                File.AppendAllText(path, "\n=== " + DateTime.UtcNow.ToString("O") + " phase=" + phase + " ===\n" + (text ?? "") + "\n");
            }
            catch { /* Diagnostics must not mask the original sync outcome. */ }
        }

        private ModSyncCommandResult Require(ModSyncRequest r, params string[] arguments)
        {
            var result = Run(r, arguments);
            if (result.ExitCode != 0)
                throw new InvalidOperationException("rclone " + arguments[0] + " failed (" + result.ExitCode + "). No deletion sync will follow.\n" + result.Log);
            return result;
        }

        /// <summary>Downloads cloud mods missing from the installed local folder without initializing or changing a pairing.</summary>
        public ModSyncResult DownloadAfterInstall(ModSyncRequest request, Func<ModSyncRequest, ModSyncResult> download)
        {
            string diagnostic = null;
            var validated = false;
            try
            {
                Validate(request);
                if (IsWithin(request.StateDirectory, Path.GetDirectoryName(Path.GetFullPath(request.LaunchFile))))
                    throw new InvalidOperationException("Install mod state must be outside the game installation.");
                if (download == null) throw new InvalidOperationException("Install mod download callback is required.");
                validated = true;
                request.CancellationToken.ThrowIfCancellationRequested();
                RejectReparsePoints(request.LocalFolder);
                RejectReparsePoints(request.StateDirectory);
                var stateParent = Path.GetDirectoryName(Path.GetFullPath(request.StateDirectory));
                Directory.CreateDirectory(stateParent);
                using (new FileStream(request.StateDirectory + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    RejectReparsePoints(request.LocalFolder);
                    RejectReparsePoints(request.StateDirectory);
                    var pairFile = Path.Combine(request.StateDirectory, "pair.txt");
                    var readyFile = Path.Combine(request.StateDirectory, "ready");
                    var identity = Path.GetFullPath(request.LocalFolder) + "\n" + request.RemoteFolder;
                    var hasPair = File.Exists(pairFile);
                    var completedPair = hasPair && File.Exists(readyFile);
                    if (hasPair && !string.Equals(File.ReadAllText(pairFile), identity, StringComparison.Ordinal))
                        throw new InvalidOperationException("Mod sync mapping changed. Restore the exact previous paths before downloading mods.");
                    // Do not alter game data or pairing state until prior identity is accepted.
                    Directory.CreateDirectory(request.LocalFolder);
                    Directory.CreateDirectory(request.StateDirectory);
                    diagnostic = DiagnosticPath(request);
                    // Incomplete initial state is not reset or promoted; downloading missing files remains safe.
                    var remote = RunPhase(request, "install-list", "lsf", request.RemoteFolder, "--recursive", "--files-only");
                    if (remote.ExitCode == 3 && !hasPair)
                        return new ModSyncResult { Success = true, DiagnosticPath = diagnostic,
                            Message = "No cloud mods are present yet; install download completed without creating a remote folder." };
                    if (remote.ExitCode != 0)
                        throw new InvalidOperationException("Unable to list cloud mod names safely; no install download was started.\n" + remote.Log);
                    var remoteFiles = remote.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    var localFiles = Directory.GetFiles(request.LocalFolder, "*", SearchOption.AllDirectories)
                        .Select(path => path.Substring(Path.GetFullPath(request.LocalFolder).TrimEnd(Path.DirectorySeparatorChar).Length + 1)
                            .Replace(Path.DirectorySeparatorChar, '/'));
                    var collision = FindWindowsCaseCollision(localFiles.Concat(remoteFiles));
                    if (collision != null)
                        throw new InvalidOperationException("Mod names collide on a Windows destination. Rename the conflicting files before installing: " + collision);
                    if (completedPair && !File.Exists(Path.Combine(request.StateDirectory, "paused")))
                        File.WriteAllText(Path.Combine(request.StateDirectory, "paused"), "Reinstalled: install-only mod download; resume background sync explicitly.");
                    request.CancellationToken.ThrowIfCancellationRequested();
                    var result = download(request);
                    if (result == null) throw new InvalidOperationException("Install mod download returned no result.");
                    result.DiagnosticPath = diagnostic;
                    if (!result.Success && string.IsNullOrWhiteSpace(result.Message))
                        result.Message = result.Cancelled ? "Install mod download cancelled; retry is safe." : "Install mod download failed; retry is safe.";
                    return result;
                }
            }
            catch (Exception ex)
            {
                var cancelled = ex is OperationCanceledException || (request != null && request.CancellationToken.IsCancellationRequested);
                var relink = request != null && (File.Exists(Path.Combine(request.StateDirectory ?? "", "paused")) ||
                    (File.Exists(Path.Combine(request.StateDirectory ?? "", "pair.txt")) && !File.Exists(Path.Combine(request.StateDirectory ?? "", "ready"))));
                AppendDiagnostic(diagnostic, "install-download", ex.ToString());
                return new ModSyncResult { Success = false, Cancelled = cancelled, RequiresRelink = relink,
                    DiagnosticPath = validated ? diagnostic : null,
                    Message = cancelled ? "Install mod download cancelled; retry is safe. No automatic resync was attempted." : ex.Message };
            }
        }

        /// <summary>
        /// Restores optional cloud mods after the installer has successfully installed the base game.
        /// The caller must gate this on the global feature setting and a configured mod path.
        /// </summary>
        public ModSyncResult RestoreAfterInstall(ModSyncRequest request)
        {
            string diagnostic = null;
            var validated = false;
            try
            {
                Validate(request);
                validated = true;
                request.CancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(request.StateDirectory));
                using (new FileStream(request.StateDirectory + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    diagnostic = DiagnosticPath(request);
                    if (Directory.Exists(request.StateDirectory))
                    {
                        var pairFile = Path.Combine(request.StateDirectory, "pair.txt");
                        var readyFile = Path.Combine(request.StateDirectory, "ready");
                        if (File.Exists(pairFile))
                        {
                            var identity = Path.GetFullPath(request.LocalFolder) + "\n" + request.RemoteFolder;
                            if (!string.Equals(File.ReadAllText(pairFile), identity, StringComparison.Ordinal))
                                throw new InvalidOperationException("Mod sync mapping changed. Restore the exact previous paths or explicitly recover the retained state before install restore.");
                            if (!File.Exists(readyFile))
                                throw new InvalidOperationException("A previous initial mod sync did not finish. State is retained; explicitly inspect and recover or relink it before retrying install restore.");
                            var archive = request.StateDirectory + ".archive-" + Guid.NewGuid().ToString("N");
                            Directory.Move(request.StateDirectory, archive);
                            Directory.CreateDirectory(request.StateDirectory);
                            diagnostic = DiagnosticPath(request);
                            AppendDiagnostic(diagnostic, "install-restore", "Archived completed pairing at " + archive);
                        }
                    }
                    // Installation explicitly restores absence; normal sync must still refuse it.
                    // Check existing ancestors before creation so a link cannot redirect mkdir.
                    RejectReparsePoints(request.LocalFolder);
                    Directory.CreateDirectory(request.LocalFolder);
                    Directory.CreateDirectory(request.StateDirectory);
                    return SynchronizeLocked(request, _ => false, true);
                }
            }
            catch (Exception ex)
            {
                var cancelled = ex is OperationCanceledException || (request != null && request.CancellationToken.IsCancellationRequested);
                var relink = request != null && (File.Exists(Path.Combine(request.StateDirectory ?? "", "paused")) ||
                    (File.Exists(Path.Combine(request.StateDirectory ?? "", "pair.txt")) && !File.Exists(Path.Combine(request.StateDirectory ?? "", "ready"))));
                AppendDiagnostic(validated ? (diagnostic ?? DiagnosticPath(request)) : null, "install-restore", ex.ToString());
                return new ModSyncResult { Success = false, Cancelled = cancelled, RequiresRelink = relink, DiagnosticPath = validated ? (diagnostic ?? DiagnosticPath(request)) : null,
                    Message = cancelled ? "Install mod restore cancelled. Base-game installation remains successful; restore can be retried." : ex.Message };
            }
        }

        public void Pause(string stateDirectory)
        {
            if (!Directory.Exists(stateDirectory)) return;
            using (new FileStream(stateDirectory + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                File.WriteAllText(Path.Combine(stateDirectory, "paused"), "Uninstalled: do not propagate local cache removal.");
        }

        public ModSyncResult Relink(ModSyncRequest request, Func<string, bool> confirm)
        {
            string diagnostic = null;
            try
            {
                Validate(request);
                request.CancellationToken.ThrowIfCancellationRequested();
                if (confirm == null || !confirm("Explicitly relink mods after reinstall/recovery? Previous sync state will be archived. Cloud files will be merged into the local folder, not deleted because the game was removed.\n\nLocal: " + request.LocalFolder + "\nCloud: " + request.RemoteFolder))
                    return Declined(request);
                Directory.CreateDirectory(Path.GetDirectoryName(request.StateDirectory));
                using (new FileStream(request.StateDirectory + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    if (Directory.Exists(request.StateDirectory))
                        Directory.Move(request.StateDirectory, request.StateDirectory + ".archive-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(request.LocalFolder);
                    Directory.CreateDirectory(request.StateDirectory);
                    diagnostic = DiagnosticPath(request);
                    // The relink confirmation already covers this initial merge.
                    return SynchronizeLocked(request, _ => true);
                }
            }
            catch (Exception ex)
            {
                var cancelled = ex is OperationCanceledException || (request != null && request.CancellationToken.IsCancellationRequested);
                AppendDiagnostic(diagnostic, "relink", ex.ToString());
                return new ModSyncResult { Success = false, Cancelled = cancelled, RequiresRelink = true, DiagnosticPath = diagnostic,
                    Message = cancelled ? "Relink cancelled. No automatic resync was attempted." : ex.Message };
            }
        }

        public static ModSyncRequest RequireCurrentMapping(ModSyncRequest imported, Func<ModSyncRequest> loadCurrent)
        {
            // Exceptions/offline errors propagate before any sync command is run.
            var current = loadCurrent();
            if (current == null || imported == null ||
                !string.Equals(current.GameId, imported.GameId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFullPath(current.LocalFolder), Path.GetFullPath(imported.LocalFolder), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFullPath(current.LaunchFile), Path.GetFullPath(imported.LaunchFile), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(current.RemoteFolder, imported.RemoteFolder, StringComparison.Ordinal))
                throw new InvalidOperationException("The live mod mapping differs from the imported mapping. Update the game library and explicitly relink changed paths before syncing.");
            return current;
        }

        private static ModSyncResult Declined(ModSyncRequest request) => new ModSyncResult { Success = false, Cancelled = true,
            DiagnosticPath = DiagnosticPath(request), Message = "Mod sync declined; game launch requires an explicit decision." };

        private static void Validate(ModSyncRequest r)
        {
            if (r == null || string.IsNullOrWhiteSpace(r.GameId) || string.IsNullOrWhiteSpace(r.LocalFolder) ||
                string.IsNullOrWhiteSpace(r.RemoteFolder) || string.IsNullOrWhiteSpace(r.StateDirectory) ||
                string.IsNullOrWhiteSpace(r.RcloneExecutable))
                throw new InvalidOperationException("Incomplete mod sync configuration.");
            if (!Path.IsPathRooted(r.LocalFolder) || !Path.IsPathRooted(r.StateDirectory))
                throw new InvalidOperationException("Local mod and state paths must be absolute.");
            if (!File.Exists(r.LaunchFile))
                throw new InvalidOperationException("Game launch file is missing. Mod sync is paused to protect cloud mods after uninstall.");
            if (IsWithin(r.LaunchFile, r.LocalFolder))
                throw new InvalidOperationException("The mod folder may not contain the game launch file or encompass the whole game installation.");
            if (IsWithin(r.StateDirectory, r.LocalFolder) || IsWithin(r.LocalFolder, r.StateDirectory))
                throw new InvalidOperationException("Sync state must be outside the mod folder.");
            if (r.TimeoutSeconds < 5 || r.TimeoutSeconds > 86400)
                throw new InvalidOperationException("Mod sync timeout must be between 5 and 86400 seconds.");
            if (r.RemoteFolder.IndexOf(':') < 0 || r.RemoteFolder.IndexOfAny(new[] { '\r', '\n', '"' }) >= 0)
                throw new InvalidOperationException("Invalid remote mod folder.");
        }

        public static string ResolveCloudModFolder(string root, string gameId)
        {
            root = (root ?? string.Empty).Replace('\\', '/').Trim('/');
            if (root.Split('/').Any(part => part == ".." || part == ".") || root.IndexOfAny(new[] { ':', '\r', '\n', '"' }) >= 0)
                throw new InvalidOperationException("Invalid rclone content root for mods.");
            if (string.IsNullOrWhiteSpace(gameId) || gameId == "." || gameId == ".." ||
                gameId.Any(c => char.IsControl(c) || "<>:\"/\\|?*".IndexOf(c) >= 0) ||
                gameId.EndsWith(".") || gameId.EndsWith(" "))
                throw new InvalidOperationException("Game ID must be a non-empty folder name, without separators or traversal.");
            return (string.IsNullOrEmpty(root) ? "" : root + "/") + "mods/" + gameId;
        }

        public static bool IsWithin(string candidate, string root)
        {
            var p = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(p, r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static void RejectReparsePoints(string root)
        {
            for (var parent = new DirectoryInfo(root); parent != null; parent = parent.Parent)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(parent.FullName); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Mod sync does not follow directory links or junctions.");
            }
            if (!Directory.Exists(root)) return;
            // Check each directory before descending, rather than following links recursively.
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.Count != 0)
                foreach (var path in Directory.GetFileSystemEntries(pending.Pop()))
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidOperationException("Mod sync does not follow file links or junctions.");
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                }
        }

        public static string Key(string value)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }
    }
}
