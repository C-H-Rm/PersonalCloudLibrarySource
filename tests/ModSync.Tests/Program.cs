using System;
using System.IO;
using System.Linq;
using PersonalCloudLibrarySource;

internal static class Program
{
    private static int passed, failed;
    private static void Main()
    {
        Test("initial merge preserves unique mods on both sides", f => {
            f.Local("local.txt", "local"); f.Cloud("cloud.txt", "cloud"); f.Sync();
            Check(File.Exists(f.Remote("local.txt")) && File.Exists(f.Path("cloud.txt")), "merge missing files");
        });
        Test("local addition uploads", f => {
            f.Local("keep", "keep"); f.Sync(); f.Local("new.txt", "new"); f.Sync();
            Check(File.ReadAllText(f.Remote("new.txt")) == "new", "upload missing");
        });
        Test("cloud addition downloads", f => {
            f.Local("keep", "keep"); f.Sync(); f.Cloud("new.txt", "new"); f.Sync();
            Check(File.ReadAllText(f.Path("new.txt")) == "new", "download missing");
        });
        Test("local update uploads", f => {
            f.Local("a.txt", "old"); f.Sync(); f.Local("a.txt", "new-longer"); f.Sync();
            Check(File.ReadAllText(f.Remote("a.txt")) == "new-longer", "update missing");
        });
        Test("cloud update downloads", f => {
            f.Local("a.txt", "old"); f.Sync(); f.Cloud("a.txt", "remote-new-longer"); f.Sync();
            Check(File.ReadAllText(f.Path("a.txt")) == "remote-new-longer", "update missing");
        });
        Test("local deletion removes cloud file", f => {
            f.Local("a", "a"); f.Local("b", "b"); f.Local("c", "c"); f.Sync();
            File.Delete(f.Path("a")); f.Sync(); Check(!File.Exists(f.Remote("a")), "deleted mod resurrected");
        });
        Test("cloud deletion removes local file", f => {
            f.Local("a", "a"); f.Local("b", "b"); f.Local("c", "c"); f.Sync();
            File.Delete(f.Remote("a")); f.Sync(); Check(!File.Exists(f.Path("a")), "deleted mod resurrected");
        });
        Test("clearing existing folder asks and propagates", f => {
            f.Local("a", "a"); f.Sync(); File.Delete(f.Path("a"));
            var asked = false;
            var denied = f.Service.Synchronize(f.Request, message => { asked = true; return false; });
            Check(asked && !denied.Success && File.Exists(f.Remote("a")), "empty clear did not pause");
            f.Sync(); Check(!File.Exists(f.Remote("a")), "empty clear not propagated");
        });
        Test("remote empty-clear asks before local removal", f => {
            f.Local("a", "a"); f.Sync(); File.Delete(f.Remote("a"));
            var asked = false;
            var denied = f.Service.Synchronize(f.Request, message => { asked = true; return false; });
            Check(asked && !denied.Success && File.Exists(f.Path("a")), "remote clear did not pause");
            f.Sync(); Check(!File.Exists(f.Path("a")), "remote clear not propagated");
        });
        Test("missing local folder does not erase cloud", f => {
            f.Local("a", "a"); f.Sync(); Directory.Move(f.LocalRoot, f.LocalRoot + "-uninstalled");
            Check(!f.Service.Synchronize(f.Request, _ => true).Success, "missing folder accepted");
            Check(File.Exists(f.Remote("a")), "cloud erased");
        });
        Test("recreated local folder without marker does not erase cloud", f => {
            f.Local("a", "a"); f.Sync(); Directory.Move(f.LocalRoot, f.LocalRoot + "-old"); Directory.CreateDirectory(f.LocalRoot);
            Check(!f.Service.Synchronize(f.Request, _ => true).Success && File.Exists(f.Remote("a")), "missing marker accepted");
        });
        Test("missing launch file refuses synchronization", f => {
            f.Local("a", "a"); f.Sync(); File.Delete(f.Request.LaunchFile); File.Delete(f.Path("a"));
            Check(!f.Service.Synchronize(f.Request, _ => true).Success && File.Exists(f.Remote("a")), "uninstalled game erased cloud");
        });
        Test("missing cloud access marker stops synchronization", f => {
            f.Local("a", "a"); f.Sync();
            foreach (var p in Directory.GetFiles(f.RemoteRoot, ".pcls-*")) File.Delete(p);
            f.Local("new", "new");
            Check(!f.Service.Synchronize(f.Request, _ => true).Success && !File.Exists(f.Remote("new")), "missing remote marker accepted");
        });
        Test("initial conflicting file is refused without overwrite", f => {
            f.Local("same", "local-value"); f.Cloud("same", "remote-value");
            var result = f.Service.Synchronize(f.Request, _ => true);
            Check(!result.Success && File.ReadAllText(f.Path("same")) == "local-value" && File.ReadAllText(f.Remote("same")) == "remote-value", "initial conflict overwrote data");
        });
        Test("normal conflict keeps both variants and reports it", f => {
            f.Local("same", "original"); f.Sync(); f.Local("same", "local-variant"); f.Cloud("same", "remote-variant-long");
            var result = f.Sync();
            var texts = Directory.GetFiles(f.LocalRoot).Select(File.ReadAllText).ToArray();
            Check(result.HasConflicts && texts.Contains("local-variant") && texts.Contains("remote-variant-long"), "conflict data lost or unreported");
        });
        Test("changed mapping is refused rather than silently reinitialized", f => {
            f.Local("a", "a"); f.Sync(); f.Request.RemoteFolder += "-different";
            Check(!f.Service.Synchronize(f.Request, _ => true).Success && !Directory.Exists(f.RemoteRoot + "-different"), "mapping changed silently");
        });
        Test("initial linkage requires confirmation", f => {
            f.Local("a", "a");
            Check(!f.Service.Synchronize(f.Request, _ => false).Success && !File.Exists(f.Remote("a")), "declined initial link wrote data");
        });
        Test("sync state may not live inside mod folder", f => {
            f.Request.StateDirectory = System.IO.Path.Combine(f.LocalRoot, "state");
            Check(!f.Service.Synchronize(f.Request, _ => true).Success, "overlapping state allowed");
        });
        Test("local mod folder may not include game launch file", f => {
            f.Request.LocalFolder = System.IO.Path.GetDirectoryName(f.Request.LaunchFile);
            Check(!f.Service.Synchronize(f.Request, _ => true).Success, "base game directory allowed");
        });
        Test("empty initial mod folders can be linked", f => { f.Sync(); });
        Test("empty-side synchronization conservatively asks even for a new addition", f => {
            f.Sync(); f.Local("first", "first");
            var asked = false;
            var result = f.Service.Synchronize(f.Request, _ => { asked = true; return false; });
            Check(asked && !result.Success && !File.Exists(f.Remote("first")), "empty-side guard bypassed");
            f.Sync(); Check(File.Exists(f.Remote("first")), "approved addition missing");
        });
        Test("stale persisted count cannot bypass whole-clear confirmation", f => {
            f.Sync(); f.Local("a", "a"); f.Sync();
            File.WriteAllText(System.IO.Path.Combine(f.Request.StateDirectory, "last-file-count"), "0");
            File.Delete(f.Path("a")); var asked = false;
            var result = f.Service.Synchronize(f.Request, _ => { asked = true; return false; });
            Check(asked && !result.Success && File.Exists(f.Remote("a")), "stale count bypassed confirmation");
        });
        Test("same-size same-time local edits are detected by checksum", f => {
            f.Local("mod", "aaaa"); f.Sync(); var stamp = File.GetLastWriteTimeUtc(f.Path("mod"));
            File.WriteAllText(f.Path("mod"), "bbbb"); File.SetLastWriteTimeUtc(f.Path("mod"), stamp); f.Sync();
            Check(File.ReadAllText(f.Remote("mod")) == "bbbb", "same-metadata local edit missed");
        });
        Test("same-size same-time cloud edits are detected by checksum", f => {
            f.Local("mod", "aaaa"); f.Sync(); var stamp = File.GetLastWriteTimeUtc(f.Remote("mod"));
            File.WriteAllText(f.Remote("mod"), "bbbb"); File.SetLastWriteTimeUtc(f.Remote("mod"), stamp); f.Sync();
            Check(File.ReadAllText(f.Path("mod")) == "bbbb", "same-metadata cloud edit missed");
        });
        Test("fresh mapping lookup failure pauses before sync", f => {
            f.Local("a", "a"); var blocked = false;
            try { ModSyncService.RequireCurrentMapping(f.Request, () => throw new IOException("manifest offline")); }
            catch (IOException) { blocked = true; }
            Check(blocked && !File.Exists(f.Remote("a")), "offline manifest allowed stale sync");
        });
        Test("changed live mod mapping is refused", f => {
            var changed = new ModSyncRequest { GameId = f.Request.GameId, LaunchFile = f.Request.LaunchFile,
                LocalFolder = f.LocalRoot + "-new", RemoteFolder = f.Request.RemoteFolder };
            var blocked = false;
            try { ModSyncService.RequireCurrentMapping(f.Request, () => changed); }
            catch (InvalidOperationException) { blocked = true; }
            Check(blocked, "changed live mapping accepted");
        });
        Test("unchanged freshly loaded mapping is accepted", f => {
            Check(ModSyncService.RequireCurrentMapping(f.Request, () => f.Request) == f.Request, "fresh mapping refused");
        });
        Test("pause after uninstall prevents sync even if mod folder still exists", f => {
            f.Local("a", "a"); f.Sync(); f.Service.Pause(f.Request.StateDirectory); File.Delete(f.Path("a"));
            Check(!f.Service.Synchronize(f.Request, _ => true).Success && File.Exists(f.Remote("a")), "paused sync erased cloud");
        });
        Test("explicit relink restores cloud mods after reinstall", f => {
            f.Local("a", "a"); f.Sync(); f.Service.Pause(f.Request.StateDirectory);
            Directory.Move(f.LocalRoot, f.LocalRoot + "-old");
            var result = f.Service.Relink(f.Request, _ => true);
            Check(result.Success && File.ReadAllText(f.Path("a")) == "a" && File.Exists(f.Remote("a")), result.Message);
            Check(Directory.GetDirectories(f.Root, "state.archive-*").Length == 1, "old state not archived");
        });
        Test("declined relink retains paused state", f => {
            f.Local("a", "a"); f.Sync(); f.Service.Pause(f.Request.StateDirectory);
            Check(!f.Service.Relink(f.Request, _ => false).Success && File.Exists(System.IO.Path.Combine(f.Request.StateDirectory, "paused")), "declined reset changed state");
        });
        Test("interrupted initial baseline is not automatically resynced", f => {
            Directory.CreateDirectory(f.Request.StateDirectory);
            File.WriteAllText(System.IO.Path.Combine(f.Request.StateDirectory, "pair.txt"), f.Request.LocalFolder + "\n" + f.Request.RemoteFolder);
            f.Local("a", "a");
            Check(!f.Service.Synchronize(f.Request, _ => true).Success && !File.Exists(f.Remote("a")), "pending initial state accepted");
        });
        Test("concurrent sync operation is refused", f => {
            f.Local("a", "a"); f.Sync();
            using (new FileStream(f.Request.StateDirectory + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(!f.Service.Synchronize(f.Request, _ => true).Success, "second operation accepted");
        });
        Test("sequential PCs propagate additions and deletions without resurrection", f => {
            f.Local("a", "a"); f.Local("b", "b"); f.Local("c", "c"); f.Sync();
            using (var other = new Fixture())
            {
                other.Request.RemoteFolder = f.Request.RemoteFolder;
                other.Sync(); Check(File.Exists(other.Path("a")), "second PC did not download");
                File.Delete(f.Path("a")); f.Sync(); other.Sync();
                Check(!File.Exists(other.Path("a")), "second PC resurrected deleted mod");
                other.Local("pc-two", "from pc two"); other.Sync(); f.Sync();
                Check(File.ReadAllText(f.Path("pc-two")) == "from pc two", "second PC addition not propagated");
            }
        });
        if (Environment.OSVersion.Platform == PlatformID.Unix)
        Test("symlinks in a mod folder are refused", f => {
            f.Local("a", "a");
            File.CreateSymbolicLink(f.Path("link"), f.Request.LaunchFile);
            Check(!f.Service.Synchronize(f.Request, _ => true).Success && !File.Exists(f.Remote("a")), "link followed");
        });
        Console.WriteLine($"RESULT: {passed} passed, {failed} failed");
        Environment.ExitCode = failed == 0 ? 0 : 1;
    }
    private static void Test(string name, Action<Fixture> test)
    {
        try { using (var f = new Fixture()) test(f); Console.WriteLine("PASS " + name); passed++; }
        catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex.Message); failed++; }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Fixture : IDisposable
    {
        public readonly string Root, LocalRoot, RemoteRoot;
        public readonly ModSyncService Service = new ModSyncService();
        public readonly ModSyncRequest Request;
        private int tick;
        public Fixture()
        {
            Root = System.IO.Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? System.IO.Path.GetTempPath(), "pcls-test-" + Guid.NewGuid().ToString("N"));
            LocalRoot = System.IO.Path.Combine(Root, "game", "Mods"); RemoteRoot = System.IO.Path.Combine(Root, "cloud", "games", "mods", "Game Name");
            Directory.CreateDirectory(LocalRoot); Directory.CreateDirectory(RemoteRoot);
            var launch = System.IO.Path.Combine(Root, "game", "game.exe"); File.WriteAllText(launch, "test fixture only");
            Request = new ModSyncRequest { GameId = "fixture-game", LocalFolder = LocalRoot, RemoteFolder = ":local:" + RemoteRoot,
                LaunchFile = launch, StateDirectory = System.IO.Path.Combine(Root, "state"),
                RcloneExecutable = Environment.GetEnvironmentVariable("PCLS_RCLONE") ?? "rclone", TimeoutSeconds = 60 };
        }
        public string Path(string name) => System.IO.Path.Combine(LocalRoot, name);
        public string Remote(string name) => System.IO.Path.Combine(RemoteRoot, name);
        public void Local(string name, string content) => Write(Path(name), content);
        public void Cloud(string name, string content) => Write(Remote(name), content);
        private void Write(string p, string content)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)); File.WriteAllText(p, content);
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(++tick));
        }
        public ModSyncResult Sync()
        {
            var result = Service.Synchronize(Request, _ => true);
            Check(result.Success, result.Message); return result;
        }
        public void Dispose() { /* Preserve isolated fixtures for inspecting failures. */ }
    }
}
