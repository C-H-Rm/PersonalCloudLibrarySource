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

    // Deliberately SDK-independent: this is the single policy used by menu resolution and deletion.
    public sealed class SafeCacheDeletionPolicy
    {
        public CacheDeletionAuthorization Authorize(string cacheRoot, string targetPath, bool allowOutsideCache)
        {
            return Authorize(cacheRoot, targetPath, allowOutsideCache, false);
        }

        // Descendant inspection is reserved for the deletion worker. Menu preparation must not walk a tree.
        public CacheDeletionAuthorization Authorize(
            string cacheRoot,
            string targetPath,
            bool allowOutsideCache,
            bool inspectDescendantLinks)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return Refuse("uninstall target path is empty");
            }

            if (IsWindowsDeviceNamespacePath(targetPath))
            {
                return Refuse("uninstall target is a Windows device namespace path");
            }

            if (!IsFullyQualifiedPath(targetPath))
            {
                return Refuse("uninstall target path must be fully rooted");
            }

            if (IsVolumeOrShareRoot(targetPath))
            {
                return Refuse("uninstall target is a drive or share root");
            }

            string normalizedTarget;
            string normalizedCache = string.Empty;
            try
            {
                normalizedTarget = NormalizePath(targetPath);
                if (!string.IsNullOrWhiteSpace(cacheRoot))
                {
                    if (IsWindowsDeviceNamespacePath(cacheRoot) || !IsFullyQualifiedPath(cacheRoot))
                    {
                        return Refuse("LocalCacheFolder must be fully rooted and must not be a device namespace path");
                    }

                    normalizedCache = NormalizePath(cacheRoot);
                }
            }
            catch (Exception ex)
            {
                return Refuse("uninstall path could not be normalized: " + ex.Message);
            }

            if (!string.IsNullOrWhiteSpace(normalizedCache))
            {
                if (PathsEqual(normalizedTarget, normalizedCache))
                {
                    return Refuse("uninstall target is LocalCacheFolder itself");
                }

                if (IsPathAncestor(normalizedTarget, normalizedCache))
                {
                    return Refuse("uninstall target is an ancestor of LocalCacheFolder");
                }

                if (!allowOutsideCache && !IsStrictDescendant(normalizedCache, normalizedTarget))
                {
                    return Refuse("uninstall target is outside LocalCacheFolder");
                }
            }
            else if (!allowOutsideCache)
            {
                return Refuse("LocalCacheFolder is required unless outside-cache uninstall is explicitly enabled");
            }

            string reparseReason;
            if (!TryFindReparsePoint(normalizedTarget, out reparseReason))
            {
                return Refuse(reparseReason);
            }

            if (inspectDescendantLinks)
            {
                string descendantReason;
                if (!TryFindDescendantReparsePoint(normalizedTarget, out descendantReason))
                {
                    return Refuse(descendantReason);
                }
            }

            return new CacheDeletionAuthorization
            {
                Allowed = true,
                TargetPath = normalizedTarget
            };
        }

        public bool IsStrictlyInsideCache(string cacheRoot, string candidatePath)
        {
            if (string.IsNullOrWhiteSpace(cacheRoot) || string.IsNullOrWhiteSpace(candidatePath))
            {
                return false;
            }

            try
            {
                return IsStrictDescendant(NormalizePath(cacheRoot), NormalizePath(candidatePath));
            }
            catch
            {
                return false;
            }
        }

        private static bool TryFindReparsePoint(string target, out string reason)
        {
            var current = target;
            while (!string.IsNullOrWhiteSpace(current))
            {
                FileAttributes attributes;
                bool exists;
                if (!TryReadAttributes(current, out attributes, out exists, out reason))
                {
                    return false;
                }

                if (exists && (attributes & FileAttributes.ReparsePoint) != 0)
                {
                    reason = "uninstall target or ancestor is a reparse point: " + current;
                    return false;
                }

                string parent;
                try
                {
                    parent = Path.GetDirectoryName(current);
                }
                catch (Exception ex)
                {
                    reason = "uninstall path ancestor could not be inspected: " + ex.Message;
                    return false;
                }

                if (string.IsNullOrWhiteSpace(parent) || PathsEqual(parent, current))
                {
                    break;
                }

                current = parent;
            }

            reason = string.Empty;
            return true;
        }

        private static bool TryFindDescendantReparsePoint(string target, out string reason)
        {
            FileAttributes attributes;
            bool exists;
            if (!TryReadAttributes(target, out attributes, out exists, out reason))
            {
                return false;
            }

            if (!exists || (attributes & FileAttributes.Directory) == 0)
            {
                reason = string.Empty;
                return true;
            }

            return TryScanDirectory(target, out reason);
        }

        private static bool TryScanDirectory(string directory, out string reason)
        {
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(directory).EnumerateFileSystemInfos();
            }
            catch (Exception ex)
            {
                reason = "uninstall target descendants could not be inspected: " + ex.Message;
                return false;
            }

            try
            {
                foreach (var entry in entries)
                {
                    FileAttributes attributes;
                    bool exists;
                    if (!TryReadAttributes(entry.FullName, out attributes, out exists, out reason))
                    {
                        return false;
                    }

                    if (!exists)
                    {
                        reason = "uninstall target descendant disappeared while it was being inspected";
                        return false;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        reason = "uninstall target contains a reparse point: " + entry.FullName;
                        return false;
                    }

                    if ((attributes & FileAttributes.Directory) != 0 && !TryScanDirectory(entry.FullName, out reason))
                    {
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                reason = "uninstall target descendants could not be inspected: " + ex.Message;
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private static bool TryReadAttributes(
            string path,
            out FileAttributes attributes,
            out bool exists,
            out string reason)
        {
            try
            {
                attributes = File.GetAttributes(path);
                exists = true;
                reason = string.Empty;
                return true;
            }
            catch (FileNotFoundException)
            {
                attributes = 0;
                exists = false;
                reason = string.Empty;
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                attributes = 0;
                exists = false;
                reason = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                attributes = 0;
                exists = false;
                reason = "uninstall path could not be inspected: " + ex.Message;
                return false;
            }
        }

        private static string NormalizePath(string path)
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrWhiteSpace(trimmed) ? root : trimmed;
        }

        private static bool IsStrictDescendant(string root, string candidate)
        {
            return !PathsEqual(root, candidate) && IsPathAncestor(root, candidate);
        }

        private static bool IsPathAncestor(string possibleAncestor, string possibleDescendant)
        {
            if (PathsEqual(possibleAncestor, possibleDescendant))
            {
                return false;
            }

            var ancestor = possibleAncestor.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var descendant = possibleDescendant.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (ancestor.Length == 0)
            {
                ancestor = Path.DirectorySeparatorChar.ToString();
            }

            return descendant.StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar &&
                 descendant.StartsWith(ancestor + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }

        private static bool PathsEqual(string left, string right)
        {
            return string.Equals(
                left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFullyQualifiedPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (IsWindowsDrivePath(path))
            {
                return Path.DirectorySeparatorChar == '\\' &&
                    path.Length >= 3 && IsDirectorySeparator(path[2]);
            }

            if (IsUncPath(path))
            {
                return Path.DirectorySeparatorChar == '\\' && HasUncShare(path);
            }

            if (Path.DirectorySeparatorChar == '/' && path[0] == '/')
            {
                return true;
            }

            return Path.DirectorySeparatorChar == '\\' &&
                path[0] != '\\' &&
                Path.IsPathRooted(path);
        }

        private static bool IsVolumeOrShareRoot(string path)
        {
            if (IsWindowsDrivePath(path))
            {
                return path.Length == 3 && IsDirectorySeparator(path[2]);
            }

            if (IsUncPath(path))
            {
                return HasUncShare(path) && CountUncComponents(path) == 2;
            }

            try
            {
                var full = Path.GetFullPath(path);
                var root = Path.GetPathRoot(full);
                return !string.IsNullOrWhiteSpace(root) && PathsEqual(full, root);
            }
            catch
            {
                return true;
            }
        }

        private static bool IsWindowsDeviceNamespacePath(string path)
        {
            return path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("//?/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("//./", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/??/", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsWindowsDrivePath(string path)
        {
            return path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':';
        }

        private static bool IsUncPath(string path)
        {
            return path.Length >= 2 && IsDirectorySeparator(path[0]) && IsDirectorySeparator(path[1]);
        }

        private static bool HasUncShare(string path)
        {
            return CountUncComponents(path) >= 2;
        }

        private static int CountUncComponents(string path)
        {
            var components = new List<string>();
            var current = string.Empty;
            for (var i = 2; i < path.Length; i++)
            {
                if (IsDirectorySeparator(path[i]))
                {
                    if (current.Length > 0)
                    {
                        components.Add(current);
                        current = string.Empty;
                    }
                }
                else
                {
                    current += path[i];
                }
            }

            if (current.Length > 0)
            {
                components.Add(current);
            }

            return components.Count;
        }

        private static bool IsDirectorySeparator(char value)
        {
            return value == '/' || value == '\\';
        }

        private static CacheDeletionAuthorization Refuse(string reason)
        {
            return new CacheDeletionAuthorization { Reason = reason };
        }
    }

    public sealed class SafeCacheDeletionExecutor
    {
        private readonly SafeCacheDeletionPolicy policy;
        private readonly Action<string> deleteFile;
        private readonly Action<string> deleteDirectory;

        public SafeCacheDeletionExecutor()
            : this(new SafeCacheDeletionPolicy(), File.Delete, path => Directory.Delete(path, true))
        {
        }

        public SafeCacheDeletionExecutor(
            SafeCacheDeletionPolicy policy,
            Action<string> deleteFile,
            Action<string> deleteDirectory)
        {
            this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
            this.deleteFile = deleteFile ?? throw new ArgumentNullException(nameof(deleteFile));
            this.deleteDirectory = deleteDirectory ?? throw new ArgumentNullException(nameof(deleteDirectory));
        }

        public CacheDeletionAuthorization Authorize(string cacheRoot, string targetPath, bool allowOutsideCache)
        {
            return policy.Authorize(cacheRoot, targetPath, allowOutsideCache, true);
        }

        public CacheDeletionAuthorization Delete(string cacheRoot, string targetPath, bool allowOutsideCache)
        {
            var authorization = Authorize(cacheRoot, targetPath, allowOutsideCache);
            if (!authorization.Allowed)
            {
                return authorization;
            }

            // This check deliberately remains separate from the managed filesystem mutation.
            // It narrows, but cannot eliminate, the check/use race with another process.
            authorization = policy.Authorize(cacheRoot, authorization.TargetPath, allowOutsideCache, true);
            if (!authorization.Allowed)
            {
                return authorization;
            }

            if (File.Exists(authorization.TargetPath))
            {
                deleteFile(authorization.TargetPath);
            }
            else if (Directory.Exists(authorization.TargetPath))
            {
                deleteDirectory(authorization.TargetPath);
            }
            else
            {
                return Refuse("uninstall target does not exist");
            }

            return authorization;
        }

        private static CacheDeletionAuthorization Refuse(string reason)
        {
            return new CacheDeletionAuthorization { Reason = reason };
        }
    }
}
