using System;
using System.Collections.Generic;
using System.Globalization;

namespace PersonalCloudLibrarySource
{
    // Operation-local UI state: closing a dialog must not become persistent sync state.
    public sealed class ModSyncUserInteraction
    {
        private readonly HashSet<string> approvals = new HashSet<string>(StringComparer.Ordinal);
        public string PendingConfirmation { get; private set; }
        public bool Relink { get; private set; }
        public bool Declined { get; private set; }

        public ModSyncUserInteraction(bool relink) { Relink = relink; }
        public void BeginAttempt() { PendingConfirmation = null; }

        public bool Confirm(string message)
        {
            if (approvals.Contains(message)) return true;
            PendingConfirmation = message;
            return false;
        }

        public bool ShouldRetry(bool requiresRelink, bool manual, Func<string, bool> confirm)
        {
            if (PendingConfirmation != null)
            {
                if (!confirm(PendingConfirmation)) { Declined = true; return false; }
                approvals.Add(PendingConfirmation);
                return true;
            }
            // Calling Relink only offers its question. It cannot mutate until Confirm approves it.
            if (requiresRelink && manual && !Relink) { Relink = true; return true; }
            return false;
        }

        public static string ProgressText(string phase, long bytes, long totalBytes,
            long checks, long totalChecks, double bytesPerSecond)
        {
            switch (phase)
            {
                case "compatibility": phase = "Checking rclone support"; break;
                case "check": phase = "Comparing initial mod folders"; break;
                case "lsf": phase = "Reading cloud mod list"; break;
                case "mkdir": phase = "Preparing cloud mod folder"; break;
                case "copyto": phase = "Linking mod folders"; break;
                case "bisync": phase = "Comparing and synchronizing mods"; break;
            }
            var parts = new List<string> { string.IsNullOrWhiteSpace(phase) ? "Synchronizing mods" : phase };
            const double mib = 1024.0 * 1024.0;
            if (totalBytes > 0)
                parts.Add(string.Format(CultureInfo.InvariantCulture, "{0:0.0} / {1:0.0} MiB", Math.Max(0, bytes) / mib, totalBytes / mib));
            else if (bytes > 0)
                parts.Add(string.Format(CultureInfo.InvariantCulture, "{0:0.0} MiB", bytes / mib));
            if (totalChecks > 0)
                parts.Add(string.Format(CultureInfo.InvariantCulture, "Files checked: {0} / {1}", Math.Max(0, checks), totalChecks));
            if (bytesPerSecond > 0 && !double.IsInfinity(bytesPerSecond) && !double.IsNaN(bytesPerSecond))
                parts.Add(string.Format(CultureInfo.InvariantCulture, "{0:0.0} MiB/s", bytesPerSecond / mib));
            return string.Join(" · ", parts);
        }
    }
}
