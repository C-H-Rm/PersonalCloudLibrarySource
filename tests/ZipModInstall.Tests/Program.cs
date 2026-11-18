using PersonalCloudLibrarySource;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

internal static class Program
{
    private static int passed, failed;
    private static string root;
    private static void Main()
    {
        root = Path.Combine(Path.GetTempPath(), "pcls-zip-mod-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Test("archive root maps directly to modPath with nested and empty files", ValidArchive);
            Test("no overwrite when modPath already contains data", ExistingFilesPreserved);
            Test("existing empty modPath accepted", ExistingEmptyPath);
            Test("path traversal rejected without outside write", TraversalRejected);
            Test("absolute, backslash, drive and Windows device paths refused", UnsafePathsRejected);
            Test("case-insensitive collisions refused", CaseCollisionRejected);
            Test("duplicate entries refused", DuplicateRejected);
            Test("file/directory collisions and symlink entries refused", EntryTypeGuards);
            Test("destination and its parents are never followed through links", LinkedDestinationRejected);
            Test("entry and aggregate expansion limits refuse publication", ExpansionLimits);
            Test("corrupt ZIP refused without partial install", CorruptArchiveRejected);
            Test("cancelled extraction leaves destination and staging clean", CancellationRejected);
            Test("real rclone local-backend download then install", RealRcloneDownload);
            Console.WriteLine($"ZipModInstall tests: {passed} passed, {failed} failed");
            if (failed != 0) Environment.ExitCode = 1;
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
    private static void Test(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
    }
    private static void ValidArchive()
    {
        var archive = MakeZip(("mod.dll", "dll-bytes"), ("nested/config.ini", "settings"));
        var target = Path.Combine(root, "valid-mod");
        InstallZipModArchive.Install(archive, target, CancellationToken.None);
        Equal("dll-bytes", File.ReadAllText(Path.Combine(target, "mod.dll")));
        Equal("settings", File.ReadAllText(Path.Combine(target, "nested", "config.ini")));
        if (File.Exists(Path.Combine(target, "mods", "mod.dll"))) throw new Exception("archive root wasn't mapped directly");
    }
    private static void ExistingFilesPreserved()
    {
        var target = Path.Combine(root, "nonempty"); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "user.txt"), "user-data");
        Throws<IOException>(() => InstallZipModArchive.Install(MakeZip(("new.txt", "new")), target, CancellationToken.None));
        Equal("user-data", File.ReadAllText(Path.Combine(target, "user.txt")));
        if (File.Exists(Path.Combine(target, "new.txt"))) throw new Exception("partial file installed");
    }
    private static void ExistingEmptyPath()
    {
        var target = Path.Combine(root, "empty"); Directory.CreateDirectory(target);
        InstallZipModArchive.Install(MakeZip(("file.dat", "ok")), target, CancellationToken.None);
        Equal("ok", File.ReadAllText(Path.Combine(target, "file.dat")));
    }
    private static void TraversalRejected()
    {
        var outside = Path.Combine(root, "escape.txt");
        Throws<InvalidDataException>(() => InstallZipModArchive.Install(MakeZip(("../escape.txt", "bad")), Path.Combine(root, "traversal"), CancellationToken.None));
        if (File.Exists(outside)) throw new Exception("traversal wrote outside target");
        NoStage();
    }
    private static void UnsafePathsRejected()
    {
        foreach (var name in new[] { "/rooted.txt", "C:/drive.txt", "folder\\escape.txt", "CON.txt", "dir/../bad.txt" })
            Throws<InvalidDataException>(() => InstallZipModArchive.Install(MakeZip((name, "x")), Path.Combine(root, "unsafe-" + Guid.NewGuid().ToString("N")), CancellationToken.None));
        NoStage();
    }
    private static void CaseCollisionRejected()
    {
        Throws<InvalidDataException>(() => InstallZipModArchive.Install(MakeZip(("A/file", "1"), ("a/other", "2")), Path.Combine(root, "collision"), CancellationToken.None));
        NoStage();
    }
    private static void DuplicateRejected()
    {
        var archive = Path.Combine(root, "duplicate.zip");
        using (var z = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("same.txt").Open())) w.Write("one");
            using (var w = new StreamWriter(z.CreateEntry("same.txt").Open())) w.Write("two");
        }
        Throws<InvalidDataException>(() => InstallZipModArchive.Install(archive, Path.Combine(root, "duplicate"), CancellationToken.None));
        NoStage();
    }
    private static void EntryTypeGuards()
    {
        Throws<InvalidDataException>(() => InstallZipModArchive.Install(MakeZip(("node", "file"), ("node/child.txt", "child")), Path.Combine(root, "file-dir-collision"), CancellationToken.None));
        var symlink = Path.Combine(root, "symlink.zip");
        using (var z = ZipFile.Open(symlink, ZipArchiveMode.Create))
        {
            var entry = z.CreateEntry("link"); entry.ExternalAttributes = unchecked((int)0xA0000000u);
            using (var writer = new StreamWriter(entry.Open())) writer.Write("target");
        }
        Throws<InvalidDataException>(() => InstallZipModArchive.Install(symlink, Path.Combine(root, "symlink-target"), CancellationToken.None));
        if (Directory.Exists(Path.Combine(root, "file-dir-collision")) || Directory.Exists(Path.Combine(root, "symlink-target")))
            throw new Exception("unsafe ZIP committed a destination");
        NoStage();
    }
    private static void LinkedDestinationRejected()
    {
        if (!OperatingSystem.IsLinux()) return; // Windows CI need not have symlink privileges.
        var outside = Directory.CreateDirectory(Path.Combine(root, "outside-mod-root")).FullName;
        var link = Path.Combine(root, "linked-mod-root");
        Directory.CreateSymbolicLink(link, outside);
        var archive = MakeZip(("file.dat", "would escape"));
        Throws<IOException>(() => InstallZipModArchive.Install(archive, link, CancellationToken.None));
        Throws<IOException>(() => InstallZipModArchive.Install(archive, Path.Combine(link, "nested", "Mods"), CancellationToken.None));
        if (Directory.EnumerateFileSystemEntries(outside).Any()) throw new Exception("archive touched data through a directory link");
    }
    private static void ExpansionLimits()
    {
        var archive = MakeZip(("a.txt", "1234"), ("b.txt", "5678"));
        var target = Directory.CreateDirectory(Path.Combine(root, "budgeted-target")).FullName;
        Throws<InvalidDataException>(() => InstallZipModArchive.Install(archive, target, CancellationToken.None, 1, 8));
        Throws<InvalidDataException>(() => InstallZipModArchive.Install(archive, target, CancellationToken.None, 2, 7));
        if (Directory.EnumerateFileSystemEntries(target).Any()) throw new Exception("failed limit check changed destination");
        NoStage();
        InstallZipModArchive.Install(archive, target, CancellationToken.None, 2, 8);
        Equal("1234", File.ReadAllText(Path.Combine(target, "a.txt")));
        Equal("5678", File.ReadAllText(Path.Combine(target, "b.txt")));
    }
    private static void CorruptArchiveRejected()
    {
        var archive = Path.Combine(root, "corrupt.zip"); File.WriteAllText(archive, "not a zip");
        Throws<InvalidDataException>(() => InstallZipModArchive.Install(archive, Path.Combine(root, "corrupt"), CancellationToken.None));
        if (Directory.Exists(Path.Combine(root, "corrupt"))) throw new Exception("corrupt archive created target");
        NoStage();
    }
    private static void CancellationRejected()
    {
        var archive = MakeZip(("a.txt", "bytes")); var target = Path.Combine(root, "cancel");
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel(); Throws<OperationCanceledException>(() => InstallZipModArchive.Install(archive, target, cts.Token));
        }
        if (Directory.Exists(target)) throw new Exception("cancelled install committed");
        NoStage();
    }
    private static void RealRcloneDownload()
    {
        var remoteRoot = Path.Combine(root, "rclone-local-remote"); Directory.CreateDirectory(Path.Combine(remoteRoot, "mods"));
        var sourceZip = MakeZip(("root-file.dat", "from-rclone"), ("sub/item.bin", "nested-rclone"));
        File.Copy(sourceZip, Path.Combine(remoteRoot, "mods", "game-id.zip"));
        var downloaded = Path.Combine(root, "rclone-downloaded.zip");
        var executable = Environment.GetEnvironmentVariable("PCLS_RCLONE") ?? "rclone";
        Run(executable, "copyto " + Q(Path.Combine(remoteRoot, "mods", "game-id.zip")) + " " + Q(downloaded) + " --log-level ERROR");
        var target = Path.Combine(root, "actual-modPath");
        InstallZipModArchive.Install(downloaded, target, CancellationToken.None);
        Equal("from-rclone", File.ReadAllText(Path.Combine(target, "root-file.dat")));
        Equal("nested-rclone", File.ReadAllText(Path.Combine(target, "sub", "item.bin")));
    }
    private static string MakeZip(params (string name, string value)[] files)
    {
        var path = Path.Combine(root, "fixture-" + Guid.NewGuid().ToString("N") + ".zip");
        using (var z = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var f in files) using (var w = new StreamWriter(z.CreateEntry(f.name).Open())) w.Write(f.value);
        return path;
    }
    private static void NoStage()
    {
        if (Directory.GetDirectories(root, ".pcls-modzip-stage-*", SearchOption.TopDirectoryOnly).Any() ||
            Directory.GetDirectories(root, ".pcls-modzip-empty-*", SearchOption.TopDirectoryOnly).Any())
            throw new Exception("temporary extraction stage leaked");
    }
    private static void Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo { FileName = exe, Arguments = args, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
        var stdout = p.StandardOutput.ReadToEnd(); var stderr = p.StandardError.ReadToEnd(); p.WaitForExit();
        if (p.ExitCode != 0) throw new Exception("rclone exit=" + p.ExitCode + " stdout=" + stdout + " stderr=" + stderr);
    }
    private static string Q(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static void Equal(string expected, string actual) { if (expected != actual) throw new Exception("Expected " + expected + ", got " + actual); }
}
