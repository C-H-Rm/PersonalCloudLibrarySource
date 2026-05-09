# Manifest Format

MVP 0 reads a local JSON manifest with this structure:

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
- `localPath`: Launch file path. It can be absolute or relative to `LocalCacheFolder`.
- `installDirectory`: Directory for the entry. It can be absolute or relative to `LocalCacheFolder`.
- `launchFile`: Launch file name used with `installDirectory` when `localPath` is not supplied.
- `notes`: Optional text imported as the Playnite description.

When `TreatMissingFilesAsUninstalled` is true, entries with missing launch files are imported as uninstalled.
