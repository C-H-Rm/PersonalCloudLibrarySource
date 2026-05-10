using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;

namespace PersonalCloudLibrarySource
{
    public class PersonalCloudLibrarySourceSettings : ObservableObject
    {
        public const string LocalFileProviderType = "LocalFile";
        public const string LocalFolderProviderType = "LocalFolder";
        public const string RcloneRemoteProviderType = "RcloneRemote";
        public const string LocalFileManifestSourceMode = LocalFileProviderType;
        public const string RcloneRemoteManifestSourceMode = RcloneRemoteProviderType;
        public const string RemoveCachedFileOnlyUninstallBehavior = "RemoveCachedFileOnly";
        public const string RemoveCachedInstallFolderUninstallBehavior = "RemoveCachedInstallFolder";
        public const string AskEachTimeUninstallBehavior = "AskEachTime";

        private bool enabled = true;
        private string libraryDisplayName = "Personal Cloud Library Source";
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
        private bool enableDiagnostics = true;
        private string uninstallBehavior = RemoveCachedInstallFolderUninstallBehavior;
        private bool allowUninstallOutsideCacheFolder = false;

        public bool Enabled
        {
            get => enabled;
            set => SetValue(ref enabled, value);
        }

        public string LibraryDisplayName
        {
            get => libraryDisplayName;
            set => SetValue(ref libraryDisplayName, value);
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

        public bool EnableDiagnostics
        {
            get => enableDiagnostics;
            set => SetValue(ref enableDiagnostics, value);
        }

        public string UninstallBehavior
        {
            get => uninstallBehavior;
            set => SetValue(ref uninstallBehavior, value);
        }

        public bool AllowUninstallOutsideCacheFolder
        {
            get => allowUninstallOutsideCacheFolder;
            set => SetValue(ref allowUninstallOutsideCacheFolder, value);
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

            var uninstallBehavior = string.IsNullOrWhiteSpace(Settings.UninstallBehavior)
                ? PersonalCloudLibrarySourceSettings.RemoveCachedInstallFolderUninstallBehavior
                : Settings.UninstallBehavior;
            if (!string.Equals(uninstallBehavior, PersonalCloudLibrarySourceSettings.RemoveCachedFileOnlyUninstallBehavior, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uninstallBehavior, PersonalCloudLibrarySourceSettings.RemoveCachedInstallFolderUninstallBehavior, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uninstallBehavior, PersonalCloudLibrarySourceSettings.AskEachTimeUninstallBehavior, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("Uninstall behavior must be RemoveCachedFileOnly, RemoveCachedInstallFolder, or AskEachTime.");
            }

            return errors.Count == 0;
        }

        public void TestRcloneConnection()
        {
            try
            {
                var executablePath = string.IsNullOrWhiteSpace(Settings.RcloneExecutablePath)
                    ? "rclone"
                    : Settings.RcloneExecutablePath.Trim();

                using (var process = new Process())
                {
                    var output = new StringBuilder();
                    var error = new StringBuilder();

                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = executablePath,
                        Arguments = "listremotes",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    process.OutputDataReceived += (sender, args) =>
                    {
                        if (args.Data != null)
                        {
                            output.AppendLine(args.Data);
                        }
                    };

                    process.ErrorDataReceived += (sender, args) =>
                    {
                        if (args.Data != null)
                        {
                            error.AppendLine(args.Data);
                        }
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    var timeoutSeconds = Settings.RcloneTimeoutSeconds < 5 ? 30 : Settings.RcloneTimeoutSeconds;
                    if (!process.WaitForExit(timeoutSeconds * 1000))
                    {
                        process.Kill();
                        MessageBox.Show("rclone listremotes timed out.", "Personal Cloud Library Source");
                        return;
                    }

                    process.WaitForExit();
                    if (process.ExitCode == 0)
                    {
                        MessageBox.Show(
                            "rclone responded successfully.\n\nConfigured remotes:\n" + output,
                            "Personal Cloud Library Source");
                    }
                    else
                    {
                        MessageBox.Show(
                            "rclone listremotes failed:\n" + RcloneManifestReader.TrimForLog(error.ToString()),
                            "Personal Cloud Library Source");
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Unable to run rclone: " + ex.Message, "Personal Cloud Library Source");
            }
        }

        public void TestManifestLoad()
        {
            try
            {
                var summary = plugin.ValidateManifest(Settings);
                MessageBox.Show(
                    "Manifest loaded successfully.\n" +
                    $"Items found: {summary.ItemsFound}\n" +
                    $"Download-eligible: {summary.DownloadEligible}\n" +
                    $"Cached/installed: {summary.CachedInstalled}\n" +
                    $"Warnings: {summary.Warnings}" +
                    (summary.Warnings > 0
                        ? "\n\nCheck for duplicate ids, missing id/title, or sourcePath values that already include RcloneContentRoot."
                        : string.Empty),
                    "Personal Cloud Library Source");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Manifest load failed: " + ex.Message, "Personal Cloud Library Source");
            }
        }

        public void OpenCacheFolder()
        {
            OpenFolder(Settings.LocalCacheFolder, createIfMissing: true);
        }

        public void OpenDiagnosticsFolder()
        {
            OpenFolder(plugin.GetDiagnosticsDirectory(), createIfMissing: true);
        }

        public void CreateSampleManifest()
        {
            try
            {
                var sampleDirectory = Path.Combine(plugin.GetPluginDataDirectory(), "samples");
                Directory.CreateDirectory(sampleDirectory);
                var samplePath = Path.Combine(sampleDirectory, "personal-cloud-library.sample.json");

                if (!File.Exists(samplePath))
                {
                    File.WriteAllText(samplePath, GetSampleManifestJson(), Encoding.UTF8);
                }

                if (string.IsNullOrWhiteSpace(Settings.LocalManifestPath))
                {
                    Settings.LocalManifestPath = samplePath;
                    OnPropertyChanged(nameof(Settings));
                }

                MessageBox.Show("Sample manifest is available at:\n" + samplePath, "Personal Cloud Library Source");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not create sample manifest: " + ex.Message, "Personal Cloud Library Source");
            }
        }

        private static void OpenFolder(string folderPath, bool createIfMissing)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                MessageBox.Show("Folder path is empty.", "Personal Cloud Library Source");
                return;
            }

            if (createIfMissing)
            {
                Directory.CreateDirectory(folderPath);
            }

            Process.Start("explorer.exe", folderPath);
        }

        private static string GetSampleManifestJson()
        {
            return @"{
  ""version"": 2,
  ""items"": [
    {
      ""id"": ""example-adventure"",
      ""title"": ""Example Adventure"",
      ""platform"": ""Example Platform"",
      ""sourcePath"": ""ExampleAdventure/ExampleAdventure.bat"",
      ""cachePath"": ""ExampleAdventure\\ExampleAdventure.bat"",
      ""installDirectory"": ""ExampleAdventure"",
      ""launchFile"": ""ExampleAdventure.bat"",
      ""notes"": ""Fake local sample entry for testing. No game content is included.""
    }
  ]
}
";
        }
    }
}
