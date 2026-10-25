# Optional two-way mod folder sync (approved scope)

Local-only implementation against e36d1aaa48ccf965159f579c592c0b7c428e2d8e. No push, release, real Nextcloud writes or Ludusavi integration.

## Acceptance

- Existing entries without `modPath` behave unchanged.
- Optional per-entry `modPath` (relative to installation, or explicit absolute local path) syncs to `<RcloneContentRoot>/mods/<game name>`; optional stable `modFolder` disambiguates names.
- Keep base game remote content untouched. Reject overlapping/unsafe local or remote mappings, reparse points, duplicate mod destinations.
- Use rclone bisync with durable per-game state outside the install/cache, plus local/remote access marker. Initialize only explicitly, never automatically resync after an error.
- Additions, updates and deletions propagate in both directions, including across sequentially used PCs. Preserve conflicting copies; report conflicts.
- Initial common-path content conflicts refuse before bisync. Initial linkage requires confirmation.
- Missing local folder/marker, missing remote marker, missing launch file, or uninstall must not erase cloud mods. Existing empty tracked folder is different from missing folder; clearing all content requires confirmation.
- Integrate before game start (wait; offer cancel launch on failure), after stop, and manual Sync mods menu. Prevent concurrent operations and syncing while game is running. Cache last-good manifest for offline metadata lookup but do not silently operate on stale changed mappings.
- Document settings, first-link/reinstall flow, limits and recovery. No filesystem watcher in v1.

## Work/verification

1. SDK-independent C# sync coordinator and process runner; first write executable red tests.
2. Real rclone tests against isolated local endpoints only: upload, download, modifications, both deletion directions, conflicts, empty-clear confirmation, missing-folder guards, failed-init recovery, serialized operations.
3. Minimal Playnite SDK adapter/settings and manifest additions; validate against SDK signatures and build where platform permits.
4. Independent review and deliver source patch/package with exact test results. Windows Playnite UI must be honestly labelled untested if unavailable.
