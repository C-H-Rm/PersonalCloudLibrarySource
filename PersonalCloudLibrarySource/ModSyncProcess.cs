using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.IO;

namespace PersonalCloudLibrarySource
{
    // Kept independent of Playnite so the actual command path can be integration-tested.
    public sealed class ModSyncProcess
    {
        public ModSyncCommandResult Run(string executable, int timeoutSeconds, params string[] arguments)
            => Run(executable, timeoutSeconds, CancellationToken.None, null, arguments);

        public ModSyncCommandResult Run(string executable, int timeoutSeconds, CancellationToken cancellationToken,
            Action<ModSyncProgress> progress, params string[] arguments)
            => RunCore(executable, timeoutSeconds, cancellationToken, progress, arguments.Length == 0 ? "command" : arguments[0], arguments);

        internal ModSyncCommandResult RunCore(string executable, int timeoutSeconds, CancellationToken cancellationToken,
            Action<ModSyncProgress> progress, string phase, params string[] arguments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var process = new Process())
            {
                var output = new StringBuilder();
                var error = new StringBuilder();
                var outputTooLarge = false;
                var gate = new object();
                Exception callbackError = null;
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = string.Join(" ", arguments.Select(Quote)),
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
                };
                Action kill = () =>
                {
                    try
                    {
                        if (process.HasExited) return;
#if NET8_0_OR_GREATER
                        process.Kill(true); // Also terminate descendants in the subprocess test harness.
#else
                        process.Kill(); // .NET Framework 4.8: rclone is launched directly, without a shell.
#endif
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                };
                process.OutputDataReceived += (sender, args) =>
                {
                    if (args.Data == null) return;
                    lock (gate)
                    {
                        if (output.Length + args.Data.Length > 8388608) outputTooLarge = true;
                        if (!outputTooLarge) output.AppendLine(args.Data);
                    }
                };
                process.ErrorDataReceived += (sender, args) =>
                {
                    if (args.Data == null) return;
                    lock (gate)
                    {
                        error.AppendLine(args.Data);
                        if (error.Length > 262144) error.Remove(0, error.Length - 131072);
                    }
                    var stats = ReadRootStats(args.Data);
                    if (stats == null || progress == null) return;
                    try { progress(new ModSyncProgress { Phase = phase, Bytes = stats.Bytes, TotalBytes = stats.TotalBytes, Checks = stats.Checks, TotalChecks = stats.TotalChecks, BytesPerSecond = stats.BytesPerSecond }); }
                    catch (Exception ex) { lock (gate) callbackError = ex; kill(); }
                };
                progress?.Invoke(new ModSyncProgress { Phase = phase });
                cancellationToken.ThrowIfCancellationRequested();
                process.Start();
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
                var timeout = Stopwatch.StartNew();
                var timedOut = false;
                using (cancellationToken.Register(kill))
                {
                    while (!process.WaitForExit(100))
                    {
                        if (cancellationToken.IsCancellationRequested) { kill(); break; }
                        lock (gate) if (callbackError != null) { kill(); break; }
                        if (timeout.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds)) { timedOut = true; kill(); break; }
                    }
                }
                if (!process.HasExited) { kill(); process.WaitForExit(); }
                else process.WaitForExit(); // drains asynchronous output/error handlers
                lock (gate)
                {
                    if (callbackError != null) throw new ModSyncCallbackException(callbackError, output.ToString(), error.ToString());
                    if (cancellationToken.IsCancellationRequested) return new ModSyncCommandResult { ExitCode = -1, Output = output.ToString(), Error = error.ToString(), Cancelled = true };
                    if (timedOut)
                        throw new ModSyncTimeoutException("Mod sync exceeded its time limit. Sync state was retained; do not automatically resync.", output.ToString(), error.ToString());
                    if (outputTooLarge) throw new InvalidOperationException("rclone output exceeded the safe listing limit. Refusing to use a partial listing.");
                    return new ModSyncCommandResult { ExitCode = process.ExitCode, Output = output.ToString(), Error = error.ToString() };
                }
            }
        }

        // Only a bounded top-level rclone stats object is accepted. Nested file counters and messages are ignored.
        private static Stats ReadRootStats(string line)
        {
            if (line == null || line.Length > 65536) return null;
            try
            {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(line)))
                    return ((LogEntry)new DataContractJsonSerializer(typeof(LogEntry)).ReadObject(stream))?.Statistics;
            }
            catch { return null; }
        }

        // Windows CommandLineToArgvW quoting, including trailing backslashes.
        internal static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            var slashes = 0;
            foreach (var c in value ?? string.Empty)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { result.Append('\\', slashes * 2 + 1); result.Append(c); }
                else { result.Append('\\', slashes); result.Append(c); }
                slashes = 0;
            }
            result.Append('\\', slashes * 2);
            return result.Append('"').ToString();
        }

        [DataContract] private sealed class LogEntry { [DataMember(Name = "stats")] public Stats Statistics { get; set; } }
        [DataContract] private sealed class Stats
        {
            [DataMember(Name = "bytes")] public long Bytes { get; set; }
            [DataMember(Name = "totalBytes")] public long TotalBytes { get; set; }
            [DataMember(Name = "checks")] public long Checks { get; set; }
            [DataMember(Name = "totalChecks")] public long TotalChecks { get; set; }
            [DataMember(Name = "speed")] public double BytesPerSecond { get; set; }
        }
    }

    public sealed class ModSyncCommandResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
        public bool Cancelled { get; set; }
        public string Log => Output + "\n" + Error;
    }
    internal sealed class ModSyncTimeoutException : TimeoutException
    {
        public string Log { get; }
        public ModSyncTimeoutException(string message, string output, string error) : base(message) { Log = output + "\n" + error; }
    }
    internal sealed class ModSyncCallbackException : Exception
    {
        public string Log { get; }
        public ModSyncCallbackException(Exception inner, string output, string error) : base("Mod sync progress callback failed.", inner) { Log = output + "\n" + error; }
    }
}
