using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        Test("missing-mod progress stays indeterminate while streaming until successful completion", f =>
        {
            var tracker = new RcloneTransferProgressTracker(freezeTotalAfterChecks: true);
            tracker.ObserveStats("{\"stats\":{\"bytes\":0,\"speed\":0,\"totalBytes\":100,\"transfers\":0}}", 0);
            Check(tracker.Progress.Phase == "Inventory" && tracker.Progress.TotalBytes == null && tracker.Progress.Percentage == null,
                "partial inventory total was exposed");
            tracker.ObserveStats("{\"stats\":{\"bytes\":10,\"speed\":4,\"totalBytes\":100,\"transfers\":1}}", 1);
            Check(tracker.Progress.Phase == "Downloading" && tracker.Progress.CompletedBytes == 10 && tracker.Progress.BytesPerSecond == 4 &&
                tracker.Progress.TotalBytes == null && tracker.Progress.Percentage == null, "streaming progress invented a final total");
            tracker.ObserveStats("{\"stats\":{\"bytes\":40,\"speed\":8,\"totalBytes\":400,\"transfers\":2}}", 2);
            Check(tracker.Progress.CompletedBytes == 40 && tracker.Progress.TotalBytes == null && tracker.Progress.Percentage == null,
                "growing discovery total was treated as final");
            tracker.ObserveLogMessage("{\"level\":\"debug\",\"msg\":\"Waiting for transfers to finish\"}");
            Check(tracker.Progress.TotalBytes == null && tracker.Progress.Percentage == null,
                "partial pre-marker stats became the final total");
            tracker.ObserveStats("{\"stats\":{\"bytes\":40,\"speed\":8,\"totalBytes\":400,\"transfers\":2}}", 3);
            Check(tracker.Progress.TotalBytes == 400 && tracker.Progress.Percentage == 10,
                "final total was not frozen from the post-check statistics snapshot");
            tracker.ObserveStats("{\"stats\":{\"bytes\":50,\"speed\":2,\"totalBytes\":900,\"transfers\":2}}", 4);
            Check(tracker.Progress.TotalBytes == 400 && tracker.Progress.Percentage == 12.5,
                "frozen final denominator changed during transfers");
            tracker.Finish("Complete");
            Check(tracker.Progress.TotalBytes == 400 && tracker.Progress.CompletedBytes == 400 && tracker.Progress.Percentage == 100,
                "successful completion did not finalize the frozen total");
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
        Test("real rclone copies paths with spaces, nested files, and empty files", f =>
        {
            var source = Path.Combine(f.Root, "source folder");
            Directory.CreateDirectory(Path.Combine(source, "nested folder"));
            File.WriteAllText(Path.Combine(source, "file with spaces.bin"), "actual local content");
            File.WriteAllBytes(Path.Combine(source, "empty file.bin"), Array.Empty<byte>());
            File.WriteAllText(Path.Combine(source, "nested folder", "nested mod.bin"), "nested content");
            var result = f.Copier.CopyRemoteDirectoryToLocalPath(f.Settings, source, f.Destination);
            Check(result.Succeeded && File.ReadAllText(Path.Combine(f.Destination, "file with spaces.bin")) == "actual local content" &&
                new FileInfo(Path.Combine(f.Destination, "empty file.bin")).Length == 0 &&
                File.ReadAllText(Path.Combine(f.Destination, "nested folder", "nested mod.bin")) == "nested content", result.Message);
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
        // Keep the 30k-file metadata stress case opt-in; it is a slow perf regression test, not a routine unit test.
        // Run with PCLS_RUN_SLOW_RCLONE_STRESS=1 and filter "slow native missing-mod" to exercise it alone.
        if (Environment.GetEnvironmentVariable("PCLS_RUN_SLOW_RCLONE_STRESS") == "1")
        {
        Test("slow native missing-mod directory streams during inventory and freezes the final total", f =>
        {
            var source = Path.Combine(f.Root, "stream-source");
            Directory.CreateDirectory(source);
            for (int i = 0; i < 30000; i++)
                File.WriteAllBytes(Path.Combine(source, "cached-" + i.ToString("D5") + ".bin"), new byte[512]);
            const int transferBytes = 16 * 1024 * 1024;
            File.WriteAllBytes(Path.Combine(source, "000-new.bin"), new byte[transferBytes]);
            var previous = Environment.GetEnvironmentVariable("RCLONE_BWLIMIT");
            Environment.SetEnvironmentVariable("RCLONE_BWLIMIT", "1M");
            try
            {
                var compare = Path.Combine(f.Root, "stream-mod-original");
                var stage = Path.Combine(f.Root, "stream-mod-stage");
                Directory.CreateDirectory(compare);
                Directory.CreateDirectory(stage);
                for (int i = 0; i < 30000; i++)
                {
                    var name = "cached-" + i.ToString("D5") + ".bin";
                    var src = Path.Combine(source, name);
                    var original = Path.Combine(compare, name);
                    File.Copy(src, original);
                    File.SetLastWriteTimeUtc(original, File.GetLastWriteTimeUtc(src));
                }
                var samples = new System.Collections.Generic.List<RcloneTransferProgress>();
                var clock = Stopwatch.StartNew();
                double? firstStreamingSeconds = null, frozenSeconds = null;
                var result = f.Copier.CopyMissingRemoteDirectoryToLocalPath(f.Settings, source, stage, compare,
                    CancellationToken.None, p => Track(p));
                void Track(RcloneTransferProgress p)
                {
                    samples.Add(p);
                    if (firstStreamingSeconds == null && p.Phase == "Downloading" && p.CompletedBytes > 0 && !p.TotalBytes.HasValue)
                        firstStreamingSeconds = clock.Elapsed.TotalSeconds;
                    if (frozenSeconds == null && p.Phase == "Downloading" && p.TotalBytes.HasValue)
                        frozenSeconds = clock.Elapsed.TotalSeconds;
                }
                Check(result.Succeeded, result.Message);
                var streaming = samples.FindIndex(p => p.Phase == "Downloading" && p.CompletedBytes > 0 && !p.TotalBytes.HasValue);
                var frozen = samples.Find(p => p.Phase == "Downloading" && p.TotalBytes.HasValue);
                Check(streaming >= 0 && firstStreamingSeconds.HasValue,
                    "missing-mod bytes did not stream before all checks completed");
                Check(frozen != null && frozen.TotalBytes == transferBytes && frozen.Percentage.HasValue && frozenSeconds.HasValue &&
                    frozenSeconds.Value > firstStreamingSeconds.Value,
                    "missing-mod denominator did not freeze after checkers drained; samples=" +
                    string.Join(";", samples.Select(p => p.Phase + ":" + p.CompletedBytes + "/" + (p.TotalBytes?.ToString() ?? "?") + ":" + p.Percentage)));
                Check(samples[samples.Count - 1].Phase == "Complete" && samples[samples.Count - 1].Percentage == 100,
                    "whole-directory transfer did not reach successful completion");
                Check(new FileInfo(Path.Combine(stage, "000-new.bin")).Length == transferBytes,
                    "missing mod was not completely staged");
                Check(!File.Exists(Path.Combine(stage, "cached-00000.bin")), "compare-dest cache entry was unnecessarily staged");
                Console.WriteLine("EVIDENCE missing-mod rclone stream: bytes at " + firstStreamingSeconds.Value.ToString("F2") +
                    "s; denominator froze at " + frozenSeconds.Value.ToString("F2") + "s after 30,000 inventory entries; fixed total " +
                    frozen.TotalBytes + " bytes; complete");
            }
            finally { Environment.SetEnvironmentVariable("RCLONE_BWLIMIT", previous); }
        });
        }
        else Console.WriteLine("SKIP slow native rclone stress test (set PCLS_RUN_SLOW_RCLONE_STRESS=1 to run it)");
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
            const int slowBytes = 768 * 1024;
            var source = Path.Combine(f.Root, "slow.bin"); File.WriteAllBytes(source, new byte[slowBytes]);
            var previous = Environment.GetEnvironmentVariable("RCLONE_BWLIMIT");
            Environment.SetEnvironmentVariable("RCLONE_BWLIMIT", "128k");
            try
            {
                var samples = new System.Collections.Generic.List<RcloneTransferProgress>();
                var timer = Stopwatch.StartNew();
                var result = f.Copier.CopyRemoteFileToLocalPath(f.Settings, source, f.Destination, CancellationToken.None, p => samples.Add(p));
                Check(result.Succeeded && timer.Elapsed.TotalSeconds > 5 && new FileInfo(f.Destination).Length == slowBytes,
                    "slow transfer failed or did not exercise old limit: " + result.Message);
                var downloading = samples.FindAll(p => p.Phase == "Downloading");
                Check(downloading.Count > 0 && downloading[0].TotalBytes == slowBytes,
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
        Test("install-only compare-dest copy stages missing files and preserves originals", f =>
        {
            var source = Path.Combine(f.Root, "install-cloud");
            var compare = Path.Combine(f.Root, "install-original");
            var stage = Path.Combine(f.Root, "install-stage");
            Directory.CreateDirectory(source); Directory.CreateDirectory(compare); Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(source, "new.bin"), "cloud new");
            File.WriteAllText(Path.Combine(source, "changed.bin"), "cloud changed");
            File.WriteAllText(Path.Combine(source, ".pcls-modsync-reserved"), "marker");
            File.WriteAllText(Path.Combine(compare, "changed.bin"), "local wins");
            var result = f.Copier.CopyMissingRemoteDirectoryToLocalPath(f.Settings, source, stage, compare,
                CancellationToken.None, null);
            Check(result.Succeeded, result.Message);
            Check(File.ReadAllText(Path.Combine(stage, "new.bin")) == "cloud new", "missing cloud file not staged");
            Check(File.ReadAllText(Path.Combine(compare, "changed.bin")) == "local wins", "existing local file overwritten");
            Check(!File.Exists(Path.Combine(stage, "changed.bin")), "existing local file was redundantly staged");
            Check(!File.Exists(Path.Combine(stage, ".pcls-modsync-reserved")), "reserved marker was copied");
            Check(File.ReadAllText(Path.Combine(source, "changed.bin")) == "cloud changed", "cloud changed");
        });
        Test("install-only command audit is copy-only with no content check or upload", f =>
        {
            if (!OperatingSystem.IsLinux()) return;
            var source = Path.Combine(f.Root, "audit-cloud"); var compare = Path.Combine(f.Root, "audit-original");
            var target = Path.Combine(f.Root, "audit-stage");
            Directory.CreateDirectory(source); Directory.CreateDirectory(compare); Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(source, "new"), "new");
            var audit = Path.Combine(f.Root, "commands.txt");
            var real = Environment.GetEnvironmentVariable("PCLS_RCLONE") ?? "rclone";
            var wrapper = Path.Combine(f.Root, "audit-rclone.sh");
            File.WriteAllText(wrapper, "#!/bin/sh\nprintf '%s\\n' \"$@\" >> \"" + audit + "\"\nexec \"" + real + "\" \"$@\"\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            f.Settings.RcloneExecutablePath = wrapper;
            var result = f.Copier.CopyMissingRemoteDirectoryToLocalPath(f.Settings, source, target, compare,
                CancellationToken.None, null);
            Check(result.Succeeded, result.Message);
            var args = File.ReadAllLines(audit);
            Check(args.Contains("copy") && args.Contains("--compare-dest") && args.Contains("--size-only") &&
                args.Contains("--ignore-size") && args.Contains("--exclude") && args.Contains(".pcls-modsync-*"),
                "production compare-dest copy flags absent: " + string.Join(" ", args));
            Check(!args.Contains("--ignore-existing"), "test unexpectedly exercised the legacy ignore-existing overload");
            Check(!args.Contains("--check-first"), "install-only copy retained the whole-tree pre-transfer barrier");
            Check(!args.Contains("bisync") && !args.Contains("--download") && !args.Contains("copyto") && !args.Contains("sync"), "install command audit found a comparison or upload: " + string.Join(" ", args));
            File.WriteAllText(audit, string.Empty);
            var baseTarget = Path.Combine(f.Root, "base-audit-local");
            var baseResult = f.Copier.CopyRemoteDirectoryToLocalPath(f.Settings, source, baseTarget, CancellationToken.None, null);
            Check(baseResult.Succeeded, baseResult.Message);
            var baseArgs = File.ReadAllLines(audit);
            Check(!baseArgs.Contains("--check-first"), "base-game copy retained the whole-tree pre-transfer barrier");
            Check(baseArgs.Count(arg => arg == "--fast-list") == 1, "base-game directory copy did not request fast listing");
            Check(baseArgs.Contains("--log-level=DEBUG"), "native checker-completion signal is not enabled");
            void CheckConcurrency(string[] command, string verb)
            {
                Check(command[0] == verb && command.Count(arg => arg == "--transfers=32") == 1 &&
                    command.Count(arg => arg == "--checkers=32") == 1,
                    "expected the 32-transfer/checker policy on " + verb + ": " + string.Join(" ", command));
            }
            CheckConcurrency(baseArgs, "copy");
            File.WriteAllText(audit, string.Empty);
            var archiveTarget = Path.Combine(f.Root, "mod-download.zip");
            var archiveProgress = new System.Collections.Generic.List<RcloneTransferProgress>();
            var archiveResult = f.Copier.CopyRemoteFileToLocalPath(f.Settings, Path.Combine(source, "new"),
                archiveTarget, CancellationToken.None, archiveProgress.Add);
            Check(archiveResult.Succeeded && File.ReadAllText(archiveTarget) == "new", archiveResult.Message);
            var archiveArgs = File.ReadAllLines(audit);
            CheckConcurrency(archiveArgs, "copyto");
            Check(!archiveArgs.Contains("--fast-list"), "a single archive does not need recursive listing");
            Check(archiveProgress.Last().TotalBytes == new FileInfo(archiveTarget).Length,
                "a fast archive download lost its exact transferred size");
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
        for (int i = 0; i < (mode == "progress" ? 24 : 120); i++)
        {
            if (mode == "progress" || mode == "idle") Console.Error.WriteLine("{\"stats\":{\"bytes\":" + (mode == "idle" ? 0 : i) + ",\"checks\":0,\"transfers\":0}}");
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
