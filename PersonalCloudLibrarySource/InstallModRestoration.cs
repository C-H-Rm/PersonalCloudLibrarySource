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

            // Freeze the approved import identity BEFORE copying the game. Invalid optional
            // configuration must be reported after base install, not prevent installing it.
            ModSyncRequest imported = null;
            Exception mappingError = null;
            try { imported = BuildModSyncRequest(item, manifest, initialSettings); }
            catch (Exception ex) { mappingError = ex; }
            var linkedDuringThisInstall = false;

            return async (token, progress, explicitRecovery) =>
            {
                var interaction = new ModSyncUserInteraction(explicitRecovery);
                while (true)
                {
                    interaction.BeginAttempt();
                    var currentSettings = settings.Settings;
                    var result = await Task.Run(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        if (currentSettings == null || !currentSettings.Enabled || !currentSettings.EnableModSync) return null;
                        if (mappingError != null) throw new InvalidOperationException("Invalid optional mod configuration: " + mappingError.Message, mappingError);
                        if (!string.Equals(GetProviderType(currentSettings), PersonalCloudLibrarySourceSettings.RcloneRemoteProviderType,
                            StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Mod restoration requires the RcloneRemote provider.");
                        progress?.Invoke(new ModSyncProgress { Phase = "Waiting for other mod operations" });
                        modSyncGate.Wait(token);
                        try
                        {
                            if (game.IsRunning) throw new InvalidOperationException("Close the game before restoring mods.");
                            progress?.Invoke(new ModSyncProgress { Phase = "Loading current mod configuration" });
                            var current = ModSyncService.RequireCurrentMapping(imported, () =>
                            {
                                var live = ParseManifest(LoadManifestJson(currentSettings, token));
                                var matches = live.Items.Where(i => i != null && string.Equals(i.Id, game.GameId, StringComparison.OrdinalIgnoreCase)).ToList();
                                if (matches.Count != 1 || string.IsNullOrWhiteSpace(matches[0].ModPath))
                                    throw new InvalidOperationException("The live mod configuration changed. Update the library before restoring mods.");
                                return BuildModSyncRequest(matches[0], live, currentSettings);
                            });
                            current.CancellationToken = token;
                            current.Progress = progress;
                            // Recover only starts the recovery flow. Actual questions must still
                            // be approved on UI, outside this worker and its operation lock.
                            var completed = explicitRecovery ? modSyncService.Relink(current, interaction.Confirm) :
                                linkedDuringThisInstall ? modSyncService.Synchronize(current, _ => false) :
                                modSyncService.RestoreAfterInstall(current);
                            if (completed.Success) linkedDuringThisInstall = true;
                            return completed;
                        }
                        finally { modSyncGate.Release(); }
                    }, token);
                    if (result == null) return null; // Mod sync was explicitly disabled.
                    if (token.IsCancellationRequested && !result.Success)
                    {
                        result.Cancelled = true;
                        result.RequiresRelink |= explicitRecovery;
                        result.Message = "Mod restoration cancelled; the game remains installed.";
                    }
                    else if (explicitRecovery && interaction.ShouldRetry(result.RequiresRelink, true, ConfirmModSync)) continue;
                    if (interaction.Declined) result = new ModSyncResult { Cancelled = true, RequiresRelink = true,
                        Message = "Mod recovery cancelled. The game remains installed; local/cloud mods were retained." };
                    if (result.RequiresRelink)
                        result.Message += "\nLocal mods: " + imported.LocalFolder + "\nCloud mods: " + imported.RemoteFolder;
                    return result;
                }
            };
        }
    }
}
