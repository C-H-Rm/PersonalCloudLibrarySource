# Personal Cloud Library Source

A Playnite library plugin that imports user-supplied personal library entries from a local JSON manifest.

## Current Status

MVP 0: local JSON manifest import works.

## What It Does

Personal Cloud Library Source reads a local JSON manifest, imports entries into Playnite, and launches local files if they exist.

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
   LocalManifestPath = D:\PersonalCloudLibrarySource\samples\personal-cloud-library.sample.json
   LocalCacheFolder = D:\PersonalCloudLibraryCache
   ```

5. Run Update Game Library.

## Expected Sample Entries

- Example Adventure
- Example Puzzle Pack
- Example Homebrew Demo

## Manifest Format

The manifest is a local JSON file with a top-level `version` and an `items` array. Each item can define a stable `id`, a display `title`, optional `platform`, path fields for launch resolution, and optional `notes`.

See [docs/manifest-format.md](docs/manifest-format.md) for the current MVP 0 field reference.

## Roadmap

- Phase 1: local manifest validation and UI polish.
- Phase 2: optional rclone provider.
- Phase 3: cache verification/hash checks.
