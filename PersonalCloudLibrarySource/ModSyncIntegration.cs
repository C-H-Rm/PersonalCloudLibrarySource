using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace PersonalCloudLibrarySource
{
    public partial class PersonalCloudLibrarySource
    {
        private readonly object modSyncGate = new object();
        private readonly ModSyncService modSyncService = new ModSyncService();

        private string ModStateDirectory(string gameId) => Path.Combine(GetPluginUserDataPath(), "mod-sync", ModSyncService.Key(gameId));
        private string ModManifestPath => Path.Combine(GetPluginUserDataPath(), "mod-sync-manifest.json");

        private void CacheModManifest(string json)
        {
            try
            {
                Directory.CreateDirectory(GetPluginUserDataPath());
                var temp = ModManifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, json);
                if (File.Exists(ModManifestPath)) File.Replace(temp, ModManifestPath, null);
                else File.Move(temp, ModManifestPath);
            }
            catch (Exception ex) { logger.Warn(ex, "Could not cache the mod manifest."); }
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            if (!settings.Settings.Enabled || !settings.Settings.EnableModSync) return new GameMenuItem[0];
            var games = args.Games.Where(g => g.PluginId == Id).ToList();
            if (games.Count != 1) return new GameMenuItem[0];
            var game = games[0];
            return new[]
            {
                new GameMenuItem { Description = "Sync mods", MenuSection = "Personal Cloud Library", Action = _ => RunModSync(game, false, true, false) },
                new GameMenuItem { Description = "Relink mods after reinstall", MenuSection = "Personal Cloud Library", Action = _ => RunModSync(game, false, true, true) }
            };
        }

        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
            if (args.CancelStartup) return;
            var result = RunModSync(args.Game, false, false, false);
            if (result != null && (!result.Success || result.HasConflicts))
                args.CancelStartup = !ConfirmModSync("Mods are not verified ready.\n\n" + ShortMessage(result.Message) + "\n\nStart the game anyway?");
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            RunModSync(args.Game, true, false, false);
        }

        private ModSyncResult RunModSync(Game game, bool justStopped, bool manual, bool relink)
        {
            var s = settings.Settings;
            if (game == null || game.PluginId != Id || !s.Enabled || !s.EnableModSync) return null;
            if (!Monitor.TryEnter(modSyncGate))
                return new ModSyncResult { Success = false, Message = "Another mod operation is active. Try again when it completes." };
            ModSyncResult result;
            try
            {
                if (game.IsRunning && !justStopped)
                    throw new InvalidOperationException("Close the game before synchronizing mods.");
                if (!File.Exists(ModManifestPath))
                {
                    if (manual) ShowModMessage("Update the game library first to import its optional mod configuration.");
                    return null;
                }
                var manifest = ParseManifest(File.ReadAllText(ModManifestPath));
                var matches = manifest.Items.Where(i => i != null && string.Equals(i.Id, game.GameId, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count != 1) throw new InvalidOperationException("Game is missing or duplicated in the manifest. Update the library before mod sync.");
                var item = matches[0];
                if (string.IsNullOrWhiteSpace(item.ModPath))
                {
                    if (manual) ShowModMessage("No modPath is configured for this game. Add it to the manifest and update the library.");
                    return null;
                }
                if (!string.Equals(GetProviderType(s), PersonalCloudLibrarySourceSettings.RcloneRemoteProviderType, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Mod synchronization requires the RcloneRemote provider.");
                var request = BuildModSyncRequest(item, manifest, s);
                result = OnModUi(() =>
                {
                    var approvals = new HashSet<string>(StringComparer.Ordinal);
                    while (true)
                    {
                        ModSyncResult completed = null;
                        string pendingConfirmation = null;
                        playniteApi.Dialogs.ActivateGlobalProgress((GlobalProgressActionArgs progress) =>
                        {
                            try
                            {
                                var current = ModSyncService.RequireCurrentMapping(request, () =>
                                {
                                    var live = ParseManifest(LoadManifestJson(s));
                                    var found = live.Items.Where(i => i != null && string.Equals(i.Id, game.GameId, StringComparison.OrdinalIgnoreCase)).ToList();
                                    if (found.Count != 1 || string.IsNullOrWhiteSpace(found[0].ModPath))
                                        throw new InvalidOperationException("The live mod configuration changed. Update the library before synchronizing.");
                                    return BuildModSyncRequest(found[0], live, s);
                                });
                                Func<string, bool> confirm = message =>
                                {
                                    if (approvals.Contains(message)) return true;
                                    pendingConfirmation = message;
                                    return false;
                                };
                                completed = relink ? modSyncService.Relink(current, confirm) : modSyncService.Synchronize(current, confirm);
                            }
                            catch (Exception ex) { completed = new ModSyncResult { Success = false, Message = ex.Message }; }
                        }, new GlobalProgressOptions("Synchronizing mods: " + game.Name) { IsIndeterminate = true, Cancelable = false });
                        // Close global progress BEFORE showing a confirmation dialog.
                        // The worker never synchronously dispatches a modal dialog.
                        if (pendingConfirmation == null || !ConfirmModSync(pendingConfirmation))
                            return completed ?? new ModSyncResult { Success = false, Message = "Mod sync did not complete." };
                        approvals.Add(pendingConfirmation);
                    }
                });
            }
            catch (Exception ex) { result = new ModSyncResult { Success = false, Message = ex.Message }; }
            finally { Monitor.Exit(modSyncGate); }
            logger.Info("Mod sync for " + game.GameId + ": " + result.Message);
            if (manual || (justStopped && !result.Success) || result.HasConflicts) ShowModMessage(ShortMessage(result.Message));
            return result;
        }

        private ModSyncRequest BuildModSyncRequest(PersonalCloudLibraryItem item, PersonalCloudLibraryManifest manifest, PersonalCloudLibrarySourceSettings s)
        {
            var launch = ResolveLaunchPath(item, s);
            var install = ResolveInstallDirectory(item, s, launch);
            var local = Path.GetFullPath(Path.IsPathRooted(item.ModPath) ? item.ModPath : Path.Combine(install, item.ModPath));
            if (!Path.IsPathRooted(item.ModPath) && !ModSyncService.IsWithin(local, install))
                throw new InvalidOperationException("Relative modPath may not escape the game install folder. Use an explicit absolute path for an external mod folder.");
            var remoteRelative = ResolveCloudModFolder(s.RcloneContentRoot, item.Title, item.ModFolder);
            if (string.IsNullOrWhiteSpace(s.RcloneRemoteName) || s.RcloneRemoteName.IndexOfAny(new[] { ':', '/', '\\', '\r', '\n', '"' }) >= 0)
                throw new InvalidOperationException("Invalid rclone remote name for mod sync.");
            foreach (var other in manifest.Items.Where(i => i != null))
            {
                var basePath = ResolveRcloneSourcePath(s, GetItemSourcePath(other));
                if (!string.IsNullOrWhiteSpace(basePath) && RemotePathsOverlap(remoteRelative, basePath))
                    throw new InvalidOperationException("The cloud mod folder overlaps a base game source path. Keep game data and mods separate.");
                if (!string.IsNullOrWhiteSpace(other.ModPath) && !string.Equals(item.Id, other.Id, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(remoteRelative, ResolveCloudModFolder(s.RcloneContentRoot, other.Title, other.ModFolder), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Two games share a cloud mod folder. Set distinct, stable modFolder values.");
                    var otherLaunch = ResolveLaunchPath(other, s);
                    var otherInstall = ResolveInstallDirectory(other, s, otherLaunch);
                    var otherLocal = Path.IsPathRooted(other.ModPath) ? other.ModPath : Path.Combine(otherInstall, other.ModPath);
                    if (ModSyncService.IsWithin(local, otherLocal) || ModSyncService.IsWithin(otherLocal, local))
                        throw new InvalidOperationException("Two games have overlapping local mod folders.");
                }
                var candidateLaunch = ResolveLaunchPath(other, s);
                if (!string.IsNullOrWhiteSpace(candidateLaunch) && ModSyncService.IsWithin(candidateLaunch, local))
                    throw new InvalidOperationException("The selected mod folder contains a game launch file.");
            }
            var state = ModStateDirectory(item.Id);
            if (!string.IsNullOrWhiteSpace(s.LocalCacheFolder) && ModSyncService.IsWithin(state, s.LocalCacheFolder))
                throw new InvalidOperationException("Plugin sync state must live outside the game cache.");
            return new ModSyncRequest
            {
                GameId = item.Id, LocalFolder = local, RemoteFolder = s.RcloneRemoteName + ":" + remoteRelative,
                LaunchFile = launch, StateDirectory = state, RcloneExecutable = s.RcloneExecutablePath,
                TimeoutSeconds = s.ModSyncTimeoutSeconds
            };
        }

        internal static string ResolveCloudModFolder(string root, string title, string explicitFolder)
        {
            root = (root ?? string.Empty).Replace('\\', '/').Trim('/');
            if (root.Split('/').Any(part => part == ".." || part == ".") || root.IndexOfAny(new[] { ':', '\r', '\n', '"' }) >= 0)
                throw new InvalidOperationException("Invalid rclone content root for mods.");
            var folder = explicitFolder;
            if (string.IsNullOrWhiteSpace(folder))
                folder = new string((title ?? string.Empty).Select(c => char.IsControl(c) || "<>:\"/\\|?*".IndexOf(c) >= 0 ? '_' : c).ToArray()).TrimEnd(' ', '.');
            if (string.IsNullOrWhiteSpace(folder) || folder == "." || folder == ".." || folder.IndexOfAny("<>:\"/\\|?*\r\n".ToCharArray()) >= 0 || folder.EndsWith(".") || folder.EndsWith(" "))
                throw new InvalidOperationException("modFolder must be one non-empty folder name, without separators or traversal.");
            return (string.IsNullOrEmpty(root) ? "" : root + "/") + "mods/" + folder;
        }

        private static bool RemotePathsOverlap(string a, string b)
        {
            a = a.Replace('\\', '/').Trim('/'); b = b.Replace('\\', '/').Trim('/');
            if (b.Split('/').Any(part => part == ".." || part == "."))
                throw new InvalidOperationException("Base game source path contains traversal; refusing mod sync.");
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + "/", StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase);
        }

        private void PauseModSync(string gameId)
        {
            if (!Monitor.TryEnter(modSyncGate)) throw new InvalidOperationException("Mod sync is active. Wait before uninstalling the cached game.");
            try { modSyncService.Pause(ModStateDirectory(gameId)); }
            finally { Monitor.Exit(modSyncGate); }
        }

        private bool ConfirmModSync(string message) => OnModUi(() => playniteApi.Dialogs.ShowMessage(message, "Mod synchronization", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
        private void ShowModMessage(string message) => OnModUi(() => { playniteApi.Dialogs.ShowMessage(message, "Mod synchronization"); return true; });
        private static string ShortMessage(string value) => value.Length <= 1800 ? value : value.Substring(0, 1800) + "\nSee the mod-sync diagnostics for details.";
        private static T OnModUi<T>(Func<T> action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            return dispatcher != null && !dispatcher.CheckAccess() ? dispatcher.Invoke(action) : action();
        }
    }
}
