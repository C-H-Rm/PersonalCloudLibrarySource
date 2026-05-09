using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System.Collections.ObjectModel;
using System.IO;

namespace PersonalCloudLibrarySource
{
    public class RcloneInstallController : InstallController
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly PersonalCloudLibraryItem item;
        private readonly PersonalCloudLibrarySourceSettings settings;
        private readonly RcloneFileCopier rcloneFileCopier;

        public RcloneInstallController(
            Game game,
            PersonalCloudLibraryItem item,
            PersonalCloudLibrarySourceSettings settings,
            RcloneFileCopier rcloneFileCopier) : base(game)
        {
            this.item = item;
            this.settings = settings;
            this.rcloneFileCopier = rcloneFileCopier;
            Name = "Download to local cache";
        }

        public override void Install(InstallActionArgs args)
        {
            var launchPath = PersonalCloudLibrarySource.ResolveLaunchPath(item, settings);
            var installDirectory = PersonalCloudLibrarySource.ResolveInstallDirectory(item, settings, launchPath);
            var destinationFilePath = PersonalCloudLibrarySource.ResolveDownloadDestinationFilePath(item, settings, launchPath);

            var result = rcloneFileCopier.CopyRemoteFileToLocalPath(settings, item.RemotePath, destinationFilePath);
            if (!result.Succeeded)
            {
                if (result.Exception != null)
                {
                    logger.Error(result.Exception, $"Personal Cloud Library Source failed to download item {item.Id}: {result.Message}");
                }
                else
                {
                    logger.Error($"Personal Cloud Library Source failed to download item {item.Id}: {result.Message}");
                }

                return;
            }

            if (string.IsNullOrWhiteSpace(launchPath) || !File.Exists(launchPath))
            {
                logger.Warn($"Personal Cloud Library Source downloaded item {item.Id}, but the expected launch file was not found.");
                return;
            }

            Game.IsInstalled = true;
            Game.InstallDirectory = installDirectory;

            if (Game.GameActions == null)
            {
                Game.GameActions = new ObservableCollection<GameAction>();
            }

            Game.GameActions.Add(new GameAction
            {
                Type = GameActionType.File,
                Path = launchPath,
                WorkingDir = installDirectory,
                IsPlayAction = true
            });

            logger.Info($"Personal Cloud Library Source downloaded item {item.Id} to local cache.");
        }
    }
}
