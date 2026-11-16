using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PersonalCloudLibrarySource
{
    public sealed class InstallProgressSnapshot
    {
        public Guid GameId { get; internal set; }
        public Guid OperationId { get; internal set; }
        public string Phase { get; internal set; }
        public string Message { get; internal set; }
        public long CompletedBytes { get; internal set; }
        public long? TotalBytes { get; internal set; }
        public double BytesPerSecond { get; internal set; }
        public double? Percentage { get; internal set; }
        public bool Active { get; internal set; }
        public bool CanRetry { get; internal set; }
        public string RetryLabel { get; internal set; }
    }

    public sealed class InstallProgressChangedEventArgs : EventArgs
    {
        public Guid GameId { get; }
        public InstallProgressChangedEventArgs(Guid gameId) { GameId = gameId; }
    }

    // SDK-independent state. The UI observes it; it never owns the transfer lifetime.
    public sealed class InstallProgressState
    {
        public static InstallProgressState Shared { get; } = new InstallProgressState();
        private readonly object gate = new object();
        private readonly Dictionary<Guid, Entry> entries = new Dictionary<Guid, Entry>();
        public event EventHandler<InstallProgressChangedEventArgs> Changed;

        public bool TryBegin(Guid gameId, out InstallProgressSession session)
        {
            lock (gate)
            {
                if (entries.TryGetValue(gameId, out var prior) && prior.Session != null)
                { session = null; return false; }
                session = new InstallProgressSession(this, gameId);
                entries[gameId] = new Entry { Session = session, Snapshot = new InstallProgressSnapshot {
                    GameId = gameId, OperationId = session.OperationId, Phase = "Inventory", Message = "Preparing download", Active = true } };
            }
            Notify(gameId);
            return true;
        }

        public InstallProgressSnapshot Get(Guid gameId)
        {
            lock (gate) return entries.TryGetValue(gameId, out var entry) ? entry.Snapshot : null;
        }

        internal void Report(InstallProgressSession session, string phase, long bytes, long? total,
            double speed, double? percentage, string message)
        {
            lock (gate)
            {
                if (!entries.TryGetValue(session.GameId, out var entry) || entry.Session != session) return;
                entry.Snapshot = new InstallProgressSnapshot {
                    GameId = session.GameId, OperationId = session.OperationId, Phase = phase, Message = message, Active = true,
                    CompletedBytes = Math.Max(0, bytes), TotalBytes = total >= 0 ? total : null,
                    BytesPerSecond = speed >= 0 && !double.IsInfinity(speed) ? speed : 0,
                    Percentage = percentage >= 0 && percentage <= 100 ? percentage : null };
            }
            Notify(session.GameId);
        }

        internal void Finish(InstallProgressSession session, string phase, string message, Action retry, string retryLabel)
        {
            lock (gate)
            {
                if (!entries.TryGetValue(session.GameId, out var entry) || entry.Session != session) return;
                if (phase == "Complete") entries.Remove(session.GameId);
                else
                {
                    entry.Session = null;
                    entry.Retry = retry;
                    entry.Snapshot = new InstallProgressSnapshot {
                        GameId = session.GameId, OperationId = session.OperationId, Phase = phase, Message = message,
                        CanRetry = retry != null, RetryLabel = retryLabel };
                }
            }
            Notify(session.GameId);
        }

        public bool Cancel(Guid gameId, Guid operationId)
        {
            InstallProgressSession session;
            lock (gate) session = entries.TryGetValue(gameId, out var entry) ? entry.Session : null;
            return session != null && session.OperationId == operationId && session.Cancel();
        }

        public bool Retry(Guid gameId)
        {
            Action retry;
            lock (gate)
            {
                if (!entries.TryGetValue(gameId, out var entry) || entry.Session != null) return false;
                retry = entry.Retry;
                entry.Retry = null; // One invocation; the retry must acquire its own new session.
            }
            if (retry == null) return false;
            retry();
            return true;
        }

        private void Notify(Guid gameId)
        {
            var handlers = Changed;
            if (handlers == null) return;
            foreach (EventHandler<InstallProgressChangedEventArgs> handler in handlers.GetInvocationList())
            {
                try { handler(this, new InstallProgressChangedEventArgs(gameId)); }
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
        public void Report(string phase, long bytes = 0, long? total = null, double speed = 0,
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
