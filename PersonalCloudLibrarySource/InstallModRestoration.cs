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

            // Legacy bool is retained for caller compatibility, not recovery consent.
            // Installation only downloads. No comparison, upload, relink or bisync here.
            return async (token, progress, unusedRecovery) =>
            {
                var currentSettings = settings.Settings;
                return await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    if (currentSettings == null || !currentSettings.Enabled || !currentSettings.EnableModSync) return null;
                    if (mappingError != null) throw new InvalidOperationException("Invalid optional mod configuration: " + mappingError.Message, mappingError);
                    modSyncGate.Wait(token);
                    try
                    {
                        if (game.IsRunning) throw new InvalidOperationException("Close the game before downloading mods.");
                        var current = LoadCurrentInstallModRequest(game, imported, currentSettings, token, progress);
                        var prefix = currentSettings.RcloneRemoteName + ":";
                        if (!current.RemoteFolder.StartsWith(prefix, StringComparison.Ordinal))
                            throw new InvalidOperationException("The current cloud mod remote changed.");
                        var remoteZip = current.RemoteFolder.Substring(prefix.Length) + ".zip";
                        InstallZipModArchive.RejectDirectoryLinks(current.LocalFolder);
                        var localParent = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(current.LocalFolder));
                        if (string.IsNullOrWhiteSpace(localParent)) throw new InvalidOperationException("Mod destination has no parent directory.");
                        System.IO.Directory.CreateDirectory(localParent);
                        var tempArchive = System.IO.Path.Combine(localParent, ".pcls-modzip-download-" + Guid.NewGuid().ToString("N") + ".zip");
                        try
                        {
                            var copy = rcloneFileCopier.CopyRemoteFileToLocalPath(currentSettings, remoteZip, tempArchive,
                                token, update => progress?.Invoke(new ModSyncProgress { Phase = "Downloading mod ZIP",
                                    Bytes = update.CompletedBytes, TotalBytes = update.TotalBytes ?? 0,
                                    BytesPerSecond = update.BytesPerSecond, Percentage = update.Percentage }));
                            if (copy.Cancelled) return new ModSyncResult { Cancelled = true, Message = "Mod ZIP download cancelled." };
                            if (!copy.Succeeded) return new ModSyncResult { Message = "Could not download optional mod ZIP " + remoteZip + ": " + copy.Message };
                            try
                            {
                                InstallZipModArchive.Install(tempArchive, current.LocalFolder, token);
                                return new ModSyncResult { Success = true, Message = "Optional mod ZIP installed." };
                            }
                            catch (OperationCanceledException) { return new ModSyncResult { Cancelled = true, Message = "Mod ZIP installation cancelled." }; }
                            catch (Exception ex) { return new ModSyncResult { Message = "Could not install optional mod ZIP " + remoteZip + ": " + ex.Message }; }
                        }
                        finally { if (System.IO.File.Exists(tempArchive)) System.IO.File.Delete(tempArchive); }
                    }
                    finally { modSyncGate.Release(); }
                }, token);
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
