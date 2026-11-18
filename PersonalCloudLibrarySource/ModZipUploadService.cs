using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PersonalCloudLibrarySource
{
    public sealed class ModZipUploadService
    {
        private readonly ModSyncProcess process = new ModSyncProcess();
        private const int MaxFiles = 20000;
        private const long MaxExpandedBytes = 8L * 1024 * 1024 * 1024;

        public ModSyncResult UploadIfChanged(ModSyncRequest request, bool beforeUninstall = false)
        {
            string archive = null;
            try
            {
                Validate(request);
                request.CancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(request.StateDirectory);
                using (new FileStream(request.StateDirectory + ".zip-upload.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    var files = ListFiles(request.LocalFolder);
                    if (files.Length == 0) return new ModSyncResult { Success = false, Message = "Empty mod folders are not uploaded; the previous cloud ZIP is preserved." };
                    if (files.Length > MaxFiles) throw new InvalidOperationException("Mod folder exceeds the safe ZIP file-count limit.");
                    ValidateNames(files.Select(path => Relative(request.LocalFolder, path).Replace(Path.DirectorySeparatorChar, '/')));
                    var total = 0L;
                    foreach (var file in files)
                    {
                        var length = new FileInfo(file).Length;
                        if (length > MaxExpandedBytes - total)
                            throw new InvalidOperationException("Mod folder exceeds the safe ZIP expanded-size limit.");
                        total += length;
                    }
                    var snapshot = Snapshot(request.LocalFolder, files, request.CancellationToken);
                    var baselinePath = Path.Combine(request.StateDirectory, "upload-baseline.txt");
                    var identity = Path.GetFullPath(request.LocalFolder) + "\n" + request.RemoteFolder;
                    var stored = ReadBaseline(baselinePath, identity);
                    if (stored != null && stored == snapshot)
                        return new ModSyncResult { Success = true, Message = "Mod ZIP unchanged; no cloud transfer was needed." };

                    var remoteZip = request.RemoteFolder.TrimEnd('/') + ".zip";
                    request.CancellationToken.ThrowIfCancellationRequested();
                    if (request.BeforeCloudWrite != null && !request.BeforeCloudWrite())
                        throw new InvalidOperationException("The live mod mapping changed; upload was refused.");
                    if (stored == null && RemoteExists(request, remoteZip))
                        throw new InvalidOperationException("An existing cloud mod ZIP has no matching local upload baseline. It was preserved; restore/review it before enabling upload.");
                    request.CancellationToken.ThrowIfCancellationRequested();
                    archive = Path.Combine(request.StateDirectory, "upload-" + Guid.NewGuid().ToString("N") + ".zip");
                    using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                    {
                        foreach (var file in files)
                        {
                            request.CancellationToken.ThrowIfCancellationRequested();
                            var entry = zip.CreateEntry(Relative(request.LocalFolder, file).Replace(Path.DirectorySeparatorChar, '/'), CompressionLevel.Optimal);
                            using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                            using (var output = entry.Open()) input.CopyTo(output);
                        }
                    }
                    if (Snapshot(request.LocalFolder, ListFiles(request.LocalFolder), request.CancellationToken) != snapshot)
                        throw new IOException("Mod files changed while the ZIP was being built; no cloud update was published.");
                    var tempRemote = remoteZip + ".pcls-stage-" + Guid.NewGuid().ToString("N");
                    Require(request, "copyto", archive, tempRemote);
                    try { Require(request, "moveto", tempRemote, remoteZip, "--ignore-times"); }
                    catch
                    {
                        // Best-effort cleanup only; the published destination is not claimed atomic.
                        Run(request, "deletefile", tempRemote);
                        throw;
                    }
                    WriteBaseline(baselinePath, identity, snapshot);
                    if (beforeUninstall && Snapshot(request.LocalFolder, ListFiles(request.LocalFolder), request.CancellationToken) != snapshot)
                        throw new IOException("Mods changed during upload. Try uninstalling again to back up the newer changes.");
                    return new ModSyncResult { Success = true, Message = "Changed mods uploaded and published as " + remoteZip + "." };
                }
            }
            catch (Exception ex)
            {
                var cancelled = ex is OperationCanceledException || (request != null && request.CancellationToken.IsCancellationRequested);
                return new ModSyncResult { Success = false, Cancelled = cancelled,
                    Message = cancelled ? "Mod ZIP upload cancelled; the previous cloud ZIP and baseline are preserved." : ex.Message };
            }
            finally { if (archive != null) try { if (File.Exists(archive)) File.Delete(archive); } catch { } }
        }

        public void SeedBaseline(ModSyncRequest request)
        {
            Validate(request);
            var files = ListFiles(request.LocalFolder);
            if (files.Length == 0) return;
            var snapshot = Snapshot(request.LocalFolder, files, request.CancellationToken);
            WriteBaseline(Path.Combine(request.StateDirectory, "upload-baseline.txt"),
                Path.GetFullPath(request.LocalFolder) + "\n" + request.RemoteFolder, snapshot);
        }

        private bool RemoteExists(ModSyncRequest request, string remoteZip)
        {
            var result = Run(request, "lsjson", remoteZip);
            if (result.ExitCode == 0) return true;
            if (result.ExitCode == 3) return false;
            throw new IOException("Could not safely check the existing cloud ZIP before first upload. " + result.Log);
        }

        private ModSyncCommandResult Run(ModSyncRequest request, params string[] args) =>
            process.Run(request.RcloneExecutable, request.TimeoutSeconds, request.CancellationToken, request.Progress, args);

        private ModSyncCommandResult Require(ModSyncRequest request, params string[] args)
        {
            var result = Run(request, args);
            if (result.ExitCode != 0) throw new IOException("rclone " + args[0] + " failed. " + result.Log);
            return result;
        }

        private static string Snapshot(string root, IEnumerable<string> files, CancellationToken token)
        {
            using (var hash = SHA256.Create())
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                foreach (var path in files)
                {
                    token.ThrowIfCancellationRequested();
                    var name = Relative(root, path).Replace(Path.DirectorySeparatorChar, '/');
                    var nameBytes = Encoding.UTF8.GetBytes(name);
                    writer.Write(nameBytes.Length); writer.Write(nameBytes);
                    using (var input = File.OpenRead(path))
                    {
                        var digest = hash.ComputeHash(input);
                        writer.Write(digest.Length); writer.Write(digest);
                    }
                }
                writer.Flush();
                return Convert.ToBase64String(hash.ComputeHash(stream.ToArray()));
            }
        }

        private static string Relative(string root, string path) => path.Substring(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length + 1);

        private static void WriteBaseline(string path, string identity, string snapshot)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, Convert.ToBase64String(Encoding.UTF8.GetBytes(identity)) + "\n" + snapshot);
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }

        private static string ReadBaseline(string path, string identity)
        {
            if (!File.Exists(path)) return null;
            var parts = File.ReadAllText(path).Split(new[] { '\n' }, 2);
            if (parts.Length != 2 || Encoding.UTF8.GetString(Convert.FromBase64String(parts[0])) != identity)
                throw new InvalidOperationException("Mod upload baseline belongs to a different local/remote mapping; refusing upload.");
            return parts[1];
        }

        private static void Validate(ModSyncRequest r)
        {
            if (r == null || string.IsNullOrWhiteSpace(r.GameId) || string.IsNullOrWhiteSpace(r.LocalFolder) ||
                string.IsNullOrWhiteSpace(r.RemoteFolder) || string.IsNullOrWhiteSpace(r.StateDirectory) || string.IsNullOrWhiteSpace(r.RcloneExecutable))
                throw new InvalidOperationException("Incomplete mod ZIP upload configuration.");
            if (!Path.IsPathRooted(r.LocalFolder) || !Path.IsPathRooted(r.StateDirectory) || !Directory.Exists(r.LocalFolder))
                throw new InvalidOperationException("The installed mod folder or upload state path is missing.");
            if (ModSyncService.IsWithin(r.StateDirectory, r.LocalFolder) || ModSyncService.IsWithin(r.LocalFolder, r.StateDirectory))
                throw new InvalidOperationException("Mod upload state must be outside the mod folder.");
            if (File.Exists(r.LaunchFile) && ModSyncService.IsWithin(r.LaunchFile, r.LocalFolder))
                throw new InvalidOperationException("The mod folder may not contain the game launch file.");
            if (r.RemoteFolder.IndexOfAny(new[] { '\r', '\n', '"' }) >= 0 || !r.RemoteFolder.Contains(":"))
                throw new InvalidOperationException("Invalid remote ZIP path.");
        }

        private static string[] ListFiles(string root)
        {
            InstallZipModArchive.RejectDirectoryLinks(root);
            var pending = new Stack<string>(); pending.Push(root);
            var files = new List<string>();
            while (pending.Count != 0)
                foreach (var path in Directory.GetFileSystemEntries(pending.Pop()))
                {
                    var entryAttributes = File.GetAttributes(path);
                    if ((entryAttributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidOperationException("Mod ZIP upload refuses linked folders/files.");
                    if ((entryAttributes & FileAttributes.Directory) != 0) pending.Push(path);
                    else files.Add(path);
                }
            return files.OrderBy(path => Relative(root, path), StringComparer.Ordinal).ToArray();
        }

        private static void ValidateNames(IEnumerable<string> names)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var reserved = new HashSet<string>(new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }, StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                if (string.IsNullOrWhiteSpace(name) || name.StartsWith("/", StringComparison.Ordinal) || name.Contains("\\") ||
                    name.IndexOfAny(new[] { ':', '\r', '\n', '\0' }) >= 0 || !seen.Add(name))
                    throw new InvalidOperationException("Mod folder contains a duplicate or unsafe archive path: " + name);
                foreach (var part in name.Split('/'))
                {
                    var stem = part.Split('.')[0];
                    if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal) ||
                        part.EndsWith(" ", StringComparison.Ordinal) || part.IndexOfAny(new[] { '<', '>', '"', '|', '?', '*' }) >= 0 ||
                        part.Any(char.IsControl) || reserved.Contains(stem))
                        throw new InvalidOperationException("Mod folder contains a Windows-unsafe archive path: " + name);
                }
            }
            foreach (var name in seen)
            {
                var parts = name.Split('/');
                for (var i = 1; i < parts.Length; i++)
                    if (seen.Contains(string.Join("/", parts.Take(i))))
                        throw new InvalidOperationException("Mod folder contains a file/directory archive path collision: " + name);
            }
        }
    }
}
