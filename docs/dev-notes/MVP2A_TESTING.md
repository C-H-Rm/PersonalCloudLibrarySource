# MVP 2A Testing

## LocalFile Regression Test

Use these settings:

```text
ManifestSourceMode = LocalFile
LocalManifestPath = D:\PersonalCloudLibrarySource\samples\personal-cloud-library.sample.json
LocalCacheFolder = D:\PersonalCloudLibraryCache
Enabled = true
TreatMissingFilesAsUninstalled = true
```

Run Update Game Library in Playnite.

Expected entries:

- Example Adventure
- Example Puzzle Pack
- Example Homebrew Demo

Each entry should launch its fake `.bat` test file.

## RcloneRemote Manual Test

Use any existing rclone remote that contains the same JSON manifest format.

Test outside Playnite first:

```text
rclone cat my_remote:PersonalLibrary/manifest.json
```

Then configure the plugin:

```text
ManifestSourceMode = RcloneRemote
RcloneExecutablePath = rclone
RcloneRemoteName = my_remote
RcloneManifestPath = PersonalLibrary/manifest.json
RcloneTimeoutSeconds = 30
LocalCacheFolder = D:\PersonalCloudLibraryCache
Enabled = true
TreatMissingFilesAsUninstalled = true
```

Expected Playnite behavior:

Update Game Library imports entries if rclone returns valid JSON. This phase only retrieves the manifest; it does not download game files.
