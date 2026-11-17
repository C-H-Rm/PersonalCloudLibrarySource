using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace PersonalCloudLibrarySource
{
    public partial class PersonalCloudLibrarySource
    {
        private bool legacyMigrationInitialized;
        private bool legacyMigrationPending;
        public bool CanUseForkLibrary { get; private set; }

        /// <summary>Call once from the host's OnApplicationStarted, before allowing GetGames imports.</summary>
        public void InitializeLegacyMigration()
        {
            legacyMigrationInitialized = true;
            CanUseForkLibrary = false;
            legacyMigrationPending = false;
            try
            {
                var migration = CreateLegacyMigration();
                migration.RecoverInterruptedMigration(IsLegacyPluginEnabled());
                settings.Settings = LoadPluginSettings<PersonalCloudLibrarySourceSettings>() ?? new PersonalCloudLibrarySourceSettings();
                var legacyData = Directory.Exists(LegacyDataDirectory) &&
                    (File.Exists(Path.Combine(LegacyDataDirectory, "config.json")) ||
                     File.Exists(Path.Combine(LegacyDataDirectory, "mod-sync-manifest.json")));
                var legacyGames = playniteApi.Database.Games.Any(game => game.PluginId == ForkIdentity.LegacyPluginId);
                var forkConfigured = File.Exists(Path.Combine(GetPluginUserDataPath(), "config.json"));
                if (forkConfigured || File.Exists(MigrationDecisionPath))
                {
                    CanUseForkLibrary = true;
                    return;
                }
                if (legacyData || legacyGames)
                {
                    legacyMigrationPending = true;
                    return;
                }
                CanUseForkLibrary = true;
            }
            catch (Exception ex)
            {
                legacyMigrationPending = true;
                logger.Error(ex, "Legacy library migration initialization failed; imports remain gated and retry remains available.");
            }
        }

        /// <summary>Call from OnApplicationStarted after InitializeLegacyMigration.</summary>
        public void PromptLegacyMigration()
        {
            try { PromptLegacyMigrationCore(); }
            catch (Exception ex)
            {
                CanUseForkLibrary = false;
                legacyMigrationPending = true;
                logger.Error(ex, "Legacy adoption needs attention; imports remain paused.");
                playniteApi.Dialogs.ShowMessage("Adoption needs attention. Imports remain paused and the legacy source data is preserved.\n\n" + ex.Message,
                    ForkIdentity.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void PromptLegacyMigrationCore()
        {
            if (!legacyMigrationInitialized) InitializeLegacyMigration();
            if (!legacyMigrationPending) return;

            if (IsLegacyPluginEnabled())
            {
                playniteApi.Dialogs.ShowMessage(
                    "CHR Cloud Library found games or data from Personal Cloud Library Source, which is still enabled. Disable or remove the old extension, restart Playnite, then retry this decision. The old extension's files and settings have not been changed.",
                    ForkIdentity.DisplayName, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var choice = playniteApi.Dialogs.ShowMessage(
                "Personal Cloud Library Source games or data were found.\n\nYes — adopt the old games in place. This changes only their library owner; their IDs and every other game field remain unchanged. Legacy settings and the cached mod manifest are copied, never removed.\n\nNo — keep a separate new library; the old games remain owned by the old extension.\n\nCancel — decide later; imports remain paused.",
                ForkIdentity.DisplayName, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return;
            if (choice == MessageBoxResult.No)
            {
                if (CreateLegacyMigration().HasPendingMigration)
                {
                    playniteApi.Dialogs.ShowMessage("An interrupted adoption still needs recovery. Retry adoption before choosing separate setup; copied settings and ownership evidence are preserved.",
                        ForkIdentity.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                legacyMigrationPending = false;
                PersistMigrationDecision("separate");
                CanUseForkLibrary = true;
                return;
            }

            var result = CreateLegacyMigration().Execute(true, IsLegacyPluginEnabled(), false);
            if (!result.Success)
            {
                settings.Settings = LoadPluginSettings<PersonalCloudLibrarySourceSettings>() ?? new PersonalCloudLibrarySourceSettings();
                playniteApi.Dialogs.ShowMessage(
                    "Adoption was not completed. Legacy source data was not removed. If rollback could not finish, copied data and its recovery journal were retained.\n\n" + result.Message + "\n\nRetry adoption after resolving the issue, or choose a separate library setup.",
                    ForkIdentity.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            legacyMigrationPending = false;
            PersistMigrationDecision("adopted");
            CanUseForkLibrary = true;
            logger.Info("Legacy library migration: " + result.Message);
        }

        private string LegacyDataDirectory => Path.Combine(playniteApi.Paths.ExtensionsDataPath, ForkIdentity.LegacyPluginId.ToString());
        private string MigrationDecisionPath => Path.Combine(GetPluginUserDataPath(), "legacy-migration-decision.txt");

        private void PersistMigrationDecision(string decision)
        {
            try
            {
                var directory = GetPluginUserDataPath();
                if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Plugin user-data folder is a reparse point.");
                Directory.CreateDirectory(directory);
                var path = MigrationDecisionPath;
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Migration decision path is a reparse point.");
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream)) writer.Write(decision);
            }
            catch (Exception ex) { logger.Warn(ex, "Could not persist the legacy migration choice; it may be requested again on next startup."); }
        }

        private LegacyLibraryMigration CreateLegacyMigration()
        {
            return new LegacyLibraryMigration(LegacyDataDirectory, GetPluginUserDataPath(), ReadLegacyGameOwnership, SetGameOwner, LoadCopiedSettings);
        }

        private IList<LegacyGameOwnership> ReadLegacyGameOwnership()
        {
            return playniteApi.Database.Games.Where(game => game.PluginId == ForkIdentity.LegacyPluginId || game.PluginId == ForkIdentity.PluginId).Select(game => new LegacyGameOwnership
            {
                GameId = game.GameId,
                PluginId = game.PluginId,
                IsRunning = game.IsRunning,
                IsInstalling = game.IsInstalling,
                IsUninstalling = game.IsUninstalling
            }).ToList();
        }

        private void SetGameOwner(string gameId, Guid owner)
        {
            var matches = playniteApi.Database.Games.Where(game =>
                string.Equals(game.GameId, gameId, StringComparison.OrdinalIgnoreCase) &&
                (game.PluginId == ForkIdentity.LegacyPluginId || game.PluginId == ForkIdentity.PluginId)).ToList();
            if (matches.Count != 1) throw new InvalidOperationException("Game ownership changed during migration or GameId is ambiguous: " + gameId);
            var ownedGame = matches[0];
            if (ownedGame.IsRunning || ownedGame.IsInstalling || ownedGame.IsUninstalling) throw new InvalidOperationException("A game started installing or uninstalling during migration: " + gameId);
            ownedGame.PluginId = owner;
            playniteApi.Database.Games.Update(ownedGame);
        }

        private void LoadCopiedSettings()
        {
            var copied = LoadPluginSettings<PersonalCloudLibrarySourceSettings>();
            if (copied == null) throw new InvalidOperationException("Copied legacy settings could not be loaded.");
            settings.Settings = copied;
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            if (!legacyMigrationPending) return Array.Empty<MainMenuItem>();
            return new[]
            {
                new MainMenuItem
                {
                    Description = "Retry CHR Cloud Library adoption",
                    MenuSection = "CHR Cloud Library",
                    Action = _ => PromptLegacyMigration()
                }
            };
        }

        private bool IsLegacyPluginEnabled()
        {
            // Playnite 6.16 exposes currently loaded plugins separately from DisabledAddons.
            // An API failure is treated as active/unknown: never migrate while detection is uncertain.
            try { return playniteApi.Addons.Plugins.Any(plugin => plugin.Id == ForkIdentity.LegacyPluginId); }
            catch (Exception ex)
            {
                logger.Warn(ex, "Could not verify whether the legacy extension is active; refusing adoption.");
                return true;
            }
        }
    }
}
