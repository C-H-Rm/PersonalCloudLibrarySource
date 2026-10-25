using System;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace PersonalCloudLibrarySource
{
    // Kept independent of Playnite so the actual command path can be integration-tested.
    public sealed class ModSyncProcess
    {
        public ModSyncCommandResult Run(string executable, int timeoutSeconds, params string[] arguments)
        {
            using (var process = new Process())
            {
                var output = new StringBuilder();
                var error = new StringBuilder();
                var outputTooLarge = false;
                var gate = new object();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = string.Join(" ", arguments.Select(Quote)),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
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
                };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(checked(timeoutSeconds * 1000)))
                {
                    process.Kill();
                    process.WaitForExit();
                    throw new TimeoutException("Mod sync exceeded its time limit. Sync state was retained; do not automatically resync.");
                }
                process.WaitForExit();
                if (outputTooLarge) throw new InvalidOperationException("rclone output exceeded the safe listing limit. Refusing to use a partial listing.");
                return new ModSyncCommandResult { ExitCode = process.ExitCode, Output = output.ToString(), Error = error.ToString() };
            }
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
    }

    public sealed class ModSyncCommandResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
        public string Log => Output + "\n" + Error;
    }
}
