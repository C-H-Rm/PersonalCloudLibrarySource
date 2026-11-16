using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
        Test("progress policy freezes whole-transfer denominator after inventory", f =>
        {
            var tracker = new RcloneTransferProgressTracker();
            Check(tracker.Progress.Phase == "Inventory" && tracker.Progress.TotalBytes == null, "inventory was reported as sized");
            tracker.ObserveStats("{\"stats\":{\"bytes\":0,\"speed\":0,\"totalBytes\":100,\"transfers\":0,\"totalTransfers\":0}}", 0);
            Check(tracker.Progress.TotalBytes == null, "inventory total leaked before copy started");
            tracker.ObserveStats("{\"stats\":{\"bytes\":10,\"speed\":4,\"totalBytes\":100,\"transfers\":1,\"totalTransfers\":2}}", 1);
            Check(tracker.Progress.Phase == "Downloading" && tracker.Progress.TotalBytes == 100 && tracker.Progress.Percentage == 10, "transfer start did not freeze denominator");
            tracker.ObserveStats("{\"stats\":{\"bytes\":20,\"speed\":8,\"totalBytes\":200,\"transfers\":1,\"totalTransfers\":3}}", 2);
            Check(tracker.Progress.TotalBytes == 100 && tracker.Progress.CompletedBytes == 20 && tracker.Progress.Percentage == 20, "denominator changed as more files were discovered");
        });
        Test("progress parser ignores malformed stats and bounds percentages", f =>
        {
            var tracker = new RcloneTransferProgressTracker();
            tracker.ObserveStats("not json", 1);
            tracker.ObserveStats("{\"stats\":{\"bytes\":5,\"speed\":12,\"totalBytes\":4,\"transfers\":1}}", 2);
            Check(tracker.Progress.TotalBytes == 4 && tracker.Progress.CompletedBytes == 4 && tracker.Progress.Percentage == null && tracker.Progress.Phase == "Finishing", "unconfirmed byte completion was falsely displayed as successful 100%");
        });
        Test("retry counter resets retain total but do not fabricate overall progress", f =>
        {
            var tracker = new RcloneTransferProgressTracker();
            tracker.ObserveStats("{\"stats\":{\"bytes\":90,\"speed\":10,\"totalBytes\":100,\"transfers\":1}}", 1);
            tracker.ObserveStats("{\"stats\":{\"bytes\":5,\"speed\":5,\"totalBytes\":100,\"transfers\":0}}", 2);
            Check(tracker.Progress.TotalBytes == 100 && tracker.Progress.Phase == "Retrying" && tracker.Progress.Percentage == null,
                "retry reset was hidden behind a misleading high-water percentage");
        });
        Test("progress terminal states only complete successfully", f =>
        {
            var tracker = new RcloneTransferProgressTracker();
            tracker.ObserveStats("{\"stats\":{\"bytes\":4,\"speed\":2,\"totalBytes\":10,\"transfers\":1}}", 1);
            tracker.Finish("Failed");
            Check(tracker.Progress.Phase == "Failed" && tracker.Progress.Percentage == null, "failure appeared complete");
            tracker = new RcloneTransferProgressTracker();
            tracker.Finish("Complete");
            Check(tracker.Progress.Phase == "Complete" && tracker.Progress.Percentage == 100, "success was not complete");
        });
        Test("cancellation stops the child and reports cancelled", f =>
        {
            f.UseProcessFixture();
            using var source = new CancellationTokenSource();
            var progress = new ManualResetEventSlim(false);
            var result = Task.Run(() => f.Copier.CopyRemoteFileToLocalPath(f.Settings, "idle", f.Destination,
                source.Token, _ => progress.Set()));
            Check(progress.Wait(TimeSpan.FromSeconds(5)), "no inventory/progress callback before cancellation");
            source.Cancel();
            Check(result.Wait(TimeSpan.FromSeconds(8)), "cancelled process did not terminate");
            Check(result.Result.Cancelled && !result.Result.Succeeded, "cancellation was reported as generic failure/success");
            CheckStopped(f.Destination);
        });
        Test("progress callback exceptions cannot leak child", f =>
        {
            f.UseProcessFixture();
            var result = f.Copier.CopyRemoteFileToLocalPath(f.Settings, "progress", f.Destination,
                CancellationToken.None, p => { if (p.Phase == "Downloading") throw new InvalidOperationException("consumer failed"); });
            Check(!result.Succeeded && !result.Cancelled && result.Exception != null, "callback failure was not surfaced as failure");
            CheckStopped(f.Destination);
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
        Test("real directory total includes every required file but not skipped cache", f =>
        {
            var source = Path.Combine(f.Root, "whole-source");
            var target = Path.Combine(f.Root, "whole-target");
            Directory.CreateDirectory(source); Directory.CreateDirectory(target);
            var first = Path.Combine(source, "first.bin");
            var second = Path.Combine(source, "second.bin");
            File.WriteAllBytes(first, new byte[128 * 1024]);
            File.WriteAllBytes(second, new byte[256 * 1024]);
            var cached = Path.Combine(source, "already-cached.bin");
            File.WriteAllBytes(cached, new byte[64 * 1024]);
            File.Copy(cached, Path.Combine(target, "already-cached.bin"));
            File.SetLastWriteTimeUtc(Path.Combine(target, "already-cached.bin"), File.GetLastWriteTimeUtc(cached));
            var expected = new FileInfo(first).Length + new FileInfo(second).Length;
            var previous = Environment.GetEnvironmentVariable("RCLONE_BWLIMIT");
            Environment.SetEnvironmentVariable("RCLONE_BWLIMIT", "128k");
            try {
                var samples = new System.Collections.Generic.List<RcloneTransferProgress>();
                var result = f.Copier.CopyRemoteDirectoryToLocalPath(f.Settings, source, target,
                    CancellationToken.None, p => samples.Add(p));
                Check(result.Succeeded, result.Message);
                Check(samples.Exists(p => p.Phase == "Downloading"), "no actual directory transfer progress");
                Check(samples.FindAll(p => p.TotalBytes.HasValue).TrueForAll(p => p.TotalBytes == expected),
                    "whole-directory total was partial, changing, or included skipped cached files");
                Check(File.ReadAllBytes(Path.Combine(target, "first.bin")).Length == new FileInfo(first).Length &&
                    File.ReadAllBytes(Path.Combine(target, "second.bin")).Length == new FileInfo(second).Length,
                    "required files were not copied");
            } finally { Environment.SetEnvironmentVariable("RCLONE_BWLIMIT", previous); }
        });
        Test("real rclone reports a missing source", f =>
        {
            var phases = new System.Collections.Generic.List<RcloneTransferProgress>();
            var result = f.Copier.CopyRemoteFileToLocalPath(f.Settings, Path.Combine(f.Root, "does-not-exist"), f.Destination,
                CancellationToken.None, p => phases.Add(p));
            Check(!result.Succeeded, "missing source accepted");
            Check(phases[phases.Count - 1].Phase == "Failed" && phases.TrueForAll(p => p.Phase != "Complete"), "failed real transfer appeared complete");
        });
        Test("real throttled transfer is not capped by total duration", f =>
        {
            var source = Path.Combine(f.Root, "slow.bin"); File.WriteAllBytes(source, new byte[1024 * 1024]);
            var previous = Environment.GetEnvironmentVariable("RCLONE_BWLIMIT");
            Environment.SetEnvironmentVariable("RCLONE_BWLIMIT", "128k");
            try
            {
                var samples = new System.Collections.Generic.List<RcloneTransferProgress>();
                var timer = Stopwatch.StartNew();
                var result = f.Copier.CopyRemoteFileToLocalPath(f.Settings, source, f.Destination, CancellationToken.None, p => samples.Add(p));
                Check(result.Succeeded && timer.Elapsed.TotalSeconds > 5 && new FileInfo(f.Destination).Length == 1024 * 1024,
                    "slow transfer failed or did not exercise old limit: " + result.Message);
                var downloading = samples.FindAll(p => p.Phase == "Downloading");
                Check(downloading.Count > 0 && downloading[0].TotalBytes == 1024 * 1024,
                    "real rclone did not report a frozen transfer total");
                Check(samples[samples.Count - 1].Phase == "Complete" && samples[samples.Count - 1].Percentage == 100,
                    "successful transfer did not finish at 100%");
            }
            finally { Environment.SetEnvironmentVariable("RCLONE_BWLIMIT", previous); }
        });
        Test("real throttled transfer cancellation terminates rclone", f =>
        {
            var sourcePath = Path.Combine(f.Root, "cancel-slow.bin");
            File.WriteAllBytes(sourcePath, new byte[1024 * 1024]);
            var previous = Environment.GetEnvironmentVariable("RCLONE_BWLIMIT");
            Environment.SetEnvironmentVariable("RCLONE_BWLIMIT", "32k");
            try
            {
                using var cancellation = new CancellationTokenSource();
                using var started = new ManualResetEventSlim(false);
                var result = Task.Run(() => f.Copier.CopyRemoteFileToLocalPath(f.Settings, sourcePath, f.Destination,
                    cancellation.Token, p => { if (p.Phase == "Downloading") { started.Set(); cancellation.Cancel(); } }));
                Check(started.Wait(TimeSpan.FromSeconds(10)), "native transfer never reached downloading");
                Check(result.Wait(TimeSpan.FromSeconds(8)), "native rclone process did not stop after cancellation");
                Check(result.Result.Cancelled && !result.Result.Succeeded, "native cancellation was not distinguished from failure");
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
