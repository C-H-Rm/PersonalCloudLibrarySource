using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.IO;

namespace PersonalCloudLibrarySource
{
    public class PersonalCloudLibrarySourceSettings : ObservableObject
    {
        private bool enabled = true;
        private string localManifestPath = string.Empty;
        private string localCacheFolder = string.Empty;
        private bool treatMissingFilesAsUninstalled = true;

        public bool Enabled
        {
            get => enabled;
            set => SetValue(ref enabled, value);
        }

        public string LocalManifestPath
        {
            get => localManifestPath;
            set => SetValue(ref localManifestPath, value);
        }

        public string LocalCacheFolder
        {
            get => localCacheFolder;
            set => SetValue(ref localCacheFolder, value);
        }

        public bool TreatMissingFilesAsUninstalled
        {
            get => treatMissingFilesAsUninstalled;
            set => SetValue(ref treatMissingFilesAsUninstalled, value);
        }
    }

    public class PersonalCloudLibrarySourceSettingsViewModel : ObservableObject, ISettings
    {
        private readonly PersonalCloudLibrarySource plugin;
        private PersonalCloudLibrarySourceSettings editingClone { get; set; }

        private PersonalCloudLibrarySourceSettings settings;
        public PersonalCloudLibrarySourceSettings Settings
        {
            get => settings;
            set
            {
                settings = value;
                OnPropertyChanged();
            }
        }

        public PersonalCloudLibrarySourceSettingsViewModel(PersonalCloudLibrarySource plugin)
        {
            this.plugin = plugin;

            var savedSettings = plugin.LoadPluginSettings<PersonalCloudLibrarySourceSettings>();
            Settings = savedSettings ?? new PersonalCloudLibrarySourceSettings();
        }

        public void BeginEdit()
        {
            editingClone = Serialization.GetClone(Settings);
        }

        public void CancelEdit()
        {
            Settings = editingClone;
        }

        public void EndEdit()
        {
            plugin.SavePluginSettings(Settings);
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();

            if (!string.IsNullOrWhiteSpace(Settings.LocalManifestPath) &&
                !File.Exists(Settings.LocalManifestPath))
            {
                errors.Add("The local manifest file does not exist.");
            }

            if (!string.IsNullOrWhiteSpace(Settings.LocalCacheFolder) &&
                !Directory.Exists(Settings.LocalCacheFolder))
            {
                errors.Add("The local cache folder does not exist.");
            }

            return errors.Count == 0;
        }
    }
}
