using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;

namespace PersonalCloudLibrarySource
{
    public class RcloneInstallController : InstallController
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly ConcurrentDictionary<Guid, byte> activeInstalls = new ConcurrentDictionary<Guid, byte>();

        private readonly PersonalCloudLibraryItem item;
        private readonly PersonalCloudLibrarySourceSettings settings;
        private readonly RcloneFileCopier rcloneFileCopier;
        private readonly LocalFileCopier localFileCopier;

        public RcloneInstallController(
            Game game,
            PersonalCloudLibraryItem item,
            PersonalCloudLibrarySourceSettings settings,
            RcloneFileCopier rcloneFileCopier,
            LocalFileCopier localFileCopier) : base(game)
        {
            this.item = item;
            this.settings = settings;
            this.rcloneFileCopier = rcloneFileCopier;
            this.localFileCopier = localFileCopier;
            Name = "Download to local cache";
        }

        public override async void Install(InstallActionArgs args)
        {
            if (!activeInstalls.TryAdd(Game.Id, 0)) return;
            var installed = false;
            try
            {
                Game.IsInstalling = true;
                var launchPath = PersonalCloudLibrarySource.ResolveLaunchPath(item, settings);
                var installDirectory = PersonalCloudLibrarySource.ResolveInstallDirectory(item, settings, launchPath);
                var destinationFolderPath = PersonalCloudLibrarySource.ResolveDownloadDestinationFolder(item, settings, launchPath);
                var sourcePath = PersonalCloudLibrarySource.GetItemSourcePath(item);
                var providerType = PersonalCloudLibrarySource.GetProviderType(settings);
                logger.Info($"Personal Cloud Library Source downloading item {item.Id} to {destinationFolderPath}.");

                // Resume on the host context before updating GameActions or raising events.
                await Task.Run(() =>
                {
                    bool succeeded;
                    string message;
                    Exception exception;
                    if (string.Equals(providerType, PersonalCloudLibrarySourceSettings.RcloneRemoteProviderType, StringComparison.OrdinalIgnoreCase))
                    {
                        var remote = PersonalCloudLibrarySource.ResolveRcloneSourcePath(settings, sourcePath);
                        var result = rcloneFileCopier.CopyRemoteDirectoryToLocalPath(settings, remote, destinationFolderPath);
                        succeeded = result.Succeeded; message = result.Message; exception = result.Exception;
                    }
                    else
                    {
                        var source = PersonalCloudLibrarySource.ResolveLocalFolderSourcePath(settings, sourcePath);
                        var result = localFileCopier.CopyFileToLocalPath(source, destinationFolderPath);
                        succeeded = result.Succeeded; message = result.Message; exception = result.Exception;
                    }
                    if (!succeeded) throw new IOException(message, exception);
                    if (string.IsNullOrWhiteSpace(launchPath) || !File.Exists(launchPath))
                        throw new IOException("Download completed, but the expected launch file was not found.");
                });

                Game.IsInstalled = true;
                Game.InstallDirectory = installDirectory;
                if (Game.GameActions == null) Game.GameActions = new ObservableCollection<GameAction>();
                else Game.GameActions.Clear();
                Game.GameActions.Add(new GameAction
                {
                    Name = "Play", Type = GameActionType.File, Path = launchPath,
                    WorkingDir = installDirectory, IsPlayAction = true
                });
                installed = true;
                InvokeOnInstalled(new GameInstalledEventArgs(new GameInstallationData { InstallDirectory = installDirectory }));
                logger.Info($"Personal Cloud Library Source downloaded item {item.Id} to local cache.");
            }
            catch (Exception ex)
            {
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
                activeInstalls.TryRemove(Game.Id, out _);
            }
        }
    }
}
