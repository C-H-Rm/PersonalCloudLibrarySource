using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace PersonalCloudLibrarySource
{
    public class RcloneFileCopier
    {
        public RcloneCopyResult CopyRemoteFileToLocalPath(PersonalCloudLibrarySourceSettings settings, string remotePath, string localFullFilePath)
            => Copy(settings, remotePath, localFullFilePath, false, CancellationToken.None, null);

        public RcloneCopyResult CopyRemoteDirectoryToLocalPath(PersonalCloudLibrarySourceSettings settings, string remotePath, string localDirectoryPath)
            => Copy(settings, remotePath, localDirectoryPath, true, CancellationToken.None, null);

        public RcloneCopyResult CopyRemoteFileToLocalPath(PersonalCloudLibrarySourceSettings settings, string remotePath,
            string localFullFilePath, CancellationToken cancellationToken, Action<RcloneTransferProgress> progress)
            => Copy(settings, remotePath, localFullFilePath, false, cancellationToken, progress);

        public RcloneCopyResult CopyRemoteDirectoryToLocalPath(PersonalCloudLibrarySourceSettings settings, string remotePath,
            string localDirectoryPath, CancellationToken cancellationToken, Action<RcloneTransferProgress> progress)
            => Copy(settings, remotePath, localDirectoryPath, true, cancellationToken, progress, false);

        public RcloneCopyResult CopyMissingRemoteDirectoryToLocalPath(PersonalCloudLibrarySourceSettings settings, string remotePath,
            string localDirectoryPath, CancellationToken cancellationToken, Action<RcloneTransferProgress> progress)
            => CopyMissingRemoteDirectoryToLocalPath(settings, remotePath, localDirectoryPath, null, cancellationToken, progress);

        public RcloneCopyResult CopyMissingRemoteDirectoryToLocalPath(PersonalCloudLibrarySourceSettings settings, string remotePath,
            string localDirectoryPath, string comparisonDirectory, CancellationToken cancellationToken, Action<RcloneTransferProgress> progress)
            => Copy(settings, remotePath, localDirectoryPath, true, cancellationToken, progress, true, comparisonDirectory);

        private static RcloneCopyResult Copy(PersonalCloudLibrarySourceSettings settings, string remotePath,
            string destination, bool isDirectory, CancellationToken cancellationToken, Action<RcloneTransferProgress> progressCallback,
            bool onlyMissing = false, string comparisonDirectory = null)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (string.IsNullOrWhiteSpace(remotePath)) return RcloneCopyResult.Fail("Remote path is empty.");
            if (string.IsNullOrWhiteSpace(destination)) return RcloneCopyResult.Fail("Local destination path is empty.");
            var remote = (settings.RcloneRemoteName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(remote)) return RcloneCopyResult.Fail("Rclone remote name is required.");

            var tracker = new RcloneTransferProgressTracker(isDirectory);
            var output = new StringBuilder();
            var error = new StringBuilder();
            var logGate = new object();
            var progressGate = new object();
            Exception callbackException = null;
            Action<RcloneTransferProgress> publish = value =>
            {
                if (progressCallback == null) return;
                try { progressCallback(value); }
                catch (Exception ex) { Interlocked.CompareExchange(ref callbackException, ex, null); }
            };
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    tracker.Finish("Cancelled"); publish(tracker.Progress);
                    return RcloneCopyResult.Cancel("rclone transfer was cancelled.");
                }
                publish(tracker.Progress);
                if (callbackException != null) return RcloneCopyResult.Fail("Progress callback failed.", callbackException);

                var connectSeconds = NormalizeTimeout(settings.RcloneTimeoutSeconds, 30);
                var inactiveSeconds = NormalizeTimeout(settings.RcloneTransferInactivityTimeoutSeconds, 300);
                var command = isDirectory ? "copy" : "copyto";
                var timer = Stopwatch.StartNew();
                var timeout = new RcloneActivityTimeout(TimeSpan.FromSeconds(connectSeconds), TimeSpan.FromSeconds(inactiveSeconds));
                var destinationFolder = isDirectory ? destination : Path.GetDirectoryName(destination);
                if (string.IsNullOrWhiteSpace(destinationFolder)) return RcloneCopyResult.Fail("Local destination folder could not be resolved.");
                Directory.CreateDirectory(destinationFolder);
                using (var process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = string.IsNullOrWhiteSpace(settings.RcloneExecutablePath) ? "rclone" : settings.RcloneExecutablePath.Trim(),
                        Arguments = command + " " + RcloneManifestReader.QuoteArgument(remote + ":" + remotePath.Trim()) +
                            " " + RcloneManifestReader.QuoteArgument(destination) +
                            (isDirectory ? string.Empty : " --check-first") +
                            (!onlyMissing ? " --transfers=32 --checkers=32" : string.Empty) +
                            (isDirectory && !onlyMissing ? " --fast-list" : string.Empty) +
                            " --use-json-log --stats=1s --stats-log-level=NOTICE --log-level=DEBUG" +
                            " --contimeout=" + connectSeconds.ToString(CultureInfo.InvariantCulture) + "s" +
                            " --timeout=" + inactiveSeconds.ToString(CultureInfo.InvariantCulture) + "s" +
                            (isDirectory ? " --retries 5 --low-level-retries 20" : string.Empty) +
                            (onlyMissing ? (string.IsNullOrWhiteSpace(comparisonDirectory) ? " --ignore-existing" : string.Empty) +
                                " --exclude \".pcls-modsync-*\" --exclude \".pcls-stage-owner\"" : string.Empty) +
                            (onlyMissing && !string.IsNullOrWhiteSpace(comparisonDirectory)
                                ? " --compare-dest " + RcloneManifestReader.QuoteArgument(comparisonDirectory) + " --size-only --ignore-size"
                                : string.Empty),
                        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
                    };
                    process.OutputDataReceived += (_, args) => Capture(output, args.Data, logGate, timeout, timer, null, null, null);
                    process.ErrorDataReceived += (_, args) => Capture(error, args.Data, logGate, timeout, timer, tracker, progressGate, publish);
                    timer.Restart();
                    process.Start();
                    try
                    {
                        process.BeginOutputReadLine(); process.BeginErrorReadLine();
                        while (!process.WaitForExit(200))
                        {
                            if (callbackException != null)
                            {
                                TryStop(process); tracker.Finish("Failed");
                                return RcloneCopyResult.Fail("Progress callback failed.", callbackException);
                            }
                            if (cancellationToken.IsCancellationRequested)
                            {
                                if (!TryStop(process)) return RcloneCopyResult.Fail("rclone was cancelled, but the child process could not be stopped.");
                                tracker.Finish("Cancelled"); publish(tracker.Progress);
                                return RcloneCopyResult.Cancel("rclone transfer was cancelled.");
                            }
                            var expired = timeout.GetExpiredKind(timer.Elapsed);
                            if (expired == RcloneTimeoutKind.None) continue;
                            if (!TryStop(process)) return RcloneCopyResult.Fail("rclone timed out, but the child process could not be stopped.");
                            tracker.Finish("Failed");
                            return RcloneCopyResult.Fail(expired == RcloneTimeoutKind.Connect
                                ? $"rclone did not report activity within {connectSeconds} seconds."
                                : $"rclone transfer was inactive for {inactiveSeconds} seconds.");
                        }
                        process.WaitForExit(); // Drain asynchronous output before reading the result.
                        if (callbackException != null) return RcloneCopyResult.Fail("Progress callback failed.", callbackException);
                        lock (logGate)
                        {
                            if (process.ExitCode == 0)
                            {
                                tracker.Finish("Finishing"); publish(tracker.Progress);
                                tracker.Finish("Complete"); publish(tracker.Progress);
                                return callbackException == null ? RcloneCopyResult.Success(RcloneManifestReader.TrimForLog(output.ToString()))
                                    : RcloneCopyResult.Fail("Progress callback failed.", callbackException);
                            }
                            tracker.Finish("Failed"); publish(tracker.Progress);
                            return RcloneCopyResult.Fail($"rclone {command} failed with exit code {process.ExitCode}: {RcloneManifestReader.TrimForLog(error.ToString())}");
                        }
                    }
                    finally
                    {
                        // Disposing Process alone does not stop an already-started child.
                        if (!process.HasExited) TryStop(process);
                    }
                }
            }
            catch (Exception ex)
            {
                tracker.Finish("Failed"); publish(tracker.Progress);
                return RcloneCopyResult.Fail("Unable to run rclone. Check its executable and destination paths.", ex);
            }
        }

        private static void Capture(StringBuilder buffer, string line, object gate, RcloneActivityTimeout timeout,
            Stopwatch timer, RcloneTransferProgressTracker tracker, object progressGate, Action<RcloneTransferProgress> publish)
        {
            if (line == null) return;
            timeout.ObserveLine(line, timer.Elapsed);
            var debugOnly = RcloneLogEntry.TryParse(line, out var logEntry) &&
                string.Equals(logEntry.Level, "DEBUG", StringComparison.OrdinalIgnoreCase);
            if (!debugOnly)
            {
                lock (gate)
                {
                    const int limit = 65536;
                    buffer.AppendLine(line.Length > limit ? line.Substring(line.Length - limit) : line);
                    if (buffer.Length > limit) buffer.Remove(0, buffer.Length - limit);
                }
            }
            if (tracker != null)
            {
                lock (progressGate)
                {
                    var previous = tracker.Progress;
                    tracker.ObserveStats(line, timer.ElapsedMilliseconds);
                    tracker.ObserveLogMessage(line);
                    var current = tracker.Progress;
                    if (current.Phase != previous.Phase || current.TotalBytes != previous.TotalBytes ||
                        current.CompletedBytes != previous.CompletedBytes || current.BytesPerSecond != previous.BytesPerSecond) publish(current);
                }
            }
        }

        private static int NormalizeTimeout(int value, int fallback) => value >= 5 && value <= 86400 ? value : fallback;
        private static bool TryStop(Process process)
        {
            try { if (!process.HasExited) process.Kill(); return process.WaitForExit(5000); }
            catch { return false; }
        }
    }

    public class RcloneCopyResult
    {
        public bool Succeeded { get; private set; }
        public bool Cancelled { get; private set; }
        public string Message { get; private set; }
        public Exception Exception { get; private set; }
        public static RcloneCopyResult Success(string message) => new RcloneCopyResult { Succeeded = true,
            Message = string.IsNullOrWhiteSpace(message) ? "rclone transfer completed." : message };
        public static RcloneCopyResult Fail(string message, Exception exception = null) =>
            new RcloneCopyResult { Succeeded = false, Message = message, Exception = exception };
        public static RcloneCopyResult Cancel(string message) =>
            new RcloneCopyResult { Succeeded = false, Cancelled = true, Message = message };
    }
}
