# Manifest Format

Personal Cloud Library Source reads a JSON manifest with this structure:

```json
{
  "version": 1,
  "items": [
    {
      "id": "example-adventure",
      "title": "Example Adventure",
      "platform": "Example Platform",
      "localPath": "ExampleAdventure\\ExampleAdventure.bat",
      "installDirectory": "ExampleAdventure",
      "launchFile": "ExampleAdventure.bat",
      "sourcePath": "ExampleAdventure/ExampleAdventure.bat",
      "notes": "Fake local sample entry for testing."
    }
  ]
}
```

## Fields

- `version`: Manifest schema version.
- `items`: Array of library entries to import.
- `id`: Stable item identifier. Keep this value stable between imports so Playnite can recognize the same entry.
- `title`: Display name shown in Playnite.
- `platform`: Optional platform label for the entry.
- `localPath`: Cached launch file path. It can be absolute or relative to `LocalCacheFolder`.
- `installDirectory`: Cached install directory. It can be absolute or relative to `LocalCacheFolder`.
- `launchFile`: Launch file name used with `installDirectory` when `localPath` is not supplied.
- `sourcePath`: Optional provider source path used for install/download actions.
- `remotePath`: Legacy fallback for `sourcePath`. New manifests should prefer `sourcePath`.
- `notes`: Optional text imported as the Playnite description.

## Provider Path Behavior

`LocalFile` reads `LocalManifestPath`. If downloads are used, `sourcePath` can be absolute or relative to the manifest folder.

`LocalFolder` reads `LocalLibraryRoot + ManifestRelativePath` and copies files from `LocalLibraryRoot + sourcePath`.

`RcloneRemote` reads the manifest with `rclone cat RcloneRemoteName:RcloneManifestPath`. Downloads use `rclone copyto RcloneRemoteName:RcloneContentRoot/sourcePath localCachePath`. If `RcloneContentRoot` is empty, `sourcePath` is used directly.

When `TreatMissingFilesAsUninstalled` is true, entries with missing cached launch files are imported as uninstalled. The plugin does not auto-download before launch.
