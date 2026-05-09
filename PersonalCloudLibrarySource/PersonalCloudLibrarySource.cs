using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Controls;

namespace PersonalCloudLibrarySource
{
    public class PersonalCloudLibrarySource : LibraryPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly RcloneManifestReader rcloneManifestReader = new RcloneManifestReader();
        private readonly RcloneFileCopier rcloneFileCopier = new RcloneFileCopier();
        private readonly LocalFileCopier localFileCopier = new LocalFileCopier();

        private PersonalCloudLibrarySourceSettingsViewModel settings { get; set; }

        public override Guid Id { get; } = Guid.Parse("61993828-67a8-4468-93a2-293442e36328");

        public override string Name => "Personal Cloud Library Source";

        public override LibraryClient Client { get; } = new PersonalCloudLibrarySourceClient();

        public PersonalCloudLibrarySource(IPlayniteAPI api) : base(api)
        {
            settings = new PersonalCloudLibrarySourceSettingsViewModel(this);

            Properties = new LibraryPluginProperties
            {
                HasSettings = true
            };
        }

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            var importedGames = new List<GameMetadata>();
            var diagnostics = new List<string>();

            try
            {
                var pluginSettings = settings.Settings;

                if (pluginSettings == null || !pluginSettings.Enabled)
                {
                    logger.Info("Personal Cloud Library Source is disabled. No games imported.");
                    return importedGames;
                }

                var providerType = GetProviderType(pluginSettings);
                diagnostics.Add($"provider={providerType}");
                diagnostics.Add($"manifestPath={ResolveManifestDescription(pluginSettings)}");

                var json = LoadManifestJson(pluginSettings);
                var manifest = ParseManifest(json);
                diagnostics.Add($"itemCount={manifest.Items.Count}");
                importedGames = ConvertManifestItemsToGameMetadata(manifest, pluginSettings, diagnostics);
                diagnostics.Add($"returnedGameCount={importedGames.Count}");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Personal Cloud Library Source failed to import the manifest.");
                diagnostics.Add($"importError={ex.Message}");
            }
            finally
            {
                WriteImportDiagnostics(diagnostics);
            }

            logger.Info($"Personal Cloud Library Source imported {importedGames.Count} manifest entries.");
            return importedGames;
        }

        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            var installActions = new List<InstallController>();

            try
            {
                var pluginSettings = settings.Settings;
                if (args?.Game == null)
                {
                    logger.Info("Personal Cloud Library Source install action not returned: no game context.");
                    return installActions;
                }

                if (pluginSettings == null || !pluginSettings.Enabled)
                {
                    logger.Info($"Personal Cloud Library Source install action not returned for {args.Game.GameId}: plugin disabled.");
                    return installActions;
                }

                if (!pluginSettings.AllowDownloads)
                {
                    logger.Info($"Personal Cloud Library Source install action not returned for {args.Game.GameId}: downloads disabled.");
                    return installActions;
                }

                if (args.Game.PluginId != Id)
                {
                    logger.Info($"Personal Cloud Library Source install action not returned for {args.Game.GameId}: game belongs to another plugin.");
                    return installActions;
                }

                var json = LoadManifestJson(pluginSettings);
                var manifest = ParseManifest(json);

                foreach (var item in manifest.Items)
                {
                    if (item == null ||
                        !string.Equals(item.Id, args.Game.GameId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var sourcePath = GetItemSourcePath(item);
                    if (string.IsNullOrWhiteSpace(sourcePath))
                    {
                        logger.Warn($"Personal Cloud Library Source item {item.Id} has no sourcePath or legacy remotePath and cannot be downloaded.");
                        return installActions;
                    }

                    var launchPath = ResolveLaunchPath(item, pluginSettings);
                    var launchFileExists = !string.IsNullOrWhiteSpace(launchPath) && File.Exists(launchPath);
                    if (launchFileExists)
                    {
                        logger.Info($"Personal Cloud Library Source install action not returned for {item.Id}: launch file already exists.");
                        return installActions;
                    }

                    if (!CanResolveSourcePath(pluginSettings, sourcePath))
                    {
                        logger.Info($"Personal Cloud Library Source install action not returned for {item.Id}: provider cannot resolve sourcePath.");
                        return installActions;
                    }

                    installActions.Add(new RcloneInstallController(
                        args.Game,
                        item,
                        pluginSettings,
                        rcloneFileCopier,
                        localFileCopier));
                    logger.Info($"Personal Cloud Library Source install action returned for {item.Id}.");
                    return installActions;
                }

                logger.Info($"Personal Cloud Library Source install action not returned for {args.Game.GameId}: manifest item was not found.");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Personal Cloud Library Source failed to prepare rclone install action.");
            }

            return installActions;
        }

        public static string GetProviderType(PersonalCloudLibrarySourceSettings pluginSettings)
        {
            return string.IsNullOrWhiteSpace(pluginSettings.SourceProviderType)
                ? PersonalCloudLibrarySourceSettings.LocalFileProviderType
                : pluginSettings.SourceProviderType;
        }

        private string LoadManifestJson(PersonalCloudLibrarySourceSettings pluginSettings)
        {
            var sourceProviderType = GetProviderType(pluginSettings);

            if (string.Equals(
                sourceProviderType,
                PersonalCloudLibrarySourceSettings.RcloneRemoteProviderType,
                StringComparison.OrdinalIgnoreCase))
            {
                logger.Info("Personal Cloud Library Source loading manifest using rclone.");
                return rcloneManifestReader.ReadManifestJson(pluginSettings);
            }

            if (string.Equals(
                sourceProviderType,
                PersonalCloudLibrarySourceSettings.LocalFolderProviderType,
                StringComparison.OrdinalIgnoreCase))
            {
                var localFolderManifestPath = ResolveLocalFolderManifestPath(pluginSettings);
                if (string.IsNullOrWhiteSpace(localFolderManifestPath))
                {
                    throw new InvalidOperationException("Personal Cloud Library Source local folder manifest path could not be resolved.");
                }

                if (!File.Exists(localFolderManifestPath))
                {
                    throw new FileNotFoundException("Personal Cloud Library Source local folder manifest was not found.", localFolderManifestPath);
                }

                return File.ReadAllText(localFolderManifestPath);
            }

            if (!string.Equals(
                sourceProviderType,
                PersonalCloudLibrarySourceSettings.LocalFileProviderType,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Source provider type must be LocalFile, LocalFolder, or RcloneRemote.");
            }

            if (string.IsNullOrWhiteSpace(pluginSettings.LocalManifestPath))
            {
                throw new InvalidOperationException("Personal Cloud Library Source local manifest path is empty.");
            }

            if (!File.Exists(pluginSettings.LocalManifestPath))
            {
                throw new FileNotFoundException("Personal Cloud Library Source manifest was not found.", pluginSettings.LocalManifestPath);
            }

            return File.ReadAllText(pluginSettings.LocalManifestPath);
        }

        private static PersonalCloudLibraryManifest ParseManifest(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidOperationException("Personal Cloud Library Source manifest JSON was empty.");
            }

            json = json.TrimStart('\uFEFF', '\u00EF', '\u00BB', '\u00BF');
            var manifest = Serialization.FromJson<PersonalCloudLibraryManifest>(json);
            if (manifest == null || manifest.Items == null)
            {
                throw new InvalidOperationException("Personal Cloud Library Source manifest was empty or invalid.");
            }

            return manifest;
        }

        private static List<GameMetadata> ConvertManifestItemsToGameMetadata(
            PersonalCloudLibraryManifest manifest,
            PersonalCloudLibrarySourceSettings pluginSettings,
            List<string> diagnostics)
        {
            var importedGames = new List<GameMetadata>();
            var importedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in manifest.Items)
            {
                if (item == null)
                {
                    diagnostics.Add("item=<null>; skipReason=null item");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Title))
                {
                    logger.Warn("Skipped manifest item because it was missing an id or title.");
                    diagnostics.Add($"itemId={item.Id}; title={item.Title}; skipReason=missing id or title");
                    continue;
                }

                if (!importedIds.Add(item.Id))
                {
                    logger.Warn($"Skipped duplicate manifest item id: {item.Id}");
                    diagnostics.Add($"itemId={item.Id}; title={item.Title}; skipReason=duplicate id");
                    continue;
                }

                var launchPath = ResolveLaunchPath(item, pluginSettings);
                var installDirectory = ResolveInstallDirectory(item, pluginSettings, launchPath);
                var launchFileExists = !string.IsNullOrWhiteSpace(launchPath) && File.Exists(launchPath);
                var isInstalled = pluginSettings.TreatMissingFilesAsUninstalled ? launchFileExists : true;
                var sourcePath = GetItemSourcePath(item);
                var cachePath = ResolveDownloadDestinationFilePath(item, pluginSettings, launchPath);
                var downloadEligible = !launchFileExists &&
                    pluginSettings.AllowDownloads &&
                    !string.IsNullOrWhiteSpace(sourcePath) &&
                    CanResolveSourcePath(pluginSettings, sourcePath);
                var playActionCount = launchFileExists ? 1 : 0;
                var playActionName = launchFileExists ? "Play" : string.Empty;
                var playActionPath = launchFileExists ? launchPath : string.Empty;

                var game = new GameMetadata
                {
                    GameId = item.Id,
                    Name = item.Title,
                    IsInstalled = isInstalled,
                    InstallDirectory = installDirectory
                };

                logger.Info(
                    $"Personal Cloud Library Source item import: id={item.Id}; title={item.Title}; launchPath={launchPath}; fileExists={launchFileExists}; sourcePathPresent={!string.IsNullOrWhiteSpace(sourcePath)}; isInstalled={isInstalled}; downloadEligible={downloadEligible}; playActionCount={playActionCount}; playActionName={playActionName}; playActionPath={playActionPath}");
                diagnostics.Add(
                    $"itemId={item.Id}; title={item.Title}; sourcePath={sourcePath}; cachePath={cachePath}; localExists={launchFileExists}; isInstalled={isInstalled}; downloadEligible={downloadEligible}; playActionCount={playActionCount}; playActionName={playActionName}; playActionPath={playActionPath}; skipReason=");

                if (!string.IsNullOrWhiteSpace(item.Notes))
                {
                    game.Description = item.Notes;
                }

                if (launchFileExists)
                {
                    game.GameActions = new List<GameAction>
                    {
                        new GameAction
                        {
                            Name = "Play",
                            Type = GameActionType.File,
                            Path = launchPath,
                            WorkingDir = installDirectory,
                            IsPlayAction = true
                        }
                    };
                }

                importedGames.Add(game);
            }

            return importedGames;
        }

        public static string ResolveLaunchPath(PersonalCloudLibraryItem item, PersonalCloudLibrarySourceSettings pluginSettings)
        {
            if (!string.IsNullOrWhiteSpace(item.LocalPath))
            {
                if (Path.IsPathRooted(item.LocalPath))
                {
                    return item.LocalPath;
                }

                if (!string.IsNullOrWhiteSpace(pluginSettings.LocalCacheFolder))
                {
                    return Path.Combine(pluginSettings.LocalCacheFolder, item.LocalPath);
                }

                return item.LocalPath;
            }

            if (!string.IsNullOrWhiteSpace(item.InstallDirectory) &&
                !string.IsNullOrWhiteSpace(item.LaunchFile))
            {
                if (Path.IsPathRooted(item.InstallDirectory))
                {
                    return Path.Combine(item.InstallDirectory, item.LaunchFile);
                }

                if (!string.IsNullOrWhiteSpace(pluginSettings.LocalCacheFolder))
                {
                    return Path.Combine(pluginSettings.LocalCacheFolder, item.InstallDirectory, item.LaunchFile);
                }

                return Path.Combine(item.InstallDirectory, item.LaunchFile);
            }

            return string.Empty;
        }

        public static string ResolveInstallDirectory(
            PersonalCloudLibraryItem item,
            PersonalCloudLibrarySourceSettings pluginSettings,
            string launchPath)
        {
            if (!string.IsNullOrWhiteSpace(item.InstallDirectory))
            {
                if (Path.IsPathRooted(item.InstallDirectory))
                {
                    return item.InstallDirectory;
                }

                if (!string.IsNullOrWhiteSpace(pluginSettings.LocalCacheFolder))
                {
                    return Path.Combine(pluginSettings.LocalCacheFolder, item.InstallDirectory);
                }

                return item.InstallDirectory;
            }

            if (!string.IsNullOrWhiteSpace(launchPath))
            {
                return Path.GetDirectoryName(launchPath);
            }

            if (!string.IsNullOrWhiteSpace(pluginSettings.LocalCacheFolder))
            {
                return pluginSettings.LocalCacheFolder;
            }

            return string.Empty;
        }

        public static string ResolveDownloadDestinationFilePath(
            PersonalCloudLibraryItem item,
            PersonalCloudLibrarySourceSettings pluginSettings,
            string launchPath)
        {
            var installDirectory = ResolveInstallDirectory(item, pluginSettings, launchPath);

            if (!string.IsNullOrWhiteSpace(installDirectory) &&
                !string.IsNullOrWhiteSpace(item.LaunchFile))
            {
                return Path.Combine(installDirectory, item.LaunchFile);
            }

            if (!string.IsNullOrWhiteSpace(launchPath))
            {
                return launchPath;
            }

            var remoteFileName = Path.GetFileName((GetItemSourcePath(item) ?? string.Empty).Replace('/', Path.DirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(remoteFileName))
            {
                remoteFileName = item.Id;
            }

            if (!string.IsNullOrWhiteSpace(pluginSettings.LocalCacheFolder) &&
                !string.IsNullOrWhiteSpace(item.Id))
            {
                return Path.Combine(pluginSettings.LocalCacheFolder, item.Id, remoteFileName);
            }

            return string.Empty;
        }

        public static string ResolveDownloadDestinationFolder(
            PersonalCloudLibraryItem item,
            PersonalCloudLibrarySourceSettings pluginSettings,
            string launchPath)
        {
            if (!string.IsNullOrWhiteSpace(item.InstallDirectory))
            {
                if (Path.IsPathRooted(item.InstallDirectory))
                {
                    return item.InstallDirectory;
                }

                if (!string.IsNullOrWhiteSpace(pluginSettings.LocalCacheFolder))
                {
                    return Path.Combine(pluginSettings.LocalCacheFolder, item.InstallDirectory);
                }
            }

            if (!string.IsNullOrWhiteSpace(launchPath))
            {
                var launchDirectory = Path.GetDirectoryName(launchPath);
                if (!string.IsNullOrWhiteSpace(launchDirectory))
                {
                    return launchDirectory;
                }
            }

            if (!string.IsNullOrWhiteSpace(pluginSettings.LocalCacheFolder) &&
                !string.IsNullOrWhiteSpace(item.Id))
            {
                return Path.Combine(pluginSettings.LocalCacheFolder, item.Id);
            }

            return string.Empty;
        }

        public static string GetItemSourcePath(PersonalCloudLibraryItem item)
        {
            if (!string.IsNullOrWhiteSpace(item.SourcePath))
            {
                return item.SourcePath;
            }

            return item.RemotePath;
        }

        public static string ResolveLocalFolderSourcePath(
            PersonalCloudLibrarySourceSettings pluginSettings,
            string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return string.Empty;
            }

            if (Path.IsPathRooted(sourcePath))
            {
                return sourcePath;
            }

            if (!string.IsNullOrWhiteSpace(pluginSettings.LocalLibraryRoot))
            {
                return Path.Combine(pluginSettings.LocalLibraryRoot, sourcePath);
            }

            if (!string.IsNullOrWhiteSpace(pluginSettings.LocalManifestPath))
            {
                var manifestFolder = Path.GetDirectoryName(pluginSettings.LocalManifestPath);
                if (!string.IsNullOrWhiteSpace(manifestFolder))
                {
                    return Path.Combine(manifestFolder, sourcePath);
                }
            }

            return string.Empty;
        }

        public static string ResolveRcloneSourcePath(
            PersonalCloudLibrarySourceSettings pluginSettings,
            string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return string.Empty;
            }

            if (string.IsNullOrWhiteSpace(pluginSettings.RcloneContentRoot))
            {
                return sourcePath;
            }

            return CombineRemotePath(pluginSettings.RcloneContentRoot, sourcePath);
        }

        private static bool CanResolveSourcePath(PersonalCloudLibrarySourceSettings pluginSettings, string sourcePath)
        {
            var providerType = GetProviderType(pluginSettings);

            if (string.Equals(providerType, PersonalCloudLibrarySourceSettings.RcloneRemoteProviderType, StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrWhiteSpace(pluginSettings.RcloneRemoteName) &&
                    !string.IsNullOrWhiteSpace(ResolveRcloneSourcePath(pluginSettings, sourcePath));
            }

            if (string.Equals(providerType, PersonalCloudLibrarySourceSettings.LocalFolderProviderType, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(providerType, PersonalCloudLibrarySourceSettings.LocalFileProviderType, StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrWhiteSpace(ResolveLocalFolderSourcePath(pluginSettings, sourcePath));
            }

            return false;
        }

        private static string ResolveLocalFolderManifestPath(PersonalCloudLibrarySourceSettings pluginSettings)
        {
            if (string.IsNullOrWhiteSpace(pluginSettings.LocalLibraryRoot) ||
                string.IsNullOrWhiteSpace(pluginSettings.ManifestRelativePath))
            {
                return string.Empty;
            }

            return Path.Combine(pluginSettings.LocalLibraryRoot, pluginSettings.ManifestRelativePath);
        }

        private static string CombineRemotePath(string root, string relativePath)
        {
            return root.TrimEnd('/', '\\') + "/" + relativePath.TrimStart('/', '\\');
        }

        private static string ResolveManifestDescription(PersonalCloudLibrarySourceSettings pluginSettings)
        {
            var providerType = GetProviderType(pluginSettings);

            if (string.Equals(providerType, PersonalCloudLibrarySourceSettings.RcloneRemoteProviderType, StringComparison.OrdinalIgnoreCase))
            {
                return $"{pluginSettings.RcloneRemoteName}:{pluginSettings.RcloneManifestPath}";
            }

            if (string.Equals(providerType, PersonalCloudLibrarySourceSettings.LocalFolderProviderType, StringComparison.OrdinalIgnoreCase))
            {
                return ResolveLocalFolderManifestPath(pluginSettings);
            }

            return pluginSettings.LocalManifestPath;
        }

        private static void WriteImportDiagnostics(List<string> diagnostics)
        {
            try
            {
                var diagnosticsDirectory = @"D:\PersonalCloudLibrarySource\diagnostics";
                Directory.CreateDirectory(diagnosticsDirectory);
                var diagnosticsPath = Path.Combine(diagnosticsDirectory, "last-import-diagnostics.txt");
                File.WriteAllLines(diagnosticsPath, diagnostics);
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Personal Cloud Library Source could not write import diagnostics.");
            }
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settings;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new PersonalCloudLibrarySourceSettingsView();
        }
    }

    public class PersonalCloudLibraryManifest
    {
        public int Version { get; set; }
        public List<PersonalCloudLibraryItem> Items { get; set; } = new List<PersonalCloudLibraryItem>();
    }

    public class PersonalCloudLibraryItem
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Platform { get; set; }
        public string LocalPath { get; set; }
        public string InstallDirectory { get; set; }
        public string LaunchFile { get; set; }
        public string SourcePath { get; set; }
        public string RemotePath { get; set; }
        public string Notes { get; set; }
    }
}
