# MVP 2B Testing

## LocalFile Regression Test

Use the existing sample settings:

```text
ManifestSourceMode = LocalFile
LocalManifestPath = D:\PersonalCloudLibrarySource\samples\personal-cloud-library.sample.json
LocalCacheFolder = D:\PersonalCloudLibraryCache
Enabled = true
TreatMissingFilesAsUninstalled = true
AllowRcloneDownloads = true
```

Run Update Game Library in Playnite.

Expected entries:

- Example Adventure
- Example Puzzle Pack
- Example Homebrew Demo

Existing local files should still launch normally.

## Prepare Fake Remote Test Files

Use an existing rclone remote that you control. Upload only the fake `.bat` test files:

```text
rclone mkdir my_remote:PersonalLibrary/files/ExampleAdventure
rclone copy D:\PersonalCloudLibraryCache\ExampleAdventure\ExampleAdventure.bat my_remote:PersonalLibrary/files/ExampleAdventure
```

Repeat for the other fake sample entries if needed.

## Manual Rclone Copy Check

Test outside Playnite first:

```text
rclone cat romcade_drive:PersonalLibrary/files/ExampleAdventure/ExampleAdventure.bat
```

To simulate a missing local file:

```text
Move-Item "D:\PersonalCloudLibraryCache\ExampleAdventure\ExampleAdventure.bat" "D:\PersonalCloudLibraryCache\ExampleAdventure\ExampleAdventure.bat.bak"
```

After the Playnite download action runs, verify:

```text
Test-Path "D:\PersonalCloudLibraryCache\ExampleAdventure\ExampleAdventure.bat"
```

## Playnite Install Action Test

Temporarily move or delete one local fake `.bat` test file from the cache, then run Update Game Library.

Expected behavior:

- The entry imports as uninstalled when `TreatMissingFilesAsUninstalled` is true.
- Playnite exposes the `Download to local cache` install action for entries with `remotePath`.
- Running the action copies the fake file from rclone into the local cache.
- The plugin does not auto-download before launch.
- The plugin does not provide content; it only copies files from the user's configured rclone remote.
