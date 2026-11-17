using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PersonalCloudLibrarySource;

var checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); checks++; }

var state = new InstallProgressState();
var gameA = Guid.NewGuid();
var gameB = Guid.NewGuid();
Check(state.TryBegin(gameA, out var a1), "first operation begins");
Check(state.TryBegin(gameB, out var b1), "different game runs concurrently");
InstallProgressSnapshot terminal = null;
state.Changed += (_, e) => { if (e.Snapshot.GameId == gameA) terminal = e.Snapshot; };
a1.Report("Downloading", 50, 100, 20, 50);
var progress = state.Get(gameA);
a1.Report("Downloading", 75, 100, 25, 75);
Check(progress.CompletedBytes == 50 && state.Get(gameA).CompletedBytes == 75, "event/get snapshots are immutable");
Check(state.Get(gameA).Revision > progress.Revision && state.Get(gameA).UpdatedUtc >= progress.UpdatedUtc, "snapshots carry increasing revisions and UTC timestamps");
a1.Finish("Complete");
Check(state.Get(gameA) == null && terminal != null && terminal.Phase == "Complete" && !terminal.Active, "completion is published after inline entry removal");
Check(terminal.OperationId == progress.OperationId, "terminal snapshot retains operation identity");
a1.Report("Failed", message: "late callback");
Check(terminal.Phase == "Complete", "late callbacks cannot mutate the terminal snapshot");
b1.Finish("Complete");

var directory = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "download-history-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var path = Path.Combine(directory, "history.json");
try
{
    var history = new DownloadHistory(state, path);
    Check(state.TryBegin(gameA, out var first), "history operation begins");
    first.Report("Downloading", 12, 80, 4, 15);
    first.Finish("Failed", "secret diagnostic and command should not persist");
    Check(history.Flush(TimeSpan.FromSeconds(5)), "history flush completes");
    var failed = history.GetSnapshot();
    Check(failed.Count == 1 && failed[0].OperationId == first.OperationId && failed[0].Phase == "Failed", "failure becomes a durable operation record");
    Check(failed[0].CompletedBytes == 12 && failed[0].TotalBytes == 80, "terminal record retains useful transfer counters");
    var json = File.ReadAllText(path);
    Check(!json.Contains("secret diagnostic") && !json.Contains("command"), "history excludes raw messages and command details");
    history.Dispose();

    var restartedState = new InstallProgressState();
    var restarted = new DownloadHistory(restartedState, path);
    Check(restarted.GetSnapshot().Count == 1 && restarted.GetSnapshot()[0].Phase == "Failed", "history survives restart");
    restarted.Dispose();

    var retry = new DownloadHistory(state, path + ".retry");
    Check(state.TryBegin(gameA, out var secondAttempt) && secondAttempt.OperationId != first.OperationId, "retry is a distinct operation identity");
    secondAttempt.Report("DownloadingMods", 25, 80, 8, 31.25);
    secondAttempt.Finish("Complete");
    Check(retry.Flush(TimeSpan.FromSeconds(5)) && retry.GetSnapshot().Count == 2 && retry.GetSnapshot().Select(x => x.OperationId).Distinct().Count() == 2, "retries are retained as separate history entries");
    retry.Dispose();

    var orderingPath = path + ".ordering";
    var orderingState = new InstallProgressState();
    Check(orderingState.TryBegin(Guid.NewGuid(), out var orderedOperation), "ordering fixture starts");
    var olderEntered = new ManualResetEventSlim(false);
    var releaseOlder = new ManualResetEventSlim(false);
    orderingState.Changed += (_, e) =>
    {
        if (e.Snapshot.OperationId == orderedOperation.OperationId && e.Snapshot.CompletedBytes == 1)
        { olderEntered.Set(); releaseOlder.Wait(TimeSpan.FromSeconds(3)); }
    };
    var orderingHistory = new DownloadHistory(orderingState, orderingPath);
    var olderReport = Task.Run(() => orderedOperation.Report("Downloading", 1, 100, 1, 1));
    Check(olderEntered.Wait(TimeSpan.FromSeconds(3)), "older callback pauses before history observer");
    orderedOperation.Report("Downloading", 2, 100, 2, 2);
    releaseOlder.Set();
    olderReport.Wait();
    Check(orderingHistory.GetSnapshot().Single().CompletedBytes == 2, "out-of-order callbacks cannot regress durable snapshots");
    orderingHistory.Dispose();

    var interruptedState = new InstallProgressState();
    var interruptedHistory = new DownloadHistory(interruptedState, path + ".active");
    interruptedState.TryBegin(Guid.NewGuid(), out var active);
    active.Report("DownloadingMods", 5, 20, 2, 25);
    interruptedHistory.Flush(TimeSpan.FromSeconds(5));
    interruptedHistory.Dispose();
    var afterCrash = new DownloadHistory(new InstallProgressState(), path + ".active");
    Check(afterCrash.GetSnapshot().Count == 1 && afterCrash.GetSnapshot()[0].Phase == "Interrupted", "startup marks in-flight transfer interrupted, never resumes it");
    afterCrash.Dispose();

    var capState = new InstallProgressState();
    var capHistory = new DownloadHistory(capState, path + ".cap");
    for (var i = 0; i < 205; i++) { capState.TryBegin(Guid.NewGuid(), out var op); op.Finish("Complete"); }
    Check(capHistory.Flush(TimeSpan.FromSeconds(10)) && capHistory.GetSnapshot().Count == 200, "history is bounded to the newest 200 operations");
    capHistory.Dispose();

    var corruptPath = path + ".corrupt";
    File.WriteAllText(corruptPath, "not valid history");
    var corrupt = new DownloadHistory(new InstallProgressState(), corruptPath);
    Check(corrupt.IsCorrupt && File.ReadAllText(corruptPath) == "not valid history", "corrupt history is preserved and not overwritten");
    corrupt.Dispose();

    var measuredState = new InstallProgressState();
    using (var measured = new DownloadHistory(measuredState, path + ".measured"))
    {
        measuredState.TryBegin(Guid.NewGuid(), out var measuredOperation, "Retained game title");
        measuredOperation.Report("Downloading", 100, 100, 5, 99.9);
        Check(measured.GetSnapshot().Single().Percentage == 99.9, "history uses authoritative percentage, not byte-ratio completion");
        measuredOperation.Report("Retrying", 1, 100, 5);
        Check(measured.GetSnapshot().Single().Percentage == null, "retry progress stays indeterminate despite known byte counters");
        measuredOperation.Report("Downloading", 100, 100, 5, 99.9);
        measuredOperation.Report("Finishing");
        Check(measured.GetSnapshot().Single().CompletedBytes == 100, "finishing phase does not erase transfer counters");
        measuredOperation.Finish("Complete");
        Check(measured.GetSnapshot().Single().Percentage == 100, "successful terminal completion authorizes 100 percent");
        Check(measured.GetSnapshot().Single().CompletedBytes == 100, "completion retains the last measured transfer count");
        Check(measured.Flush(TimeSpan.FromSeconds(5)), "named operation is persisted");
        using (var savedName = new DownloadHistory(new InstallProgressState(), path + ".measured"))
            Check(savedName.GetSnapshot().Single().GameName == "Retained game title", "history retains the game title without a database lookup after restart");
    }

    var activeCapState = new InstallProgressState();
    using (var activeCap = new DownloadHistory(activeCapState, path + ".active-cap"))
    {
        activeCapState.TryBegin(Guid.NewGuid(), out var oldestActive);
        Thread.Sleep(30); // JSON timestamps have millisecond precision; make this operation unambiguously older.
        for (var i = 0; i < 205; i++) { activeCapState.TryBegin(Guid.NewGuid(), out var done); done.Finish("Complete"); }
        Check(activeCap.GetSnapshot().Any(x => x.OperationId == oldestActive.OperationId && x.Active), "history retention never evicts a live download");
        Check(activeCap.GetSnapshot().Count(x => !x.Active) == DownloadHistory.MaximumRecords, "only terminal history is capped");
        Check(activeCap.Flush(TimeSpan.FromSeconds(5)), "live and terminal history are persisted together");
        using (var recoveredCap = new DownloadHistory(new InstallProgressState(), path + ".active-cap"))
        {
            Check(recoveredCap.GetSnapshot().Any(x => x.OperationId == oldestActive.OperationId && x.Phase == "Interrupted"), "restart preserves an old live record as interrupted even after 200 newer completions");
            Check(recoveredCap.GetSnapshot().Count <= DownloadHistory.MaximumRecords, "startup terminal retention remains bounded after interruption recovery");
        }
        oldestActive.Finish("Complete");
    }

    var concurrentState = new InstallProgressState();
    var concurrentHistory = new DownloadHistory(concurrentState, path + ".concurrent");
    Parallel.For(0, 24, i => { concurrentState.TryBegin(Guid.NewGuid(), out var op); op.Report("Downloading", i, 24, 1, 1); op.Finish("Complete"); });
    Check(concurrentHistory.Flush(TimeSpan.FromSeconds(10)) && concurrentHistory.GetSnapshot().Count == 24, "concurrent game operations remain distinct");
    concurrentHistory.Dispose();
}
finally { try { Directory.Delete(directory, true); } catch { } }
Console.WriteLine("RESULT download center: " + checks + " passed");
