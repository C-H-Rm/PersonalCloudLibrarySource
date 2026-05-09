using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.IO;

namespace PersonalCloudLibrarySource
{
    public class PersonalCloudLibrarySourceSettings : ObservableObject
    {
        public const string LocalFileManifestSourceMode = "LocalFile";
        public const string RcloneRemoteManifestSourceMode = "RcloneRemote";

        private bool enabled = true;
        private string manifestSourceMode = LocalFileManifestSourceMode;
        private string localManifestPath = string.Empty;
        private string localCacheFolder = string.Empty;
        private bool treatMissingFilesAsUninstalled = true;
        private string rcloneExecutablePath = "rclone";
        private string rcloneRemoteName = string.Empty;
        private string rcloneManifestPath = string.Empty;
        private int rcloneTimeoutSeconds = 30;

        public bool Enabled
        {
            get => enabled;
            set => SetValue(ref enabled, value);
        }

        public string ManifestSourceMode
        {
            get => manifestSourceMode;
            set => SetValue(ref manifestSourceMode, value);
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

        public string RcloneExecutablePath
        {
            get => rcloneExecutablePath;
            set => SetValue(ref rcloneExecutablePath, value);
        }

        public string RcloneRemoteName
        {
            get => rcloneRemoteName;
            set => SetValue(ref rcloneRemoteName, value);
        }

        public string RcloneManifestPath
        {
            get => rcloneManifestPath;
            set => SetValue(ref rcloneManifestPath, value);
        }

        public int RcloneTimeoutSeconds
        {
            get => rcloneTimeoutSeconds;
            set => SetValue(ref rcloneTimeoutSeconds, value);
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
            var manifestSourceMode = string.IsNullOrWhiteSpace(Settings.ManifestSourceMode)
                ? PersonalCloudLibrarySourceSettings.LocalFileManifestSourceMode
                : Settings.ManifestSourceMode;

            if (string.Equals(
                manifestSourceMode,
                PersonalCloudLibrarySourceSettings.LocalFileManifestSourceMode,
                StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(Settings.LocalManifestPath) &&
                    !File.Exists(Settings.LocalManifestPath))
                {
                    errors.Add("The local manifest file does not exist.");
                }
            }
            else if (string.Equals(
                manifestSourceMode,
                PersonalCloudLibrarySourceSettings.RcloneRemoteManifestSourceMode,
                StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(Settings.RcloneExecutablePath))
                {
                    errors.Add("The rclone executable path is required for RcloneRemote mode.");
                }

                if (string.IsNullOrWhiteSpace(Settings.RcloneRemoteName))
                {
                    errors.Add("The rclone remote name is required for RcloneRemote mode.");
                }

                if (string.IsNullOrWhiteSpace(Settings.RcloneManifestPath))
                {
                    errors.Add("The rclone manifest path is required for RcloneRemote mode.");
                }

                if (Settings.RcloneTimeoutSeconds < 5 || Settings.RcloneTimeoutSeconds > 300)
                {
                    errors.Add("The rclone timeout must be between 5 and 300 seconds.");
                }
            }
            else
            {
                errors.Add("Manifest source mode must be LocalFile or RcloneRemote.");
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
