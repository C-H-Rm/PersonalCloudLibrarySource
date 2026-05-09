# Personal Cloud Library Source

A Playnite library plugin that imports user-supplied personal library entries from a local JSON manifest.

## Current Status

MVP 0: local JSON manifest import works.

## What It Does

Personal Cloud Library Source reads a local JSON manifest, imports entries into Playnite, and launches local files if they exist.

Phase 2A also supports reading the same manifest JSON through an existing rclone remote by running `rclone cat`. Phase 2B can copy a selected missing item from the configured rclone remote into the local cache.

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

`RcloneRemote` mode requires the user to configure rclone separately before using the plugin. The plugin does not authenticate to Google Drive directly and does not store OAuth credentials. Manifest retrieval only reads JSON with `rclone cat`.

Example settings:

```text
ManifestSourceMode = RcloneRemote
RcloneExecutablePath = rclone
RcloneRemoteName = my_remote
RcloneManifestPath = PersonalLibrary/manifest.json
RcloneTimeoutSeconds = 30
LocalCacheFolder = D:\PersonalCloudLibraryCache
```

## Phase 2B Rclone Item Copy

Manifest items can include an optional `remotePath`. If an item is missing locally and `AllowRcloneDownloads` is enabled, Playnite can expose a `Download to local cache` install action for that item.

This phase does not auto-download before launch. It only copies a selected item from the user's configured rclone remote into the local cache. The plugin does not provide content; it only copies files from the remote configured by the user.

Manual rclone test command:

```text
rclone copyto my_remote:PersonalLibrary/files/ExampleAdventure/ExampleAdventure.bat D:\PersonalCloudLibraryCache\ExampleAdventure\ExampleAdventure.bat
```

## Expected Sample Entries

- Example Adventure
- Example Puzzle Pack
- Example Homebrew Demo

## Manifest Format

The manifest is a JSON file with a top-level `version` and an `items` array. It can be loaded from a local file or fetched from an rclone remote. Each item can define a stable `id`, a display `title`, optional `platform`, path fields for launch resolution, optional `remotePath`, and optional `notes`.

See [docs/manifest-format.md](docs/manifest-format.md) for the current MVP 0 field reference.

## Roadmap

- Phase 1: local manifest validation and UI polish.
- Phase 2: optional rclone provider.
- Phase 3: cache verification/hash checks.
