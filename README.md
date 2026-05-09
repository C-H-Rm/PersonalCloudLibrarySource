# Personal Cloud Library Source

A Playnite library plugin that imports user-supplied personal library entries into Playnite's normal library view.

## Current Status

Personal Cloud Library Source supports provider-based manifest import from a local JSON file, a local folder or mounted drive, and generic rclone remotes.

## What It Does

- Reads a JSON manifest and imports every valid entry into Playnite.
- Shows cached/local entries as installed with a Play action.
- Shows cloud-only or missing entries as uninstalled.
- Exposes `Download to local cache` when the provider can copy the entry source file.
- Supports Google Drive, OneDrive, Dropbox, and similar providers through the user's existing rclone setup.
- Supports local folders, external drives, mounted drives, and NAS folders directly without rclone.

## What It Does Not Do

This plugin does not provide games, ROMs, BIOS files, cracks, keys, copyrighted artwork, download links, scraping, or copyrighted content. Users are responsible for only using files they own or have rights to use.

The plugin does not authenticate to Google Drive or OneDrive directly, does not store OAuth credentials, and does not auto-download before launch.

## Setup

1. Build Debug Any CPU.
2. Add this folder as a Playnite external extension:

   ```text
   D:\PersonalCloudLibrarySource\PersonalCloudLibrarySource\bin\Debug
   ```

3. Restart Playnite.
4. Configure one provider:

   ```text
   SourceProviderType = LocalFile
   LocalManifestPath = D:\PersonalCloudLibrarySource\samples\personal-cloud-library.sample.json
   LocalCacheFolder = D:\PersonalCloudLibraryCache
   ```

5. Run Update Game Library.

## Provider Modes

`LocalFile` reads a specific manifest file. Downloads are only possible when an item's `sourcePath` can be resolved as a local file path.

`LocalFolder` reads a manifest from `LocalLibraryRoot + ManifestRelativePath` and copies item files from `LocalLibraryRoot + sourcePath`.

`RcloneRemote` reads a manifest with `rclone cat` and downloads item files with `rclone copyto`.

Example rclone settings:

```text
SourceProviderType = RcloneRemote
RcloneExecutablePath = rclone
RcloneRemoteName = google_drive
RcloneManifestPath = PersonalLibrary/personal-cloud-library.sample.json
RcloneContentRoot = PersonalLibrary/files
RcloneTimeoutSeconds = 30
LocalCacheFolder = D:\PersonalCloudLibraryCache
AllowDownloads = true
```

Example local folder settings:

```text
SourceProviderType = LocalFolder
LocalLibraryRoot = E:\PersonalLibrary
ManifestRelativePath = personal-cloud-library.sample.json
LocalCacheFolder = D:\PersonalCloudLibraryCache
AllowDownloads = true
```

## Expected Sample Entries

- Example Adventure
- Example Puzzle Pack
- Example Homebrew Demo

## Manifest Format

The manifest is a JSON file with a top-level `version` and an `items` array. Items use `sourcePath` for provider source files. Legacy `remotePath` remains supported as a fallback.

See [docs/manifest-format.md](docs/manifest-format.md) for the field reference.

## Roadmap

- Phase 1: local manifest validation and UI polish.
- Phase 2: provider-based local folder and rclone copy support.
- Phase 3: cache verification/hash checks.
