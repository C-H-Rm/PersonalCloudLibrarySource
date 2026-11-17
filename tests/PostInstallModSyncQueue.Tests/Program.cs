using System;
using System.Threading;
using System.Threading.Tasks;
using PersonalCloudLibrarySource;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL " + description);
        checks++;
        Console.WriteLine("PASS " + description);
    }

    private static async Task Main()
    {
        await RegisterAndStart();
        await DeferPending();
        await DeferRunningAndResume();
        await FaultCleanupAndReplacement();
        await IndependentGames();
        await CancelAll();
        Console.WriteLine("RESULT post-install mod sync queue: " + checks + " passed");
    }

    private static async Task RegisterAndStart()
    {
        var queue = new PostInstallModSyncQueue();
        var id = Guid.NewGuid();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ModSyncResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callerThread = Thread.CurrentThread.ManagedThreadId;
        var ranOnThread = callerThread;
        Check(queue.Register(id, _ => { ranOnThread = Thread.CurrentThread.ManagedThreadId; entered.TrySetResult(true); return release.Task; }), "register accepts pending work");
        Check(!queue.Register(id, _ => Task.FromResult(new ModSyncResult())), "duplicate pending registration is rejected");
        Check(queue.Contains(id) && !entered.Task.IsCompleted, "register records work but does not run it");
        var before = DateTime.UtcNow;
        var first = queue.Start(id);
        Check((DateTime.UtcNow - before) < TimeSpan.FromSeconds(1), "start returns without waiting for blocked worker");
        await entered.Task;
        Check(ranOnThread != callerThread, "worker starts on a thread-pool thread");
        Check(ReferenceEquals(first, queue.Start(id)), "repeated start returns the exact running task");
        Check(!queue.Register(id, _ => Task.FromResult(new ModSyncResult())), "duplicate running registration is rejected");
        var result = new ModSyncResult { Success = true };
        release.SetResult(result);
        Check(ReferenceEquals(result, await first), "worker result is returned");
        Check(!queue.Contains(id), "successful completion removes queue entry");
        Check(queue.Start(id) == null, "missing work cannot start");
    }

    private static async Task DeferPending()
    {
        var queue = new PostInstallModSyncQueue();
        var id = Guid.NewGuid();
        var invoked = false;
        queue.Register(id, _ => { invoked = true; return Task.FromResult(new ModSyncResult()); });
        Check(queue.DeferForLaunch(id) == null, "deferring pending work returns null");
        Check(queue.Contains(id) && queue.Start(id) == null && !invoked, "pending deferral retains work without autostart");
        var resumed = queue.Start(id, resume: true);
        await resumed;
        Check(invoked && !queue.Contains(id), "explicit resume starts deferred pending work");
    }

    private static async Task DeferRunningAndResume()
    {
        var queue = new PostInstallModSyncQueue();
        var id = Guid.NewGuid();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCanceled = new TaskCompletionSource<ModSyncResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        queue.Register(id, async token =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult(true);
            using (token.Register(() => canceled.TrySetResult(true))) { return await releaseCanceled.Task.ConfigureAwait(false); }
        });
        var running = queue.Start(id);
        await started.Task;
        Check(ReferenceEquals(running, queue.DeferForLaunch(id)), "running launch deferral returns its worker task");
        await canceled.Task;
        Check(queue.Contains(id) && queue.Start(id) == running, "canceled worker remains deferred and resume does not overlap it");
        releaseCanceled.SetResult(new ModSyncResult { Cancelled = true });
        await running;
        Check(queue.Contains(id) && queue.Start(id) == null, "deferred completion remains pending and does not autostart");
        var resumed = queue.Start(id, resume: true);
        await resumed;
        Check(calls == 2 && !queue.Contains(id), "resume runs exactly once after prior worker completion");

        var staleQueue = new PostInstallModSyncQueue();
        var staleId = Guid.NewGuid();
        var finishOld = new TaskCompletionSource<ModSyncResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var generation = 0;
        staleQueue.Register(staleId, _ => Interlocked.Increment(ref generation) == 1 ? finishOld.Task : Task.FromResult(new ModSyncResult { Success = true }));
        var oldTask = staleQueue.Start(staleId);
        Check(ReferenceEquals(oldTask, staleQueue.DeferForLaunch(staleId)), "stale generation is deferred before replacement");
        finishOld.SetResult(new ModSyncResult { Cancelled = true });
        await oldTask;
        var newTask = staleQueue.Start(staleId, resume: true);
        await newTask;
        Check(generation == 2 && !staleQueue.Contains(staleId), "late completion cannot erase the resumed generation");
    }

    private static async Task FaultCleanupAndReplacement()
    {
        var queue = new PostInstallModSyncQueue();
        var id = Guid.NewGuid();
        var expected = new InvalidOperationException("expected test failure");
        queue.Register(id, _ => Task.FromException<ModSyncResult>(expected));
        var failed = queue.Start(id);
        Exception observed = null;
        try { await failed; } catch (Exception ex) { observed = ex; }
        Check(ReferenceEquals(expected, observed), "worker faults propagate to parent observer");
        Check(!queue.Contains(id), "faulted completion removes entry");
        Check(queue.Register(id, _ => Task.FromResult(new ModSyncResult { Success = true })), "fault cleanup permits replacement registration");
        await queue.Start(id);
        Check(!queue.Contains(id), "replacement completion removes only its own entry");
    }

    private static async Task IndependentGames()
    {
        var queue = new PostInstallModSyncQueue();
        var one = Guid.NewGuid(); var two = Guid.NewGuid();
        var releaseOne = new TaskCompletionSource<ModSyncResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTwo = new TaskCompletionSource<ModSyncResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Register(one, _ => releaseOne.Task);
        queue.Register(two, _ => releaseTwo.Task);
        var taskOne = queue.Start(one); var taskTwo = queue.Start(two);
        Check(!ReferenceEquals(taskOne, taskTwo) && queue.Contains(one) && queue.Contains(two), "different games have independent workers");
        releaseOne.SetResult(new ModSyncResult()); releaseTwo.SetResult(new ModSyncResult());
        await Task.WhenAll(taskOne, taskTwo);
        Check(!queue.Contains(one) && !queue.Contains(two), "independent completions clean both games");
    }

    private static async Task CancelAll()
    {
        var queue = new PostInstallModSyncQueue();
        var id = Guid.NewGuid();
        var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Register(id, async token =>
        {
            using (token.Register(() => canceled.TrySetResult(true))) await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return new ModSyncResult();
        });
        var task = queue.Start(id);
        queue.CancelAll();
        await canceled.Task;
        Exception observed = null;
        try { await task; } catch (OperationCanceledException ex) { observed = ex; }
        Check(observed is OperationCanceledException, "shutdown cancellation is observed through worker task");
        Check(!queue.Contains(id) && !queue.Register(id, _ => Task.FromResult(new ModSyncResult())), "shutdown clears entries and rejects reuse");
        Check(queue.Start(id) == null, "shutdown queue cannot start new work");
    }
}
