using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PersonalCloudLibrarySource
{
    public partial class PersonalCloudLibrarySource
    {
        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            if (!settings.Settings.Enabled || !settings.Settings.EnableModSync || args?.Games == null)
                return new GameMenuItem[0];
            var games = args.Games.ToList();
            if (games.Count != 1 || games[0] == null || games[0].PluginId != Id)
                return new GameMenuItem[0];
            var game = games[0];
            try
            {
                // Menu visibility uses imported metadata only; never fetch the cloud here.
                if (!File.Exists(ModManifestPath)) return new GameMenuItem[0];
                var manifest = ParseManifest(File.ReadAllText(ModManifestPath));
                var matches = manifest.Items.Where(i => i != null &&
                    string.Equals(i.Id, game.GameId, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count != 1 || string.IsNullOrWhiteSpace(matches[0].ModPath))
                    return new GameMenuItem[0];
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Could not read cached mod configuration for the game menu.");
                return new GameMenuItem[0];
            }
            return new[]
            {
                new GameMenuItem { Description = "Sync mods", MenuSection = "Personal Cloud Library", Action = _ => RunModSync(game, false, true, false) },
                new GameMenuItem { Description = "Relink mods after reinstall", MenuSection = "Personal Cloud Library", Action = _ => RunModSync(game, false, true, true) }
            };
        }
    }
}
