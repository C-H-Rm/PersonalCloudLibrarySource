using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.IO;

namespace PersonalCloudLibrarySource
{
    public class PersonalCloudLibrarySourceSettings : ObservableObject
    {
        public const string LocalFileProviderType = "LocalFile";
        public const string LocalFolderProviderType = "LocalFolder";
        public const string RcloneRemoteProviderType = "RcloneRemote";
        public const string LocalFileManifestSourceMode = LocalFileProviderType;
        public const string RcloneRemoteManifestSourceMode = RcloneRemoteProviderType;

        private bool enabled = true;
        private string sourceProviderType = LocalFileProviderType;
        private string localManifestPath = string.Empty;
        private string localLibraryRoot = string.Empty;
        private string manifestRelativePath = string.Empty;
        private string localCacheFolder = string.Empty;
        private bool treatMissingFilesAsUninstalled = true;
        private string rcloneExecutablePath = "rclone";
        private string rcloneRemoteName = string.Empty;
        private string rcloneManifestPath = string.Empty;
        private string rcloneContentRoot = string.Empty;
        private int rcloneTimeoutSeconds = 30;
        private bool allowDownloads = true;

        public bool Enabled
        {
            get => enabled;
            set => SetValue(ref enabled, value);
        }

        public string SourceProviderType
        {
            get => sourceProviderType;
            set => SetValue(ref sourceProviderType, value);
        }

        public string ManifestSourceMode
        {
            get => SourceProviderType;
            set => SourceProviderType = value;
        }

        public string LocalManifestPath
        {
            get => localManifestPath;
            set => SetValue(ref localManifestPath, value);
        }

        public string LocalLibraryRoot
        {
            get => localLibraryRoot;
            set => SetValue(ref localLibraryRoot, value);
        }

        public string ManifestRelativePath
        {
            get => manifestRelativePath;
            set => SetValue(ref manifestRelativePath, value);
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

        public string RcloneContentRoot
        {
            get => rcloneContentRoot;
            set => SetValue(ref rcloneContentRoot, value);
        }

        public int RcloneTimeoutSeconds
        {
            get => rcloneTimeoutSeconds;
            set => SetValue(ref rcloneTimeoutSeconds, value);
        }

        public bool AllowDownloads
        {
            get => allowDownloads;
            set => SetValue(ref allowDownloads, value);
        }

        public bool AllowRcloneDownloads
        {
            get => AllowDownloads;
            set => AllowDownloads = value;
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
            var sourceProviderType = string.IsNullOrWhiteSpace(Settings.SourceProviderType)
                ? PersonalCloudLibrarySourceSettings.LocalFileProviderType
                : Settings.SourceProviderType;

            if (string.Equals(
                sourceProviderType,
                PersonalCloudLibrarySourceSettings.LocalFileProviderType,
                StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(Settings.LocalManifestPath) &&
                    !File.Exists(Settings.LocalManifestPath))
                {
                    errors.Add("The local manifest file does not exist.");
                }
            }
            else if (string.Equals(
                sourceProviderType,
                PersonalCloudLibrarySourceSettings.LocalFolderProviderType,
                StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(Settings.LocalLibraryRoot))
                {
                    errors.Add("The local library root is required for LocalFolder mode.");
                }
                else if (!Directory.Exists(Settings.LocalLibraryRoot))
                {
                    errors.Add("The local library root does not exist.");
                }

                if (string.IsNullOrWhiteSpace(Settings.ManifestRelativePath))
                {
                    errors.Add("The manifest relative path is required for LocalFolder mode.");
                }
            }
            else if (string.Equals(
                sourceProviderType,
                PersonalCloudLibrarySourceSettings.RcloneRemoteProviderType,
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
                errors.Add("Source provider type must be LocalFile, LocalFolder, or RcloneRemote.");
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
