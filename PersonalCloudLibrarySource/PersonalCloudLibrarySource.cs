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

            try
            {
                var pluginSettings = settings.Settings;

                if (pluginSettings == null || !pluginSettings.Enabled)
                {
                    logger.Info("Personal Cloud Library Source is disabled. No games imported.");
                    return importedGames;
                }

                var json = LoadManifestJson(pluginSettings);
                var manifest = ParseManifest(json);
                importedGames = ConvertManifestItemsToGameMetadata(manifest, pluginSettings);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Personal Cloud Library Source failed to import the manifest.");
            }

            logger.Info($"Personal Cloud Library Source imported {importedGames.Count} manifest entries.");
            return importedGames;
        }

        private string LoadManifestJson(PersonalCloudLibrarySourceSettings pluginSettings)
        {
            var manifestSourceMode = string.IsNullOrWhiteSpace(pluginSettings.ManifestSourceMode)
                ? PersonalCloudLibrarySourceSettings.LocalFileManifestSourceMode
                : pluginSettings.ManifestSourceMode;

            if (string.Equals(
                manifestSourceMode,
                PersonalCloudLibrarySourceSettings.RcloneRemoteManifestSourceMode,
                StringComparison.OrdinalIgnoreCase))
            {
                logger.Info("Personal Cloud Library Source loading manifest using rclone.");
                return rcloneManifestReader.ReadManifestJson(pluginSettings);
            }

            if (!string.Equals(
                manifestSourceMode,
                PersonalCloudLibrarySourceSettings.LocalFileManifestSourceMode,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Manifest source mode must be LocalFile or RcloneRemote.");
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

            var manifest = Serialization.FromJson<PersonalCloudLibraryManifest>(json);
            if (manifest == null || manifest.Items == null)
            {
                throw new InvalidOperationException("Personal Cloud Library Source manifest was empty or invalid.");
            }

            return manifest;
        }

        private static List<GameMetadata> ConvertManifestItemsToGameMetadata(
            PersonalCloudLibraryManifest manifest,
            PersonalCloudLibrarySourceSettings pluginSettings)
        {
            var importedGames = new List<GameMetadata>();
            var importedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in manifest.Items)
            {
                if (item == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Title))
                {
                    logger.Warn("Skipped manifest item because it was missing an id or title.");
                    continue;
                }

                if (!importedIds.Add(item.Id))
                {
                    logger.Warn($"Skipped duplicate manifest item id: {item.Id}");
                    continue;
                }

                var launchPath = ResolveLaunchPath(item, pluginSettings);
                var installDirectory = ResolveInstallDirectory(item, pluginSettings, launchPath);
                var launchFileExists = !string.IsNullOrWhiteSpace(launchPath) && File.Exists(launchPath);

                var game = new GameMetadata
                {
                    GameId = item.Id,
                    Name = item.Title,
                    IsInstalled = pluginSettings.TreatMissingFilesAsUninstalled ? launchFileExists : true,
                    InstallDirectory = installDirectory
                };

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

        private static string ResolveLaunchPath(PersonalCloudLibraryItem item, PersonalCloudLibrarySourceSettings pluginSettings)
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
                return Path.Combine(item.InstallDirectory, item.LaunchFile);
            }

            return string.Empty;
        }

        private static string ResolveInstallDirectory(
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
        public string Notes { get; set; }
    }
}
