using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace PersonalCloudLibrarySource
{
    /// <summary>Safely installs an archive whose root is the contents of the modPath folder.</summary>
    public static class InstallZipModArchive
    {
        public static void Install(string archivePath, string targetDirectory, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
                throw new FileNotFoundException("The optional mod ZIP was not downloaded.", archivePath);
            if (string.IsNullOrWhiteSpace(targetDirectory)) throw new ArgumentException("Mod destination is empty.", nameof(targetDirectory));
            targetDirectory = Path.GetFullPath(targetDirectory);
            var parent = Path.GetDirectoryName(targetDirectory);
            if (string.IsNullOrWhiteSpace(parent)) throw new InvalidOperationException("Mod destination has no parent directory.");
            Directory.CreateDirectory(parent);
            if (Directory.Exists(targetDirectory) && Directory.EnumerateFileSystemEntries(targetDirectory).Any())
                throw new IOException("Mod destination is not empty; ZIP install refused to overwrite existing user files.");
            if (File.Exists(targetDirectory)) throw new IOException("Mod destination is a file, not an empty directory.");

            var stage = Path.Combine(parent, ".pcls-modzip-stage-" + Guid.NewGuid().ToString("N"));
            var displacedEmpty = Path.Combine(parent, ".pcls-modzip-empty-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            var movedOld = false;
            try
            {
                using (var archive = ZipFile.OpenRead(archivePath))
                {
                    var seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    var seenSpelling = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var explicitPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in archive.Entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
                        var relative = entry.FullName.TrimEnd('/');
                        ValidateName(relative, entry.FullName);
                        var segments = relative.Split('/');
                        for (var i = 1; i <= segments.Length; i++)
                        {
                            var path = string.Join("/", segments.Take(i));
                            var directory = i < segments.Length || isDirectory;
                            if (seen.TryGetValue(path, out var priorDirectory))
                            {
                                if (priorDirectory != directory)
                                    throw new InvalidDataException("ZIP contains a file/directory name collision: " + entry.FullName);
                                if (!string.Equals(seenSpelling[path], path, StringComparison.Ordinal))
                                    throw new InvalidDataException("ZIP contains a case-insensitive path collision: " + entry.FullName);
                            }
                            else { seen.Add(path, directory); seenSpelling.Add(path, path); }
                        }
                        if (!explicitPaths.Add(relative))
                            throw new InvalidDataException("ZIP contains a duplicate entry: " + entry.FullName);
                        var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
                        if (unixMode == 0xA000 || (entry.ExternalAttributes & 0x400) != 0)
                            throw new InvalidDataException("ZIP links and reparse-point entries are not supported: " + entry.FullName);
                    }
                    foreach (var entry in archive.Entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
                        var relative = entry.FullName.TrimEnd('/');
                        var destination = Path.GetFullPath(Path.Combine(stage, relative.Replace('/', Path.DirectorySeparatorChar)));
                        if (!destination.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("ZIP entry escapes the staging directory: " + entry.FullName);
                        if (isDirectory) { Directory.CreateDirectory(destination); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        using (var input = entry.Open())
                        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            var buffer = new byte[81920]; int read;
                            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                output.Write(buffer, 0, read);
                            }
                        }
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(targetDirectory))
                {
                    if (Directory.EnumerateFileSystemEntries(targetDirectory).Any())
                        throw new IOException("Mod destination changed during download and is no longer empty.");
                    Directory.Move(targetDirectory, displacedEmpty);
                    movedOld = true;
                }
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Move(stage, targetDirectory);
                if (movedOld) Directory.Delete(displacedEmpty);
            }
            catch
            {
                if (movedOld && !Directory.Exists(targetDirectory) && Directory.Exists(displacedEmpty))
                    Directory.Move(displacedEmpty, targetDirectory);
                throw;
            }
            finally
            {
                if (Directory.Exists(stage)) Directory.Delete(stage, true);
                if (Directory.Exists(displacedEmpty)) Directory.Delete(displacedEmpty);
            }
        }

        private static void ValidateName(string relative, string original)
        {
            if (string.IsNullOrEmpty(relative) || relative.StartsWith("/", StringComparison.Ordinal) || relative.Contains("\\") ||
                Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0)
                throw new InvalidDataException("ZIP entry has an unsafe path: " + original);
            foreach (var segment in relative.Split('/'))
            {
                if (segment.Length == 0 || segment == "." || segment == ".." || segment.EndsWith(".", StringComparison.Ordinal) ||
                    segment.EndsWith(" ", StringComparison.Ordinal) || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    segment.IndexOfAny(new[] { '<', '>', ':', '"', '|', '?', '*' }) >= 0 || IsReservedWindowsName(segment))
                    throw new InvalidDataException("ZIP entry has an unsafe path: " + original);
            }
        }

        private static bool IsReservedWindowsName(string name)
        {
            var stem = name.Split('.')[0].ToUpperInvariant();
            return stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] >= '1' && stem[3] <= '9');
        }
    }
}
