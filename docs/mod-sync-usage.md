# Optional mod ZIP downloads

Enable **Download the configured mod ZIP during game installation** and update the game library after adding or changing a game's `modPath`. Mod ZIP downloads are optional and run only during game installation, or through the install controller's **Retry mods** action after the base game is installed.

## Archive contract

For an `RcloneContentRoot` of `games` and literal game ID `a1b2`, the archive is `<rclone remote>:games/mods/a1b2.zip`. The ZIP root contains the contents that should be extracted into the configured `modPath`; it must not contain a wrapping folder.

The feature requires the enabled plugin, enabled ZIP-download setting, RcloneRemote provider, and a nonblank `modPath` in the current live manifest. The current mapping is checked before writing. Missing, inaccessible, or invalid ZIP files produce an actionable optional-mod failure while keeping the base game installed. **Retry mods** downloads only the ZIP again; it does not redownload the base game or authorize recovery.

Extraction is staged and validates every path before publication. ZIP traversal/rooted/device/invalid names, duplicate or case-insensitive collisions, links and reparse-point entries are rejected. To protect existing user data, a populated destination is refused without modification; an absent or empty destination is eligible for extraction. Cancellation and extraction failure do not publish partial staged contents.

## No synchronization lifecycle

This shipped mode is strictly cloud-to-client ZIP download during game installation. It never uploads files, propagates deletions, runs loose-folder inventory/bisync/relink, or synchronizes automatically on game launch or exit. No manual Sync mods or recovery menu action is exposed. Existing pairing/configuration bytes are not migrated, reset, or rewritten by ZIP installation. Legacy loose-folder sync code is retained only as unreachable rollback code.

## Verification scope

Local tests exercise ZIP safety and real rclone transfers against disposable local data. They do not verify a Windows Playnite runtime/UI, a live Nextcloud service, or custom theme rendering. Never use a production mod folder or live cloud data as a test fixture.
