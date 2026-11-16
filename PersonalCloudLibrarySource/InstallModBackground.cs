using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PersonalCloudLibrarySource
{
    public partial class PersonalCloudLibrarySource
    {
        private readonly PostInstallModSyncQueue postInstallModSync = new PostInstallModSyncQueue();
        private readonly Dictionary<Guid, InstallProgressSession> backgroundModSessions = new Dictionary<Guid, InstallProgressSession>();
        private readonly object backgroundModSessionGate = new object();
        // Retain failed/download-only setup independently of the transient worker entry.
        // Launch must not turn a failed background setup into foreground initialization.
        private readonly HashSet<Guid> pendingInstallModSync = new HashSet<Guid>();

        private void SetInstallModSyncPending(Guid id, bool pending)
        {
            lock (backgroundModSessionGate)
            {
                if (pending) pendingInstallModSync.Add(id);
                else pendingInstallModSync.Remove(id);
            }
        }

        private bool HasPendingInstallModSync(Guid id)
        {
            lock (backgroundModSessionGate) return pendingInstallModSync.Contains(id);
        }

        private InstallProgressSession BackgroundInstallModSession(Guid id)
        {
            lock (backgroundModSessionGate) return backgroundModSessions.TryGetValue(id, out var value) ? value : null;
        }

        private async void StartInstallBackgroundModSync(Game game, bool resume = false)
        {
            if (game == null || !postInstallModSync.Contains(game.Id)) return;
            // Let a prior cancelled observer finish and the host clear its stopped-game
            // flag before taking a new progress session. A newer launch must win.
            if (resume) await Task.Yield();
            if (game.IsRunning)
            {
                var stopping = postInstallModSync.DeferForLaunch(game.Id);
                if (stopping != null)
                    try { await stopping; }
                    catch (Exception ex) { logger.Warn(ex, "Background mods deferred while the game runs."); }
                return;
            }
            if (!InstallProgressState.Shared.TryBegin(game.Id, out var operation)) return;
            lock (backgroundModSessionGate) backgroundModSessions[game.Id] = operation;
            var notificationId = "pcls-mod-background-" + game.Id;
            try
            {
                operation.Report("BackgroundModSync", message: "Game installed — synchronizing mods in the background");
                var work = postInstallModSync.Start(game.Id, resume);
                if (work == null) { operation.Finish("Complete"); return; }
                try
                {
                    playniteApi.Notifications.Add(new NotificationMessage(notificationId,
                        game.Name + ": mods synchronize in the background. Click to cancel.", NotificationType.Info,
                        () => CancelBackgroundInstallMods(game.Id, operation)));
                }
                catch (Exception ex) { logger.Warn(ex, "Could not show background mod status; synchronization continues."); }
                var result = await work;
                if (result == null || result.Success && !result.HasConflicts)
                {
                    if (result?.Success == true) SetInstallModSyncPending(game.Id, false);
                    operation.Finish("Complete");
                }
                else
                {
                    operation.Finish(result.Cancelled ? "Cancelled" : "Failed", "Game installed. Background mods: " + result.Message,
                        () => RunModSync(game, false, true, false), "Review mod sync");
                    if (!result.Cancelled) NotifyBackgroundModFailure(game, result);
                }
            }
            catch (OperationCanceledException)
            {
                operation.Finish("Cancelled", "Game installed. Background mod synchronization stopped.");
            }
            catch (Exception ex)
            {
                operation.Finish("Failed", "Game installed. Background mod sync needs attention: " + ex.Message,
                    () => RunModSync(game, false, true, false), "Review mod sync");
                logger.Warn(ex, "Post-install background mod synchronization stopped.");
                NotifyBackgroundModFailure(game, new ModSyncResult { Message = "Background mod sync needs attention: " + ex.Message });
            }
            finally
            {
                lock (backgroundModSessionGate)
                    if (backgroundModSessions.TryGetValue(game.Id, out var current) && current == operation)
                        backgroundModSessions.Remove(game.Id);
                try { playniteApi.Notifications.Remove(notificationId); }
                catch (Exception ex) { logger.Warn(ex, "Could not remove background mod status."); }
                finally { operation.Dispose(); }
            }
        }

        private void NotifyBackgroundModFailure(Game game, ModSyncResult result)
        {
            try { NotifyModSync(game, result); }
            catch (Exception ex) { logger.Warn(ex, "Could not display background mod failure; diagnostics are retained."); }
        }

        private static async void CancelBackgroundInstallMods(Guid gameId, InstallProgressSession operation)
        {
            try { await Task.Run(() => InstallProgressState.Shared.Cancel(gameId, operation.OperationId)); }
            catch (Exception ex) { logger.Warn(ex, "Could not cancel background mod synchronization."); }
        }

        private bool DeferInstallBackgroundModsForLaunch(Game game, out bool stopped)
        {
            stopped = true;
            if (game == null) return false;
            if (!postInstallModSync.Contains(game.Id)) return HasPendingInstallModSync(game.Id);
            // Worker cancellation never needs UI dispatch. Its notifications occur only
            // after completion, so this bounded launch barrier cannot deadlock on a dialog.
            var stop = Task.Run(() =>
            {
                var running = postInstallModSync.DeferForLaunch(game.Id);
                if (running == null) return;
                try { running.GetAwaiter().GetResult(); }
                catch { /* Work is stopped; the background observer reports actual errors. */ }
            });
            try { stopped = stop.Wait(TimeSpan.FromSeconds(7)); }
            catch (Exception ex) { logger.Warn(ex, "Could not defer post-install mod sync before game launch."); stopped = false; }
            return true;
        }
    }
}
