using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PersonalCloudLibrarySource
{
    public class RcloneInstallController : InstallController
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly PersonalCloudLibraryItem item;
        private readonly PersonalCloudLibrarySourceSettings settings;
        private readonly RcloneFileCopier rcloneFileCopier;
        private readonly LocalFileCopier localFileCopier;
        private readonly Func<CancellationToken, Action<ModSyncProgress>, bool, Task<ModSyncResult>> restoreMods;
        private readonly Action startBackgroundMods;

        public RcloneInstallController(Game game, PersonalCloudLibraryItem item,
            PersonalCloudLibrarySourceSettings settings, RcloneFileCopier rcloneFileCopier,
            LocalFileCopier localFileCopier,
            Func<CancellationToken, Action<ModSyncProgress>, bool, Task<ModSyncResult>> restoreMods = null,
            Action startBackgroundMods = null) : base(game)
        {
            this.item = item; this.settings = settings;
            this.rcloneFileCopier = rcloneFileCopier; this.localFileCopier = localFileCopier;
            this.restoreMods = restoreMods;
            this.startBackgroundMods = startBackgroundMods;
            Name = "Download to local cache";
        }

        public override async void Install(InstallActionArgs args)
        {
            if (!InstallProgressState.Shared.TryBegin(Game.Id, out var operation, Game.Name)) return;
            var installed = false;
            var modsDownloaded = false;
            try
            {
                Game.IsInstalling = true;
                var launchPath = PersonalCloudLibrarySource.ResolveLaunchPath(item, settings);
                var installDirectory = PersonalCloudLibrarySource.ResolveInstallDirectory(item, settings, launchPath);
                var destinationFolderPath = PersonalCloudLibrarySource.ResolveDownloadDestinationFolder(item, settings, launchPath);
                var sourcePath = PersonalCloudLibrarySource.GetItemSourcePath(item);
                var providerType = PersonalCloudLibrarySource.GetProviderType(settings);
                logger.Info($"Personal Cloud Library Source downloading item {item.Id} to {destinationFolderPath}.");

                // Transfer callbacks only publish SDK-independent snapshots, never touch Game/UI.
                await Task.Run(() =>
                {
                    bool succeeded;
                    string message;
                    Exception exception;
                    if (string.Equals(providerType, PersonalCloudLibrarySourceSettings.RcloneRemoteProviderType, StringComparison.OrdinalIgnoreCase))
                    {
                        var remote = PersonalCloudLibrarySource.ResolveRcloneSourcePath(settings, sourcePath);
                        var result = rcloneFileCopier.CopyRemoteDirectoryToLocalPath(settings, remote, destinationFolderPath,
                            operation.CancellationToken, progress => operation.Report(progress.Phase == "Complete" ? "Finishing" : progress.Phase,
                                progress.CompletedBytes, progress.TotalBytes, progress.BytesPerSecond, progress.Percentage));
                        if (result.Cancelled) throw new OperationCanceledException(operation.CancellationToken);
                        succeeded = result.Succeeded; message = result.Message; exception = result.Exception;
                    }
                    else
                    {
                        operation.Report("Downloading", message: "Copying local game files");
                        var source = PersonalCloudLibrarySource.ResolveLocalFolderSourcePath(settings, sourcePath);
                        var result = localFileCopier.CopyFileToLocalPath(source, destinationFolderPath);
                        succeeded = result.Succeeded; message = result.Message; exception = result.Exception;
                    }
                    if (!succeeded) throw new IOException(message, exception);
                    operation.Report("Finishing");
                    if (string.IsNullOrWhiteSpace(launchPath) || !File.Exists(launchPath))
                        throw new IOException("Download completed, but the expected launch file was not found.");
                });

                Game.IsInstalled = true;
                Game.InstallDirectory = installDirectory;
                if (Game.GameActions == null) Game.GameActions = new ObservableCollection<GameAction>();
                else Game.GameActions.Clear();
                Game.GameActions.Add(new GameAction { Name = "Play", Type = GameActionType.File,
                    Path = launchPath, WorkingDir = installDirectory, IsPlayAction = true });
                installed = true;
                if (ShouldRestoreMods())
                {
                    var result = await RestoreMods(operation);
                    modsDownloaded = result?.Success == true && !result.HasConflicts;
                    FinishMods(operation, result);
                }
                else operation.Finish("Complete");
                Game.IsInstalling = false;
                InvokeOnInstalled(new GameInstalledEventArgs(new GameInstallationData { InstallDirectory = installDirectory }));
                logger.Info($"Personal Cloud Library Source downloaded item {item.Id} to local cache.");
            }
            catch (OperationCanceledException)
            {
                operation.Finish("Cancelled", "Download cancelled. Completed files are retained.");
                logger.Info($"Personal Cloud Library Source download cancelled for {item.Id}.");
            }
            catch (Exception ex)
            {
                if (!installed) operation.Finish("Failed", "Download failed: " + ex.Message);
                logger.Error(ex, $"Personal Cloud Library Source failed to download item {item.Id}.");
            }
            finally
            {
                try
                {
                    Game.IsInstalling = false;
                    if (!installed) InvokeOnInstallationCancelled(new GameInstallationCancelledEventArgs());
                }
                catch (Exception ex) { logger.Error(ex, "Could not finish cached install state."); }
                operation.Dispose();
                if (installed && modsDownloaded)
                {
                    try { startBackgroundMods?.Invoke(); }
                    catch (Exception ex) { logger.Error(ex, "Game installed, but background mod work could not be queued."); }
                }
            }
        }

        private bool ShouldRestoreMods() => restoreMods != null && settings.Enabled && settings.EnableModSync &&
            !string.IsNullOrWhiteSpace(item.ModPath) && string.Equals(PersonalCloudLibrarySource.GetProviderType(settings),
                PersonalCloudLibrarySourceSettings.RcloneRemoteProviderType, StringComparison.OrdinalIgnoreCase);

        private async Task<ModSyncResult> RestoreMods(InstallProgressSession operation)
        {
            operation.Report("DownloadingMods", message: "Downloading cloud mods — no two-way synchronization");
            try
            {
                return await restoreMods(operation.CancellationToken, progress => operation.Report("DownloadingMods",
                    progress.Bytes, progress.TotalBytes > 0 ? (long?)progress.TotalBytes : null, progress.BytesPerSecond,
                    percentage: progress.Percentage,
                    message: ModSyncUserInteraction.ProgressText(progress.Phase, 0, 0,
                        progress.Checks, progress.TotalChecks, 0)), false);
            }
            catch (OperationCanceledException) { return new ModSyncResult { Cancelled = true, Message = "Mod restoration cancelled; the game remains installed." }; }
            catch (Exception ex) { return new ModSyncResult { Message = ex.Message }; }
        }

        private void FinishMods(InstallProgressSession operation, ModSyncResult result)
        {
            if (result == null || result.Success && !result.HasConflicts) { operation.Finish("Complete"); return; }

            var diagnostic = string.IsNullOrWhiteSpace(result.DiagnosticPath) ? "" : "\nDiagnostics: " + result.DiagnosticPath;

            operation.Finish(result.Cancelled ? "Cancelled" : "Failed",
                "Game installed; mod download needs attention. " + result.Message + diagnostic,
                RetryMods, "Retry mods");
        }

        private async void RetryMods()
        {
            if (!InstallProgressState.Shared.TryBegin(Game.Id, out var operation, Game.Name)) return;
            var downloaded = false;
            try
            {
                if (!Game.IsInstalled || !ShouldRestoreMods())
                {
                    operation.Finish("Failed", "Install the game and enable its configured mod synchronization before retrying mods.");
                    return;
                }
                Game.IsInstalling = true;
                var result = await RestoreMods(operation);
                downloaded = result?.Success == true && !result.HasConflicts;
                FinishMods(operation, result);
            }
            catch (Exception ex)
            {
                operation.Finish("Failed", "Could not retry mods: " + ex.Message);
                logger.Error(ex, "Optional mod-only retry failed.");
            }
            finally
            {
                Game.IsInstalling = false; operation.Dispose();
                if (downloaded)
                {
                    try { startBackgroundMods?.Invoke(); }
                    catch (Exception ex) { logger.Error(ex, "Could not start background mods after download-only retry."); }
                }
            }
        }
    }
}
