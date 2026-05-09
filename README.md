# Personal Cloud Library Source

A Playnite library plugin that imports user-supplied personal library entries from a local JSON manifest.

## Current Status

MVP 0: local JSON manifest import works.

## What It Does

Personal Cloud Library Source reads a local JSON manifest, imports entries into Playnite, and launches local files if they exist.

Phase 2A also supports reading the same manifest JSON through an existing rclone remote by running `rclone cat`.

## What It Does Not Do

This plugin does not provide games, ROMs, BIOS files, cracks, keys, copyrighted artwork, download links, scraping, or copyrighted content.

## Setup

1. Build Debug Any CPU.
2. Add this folder as a Playnite external extension:

   ```text
   D:\PersonalCloudLibrarySource\PersonalCloudLibrarySource\bin\Debug
   ```

3. Restart Playnite.
4. Configure:

   ```text
   ManifestSourceMode = LocalFile
   LocalManifestPath = D:\PersonalCloudLibrarySource\samples\personal-cloud-library.sample.json
   LocalCacheFolder = D:\PersonalCloudLibraryCache
   ```

5. Run Update Game Library.

## Phase 2A Rclone Manifest Mode

`LocalFile` remains the default manifest source mode.

`RcloneRemote` mode requires the user to configure rclone separately before using the plugin. The plugin does not authenticate to Google Drive directly, does not store OAuth credentials, and does not download game files in this phase.

Example settings:

```text
ManifestSourceMode = RcloneRemote
RcloneExecutablePath = rclone
RcloneRemoteName = my_remote
RcloneManifestPath = PersonalLibrary/manifest.json
RcloneTimeoutSeconds = 30
LocalCacheFolder = D:\PersonalCloudLibraryCache
```

## Expected Sample Entries

- Example Adventure
- Example Puzzle Pack
- Example Homebrew Demo

## Manifest Format

The manifest is a JSON file with a top-level `version` and an `items` array. It can be loaded from a local file or fetched from an rclone remote. Each item can define a stable `id`, a display `title`, optional `platform`, path fields for launch resolution, and optional `notes`.

See [docs/manifest-format.md](docs/manifest-format.md) for the current MVP 0 field reference.

## Roadmap

- Phase 1: local manifest validation and UI polish.
- Phase 2: optional rclone provider.
- Phase 3: cache verification/hash checks.
