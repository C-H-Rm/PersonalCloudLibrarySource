using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using PersonalCloudLibrarySource;

// Real copier source, disposable filesystem endpoints and explicitly labelled process fixtures.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length > 0 && (args[0] == "copy" || args[0] == "copyto")) return FixtureProcess(args);
        var dotnet = Environment.GetEnvironmentVariable("PCLS_DOTNET");
        if (!string.IsNullOrEmpty(dotnet)) Environment.SetEnvironmentVariable("DOTNET_ROOT", Path.GetDirectoryName(dotnet));
        int passed = 0, failed = 0;
        void Test(string name, Action<Fixture> action)
        {
            if (args.Length > 0 && !name.StartsWith(args[0], StringComparison.Ordinal)) return;
            using var f = new Fixture();
            try { action(f); Console.WriteLine("PASS " + name); passed++; }
            catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex.Message); failed++; }
        }
        Test("progressing child outlives connection timeout", f =>
        {
            f.UseProcessFixture();
            var result = f.Copier.CopyRemoteFileToLocalPath(f.Settings, "progress", f.Destination);
            Check(result.Succeeded && File.Exists(f.Destination), "active copy was killed: " + result.Message);
        });
        Test("silent child times out and is stopped", f =>
        {
            f.UseProcessFixture();
            var result = f.Copier.CopyRemoteFileToLocalPath(f.Settings, "silent", f.Destination);
            Check(!result.Succeeded && result.Message.Contains("activity"), "missing startup timeout: " + result.Message);
            CheckStopped(f.Destination);
        });
        Test("unchanged stats do not keep a stalled copy alive", f =>
        {
            f.UseProcessFixture();
            var result = f.Copier.CopyRemoteFileToLocalPath(f.Settings, "idle", f.Destination);
            Check(!result.Succeeded && result.Message.Contains("inactive"), "heartbeat masked inactivity: " + result.Message);
            CheckStopped(f.Destination);
        });
        Test("error messages do not count as transfer progress", f =>
        {
            f.UseProcessFixture();
            var result = f.Copier.CopyRemoteFileToLocalPath(f.Settings, "errors", f.Destination);
            Check(!result.Succeeded && result.Message.Contains("activity"), "error spam kept child alive: " + result.Message);
            CheckStopped(f.Destination);
        });
        Test("nonzero child exit is not success", f =>
        {
            f.UseProcessFixture();
            var result = f.Copier.CopyRemoteFileToLocalPath(f.Settings, "fail", f.Destination);
            Check(!result.Succeeded && result.Message.Contains("exit code 7"), "exit failure was lost");
        });
        Test("missing executable returns a failure", f =>
        {
            f.Settings.RcloneExecutablePath = Path.Combine(f.Root, "missing-rclone");
            Check(!f.Copier.CopyRemoteFileToLocalPath(f.Settings, "anything", f.Destination).Succeeded, "missing executable accepted");
        });
        Test("real rclone copies a file with spaces", f =>
        {
            var source = Path.Combine(f.Root, "source file.bin"); File.WriteAllText(source, "actual local content");
            var result = f.Copier.CopyRemoteFileToLocalPath(f.Settings, source, f.Destination);
            Check(result.Succeeded && File.ReadAllText(f.Destination) == "actual local content", result.Message);
        });
        Test("real rclone accepts an empty file", f =>
        {
            var source = Path.Combine(f.Root, "empty.bin"); File.WriteAllBytes(source, Array.Empty<byte>());
            var result = f.Copier.CopyRemoteFileToLocalPath(f.Settings, source, f.Destination);
            Check(result.Succeeded && new FileInfo(f.Destination).Length == 0, result.Message);
        });
        Test("real rclone copies nested directories", f =>
        {
            var source = Path.Combine(f.Root, "source folder"); Directory.CreateDirectory(Path.Combine(source, "nested"));
            File.WriteAllText(Path.Combine(source, "nested", "mod.bin"), "nested content");
            var result = f.Copier.CopyRemoteDirectoryToLocalPath(f.Settings, source, f.Destination);
            Check(result.Succeeded && File.ReadAllText(Path.Combine(f.Destination, "nested", "mod.bin")) == "nested content", result.Message);
        });
        Test("real rclone reports a missing source", f =>
        {
            var result = f.Copier.CopyRemoteFileToLocalPath(f.Settings, Path.Combine(f.Root, "does-not-exist"), f.Destination);
            Check(!result.Succeeded, "missing source accepted");
        });
        Test("real throttled transfer is not capped by total duration", f =>
        {
            var source = Path.Combine(f.Root, "slow.bin"); File.WriteAllBytes(source, new byte[1024 * 1024]);
            var previous = Environment.GetEnvironmentVariable("RCLONE_BWLIMIT");
            Environment.SetEnvironmentVariable("RCLONE_BWLIMIT", "128k");
            try
            {
                var timer = Stopwatch.StartNew();
                var result = f.Copier.CopyRemoteFileToLocalPath(f.Settings, source, f.Destination);
                Check(result.Succeeded && timer.Elapsed.TotalSeconds > 5 && new FileInfo(f.Destination).Length == 1024 * 1024,
                    "slow transfer failed or did not exercise old limit: " + result.Message);
            }
            finally { Environment.SetEnvironmentVariable("RCLONE_BWLIMIT", previous); }
        });
        Test("watchdog follows real progress rather than total elapsed time", f =>
        {
            var clock = new RcloneActivityTimeout(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
            clock.ObserveLine("{\"stats\":{\"bytes\":1}}", TimeSpan.FromSeconds(4));
            clock.ObserveLine("{\"stats\":{\"bytes\":2}}", TimeSpan.FromSeconds(12));
            clock.ObserveLine("{\"stats\":{\"bytes\":3}}", TimeSpan.FromSeconds(20));
            Check(clock.GetExpiredKind(TimeSpan.FromSeconds(29)) == RcloneTimeoutKind.None, "active transfer expired");
            Check(clock.GetExpiredKind(TimeSpan.FromSeconds(30)) == RcloneTimeoutKind.Inactivity, "inactivity boundary wrong");
        });
        Test("watchdog ignores changing nested per-file counters and error text", f =>
        {
            var clock = new RcloneActivityTimeout(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
            clock.ObserveLine("{\"stats\":{\"bytes\":1}}", TimeSpan.Zero);
            clock.ObserveLine("{\"stats\":{\"bytes\":1,\"transferring\":[{\"bytes\":9999}]}}", TimeSpan.FromSeconds(9));
            clock.ObserveLine("{\"msg\":\"arbitrary status\",\"level\":\"error\"}", TimeSpan.FromSeconds(9));
            Check(clock.GetExpiredKind(TimeSpan.FromSeconds(10)) == RcloneTimeoutKind.Inactivity, "non-progress prolonged watchdog");
        });
        Test("watchdog recognizes checks and progress after retry counter resets", f =>
        {
            var clock = new RcloneActivityTimeout(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
            clock.ObserveLine("{\"stats\":{\"bytes\":100}}", TimeSpan.Zero);
            clock.ObserveLine("{\"stats\":{\"bytes\":0}}", TimeSpan.FromSeconds(4));
            clock.ObserveLine("{\"stats\":{\"bytes\":1}}", TimeSpan.FromSeconds(8));
            clock.ObserveLine("{\"stats\":{\"bytes\":1,\"checks\":1}}", TimeSpan.FromSeconds(16));
            Check(clock.GetExpiredKind(TimeSpan.FromSeconds(25)) == RcloneTimeoutKind.None, "retry/check progress was ignored");
        });
        Test("invalid or oversized stats cannot crash or refresh the watchdog", f =>
        {
            var clock = new RcloneActivityTimeout(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
            foreach (var line in new[] { "not json", "{\"stats\":null}", "{\"stats\":\"wrong type\"}", "{\"stats\":{\"bytes\":9223372036854775808}}", new string('x', 70000) })
                clock.ObserveLine(line, TimeSpan.FromSeconds(4));
            Check(clock.GetExpiredKind(TimeSpan.FromSeconds(5)) == RcloneTimeoutKind.Connect, "invalid line prolonged startup");
        });
        Console.WriteLine($"RESULT Rclone transfers: {passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static int FixtureProcess(string[] args)
    {
        string mode = args[1].Substring(args[1].IndexOf(':') + 1), destination = args[2];
        File.WriteAllText(destination + ".pid", Environment.ProcessId.ToString());
        if (mode == "fail") { Console.Error.WriteLine("intentional fixture failure"); return 7; }
        for (int i = 0; i < (mode == "progress" ? 28 : 120); i++)
        {
            if (mode == "progress" || mode == "idle") Console.Error.WriteLine("{\"stats\":{\"bytes\":" + (mode == "idle" ? 0 : i) + ",\"checks\":0,\"transfers\":0}}");
            if (mode == "errors") Console.Error.WriteLine("{\"level\":\"error\",\"msg\":\"intentional fixture error\"}");
            Thread.Sleep(250);
        }
        File.WriteAllText(destination, "completed fixture");
        return 0;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void CheckStopped(string destination)
    {
        int pid = int.Parse(File.ReadAllText(destination + ".pid"));
        try { using var p = Process.GetProcessById(pid); Check(p.HasExited, "timed-out child still running"); }
        catch (ArgumentException) { }
    }
    private sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "pcls-copy-test-" + Guid.NewGuid().ToString("N"));
        public string Destination => Path.Combine(Root, "destination with spaces");
        public readonly RcloneFileCopier Copier = new RcloneFileCopier();
        public readonly PersonalCloudLibrarySourceSettings Settings = new PersonalCloudLibrarySourceSettings();
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            var config = Path.Combine(Root, "rclone.conf"); File.WriteAllText(config, "[fixture]\ntype = local\n");
            Environment.SetEnvironmentVariable("RCLONE_CONFIG", config);
        }
        public void UseProcessFixture()
        {
            var assembly = Assembly.GetExecutingAssembly().Location;
            Settings.RcloneExecutablePath = Path.ChangeExtension(assembly, OperatingSystem.IsWindows() ? ".exe" : null);
        }
        public void Dispose() { Directory.Delete(Root, true); }
    }
}

namespace PersonalCloudLibrarySource
{
    // No Playnite UI in this harness; copy paths and subprocess behavior are real.
    public class PersonalCloudLibrarySourceSettings
    {
        public string RcloneExecutablePath = Environment.GetEnvironmentVariable("PCLS_RCLONE") ?? "rclone";
        public string RcloneRemoteName = "fixture", RcloneManifestPath = "manifest.json";
        public int RcloneTimeoutSeconds = 5, RcloneTransferInactivityTimeoutSeconds = 5;
    }
}
