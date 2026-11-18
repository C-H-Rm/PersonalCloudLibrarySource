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
            // The shipped ZIP-only lifecycle intentionally has no manual cloud sync,
            // relink, or recovery action. Legacy code stays unreachable for rollback.
            return new GameMenuItem[0];
        }
    }
}
