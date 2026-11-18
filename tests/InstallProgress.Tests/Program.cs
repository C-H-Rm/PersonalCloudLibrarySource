using System;
using System.Threading.Tasks;
using PersonalCloudLibrarySource;

var count = 0;
void Check(bool condition, string description) {
    if (!condition) throw new Exception(description);
    Console.WriteLine("PASS " + description); count++;
}
var state = new InstallProgressState();
var game = Guid.NewGuid();
Check(state.TryBegin(game, out var first), "one session starts per game");
Check(!state.TryBegin(game, out _), "duplicate installation cannot replace active progress");
Check(state.Get(game).Percentage == null && state.Get(game).TotalBytes == null, "inventory does not invent a total or percentage");
var other = Guid.NewGuid();
Check(state.TryBegin(other, out var concurrent), "different games have independent progress");
var observedOutsideLock = false;
state.Changed += (_, e) => observedOutsideLock = Task.Run(() => state.Get(e.GameId)).Wait(TimeSpan.FromSeconds(1));
state.Changed += (_, __) => throw new Exception("explicit observer fixture failure");
first.Report("Downloading", 10, 100, 20, 10);
Check(observedOutsideLock, "UI notification is outside the state lock");
Check(state.Get(game).CompletedBytes == 10 && state.Get(other).CompletedBytes == 0, "a rendering observer failure cannot abort or cross-wire transfers");
var phaseGame = Guid.NewGuid();
Check(state.TryBegin(phaseGame, out var phaseOperation), "phase-accounting operation starts independently");
phaseOperation.Report("Downloading", 40, 40, 10, 100);
Check(state.Get(phaseGame).GameCompletedBytes == 40 && state.Get(phaseGame).GameTotalBytes == 40, "upstream one-file total is retained for game download");
phaseOperation.Report("DownloadingMods", 3, 12, 2, 25);
var modProgress = state.Get(phaseGame);
Check(modProgress.GameCompletedBytes == 40 && modProgress.GameTotalBytes == 40 && modProgress.ModCompletedBytes == 3 && modProgress.ModTotalBytes == 12,
    "mod progress keeps its own counters without replacing game totals");
Check(modProgress.CompletedBytes == 3 && modProgress.TotalBytes == 12 && modProgress.Percentage == 25,
    "active phase percentage uses only the current phase denominator");
phaseOperation.Report("DownloadingMods", 12, 12, 2, 100);
phaseOperation.Finish("Failed");
Check(state.Get(phaseGame).GameCompletedBytes == 40 && state.Get(phaseGame).ModCompletedBytes == 12,
    "failed terminal state retains both phase counters");
phaseOperation.Dispose();
var prior = state.Get(game);
first.Report("Downloading", 20, 100, 20, 20);
Check(prior.CompletedBytes == 10 && state.Get(game).CompletedBytes == 20, "published snapshots are not mutated by later updates");
first.Finish("Complete");
Check(state.Get(game) == null, "completed installation removes its shared progress state");
Check(state.TryBegin(game, out var second), "a later install can start normally");
first.Report("Failed", message: "stale callback");
Check(state.Get(game).OperationId == second.OperationId && state.Get(game).Phase == "Inventory", "late callback cannot overwrite a new installation");
Check(!state.Cancel(game, first.OperationId) && !second.CancellationToken.IsCancellationRequested, "delayed cancellation cannot cancel a newer operation");
Check(state.Cancel(game, second.OperationId) && second.CancellationToken.IsCancellationRequested, "cancellation targets the exact active session");
second.Finish("Failed", "Mods need retry", () => state.TryBegin(game, out _));
Check(state.Get(game).CanRetry, "mod-only failure can offer a separate retry action");
Check(!state.Retry(game, first.OperationId) && state.Get(game).OperationId == second.OperationId,
    "a historical row cannot retry a newer failure");
Check(state.Retry(game, second.OperationId) && !state.Retry(game, second.OperationId), "retry acquires a fresh session and cannot run twice");
concurrent.Report("Downloading", 0, null, double.NaN, double.NaN);
Check(state.Get(other).Percentage == null && state.Get(other).BytesPerSecond == 0, "invalid telemetry is not rendered as a fake percentage or speed");
concurrent.Dispose();
Check(state.TryBegin(other, out var afterDispose), "closing an operation does not permanently lock the game");
afterDispose.Finish("Complete"); afterDispose.Dispose(); first.Dispose(); second.Dispose();
Console.WriteLine("RESULT install progress state: " + count + " passed");
