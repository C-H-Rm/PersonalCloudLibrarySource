using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace PersonalCloudLibrarySource
{
    // A point-in-time immutable view. Terminal snapshots are published even when removed from Get().
    public sealed class InstallProgressSnapshot
    {
        public Guid GameId { get; internal set; }
        public Guid OperationId { get; internal set; }
        public string GameName { get; internal set; }
        public long Revision { get; internal set; }
        public DateTime UpdatedUtc { get; internal set; }
        public string Phase { get; internal set; }
        public string Message { get; internal set; }
        public long CompletedBytes { get; internal set; }
        public long? TotalBytes { get; internal set; }
        public long? GameCompletedBytes { get; internal set; }
        public long? GameTotalBytes { get; internal set; }
        public long? ModCompletedBytes { get; internal set; }
        public long? ModTotalBytes { get; internal set; }
        public double BytesPerSecond { get; internal set; }
        public double? Percentage { get; internal set; }
        public bool Active { get; internal set; }
        public bool CanRetry { get; internal set; }
        public string RetryLabel { get; internal set; }
    }

    public sealed class InstallProgressChangedEventArgs : EventArgs
    {
        public Guid GameId { get; }
        public InstallProgressSnapshot Snapshot { get; }
        public InstallProgressChangedEventArgs(Guid gameId, InstallProgressSnapshot snapshot) { GameId = gameId; Snapshot = snapshot; }
    }

    // SDK-independent state. The UI observes it; it never owns the transfer lifetime.
    public sealed class InstallProgressState
    {
        public static InstallProgressState Shared { get; } = new InstallProgressState();
        private readonly object gate = new object();
        private readonly Dictionary<Guid, Entry> entries = new Dictionary<Guid, Entry>();
        private long revision;
        public event EventHandler<InstallProgressChangedEventArgs> Changed;

        public bool TryBegin(Guid gameId, out InstallProgressSession session, string gameName = null)
        {
            InstallProgressSnapshot snapshot;
            lock (gate)
            {
                if (entries.TryGetValue(gameId, out var prior) && prior.Session != null)
                { session = null; return false; }
                session = new InstallProgressSession(this, gameId);
                snapshot = NewSnapshot(gameId, session.OperationId, "Inventory", "Preparing download", active: true);
                snapshot.GameName = gameName;
                entries[gameId] = new Entry { Session = session, Snapshot = snapshot };
            }
            Notify(snapshot);
            return true;
        }

        public InstallProgressSnapshot Get(Guid gameId)
        {
            lock (gate) return entries.TryGetValue(gameId, out var entry) ? entry.Snapshot : null;
        }

        public IList<InstallProgressSnapshot> GetAllSnapshots()
        {
            lock (gate) return new List<InstallProgressSnapshot>(entries.Values.Select(x => x.Snapshot)).AsReadOnly();
        }

        internal void Report(InstallProgressSession session, string phase, long? bytes, long? total,
            double speed, double? percentage, string message)
        {
            InstallProgressSnapshot snapshot;
            lock (gate)
            {
                if (!entries.TryGetValue(session.GameId, out var entry) || entry.Session != session) return;
                snapshot = NewSnapshot(session.GameId, session.OperationId, phase, message, active: true);
                snapshot.GameName = entry.Snapshot.GameName;
                var phaseChanged = !string.Equals(entry.Snapshot.Phase, phase, StringComparison.Ordinal);
                snapshot.CompletedBytes = bytes.HasValue ? Math.Max(0, bytes.Value) : (phaseChanged ? 0 : entry.Snapshot.CompletedBytes);
                snapshot.TotalBytes = total.HasValue ? (total.Value >= 0 ? total : null) : (phaseChanged ? null : entry.Snapshot.TotalBytes);
                if (IsModPhase(phase))
                {
                    snapshot.GameCompletedBytes = entry.Snapshot.GameCompletedBytes;
                    snapshot.GameTotalBytes = entry.Snapshot.GameTotalBytes;
                    snapshot.ModCompletedBytes = bytes.HasValue ? snapshot.CompletedBytes : entry.Snapshot.ModCompletedBytes;
                    snapshot.ModTotalBytes = total.HasValue ? snapshot.TotalBytes : entry.Snapshot.ModTotalBytes;
                }
                else
                {
                    snapshot.GameCompletedBytes = bytes.HasValue ? snapshot.CompletedBytes : entry.Snapshot.GameCompletedBytes;
                    snapshot.GameTotalBytes = total.HasValue ? snapshot.TotalBytes : entry.Snapshot.GameTotalBytes;
                    snapshot.ModCompletedBytes = entry.Snapshot.ModCompletedBytes;
                    snapshot.ModTotalBytes = entry.Snapshot.ModTotalBytes;
                }
                snapshot.BytesPerSecond = speed >= 0 && !double.IsNaN(speed) && !double.IsInfinity(speed) ? speed : 0;
                snapshot.Percentage = percentage >= 0 && percentage <= 100 && !double.IsNaN(percentage.Value) ? percentage : null;
                entry.Snapshot = snapshot;
            }
            Notify(snapshot);
        }

        internal void Finish(InstallProgressSession session, string phase, string message, Action retry, string retryLabel)
        {
            InstallProgressSnapshot snapshot;
            lock (gate)
            {
                if (!entries.TryGetValue(session.GameId, out var entry) || entry.Session != session) return;
                snapshot = NewSnapshot(session.GameId, session.OperationId, phase, message, active: false);
                if (entry.Snapshot != null)
                {
                    snapshot.CompletedBytes = entry.Snapshot.CompletedBytes;
                    snapshot.GameName = entry.Snapshot.GameName;
                    snapshot.TotalBytes = entry.Snapshot.TotalBytes;
                    snapshot.GameCompletedBytes = entry.Snapshot.GameCompletedBytes;
                    snapshot.GameTotalBytes = entry.Snapshot.GameTotalBytes;
                    snapshot.ModCompletedBytes = entry.Snapshot.ModCompletedBytes;
                    snapshot.ModTotalBytes = entry.Snapshot.ModTotalBytes;
                    snapshot.BytesPerSecond = entry.Snapshot.BytesPerSecond;
                    snapshot.Percentage = entry.Snapshot.Percentage;
                }
                if (phase == "Complete")
                {
                    entries.Remove(session.GameId);
                    snapshot.Percentage = 100;
                }
                else
                {
                    entry.Session = null;
                    entry.Retry = retry;
                    snapshot.CanRetry = retry != null;
                    snapshot.RetryLabel = retryLabel;
                    entry.Snapshot = snapshot;
                }
            }
            Notify(snapshot);
        }

        public bool Cancel(Guid gameId, Guid operationId)
        {
            InstallProgressSession session;
            lock (gate) session = entries.TryGetValue(gameId, out var entry) ? entry.Session : null;
            return session != null && session.OperationId == operationId && session.Cancel();
        }

        public bool Retry(Guid gameId) => Retry(gameId, null);

        public bool Retry(Guid gameId, Guid? operationId)
        {
            Action retry;
            lock (gate)
            {
                if (!entries.TryGetValue(gameId, out var entry) || entry.Session != null ||
                    (operationId.HasValue && entry.Snapshot.OperationId != operationId.Value)) return false;
                retry = entry.Retry;
                entry.Retry = null;
            }
            if (retry == null) return false;
            retry();
            return true;
        }

        private InstallProgressSnapshot NewSnapshot(Guid gameId, Guid operationId, string phase, string message, bool active)
        {
            return new InstallProgressSnapshot { GameId = gameId, OperationId = operationId, Revision = Interlocked.Increment(ref revision),
                UpdatedUtc = DateTime.UtcNow, Phase = phase, Message = message, Active = active };
        }

        private static bool IsModPhase(string phase) => phase != null &&
            (phase.IndexOf("Mod", StringComparison.OrdinalIgnoreCase) >= 0 ||
             string.Equals(phase, "BackgroundModSync", StringComparison.OrdinalIgnoreCase));

        private void Notify(InstallProgressSnapshot snapshot)
        {
            var handlers = Changed;
            if (handlers == null) return;
            foreach (EventHandler<InstallProgressChangedEventArgs> handler in handlers.GetInvocationList())
            {
                try { handler(this, new InstallProgressChangedEventArgs(snapshot.GameId, snapshot)); }
                catch (Exception ex) { Trace.TraceError("Installation progress observer failed: {0}", ex); }
            }
        }

        private sealed class Entry
        {
            public InstallProgressSession Session;
            public InstallProgressSnapshot Snapshot;
            public Action Retry;
        }
    }

    public sealed class InstallProgressSession : IDisposable
    {
        private readonly InstallProgressState owner;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private int finished;
        public Guid GameId { get; }
        public Guid OperationId { get; } = Guid.NewGuid();
        public CancellationToken CancellationToken { get; }
        internal InstallProgressSession(InstallProgressState owner, Guid gameId)
        { this.owner = owner; GameId = gameId; CancellationToken = cancellation.Token; }
        public void Report(string phase, long? bytes = null, long? total = null, double speed = 0,
            double? percentage = null, string message = null)
        { if (Volatile.Read(ref finished) == 0) owner.Report(this, phase, bytes, total, speed, percentage, message); }
        public void Finish(string phase, string message = null, Action retry = null, string retryLabel = "Retry mods")
        { if (Interlocked.Exchange(ref finished, 1) == 0) owner.Finish(this, phase, message, retry, retryLabel); }
        internal bool Cancel()
        {
            if (Volatile.Read(ref finished) != 0) return false;
            try { cancellation.Cancel(); return true; }
            catch (ObjectDisposedException) { return false; }
        }
        public void Dispose()
        { Finish("Cancelled", "Installation stopped. Use Install to retry."); cancellation.Dispose(); }
    }
}
