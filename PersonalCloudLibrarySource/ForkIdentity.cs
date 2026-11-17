using System;

namespace PersonalCloudLibrarySource
{
    /// <summary>Stable identity for the standalone CHR fork and its predecessor.</summary>
    public static class ForkIdentity
    {
        public static readonly Guid PluginId = Guid.Parse("402561c2-ea33-4c60-a8f8-ec25e997a1d5");
        public static readonly Guid LegacyPluginId = Guid.Parse("61993828-67a8-4468-93a2-293442e36328");
        public const string DisplayName = "CHR Cloud Library";
        public const string ExtensionYamlId = "CHRCloudLibrary_402561c2-ea33-4c60-a8f8-ec25e997a1d5";
        public const string LegacyDefaultDisplayName = "Personal Cloud Library Source";

        // Presentation only: adopting a legacy config must not rewrite its bytes or timestamp.
        public static string ResolveDisplayName(string configuredName)
        {
            return string.IsNullOrWhiteSpace(configuredName) ||
                string.Equals(configuredName, LegacyDefaultDisplayName, StringComparison.Ordinal)
                ? DisplayName : configuredName.Trim();
        }
    }
}
