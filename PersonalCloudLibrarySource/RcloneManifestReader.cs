using System;
using System.Threading;

namespace PersonalCloudLibrarySource
{
    public class RcloneManifestReader
    {
        public string ReadManifestJson(PersonalCloudLibrarySourceSettings settings)
        {
            return ReadManifestJson(settings, CancellationToken.None);
        }

        public string ReadManifestJson(PersonalCloudLibrarySourceSettings settings, CancellationToken cancellationToken)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            cancellationToken.ThrowIfCancellationRequested();
            var executablePath = string.IsNullOrWhiteSpace(settings.RcloneExecutablePath)
                ? "rclone" : settings.RcloneExecutablePath.Trim();
            var remoteName = (settings.RcloneRemoteName ?? string.Empty).Trim();
            var manifestPath = (settings.RcloneManifestPath ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(remoteName))
                throw new InvalidOperationException("Rclone remote name is required.");
            if (string.IsNullOrWhiteSpace(manifestPath))
                throw new InvalidOperationException("Rclone manifest path is required.");
            var timeoutSeconds = settings.RcloneTimeoutSeconds < 5 ? 30 : settings.RcloneTimeoutSeconds;
            // One bounded, cancellable process runner; stdout remains the JSON manifest only.
            var result = new ModSyncProcess().Run(executablePath, timeoutSeconds, cancellationToken, null,
                "cat", remoteName + ":" + manifestPath);
            if (result.Cancelled) throw new OperationCanceledException(cancellationToken);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"rclone cat failed with exit code {result.ExitCode}: {TrimForLog(result.Error)}");
            if (string.IsNullOrWhiteSpace(result.Output))
                throw new InvalidOperationException("rclone returned an empty manifest.");
            return result.Output;
        }

        internal static string QuoteArgument(string value) => ModSyncProcess.Quote(value);

        internal static string TrimForLog(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "No error output.";
            var trimmed = value.Trim();
            return trimmed.Length <= 500 ? trimmed : trimmed.Substring(0, 500);
        }
    }
}
