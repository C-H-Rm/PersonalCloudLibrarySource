using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace PersonalCloudLibrarySource
{
    public class PersonalCloudLibraryUninstallController : UninstallController
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly ConcurrentDictionary<Guid, byte> activeUninstalls = new ConcurrentDictionary<Guid, byte>();

        private readonly IPlayniteAPI playniteApi;
        private readonly PersonalCloudLibraryItem item;
        private readonly PersonalCloudLibrarySourceSettings settings;
        private readonly Action beforeUninstall;
        private readonly SafeCacheDeletionExecutor deletionExecutor = new SafeCacheDeletionExecutor();

        public PersonalCloudLibraryUninstallController(
            IPlayniteAPI playniteApi,
            Game game,
            PersonalCloudLibraryItem item,
            PersonalCloudLibrarySourceSettings settings,
            Action beforeUninstall = null) : base(game)
        {
            this.playniteApi = playniteApi;
            this.item = item;
            this.settings = settings;
            this.beforeUninstall = beforeUninstall;
            Name = "Remove cached copy";
        }

        public override async void Uninstall(UninstallActionArgs args)
        {
            if (!activeUninstalls.TryAdd(Game.Id, 0)) return;
            var targetPath = string.Empty;
            try
            {
                var behavior = NormalizeUninstallBehavior(settings.UninstallBehavior);
                if (string.Equals(behavior, PersonalCloudLibrarySourceSettings.AskEachTimeUninstallBehavior, StringComparison.OrdinalIgnoreCase))
                    behavior = ChooseUninstallBehavior();
                if (string.IsNullOrEmpty(behavior)) return;

                Game.IsUninstalling = true;
                // Keep the host context for game state/events; only file work runs on the pool.
                var deleted = await Task.Run(() =>
                {
                    var launchPath = PersonalCloudLibrarySource.ResolveLaunchPath(item, settings);
                    var installDirectory = PersonalCloudLibrarySource.ResolveInstallDirectory(item, settings, launchPath);
                    var requestedTargetPath = ResolveTargetPath(behavior, launchPath, installDirectory);
                    string refusalReason;
                    targetPath = PersonalCloudLibrarySource.ResolveSafeUninstallTarget(settings, requestedTargetPath, out refusalReason);
                    if (!string.IsNullOrWhiteSpace(refusalReason))
                    {
                        logger.Warn($"Personal Cloud Library Source refused uninstall for {Game.GameId}: {refusalReason}");
                        return false;
                    }

                    beforeUninstall?.Invoke();
                    var afterPause = deletionExecutor.Authorize(
                        settings.LocalCacheFolder,
                        targetPath,
                        settings.AllowUninstallOutsideCacheFolder);
                    if (!afterPause.Allowed)
                    {
                        logger.Warn($"Personal Cloud Library Source refused uninstall for {Game.GameId} after beforeUninstall: {afterPause.Reason}");
                        return false;
                    }

                    targetPath = afterPause.TargetPath;
                    var deletion = deletionExecutor.Delete(
                        settings.LocalCacheFolder,
                        targetPath,
                        settings.AllowUninstallOutsideCacheFolder);
                    if (!deletion.Allowed)
                    {
                        logger.Info($"Personal Cloud Library Source uninstall skipped for {Game.GameId}: {deletion.Reason}.");
                        return false;
                    }

                    return true;
                });
                if (!deleted) return;

                Game.IsInstalled = false;
                InvokeOnUninstalled();
                logger.Info($"Personal Cloud Library Source uninstall succeeded for {Game.GameId}: deleted {targetPath}.");
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Personal Cloud Library Source uninstall failed for {Game.GameId}: targetPath={targetPath}");
                playniteApi?.Dialogs?.ShowErrorMessage("Could not remove the cached copy: " + ex.Message, "Personal Cloud Library Source");
            }
            finally
            {
                // The SDK has no uninstall-cancelled event. Also clear host state on refusal/failure.
                try
                {
                    var game = playniteApi?.Database?.Games.Get(Game.Id);
                    Game.IsUninstalling = false;
                    if (game != null)
                    {
                        game.IsUninstalling = false;
                        playniteApi.Database.Games.Update(game);
                    }
                }
                catch (Exception ex) { logger.Error(ex, "Could not clear cached uninstall state."); }
                activeUninstalls.TryRemove(Game.Id, out _);
            }
        }

        private string ChooseUninstallBehavior()
        {
            if (playniteApi?.Dialogs == null)
            {
                logger.Warn("Personal Cloud Library Source AskEachTime uninstall requested, but no Playnite dialog API was available. Defaulting to RemoveCachedFileOnly.");
                return PersonalCloudLibrarySourceSettings.RemoveCachedFileOnlyUninstallBehavior;
            }

            var result = playniteApi.Dialogs.ShowMessage(
                "Remove the cached install folder? Choose No to remove only the cached launch file.",
                "Personal Cloud Library Source",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                return PersonalCloudLibrarySourceSettings.RemoveCachedInstallFolderUninstallBehavior;
            }

            if (result == MessageBoxResult.No)
            {
                return PersonalCloudLibrarySourceSettings.RemoveCachedFileOnlyUninstallBehavior;
            }

            return string.Empty;
        }

        private static string ResolveTargetPath(string behavior, string launchPath, string installDirectory)
        {
            if (string.Equals(behavior, PersonalCloudLibrarySourceSettings.RemoveCachedFileOnlyUninstallBehavior, StringComparison.OrdinalIgnoreCase))
            {
                return launchPath;
            }

            if (string.Equals(behavior, PersonalCloudLibrarySourceSettings.RemoveCachedInstallFolderUninstallBehavior, StringComparison.OrdinalIgnoreCase))
            {
                return installDirectory;
            }

            return string.Empty;
        }

        private static string NormalizeUninstallBehavior(string behavior)
        {
            return string.IsNullOrWhiteSpace(behavior)
                ? PersonalCloudLibrarySourceSettings.RemoveCachedInstallFolderUninstallBehavior
                : behavior;
        }
    }
}
