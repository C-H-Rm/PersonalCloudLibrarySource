using Playnite.SDK.Events;

namespace PersonalCloudLibrarySource
{
    public partial class PersonalCloudLibrarySource
    {
        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            // Adoption is an explicit local operation, not an import-time side effect.
            InitializeLegacyMigration();
            PromptLegacyMigration();
            InitializeDownloadCenter();
            StartModZipUploadTimer();
        }


    }
}
