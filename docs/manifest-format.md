# Manifest Format

Personal Cloud Library Source reads a JSON manifest with a top-level `version` and an `items` array.

## Version 2 Recommended Format

Version 2 manifests should prefer `sourcePath` and `cachePath`.

```json
{
  "version": 2,
  "items": [
    {
      "id": "example-adventure",
      "title": "Example Adventure",
      "platform": "Example Platform",
      "sourcePath": "ExampleAdventure/ExampleAdventure.bat",
      "cachePath": "ExampleAdventure\\ExampleAdventure.bat",
      "installDirectory": "ExampleAdventure",
      "launchFile": "ExampleAdventure.bat",
      "notes": "Fake local sample entry for testing."
    }
  ]
}
```

## Version 1 Compatibility

Version 1 manifests remain supported. Existing fields such as `localPath`, `installDirectory`, `launchFile`, and legacy `remotePath` can still be used.

New manifests should use `sourcePath` instead of `remotePath`.

## Fields

- `version`: Manifest schema version.
- `items`: Array of library entries to import.
- `id`: Stable item identifier. Keep this value stable between imports so Playnite can recognize the same entry.
- `title`: Display name shown in Playnite.
- `platform`: Optional platform label for the entry.
- `sourcePath`: Provider source path used for install/download actions.
- `cachePath`: Preferred local cached launch file path. It can be absolute or relative to `LocalCacheFolder`.
- `localPath`: Legacy cached launch file path. It can be absolute or relative to `LocalCacheFolder`.
- `installDirectory`: Legacy cached install directory. It can be absolute or relative to `LocalCacheFolder`.
- `launchFile`: Launch file name used with `installDirectory`, and useful as a clear launch-file hint with `cachePath`.
- `remotePath`: Legacy fallback for `sourcePath`.
- `notes`: Optional text imported as the Playnite description.

## Path Resolution

`cachePath` is preferred for the local cached launch file. If `cachePath` is not present, the plugin falls back to `localPath`, then `installDirectory + launchFile`.

`sourcePath` is preferred for provider source files. If `sourcePath` is not present, the plugin falls back to legacy `remotePath`.

`sourcePath` points to the source provider path. In LocalFolder mode it is relative to `LocalLibraryRoot`. In RcloneRemote mode it is relative to `RcloneContentRoot` when that setting is provided.

`cachePath` points to the local cache destination. It can be absolute, but relative paths are usually better because they resolve inside `LocalCacheFolder`.

Cloud-only items are normal. If the cached launch file is missing, the item should still import and appear as uninstalled when `TreatMissingFilesAsUninstalled` is enabled.

After import, Playnite metadata tools can enrich entries with covers, descriptions, genres, screenshots, and other metadata before the item is downloaded or copied into the local cache.

## Provider Behavior

`LocalFile` reads `LocalManifestPath`. If downloads are used, `sourcePath` can be absolute or relative to the manifest folder.

`LocalFolder` reads `LocalLibraryRoot + ManifestRelativePath` and copies files from `LocalLibraryRoot + sourcePath`.

`RcloneRemote` reads the manifest with `rclone cat RcloneRemoteName:RcloneManifestPath`. Downloads use `rclone copyto RcloneRemoteName:RcloneContentRoot/sourcePath localCachePath`. If `RcloneContentRoot` is empty, `sourcePath` is used directly.

When `RcloneContentRoot` is set, keep `sourcePath` relative to that root. Do not repeat the content root inside each item.

Correct:

```text
RcloneContentRoot = PersonalLibrary/files
sourcePath = Game/Game.exe
```

Incorrect:

```text
RcloneContentRoot = PersonalLibrary/files
sourcePath = PersonalLibrary/files/Game/Game.exe
```

The incorrect form becomes `PersonalLibrary/files/PersonalLibrary/files/Game/Game.exe`.

When `TreatMissingFilesAsUninstalled` is true, entries with missing cached launch files are imported as uninstalled. The plugin does not auto-download before launch.
