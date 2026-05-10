using System.Windows;
using System.Windows.Controls;

namespace PersonalCloudLibrarySource
{
    public partial class PersonalCloudLibrarySourceSettingsView : UserControl
    {
        public PersonalCloudLibrarySourceSettingsView()
        {
            InitializeComponent();
        }

        private PersonalCloudLibrarySourceSettingsViewModel ViewModel => DataContext as PersonalCloudLibrarySourceSettingsViewModel;

        private void TestRcloneConnection_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.TestRcloneConnection();
        }

        private void TestManifestLoad_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.TestManifestLoad();
        }

        private void OpenCacheFolder_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.OpenCacheFolder();
        }

        private void OpenDiagnosticsFolder_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.OpenDiagnosticsFolder();
        }

        private void CreateSampleManifest_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.CreateSampleManifest();
        }
    }
}
