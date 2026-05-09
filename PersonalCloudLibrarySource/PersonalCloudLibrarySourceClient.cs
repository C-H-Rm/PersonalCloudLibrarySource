namespace PersonalCloudLibrarySource
{
    public class PersonalCloudLibrarySourceClient : Playnite.SDK.LibraryClient
    {
        public override bool IsInstalled => true;

        public override void Open()
        {
            // No external client app is required for this library source.
        }
    }
}
