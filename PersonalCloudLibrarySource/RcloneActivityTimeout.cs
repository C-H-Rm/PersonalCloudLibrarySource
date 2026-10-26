using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;

namespace PersonalCloudLibrarySource
{
    internal enum RcloneTimeoutKind { None, Connect, Inactivity }

    // Adapted from upstream's activity timeout, using monotonic elapsed time and
    // real stats changes: repeated heartbeat/error lines must not hide a stall.
    internal sealed class RcloneActivityTimeout
    {
        private readonly object gate = new object();
        private readonly TimeSpan connectTimeout;
        private readonly TimeSpan inactivityTimeout;
        private TimeSpan lastActivity;
        private Stats previous;

        public RcloneActivityTimeout(TimeSpan connectTimeout, TimeSpan inactivityTimeout)
        {
            if (connectTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(connectTimeout));
            if (inactivityTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(inactivityTimeout));
            this.connectTimeout = connectTimeout;
            this.inactivityTimeout = inactivityTimeout;
        }

        public void ObserveLine(string line, TimeSpan elapsed)
        {
            if (string.IsNullOrWhiteSpace(line) || line.Length > 65536) return;
            LogEntry entry;
            try
            {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(line)))
                    entry = (LogEntry)new DataContractJsonSerializer(typeof(LogEntry)).ReadObject(stream);
            }
            catch (SerializationException) { return; }
            catch (XmlException) { return; }
            if (entry?.Statistics == null) return;
            var stats = entry.Statistics;
            lock (gate)
            {
                if (previous == null || stats.Bytes > previous.Bytes || stats.Checks > previous.Checks ||
                    stats.Transfers > previous.Transfers || stats.TotalChecks > previous.TotalChecks ||
                    stats.TotalTransfers > previous.TotalTransfers || stats.Listed > previous.Listed)
                    if (elapsed > lastActivity) lastActivity = elapsed;
                previous = stats;
            }
        }

        public RcloneTimeoutKind GetExpiredKind(TimeSpan elapsed)
        {
            lock (gate)
            {
                if (previous == null) return elapsed >= connectTimeout ? RcloneTimeoutKind.Connect : RcloneTimeoutKind.None;
                return elapsed - lastActivity >= inactivityTimeout ? RcloneTimeoutKind.Inactivity : RcloneTimeoutKind.None;
            }
        }

        [DataContract]
        private sealed class LogEntry
        {
            [DataMember(Name = "stats")] public Stats Statistics { get; set; }
        }
        [DataContract]
        private sealed class Stats
        {
            [DataMember(Name = "bytes")] public long Bytes { get; set; }
            [DataMember(Name = "checks")] public long Checks { get; set; }
            [DataMember(Name = "transfers")] public long Transfers { get; set; }
            [DataMember(Name = "totalChecks")] public long TotalChecks { get; set; }
            [DataMember(Name = "totalTransfers")] public long TotalTransfers { get; set; }
            [DataMember(Name = "listed")] public long Listed { get; set; }
        }
    }
}
