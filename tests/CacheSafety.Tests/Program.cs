using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using PersonalCloudLibrarySource;

class Program
{
    private sealed class SkipTest : Exception
    {
        public SkipTest(string message) : base(message) { }
    }

    static int Main()
    {
        int passed = 0, failed = 0, skipped = 0;
        void Test(string name, Action action)
        {
            try
            {
                action();
                Console.WriteLine("PASS " + name);
                passed++;
            }
            catch (SkipTest ex)
            {
                Console.WriteLine("SKIP " + name + ": " + ex.Message);
                skipped++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL " + name + ": " + ex.Message);
                failed++;
            }
        }

        Test("refuses relative drive-relative device and root paths", () =>
        {
            using var f = new Fixture();
            var candidates = new[]
            {
                ".",
                "child\\game.exe",
                "C:game.exe",
                @"\\?\C:\game.exe",
                @"\\.\PhysicalDrive0",
                @"\??\C:\game.exe",
                @"\Device\HarddiskVolume1\game.exe",
                Path.GetPathRoot(f.Cache),
                @"\\server\share\",
                @"C:\folder\..",
                @"C:\.",
                @"\\server\share\folder\.."
            };
            foreach (var candidate in candidates)
            {
                var result = new SafeCacheDeletionPolicy().Authorize(f.Cache, candidate, true);
                Check(!result.Allowed, "accepted unsafe path: " + candidate);
            }
        });

        Test("refuses cache root and every ancestor of cache", () =>
        {
            using var f = new Fixture();
            var policy = new SafeCacheDeletionPolicy();
            Check(!policy.Authorize(f.Cache, f.Cache, true).Allowed, "cache root was accepted");
            Check(!policy.Authorize(f.Cache, f.Root, true).Allowed, "cache parent was accepted");
            Check(!policy.Authorize(f.Cache, Path.GetDirectoryName(f.Root), true).Allowed, "cache grandparent was accepted");
        });

        Test("normalized roots are refused even with a cache on another volume", () =>
        {
            var policy = new SafeCacheDeletionPolicy();
            foreach (var target in new[] { @"C:\folder\..", @"C:\.", @"\\server\share\folder\.." })
                Check(!policy.Authorize(@"Z:\cache", target, true).Allowed, "normalized volume/share root accepted: " + target);
        });

        Test("outside-cache deletion requires explicit opt-in", () =>
        {
            using var f = new Fixture();
            var outside = Path.Combine(f.Root, "outside.bin");
            File.WriteAllText(outside, "outside");
            var executor = new SafeCacheDeletionExecutor();
            Check(!executor.Delete(f.Cache, outside, false).Allowed, "outside target was deleted without opt-in");
            Check(File.Exists(outside), "outside target changed without opt-in");
            Check(executor.Delete(f.Cache, outside, true).Allowed, "ordinary outside target was not allowed with opt-in");
            Check(!File.Exists(outside), "opted-in outside target was not deleted");
        });

        Test("deletes valid file and folder while preserving external data", () =>
        {
            using var f = new Fixture();
            var file = Path.Combine(f.Cache, "game.bin");
            var folder = Path.Combine(f.Cache, "game");
            var external = Path.Combine(f.Root, "external");
            File.WriteAllText(file, "cache");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "child.bin"), "cache");
            Directory.CreateDirectory(external);
            File.WriteAllText(Path.Combine(external, "keep.bin"), "keep");

            var executor = new SafeCacheDeletionExecutor();
            Check(executor.Delete(f.Cache, file, false).Allowed, "valid file was not deleted");
            Check(executor.Delete(f.Cache, folder, false).Allowed, "valid folder was not deleted");
            Check(!File.Exists(file) && !Directory.Exists(folder), "valid cache data remains");
            Check(File.Exists(Path.Combine(external, "keep.bin")), "external data was deleted");
        });

        Test("missing target is not reported as deletion", () =>
        {
            using var f = new Fixture();
            var result = new SafeCacheDeletionExecutor().Delete(
                f.Cache,
                Path.Combine(f.Cache, "missing", "game.exe"),
                false);
            Check(!result.Allowed, "missing target was reported as deleted");
            Check(result.Reason.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) >= 0, "wrong missing-target reason");
        });

        Test("refuses target and descendant Linux symlinks or Windows junctions", () =>
        {
            using var f = new Fixture();
            var external = Path.Combine(f.Root, "external");
            var target = Path.Combine(f.Cache, "target");
            var link = Path.Combine(target, "external-link");
            Directory.CreateDirectory(external);
            File.WriteAllText(Path.Combine(external, "keep.bin"), "keep");
            Directory.CreateDirectory(target);
            if (!TryCreateDirectoryLink(link, external))
            {
                throw new SkipTest("directory link creation is unavailable");
            }

            try
            {
                var policy = new SafeCacheDeletionPolicy();
                Check(policy.Authorize(f.Cache, link, true).Allowed == false, "link target was accepted");
                Check(policy.Authorize(f.Cache, target, false).Allowed, "menu-time policy unexpectedly walked descendants");
                var result = new SafeCacheDeletionExecutor().Delete(f.Cache, target, false);
                Check(!result.Allowed, "recursive deletion followed a descendant link");
                Check(File.Exists(Path.Combine(external, "keep.bin")), "external data was deleted through a link");
            }
            finally
            {
                RemovePath(link);
            }
        });

        Test("fails closed when an ancestor cannot be read", () =>
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                throw new SkipTest("real permission fixture is Linux-only");
            }

            using var f = new Fixture();
            var blocked = Path.Combine(f.Cache, "blocked");
            Directory.CreateDirectory(blocked);
            var target = Path.Combine(blocked, "game.exe");
            File.WriteAllText(target, "cache");
            File.SetUnixFileMode(blocked, UnixFileMode.None);
            try
            {
                var result = new SafeCacheDeletionPolicy().Authorize(f.Cache, target, false);
                Check(!result.Allowed, "unreadable ancestor was accepted");
            }
            finally
            {
                File.SetUnixFileMode(blocked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        });

        Test("revalidates a pause-to-delete path swap", () =>
        {
            using var f = new Fixture();
            var external = Path.Combine(f.Root, "external");
            var victim = Path.Combine(f.Cache, "victim");
            Directory.CreateDirectory(external);
            File.WriteAllText(Path.Combine(external, "keep.bin"), "keep");
            Directory.CreateDirectory(victim);

            var policy = new SafeCacheDeletionPolicy();
            Check(policy.Authorize(f.Cache, victim, false).Allowed, "ordinary target was not initially allowed");
            RemovePath(victim);
            if (!TryCreateDirectoryLink(victim, external))
            {
                throw new SkipTest("directory link creation is unavailable");
            }

            try
            {
                var result = new SafeCacheDeletionExecutor().Delete(f.Cache, victim, false);
                Check(!result.Allowed, "swapped link was deleted");
                Check(File.Exists(Path.Combine(external, "keep.bin")), "external data was deleted after path swap");
            }
            finally
            {
                RemovePath(victim);
            }
        });

        Console.WriteLine($"RESULT Cache safety: {passed} passed, {failed} failed, {skipped} skipped");
        return failed == 0 ? 0 : 1;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c mklink /J \"" + link + "\" \"" + target + "\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                process.WaitForExit();
                return process.ExitCode == 0 && Directory.Exists(link);
            }

            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void RemovePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(path, false);
                else File.Delete(path);
            }
            else if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.Delete(path, true);
            }
            else
            {
                File.Delete(path);
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "pcls-cache-safety-" + Guid.NewGuid().ToString("N"));
        public readonly string Cache;

        public Fixture()
        {
            Cache = Path.Combine(Root, "cache");
            Directory.CreateDirectory(Cache);
        }

        public void Dispose()
        {
            var fullRoot = Path.GetFullPath(Root);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Check(fullRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase), "fixture cleanup escaped TMPDIR");
            RemovePath(fullRoot);
        }
    }
}
