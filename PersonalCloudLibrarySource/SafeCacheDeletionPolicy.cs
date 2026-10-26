using System;
using System.Collections.Generic;
using System.IO;

namespace PersonalCloudLibrarySource
{
    public sealed class CacheDeletionAuthorization
    {
        public bool Allowed { get; set; }
        public string TargetPath { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }

    // Shared by menu eligibility and the deletion worker; adapted from upstream's policy.
    public sealed class SafeCacheDeletionPolicy
    {
        private static readonly StringComparison PathComparison = Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        public CacheDeletionAuthorization Authorize(string cacheRoot, string targetPath, bool allowOutsideCache)
            => Authorize(cacheRoot, targetPath, allowOutsideCache, false);

        public CacheDeletionAuthorization Authorize(string cacheRoot, string targetPath, bool allowOutsideCache, bool inspectDescendantLinks)
        {
            try
            {
                if (!IsFullyQualified(targetPath) || IsDevicePath(targetPath))
                    return Refuse("uninstall target must be fully rooted and must not be a device namespace path");
                var target = Normalize(targetPath);
                // Check AFTER normalization: C:\\folder\\.. is also a volume root.
                if (string.Equals(target, Normalize(Path.GetPathRoot(target)), PathComparison))
                    return Refuse("uninstall target is a drive or share root");
                string cache = null;
                if (!string.IsNullOrWhiteSpace(cacheRoot))
                {
                    if (!IsFullyQualified(cacheRoot) || IsDevicePath(cacheRoot))
                        return Refuse("LocalCacheFolder must be fully rooted and must not be a device namespace path");
                    cache = Normalize(cacheRoot);
                    if (string.Equals(cache, target, PathComparison) || IsDescendant(target, cache))
                        return Refuse("uninstall target is LocalCacheFolder itself or an ancestor of it");
                }
                if (!allowOutsideCache && (cache == null || !IsDescendant(cache, target)))
                    return Refuse("uninstall target is outside LocalCacheFolder");

                for (var current = target; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                    RejectLink(current, ReadAttributes(current));
                if (inspectDescendantLinks) InspectDescendants(target);
                return new CacheDeletionAuthorization { Allowed = true, TargetPath = target };
            }
            catch (Exception ex)
            {
                return Refuse("uninstall path could not be safely inspected: " + ex.Message);
            }
        }

        public bool IsStrictlyInsideCache(string cacheRoot, string candidatePath)
        {
            try
            {
                return IsFullyQualified(cacheRoot) && IsFullyQualified(candidatePath) &&
                    IsDescendant(Normalize(cacheRoot), Normalize(candidatePath));
            }
            catch { return false; }
        }

        // Never use recursive enumeration, which could follow a junction before it is checked.
        private static void InspectDescendants(string target)
        {
            var attributes = ReadAttributes(target);
            if (attributes == null || (attributes.Value & FileAttributes.Directory) == 0) return;
            var pending = new Stack<string>();
            pending.Push(target);
            while (pending.Count != 0)
            {
                var directory = pending.Pop();
                RejectLink(directory, ReadAttributes(directory));
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    var child = ReadAttributes(entry);
                    if (child == null) throw new IOException("A descendant disappeared during validation.");
                    RejectLink(entry, child);
                    if ((child.Value & FileAttributes.Directory) != 0) pending.Push(entry);
                }
            }
        }

        private static FileAttributes? ReadAttributes(string path)
        {
            try { return File.GetAttributes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            // Access/IO failures are not absence: the caller refuses deletion.
        }

        private static void RejectLink(string path, FileAttributes? attributes)
        {
            if (attributes != null && (attributes.Value & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The uninstall target contains or passes through a reparse point: " + path);
        }

        private static bool IsFullyQualified(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return false;
            var root = Path.GetPathRoot(path);
            if (Path.DirectorySeparatorChar == '/') return path[0] == '/' && !path.StartsWith("//", StringComparison.Ordinal);
            // Exclude C:relative and \\current-drive paths; Path.IsPathRooted alone accepts them.
            return root.Length >= 3 && ((root[1] == ':' && (root[2] == '\\' || root[2] == '/')) ||
                root.StartsWith(@"\\", StringComparison.Ordinal));
        }

        private static bool IsDevicePath(string path)
        {
            path = path.Replace('/', '\\');
            return path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string path)
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(trimmed, root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), PathComparison)
                ? root : trimmed;
        }

        private static bool IsDescendant(string root, string candidate)
            => !string.Equals(root, candidate, PathComparison) &&
                candidate.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, PathComparison);

        private static CacheDeletionAuthorization Refuse(string reason) => new CacheDeletionAuthorization { Reason = reason };
    }

    public sealed class SafeCacheDeletionExecutor
    {
        private readonly SafeCacheDeletionPolicy policy;
        private readonly Action<string> deleteFile;
        private readonly Action<string> deleteDirectory;

        public SafeCacheDeletionExecutor() : this(new SafeCacheDeletionPolicy(), File.Delete, path => Directory.Delete(path, true)) { }

        public SafeCacheDeletionExecutor(SafeCacheDeletionPolicy policy, Action<string> deleteFile, Action<string> deleteDirectory)
        {
            this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
            this.deleteFile = deleteFile ?? throw new ArgumentNullException(nameof(deleteFile));
            this.deleteDirectory = deleteDirectory ?? throw new ArgumentNullException(nameof(deleteDirectory));
        }

        public CacheDeletionAuthorization Authorize(string cacheRoot, string targetPath, bool allowOutsideCache)
            => policy.Authorize(cacheRoot, targetPath, allowOutsideCache, true);

        public CacheDeletionAuthorization Delete(string cacheRoot, string targetPath, bool allowOutsideCache)
        {
            var authorization = Authorize(cacheRoot, targetPath, allowOutsideCache);
            if (!authorization.Allowed) return authorization;
            // Recheck target/ancestors after the tree scan. Managed check/use cannot be atomic.
            authorization = policy.Authorize(cacheRoot, authorization.TargetPath, allowOutsideCache);
            if (!authorization.Allowed) return authorization;
            if (File.Exists(authorization.TargetPath)) deleteFile(authorization.TargetPath);
            else if (Directory.Exists(authorization.TargetPath)) deleteDirectory(authorization.TargetPath);
            else return new CacheDeletionAuthorization { Reason = "uninstall target does not exist" };
            return authorization;
        }
    }
}
