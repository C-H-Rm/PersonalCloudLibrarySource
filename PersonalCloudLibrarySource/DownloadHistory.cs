using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PersonalCloudLibrarySource
{
    public sealed class DownloadHistoryRecord
    {
        public Guid GameId { get; internal set; }
        public Guid OperationId { get; internal set; }
        public string GameName { get; internal set; }
        public string Phase { get; internal set; }
        public string OperationKind { get; internal set; }
        public long CompletedBytes { get; internal set; }
        public long? TotalBytes { get; internal set; }
        public double BytesPerSecond { get; internal set; }
        public double? Percentage { get; internal set; }
        public DateTime StartedUtc { get; internal set; }
        public DateTime UpdatedUtc { get; internal set; }
        public bool Active { get; internal set; }
        internal long Revision { get; set; }
        internal DownloadHistoryRecord Copy() => (DownloadHistoryRecord)MemberwiseClone();
    }

    // Durable allowlisted operation history. Progress changes are coalesced to a background writer.
    public sealed class DownloadHistory : IDisposable
    {
        public const int MaximumRecords = 200;
        private readonly object gate = new object();
        private readonly InstallProgressState state;
        private readonly string path;
        private readonly List<DownloadHistoryRecord> records = new List<DownloadHistoryRecord>();
        private readonly Task writer;
        private long version;
        private long savedVersion;
        private bool stopping;
        private bool disposed;
        private bool corrupt;
        private bool persistenceDisabled;
        public bool IsCorrupt { get { lock (gate) return corrupt; } }
        public event EventHandler Changed;

        public DownloadHistory(InstallProgressState state, string path)
        {
            this.state = state ?? throw new ArgumentNullException(nameof(state));
            this.path = path ?? throw new ArgumentNullException(nameof(path));
            Load();
            state.Changed += OnChanged;
            foreach (var snapshot in state.GetAllSnapshots()) OnChanged(state, new InstallProgressChangedEventArgs(snapshot.GameId, snapshot));
            writer = Task.Run(WriterLoop);
        }

        public IList<DownloadHistoryRecord> GetSnapshot()
        {
            lock (gate) return records.Select(x => x.Copy()).ToList().AsReadOnly();
        }

        public bool Flush(TimeSpan timeout)
        {
            lock (gate)
            {
                if (persistenceDisabled) return true;
                Monitor.PulseAll(gate);
                var deadline = DateTime.UtcNow + timeout;
                while (savedVersion < version && !persistenceDisabled)
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero) return false;
                    Monitor.Wait(gate, remaining);
                }
                return true;
            }
        }

        private void Load()
        {
            if (!File.Exists(path)) return;
            try
            {
                using (var input = File.OpenRead(path))
                {
                    var serializer = new DataContractJsonSerializer(typeof(HistoryFile));
                    var data = (HistoryFile)serializer.ReadObject(input);
                    if (data?.Records == null) throw new SerializationException("History records were missing.");
                    foreach (var stored in data.Records)
                    {
                        if (stored == null || !Guid.TryParse(stored.Operation, out var operationId) || operationId == Guid.Empty) continue;
                        var item = stored.ToRecord();
                        if (item.Active) { item.Active = false; item.Phase = "Interrupted"; item.UpdatedUtc = DateTime.UtcNow; }
                        records.Add(item);
                    }
                    // Recover in-flight attempts before applying terminal-history retention.
                    // Their old last-progress time must not cause silent loss on restart.
                    records.Sort((left, right) => left.UpdatedUtc.CompareTo(right.UpdatedUtc));
                    while (records.Count > MaximumRecords) records.RemoveAt(0);
                    if (records.Any(x => x.Phase == "Interrupted") || data.Records.Count != records.Count) version++;
                    savedVersion = 0;
                }
            }
            catch
            {
                corrupt = true;
                persistenceDisabled = true; // Preserve the original bytes; never replace unrecognized user data.
            }
        }

        private void OnChanged(object sender, InstallProgressChangedEventArgs args)
        {
            var snapshot = args?.Snapshot;
            if (snapshot == null) return;
            lock (gate)
            {
                if (disposed) return;
                var existing = records.FirstOrDefault(x => x.OperationId == snapshot.OperationId);
                if (existing != null && snapshot.Revision <= existing.Revision) return;
                if (existing == null)
                {
                    existing = new DownloadHistoryRecord { GameId = snapshot.GameId, OperationId = snapshot.OperationId,
                        StartedUtc = snapshot.UpdatedUtc, OperationKind = Kind(snapshot.Phase) };
                    records.Add(existing);
                }
                existing.Revision = snapshot.Revision;
                if (!string.IsNullOrWhiteSpace(snapshot.GameName)) existing.GameName = snapshot.GameName;
                existing.Phase = SafePhase(snapshot.Phase);
                var operationKind = Kind(snapshot.Phase);
                if (operationKind == "Background mod sync" || string.IsNullOrEmpty(existing.OperationKind)) existing.OperationKind = operationKind;
                if ((snapshot.Active && snapshot.Phase != "Finishing") || snapshot.TotalBytes.HasValue || snapshot.CompletedBytes > 0)
                {
                    existing.CompletedBytes = Math.Max(0, snapshot.CompletedBytes);
                    existing.TotalBytes = snapshot.TotalBytes >= 0 ? snapshot.TotalBytes : null;
                }
                existing.Percentage = snapshot.Percentage;
                existing.BytesPerSecond = snapshot.Active ? snapshot.BytesPerSecond : 0;
                existing.UpdatedUtc = snapshot.UpdatedUtc;
                existing.Active = snapshot.Active;
                while (records.Count(x => !x.Active) > MaximumRecords)
                    records.Remove(records.First(x => !x.Active));
                version++;
                Monitor.PulseAll(gate);
            }
            NotifyChanged();
        }

        private void NotifyChanged()
        {
            var handlers = Changed;
            if (handlers == null) return;
            foreach (EventHandler handler in handlers.GetInvocationList())
                try { handler(this, EventArgs.Empty); } catch { /* A view must not disrupt transfer observation. */ }
        }

        private void WriterLoop()
        {
            while (true)
            {
                HistoryFile copy;
                long writingVersion;
                lock (gate)
                {
                    while (!stopping && (version == savedVersion || persistenceDisabled)) Monitor.Wait(gate);
                    if (stopping && (version == savedVersion || persistenceDisabled)) return;
                    // Coalesce rapid reports; terminal changes still flush promptly after this short quiet window.
                    var delay = System.Diagnostics.Stopwatch.StartNew();
                    while (!stopping)
                    {
                        var remaining = 300 - delay.ElapsedMilliseconds;
                        if (remaining <= 0) break;
                        Monitor.Wait(gate, TimeSpan.FromMilliseconds(remaining));
                    }
                    writingVersion = version;
                    copy = new HistoryFile { Records = records.Select(StoredRecord.From).ToList() };
                }
                try
                {
                    Persist(copy);
                    lock (gate) { savedVersion = Math.Max(savedVersion, writingVersion); Monitor.PulseAll(gate); }
                }
                catch
                {
                    lock (gate) { persistenceDisabled = true; Monitor.PulseAll(gate); }
                }
            }
        }

        private void Persist(HistoryFile data)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporary = path + ".tmp";
            try
            {
                using (var output = File.Create(temporary))
                    new DataContractJsonSerializer(typeof(HistoryFile)).WriteObject(output, data);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static string SafePhase(string phase)
        {
            if (string.IsNullOrWhiteSpace(phase)) return "Working";
            return new string(phase.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').Take(40).ToArray());
        }
        private static string Kind(string phase)
        {
            if (phase == "BackgroundModSync") return "Background mod sync";
            if (phase != null && phase.IndexOf("Mod", StringComparison.OrdinalIgnoreCase) >= 0) return "Mod download";
            return "Game download";
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                state.Changed -= OnChanged;
                stopping = true;
                Monitor.PulseAll(gate);
            }
            try { writer.Wait(TimeSpan.FromSeconds(10)); } catch { }
        }

        [DataContract]
        private sealed class HistoryFile { [DataMember(Name = "records")] public List<StoredRecord> Records { get; set; } = new List<StoredRecord>(); }
        [DataContract]
        private sealed class StoredRecord
        {
            [DataMember(Name="gameId")] public string Game { get; set; }
            [DataMember(Name="operationId")] public string Operation { get; set; }
            [DataMember(Name="gameName", EmitDefaultValue=false)] public string GameName { get; set; }
            [DataMember(Name="phase")] public string Phase { get; set; }
            [DataMember(Name="kind")] public string Kind { get; set; }
            [DataMember(Name="completedBytes")] public long Bytes { get; set; }
            [DataMember(Name="totalBytes", EmitDefaultValue=false)] public long? Total { get; set; }
            [DataMember(Name="bytesPerSecond")] public double Speed { get; set; }
            [DataMember(Name="percentage", EmitDefaultValue=false)] public double? Percentage { get; set; }
            [DataMember(Name="startedUtc")] public DateTime Started { get; set; }
            [DataMember(Name="updatedUtc")] public DateTime Updated { get; set; }
            [DataMember(Name="active")] public bool Active { get; set; }
            public static StoredRecord From(DownloadHistoryRecord x) => new StoredRecord { Game=x.GameId.ToString("D"), GameName=x.GameName, Operation=x.OperationId.ToString("D"), Phase=x.Phase, Kind=x.OperationKind, Bytes=x.CompletedBytes, Total=x.TotalBytes, Speed=x.BytesPerSecond, Percentage=x.Percentage, Started=x.StartedUtc, Updated=x.UpdatedUtc, Active=x.Active };
            public DownloadHistoryRecord ToRecord() => new DownloadHistoryRecord { GameId=Guid.Parse(Game), GameName=GameName, OperationId=Guid.Parse(Operation), Phase=Phase, OperationKind=Kind, CompletedBytes=Math.Max(0,Bytes), TotalBytes=Total, BytesPerSecond=Speed, Percentage=Percentage, StartedUtc=Started, UpdatedUtc=Updated, Active=Active };
        }
    }
}
