using Playnite.SDK.Models;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PersonalCloudLibrarySource
{
    public partial class PersonalCloudLibrarySource
    {
        private Func<CancellationToken, Action<ModSyncProgress>, bool, Task<ModSyncResult>> CreateInstallModRestore(
            Game game, PersonalCloudLibraryItem item, PersonalCloudLibraryManifest manifest, PersonalCloudLibrarySourceSettings initialSettings)
        {
            if (!initialSettings.Enabled || !initialSettings.EnableModSync || string.IsNullOrWhiteSpace(item.ModPath) ||
                !string.Equals(GetProviderType(initialSettings), PersonalCloudLibrarySourceSettings.RcloneRemoteProviderType,
                    StringComparison.OrdinalIgnoreCase)) return null;

            ModSyncRequest imported = null;
            Exception mappingError = null;
            try { imported = BuildModSyncRequest(item, manifest, initialSettings); }
            catch (Exception ex) { mappingError = ex; }
            var linkedDuringThisInstall = false;

            // Legacy bool is retained for caller compatibility, not recovery consent.
            // Installation only downloads. No comparison, upload, relink or bisync here.
            return async (token, progress, unusedRecovery) =>
            {
                var currentSettings = settings.Settings;
                if (currentSettings?.Enabled == true && currentSettings.EnableModSync)
                    SetInstallModSyncPending(game.Id, true);
                var result = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    if (currentSettings == null || !currentSettings.Enabled || !currentSettings.EnableModSync) return null;
                    if (mappingError != null) throw new InvalidOperationException("Invalid optional mod configuration: " + mappingError.Message, mappingError);
                    modSyncGate.Wait(token);
                    try
                    {
                        if (game.IsRunning) throw new InvalidOperationException("Close the game before downloading mods.");
                        var current = LoadCurrentInstallModRequest(game, imported, currentSettings, token, progress);
                        return modSyncService.DownloadAfterInstall(current, request =>
                        {
                            var prefix = currentSettings.RcloneRemoteName + ":";
                            if (!request.RemoteFolder.StartsWith(prefix, StringComparison.Ordinal))
                                throw new InvalidOperationException("The current cloud mod remote changed.");
                            var copy = rcloneFileCopier.CopyMissingRemoteDirectoryToLocalPath(currentSettings,
                                request.RemoteFolder.Substring(prefix.Length), request.LocalFolder, token,
                                update => progress?.Invoke(new ModSyncProgress { Phase = "Downloading cloud mods",
                                    Bytes = update.CompletedBytes, TotalBytes = update.TotalBytes ?? 0,
                                    BytesPerSecond = update.BytesPerSecond, Percentage = update.Percentage }));
                            return new ModSyncResult { Success = copy.Succeeded, Cancelled = copy.Cancelled, Message = copy.Message };
                        });
                    }
                    finally { modSyncGate.Release(); }
                }, token);
                if (result?.Success == true)
                {
                    postInstallModSync.Register(game.Id, backgroundToken => Task.Run(() =>
                    {
                        var session = BackgroundInstallModSession(game.Id);
                        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(backgroundToken,
                            session?.CancellationToken ?? CancellationToken.None))
                        {
                            var tokenForSync = linked.Token;
                            var s = settings.Settings;
                            if (s == null || !s.Enabled || !s.EnableModSync) return (ModSyncResult)null;
                            modSyncGate.Wait(tokenForSync);
                            try
                            {
                                tokenForSync.ThrowIfCancellationRequested();
                                // The owner defers/cancels this queue before launch; OnGameStopped
                                // may still expose a stale IsRunning flag while resuming safe work.
                                var current = LoadCurrentInstallModRequest(game, imported, s, tokenForSync,
                                    update => session?.Report("BackgroundModSync", update.Bytes,
                                        update.TotalBytes > 0 ? (long?)update.TotalBytes : null, update.BytesPerSecond,
                                        message: "Background: " + ModSyncUserInteraction.ProgressText(update.Phase, 0, 0, update.Checks, update.TotalChecks, 0)));
                                // This expensive work is invoked only by the separate post-Installed queue.
                                var completed = linkedDuringThisInstall ? modSyncService.Synchronize(current, _ => false) :
                                    modSyncService.RestoreAfterInstall(current);
                                if (completed.Success) linkedDuringThisInstall = true;
                                return completed;
                            }
                            finally { modSyncGate.Release(); }
                        }
                    }, backgroundToken));
                }
                return result;
            };
        }

        private ModSyncRequest LoadCurrentInstallModRequest(Game game, ModSyncRequest imported,
            PersonalCloudLibrarySourceSettings currentSettings, CancellationToken token, Action<ModSyncProgress> progress)
        {
            if (!string.Equals(GetProviderType(currentSettings), PersonalCloudLibrarySourceSettings.RcloneRemoteProviderType,
                StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Mod downloads require the RcloneRemote provider.");
            progress?.Invoke(new ModSyncProgress { Phase = "Loading current mod configuration" });
            var current = ModSyncService.RequireCurrentMapping(imported, () =>
            {
                var live = ParseManifest(LoadManifestJson(currentSettings, token));
                var matches = live.Items.Where(i => i != null && string.Equals(i.Id, game.GameId, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count != 1 || string.IsNullOrWhiteSpace(matches[0].ModPath))
                    throw new InvalidOperationException("The live mod configuration changed. Update the library before downloading mods.");
                return BuildModSyncRequest(matches[0], live, currentSettings);
            });
            current.CancellationToken = token;
            current.Progress = progress;
            return current;
        }
    }
}
