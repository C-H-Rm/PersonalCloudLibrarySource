using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace PersonalCloudLibrarySource
{
    public sealed class RcloneTransferProgress
    {
        public string Phase { get; internal set; }
        public long? TotalBytes { get; internal set; }
        public long CompletedBytes { get; internal set; }
        public double BytesPerSecond { get; internal set; }
        public double? Percentage { get; internal set; }

        internal RcloneTransferProgress Copy()
        {
            return new RcloneTransferProgress { Phase = Phase, TotalBytes = TotalBytes, CompletedBytes = CompletedBytes,
                BytesPerSecond = BytesPerSecond, Percentage = Percentage };
        }
    }

    /// <summary>Converts rclone JSON statistics into transfer progress without treating discovery totals as final.</summary>
    public sealed class RcloneTransferProgressTracker
    {
        private readonly object gate = new object();
        private readonly bool freezeTotalAfterChecks;
        private readonly RcloneTransferProgress progress = new RcloneTransferProgress { Phase = "Inventory" };
        private bool transferStarted;
        private long previousBytes;
        private long? latestTotalBytes;
        private bool checksFinished;
        private bool totalFrozen;
        private bool retrying;

        public RcloneTransferProgressTracker(bool freezeTotalAfterChecks = false)
        {
            this.freezeTotalAfterChecks = freezeTotalAfterChecks;
        }

        public RcloneTransferProgress Progress { get { lock (gate) return progress.Copy(); } }

        public void ObserveStats(string jsonLine, long elapsedMilliseconds)
        {
            RcloneStats stats;
            if (!RcloneStats.TryParse(jsonLine, out stats)) return;
            lock (gate)
            {
                if (transferStarted && stats.Bytes < previousBytes) retrying = true;
                previousBytes = stats.Bytes;
                if (stats.TotalBytes.HasValue) latestTotalBytes = stats.TotalBytes.Value;
                if (freezeTotalAfterChecks && checksFinished && !totalFrozen && stats.TotalBytes.HasValue)
                {
                    progress.TotalBytes = latestTotalBytes.Value;
                    totalFrozen = true;
                }
                if (!transferStarted && (stats.Transfers > 0 || stats.Bytes > 0))
                {
                    transferStarted = true;
                    progress.Phase = "Downloading";
                }
                if (!transferStarted) return;
                if (!freezeTotalAfterChecks && !progress.TotalBytes.HasValue && stats.TotalBytes.HasValue)
                    progress.TotalBytes = stats.TotalBytes.Value;

                progress.CompletedBytes = stats.Bytes;
                if (progress.TotalBytes.HasValue && progress.CompletedBytes > progress.TotalBytes.Value)
                    progress.CompletedBytes = progress.TotalBytes.Value;
                progress.BytesPerSecond = IsFiniteNonNegative(stats.Speed) ? stats.Speed : 0;
                if (retrying)
                {
                    progress.Phase = "Retrying";
                    progress.Percentage = null;
                }
                else if ((!freezeTotalAfterChecks || totalFrozen) && progress.TotalBytes > 0 && progress.CompletedBytes >= progress.TotalBytes)
                {
                    progress.Phase = "Finishing";
                    progress.Percentage = null;
                }
                else
                {
                    progress.Phase = "Downloading";
                    progress.Percentage = progress.TotalBytes > 0
                        ? Math.Floor(progress.CompletedBytes * 1000.0 / progress.TotalBytes.Value) / 10.0
                        : (double?)null;
                }
            }
        }

        public void ObserveLogMessage(string jsonLine)
        {
            if (!freezeTotalAfterChecks || !RcloneLogEntry.TryParse(jsonLine, out var entry) ||
                string.IsNullOrEmpty(entry.Message) || !string.Equals(entry.Level, "DEBUG", StringComparison.OrdinalIgnoreCase) ||
                entry.Message.IndexOf("Waiting for transfers to finish", StringComparison.Ordinal) < 0) return;
            lock (gate)
            {
                checksFinished = true;
            }
        }

        public void Finish(string phase)
        {
            if (phase != "Finishing" && phase != "Complete" && phase != "Failed" && phase != "Cancelled")
                throw new ArgumentException("Invalid transfer phase.", nameof(phase));
            lock (gate)
            {
                progress.Phase = phase;
                progress.BytesPerSecond = 0;
                if (phase == "Failed" || phase == "Cancelled") progress.Percentage = null;
                if (phase == "Complete")
                {
                    if (!progress.TotalBytes.HasValue) progress.TotalBytes = progress.CompletedBytes;
                    progress.CompletedBytes = progress.TotalBytes.Value;
                    progress.Percentage = 100;
                }
            }
        }

        private static bool IsFiniteNonNegative(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;
    }

    [DataContract]
    internal sealed class RcloneLogEntry
    {
        [DataMember(Name = "stats")] public RcloneStats Stats { get; set; }
        [DataMember(Name = "level")] public string Level { get; set; }
        [DataMember(Name = "msg")] public string Message { get; set; }

        internal static bool TryParse(string line, out RcloneLogEntry entry)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(line) || line.Length > 65536) return false;
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(RcloneLogEntry));
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(line)))
                    entry = serializer.ReadObject(stream) as RcloneLogEntry;
                return entry != null;
            }
            catch { return false; }
        }
    }

    [DataContract]
    internal sealed class RcloneStats
    {
        [DataMember(Name = "bytes")] public long Bytes { get; set; }
        [DataMember(Name = "totalBytes")] public long? TotalBytes { get; set; }
        [DataMember(Name = "speed")] public double Speed { get; set; }
        [DataMember(Name = "transfers")] public int Transfers { get; set; }

        internal static bool TryParse(string line, out RcloneStats stats)
        {
            stats = null;
            if (string.IsNullOrWhiteSpace(line) || line.Length > 65536) return false;
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(RcloneLogEntry));
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(line)))
                {
                    var entry = serializer.ReadObject(stream) as RcloneLogEntry;
                    if (entry == null || entry.Stats == null || entry.Stats.Bytes < 0 ||
                        (entry.Stats.TotalBytes.HasValue && entry.Stats.TotalBytes.Value < 0)) return false;
                    stats = entry.Stats;
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
