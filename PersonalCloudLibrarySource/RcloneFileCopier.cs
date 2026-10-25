using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace PersonalCloudLibrarySource
{
    public class RcloneFileCopier
    {
        public RcloneCopyResult CopyRemoteFileToLocalPath(
            PersonalCloudLibrarySourceSettings settings, string remotePath, string localFullFilePath)
        {
            return Copy(settings, remotePath, localFullFilePath, false);
        }

        public RcloneCopyResult CopyRemoteDirectoryToLocalPath(
            PersonalCloudLibrarySourceSettings settings, string remotePath, string localDirectoryPath)
        {
            return Copy(settings, remotePath, localDirectoryPath, true);
        }

        private static RcloneCopyResult Copy(
            PersonalCloudLibrarySourceSettings settings, string remotePath, string destination, bool isDirectory)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (string.IsNullOrWhiteSpace(remotePath)) return RcloneCopyResult.Fail("Remote path is empty.");
            if (string.IsNullOrWhiteSpace(destination)) return RcloneCopyResult.Fail("Local destination path is empty.");
            var remote = (settings.RcloneRemoteName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(remote)) return RcloneCopyResult.Fail("Rclone remote name is required.");

            var connectSeconds = NormalizeTimeout(settings.RcloneTimeoutSeconds, 30);
            var inactiveSeconds = NormalizeTimeout(settings.RcloneTransferInactivityTimeoutSeconds, 300);
            var command = isDirectory ? "copy" : "copyto";
            var output = new StringBuilder();
            var error = new StringBuilder();
            var logGate = new object();
            var timer = Stopwatch.StartNew();
            var timeout = new RcloneActivityTimeout(TimeSpan.FromSeconds(connectSeconds), TimeSpan.FromSeconds(inactiveSeconds));
            try
            {
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
                            " --use-json-log --stats=1s --stats-log-level=NOTICE" +
                            " --contimeout=" + connectSeconds.ToString(CultureInfo.InvariantCulture) + "s" +
                            " --timeout=" + inactiveSeconds.ToString(CultureInfo.InvariantCulture) + "s" +
                            (isDirectory ? " --retries 5 --low-level-retries 20" : string.Empty),
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    process.OutputDataReceived += (_, args) => Capture(output, args.Data, logGate, timeout, timer);
                    process.ErrorDataReceived += (_, args) => Capture(error, args.Data, logGate, timeout, timer);
                    timer.Restart();
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    while (!process.WaitForExit(200))
                    {
                        var expired = timeout.GetExpiredKind(timer.Elapsed);
                        if (expired == RcloneTimeoutKind.None) continue;
                        if (!TryStop(process)) return RcloneCopyResult.Fail("rclone timed out, but the child process could not be stopped.");
                        return RcloneCopyResult.Fail(expired == RcloneTimeoutKind.Connect
                            ? $"rclone did not report activity within {connectSeconds} seconds."
                            : $"rclone transfer was inactive for {inactiveSeconds} seconds.");
                    }
                    process.WaitForExit(); // Drain asynchronous output before reading the result.
                    lock (logGate)
                    {
                        return process.ExitCode == 0
                            ? RcloneCopyResult.Success(RcloneManifestReader.TrimForLog(output.ToString()))
                            : RcloneCopyResult.Fail($"rclone {command} failed with exit code {process.ExitCode}: {RcloneManifestReader.TrimForLog(error.ToString())}");
                    }
                }
            }
            catch (Exception ex)
            {
                return RcloneCopyResult.Fail("Unable to run rclone. Check its executable and destination paths.", ex);
            }
        }

        private static void Capture(StringBuilder buffer, string line, object gate, RcloneActivityTimeout timeout, Stopwatch timer)
        {
            if (line == null) return;
            timeout.ObserveLine(line, timer.Elapsed);
            lock (gate)
            {
                const int limit = 65536;
                buffer.AppendLine(line.Length > limit ? line.Substring(line.Length - limit) : line);
                if (buffer.Length > limit) buffer.Remove(0, buffer.Length - limit);
            }
        }

        private static int NormalizeTimeout(int value, int fallback) => value >= 5 && value <= 86400 ? value : fallback;

        private static bool TryStop(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill();
                return process.WaitForExit(5000);
            }
            catch { return false; }
        }
    }

    public class RcloneCopyResult
    {
        public bool Succeeded { get; private set; }
        public string Message { get; private set; }
        public Exception Exception { get; private set; }

        public static RcloneCopyResult Success(string message)
        {
            return new RcloneCopyResult { Succeeded = true, Message = string.IsNullOrWhiteSpace(message) ? "rclone transfer completed." : message };
        }

        public static RcloneCopyResult Fail(string message, Exception exception = null)
        {
            return new RcloneCopyResult { Succeeded = false, Message = message, Exception = exception };
        }
    }
}
