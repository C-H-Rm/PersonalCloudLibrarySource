using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PersonalCloudLibrarySource
{
    /// <summary>Coordinates deferred, one-at-a-time post-install mod work per game.</summary>
    public sealed class PostInstallModSyncQueue
    {
        private sealed class Entry
        {
            public readonly Func<CancellationToken, Task<ModSyncResult>> Work;
            public readonly object CancellationGate = new object();
            public bool Deferred;
            public Task<ModSyncResult> Worker;
            public CancellationTokenSource Cancellation;
            public bool CancellationDisposed;

            public Entry(Func<CancellationToken, Task<ModSyncResult>> work) { Work = work; }
        }

        private readonly object gate = new object();
        private readonly Dictionary<Guid, Entry> entries = new Dictionary<Guid, Entry>();
        private bool shutDown;

        /// <summary>Records work without executing it. Returns false for duplicate IDs or after shutdown.</summary>
        public bool Register(Guid gameId, Func<CancellationToken, Task<ModSyncResult>> work)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            lock (gate)
            {
                if (shutDown || entries.ContainsKey(gameId)) return false;
                entries.Add(gameId, new Entry(work));
                return true;
            }
        }

        public bool Contains(Guid gameId)
        {
            lock (gate) return entries.ContainsKey(gameId);
        }

        /// <summary>Starts pending work on the thread pool, or returns its existing task.</summary>
        public Task<ModSyncResult> Start(Guid gameId, bool resume = false)
        {
            lock (gate)
            {
                Entry entry;
                if (shutDown || !entries.TryGetValue(gameId, out entry)) return null;
                if (entry.Worker != null) return entry.Worker;
                if (entry.Deferred && !resume) return null;
                if (resume) entry.Deferred = false;

                entry.Cancellation = new CancellationTokenSource();
                entry.CancellationDisposed = false;
                entry.Worker = Task.Run(() => RunWorker(gameId, entry));
                return entry.Worker;
            }
        }

        /// <summary>Defers work until an explicit resume after launch; cancellation runs outside the queue lock.</summary>
        public Task<ModSyncResult> DeferForLaunch(Guid gameId)
        {
            Entry entry;
            Task<ModSyncResult> worker;
            CancellationTokenSource cancellation;
            lock (gate)
            {
                if (!entries.TryGetValue(gameId, out entry)) return null;
                entry.Deferred = true;
                worker = entry.Worker;
                cancellation = entry.Cancellation;
            }
            if (cancellation != null) CancelSafely(entry, cancellation);
            return worker;
        }

        /// <summary>Stops accepting work and cancels every active worker without holding the queue lock.</summary>
        public void CancelAll()
        {
            var active = new List<Tuple<Entry, CancellationTokenSource>>();
            lock (gate)
            {
                if (shutDown) return;
                shutDown = true;
                foreach (var entry in entries.Values)
                    if (entry.Cancellation != null) active.Add(Tuple.Create(entry, entry.Cancellation));
                entries.Clear();
            }
            foreach (var item in active) CancelSafely(item.Item1, item.Item2);
        }

        private async Task<ModSyncResult> RunWorker(Guid gameId, Entry entry)
        {
            try
            {
                return await entry.Work(entry.Cancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                DisposeCancellation(entry);
                lock (gate)
                {
                    Entry current;
                    if (entries.TryGetValue(gameId, out current) && ReferenceEquals(current, entry))
                    {
                        if (entry.Deferred)
                        {
                            entry.Worker = null;
                            entry.Cancellation = null;
                        }
                        else entries.Remove(gameId);
                    }
                }
            }
        }

        private static void CancelSafely(Entry entry, CancellationTokenSource cancellation)
        {
            lock (entry.CancellationGate)
            {
                if (entry.CancellationDisposed) return;
                try { cancellation.Cancel(); }
                catch (AggregateException) { /* Cancellation is best-effort even when a callback fails. */ }
                catch (ObjectDisposedException) { /* Defensive guard for provider-specific callback races. */ }
            }
        }

        private static void DisposeCancellation(Entry entry)
        {
            lock (entry.CancellationGate)
            {
                if (entry.CancellationDisposed) return;
                entry.CancellationDisposed = true;
                entry.Cancellation.Dispose();
            }
        }
    }
}
