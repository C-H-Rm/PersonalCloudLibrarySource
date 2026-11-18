# ZIP mod downloads during installation

This feature downloads one optional archive during game installation using the configured rclone executable and remote. It does not alter the regular game-file transfer or require any cloud-side writes.

## Exact archive contract

For a configured `RcloneContentRoot` of `games`, literal game ID `a1b2`, and `modPath` resolving to `D:\\Games\\Example\\Mods`, the archive object is:

```text
<RcloneContentRoot>/mods/<literal game id>.zip
=> <rclone remote>:games/mods/a1b2.zip
```

The ZIP archive root is the content of `modPath`: `mod.dll` installs directly in `Mods`, and `nested/settings.ini` installs beneath `Mods/nested`. Do not put a wrapping `mods/` or game-ID directory inside the ZIP. Use the literal game ID, not the game title.

The optional download runs only for the existing enabled RcloneRemote mod setting and nonblank live `modPath`; the live manifest must still match the imported mapping. An absent/unreadable archive or invalid ZIP reports an optional mod failure while leaving the successfully installed game installed. There is no fallback to loose-file mod folders.

Before extraction, the destination must not exist or must be an empty directory. Existing populated mod paths are refused, never merged or overwritten. Extraction occurs in an owned sibling stage and is committed only after the whole ZIP validates and extracts. Cancellation/corruption/unsafe names do not commit staged files. ZIP paths reject traversal, rooted/drive paths, backslashes, Windows-invalid/device names, duplicate and case-insensitive collisions, and symlink/reparse entries. Temporary archive/stage files are cleaned up; pre-existing user data is never automatically cleaned up.

## Download-only behavior

- No ordinary loose-file mod inventory/listing is run for the initial restore.
- Backup and restore have no plugin-imposed entry limit and are capped at 64 GiB uncompressed. On local drives, ZIP creation and extraction check for enough free space with a 1 GiB reserve. This check is not a guarantee against concurrent disk usage.
- No automatic post-install bisync, launch/exit transfers, or loose-folder migration.
- If the ZIP step fails, the game remains installed. In the Downloads window, use **History → Retry mods** for the current failure after correcting the cause. This does not repeat the game download.

## Optional one-way backup

Set **Automatic upload interval in minutes** to enable local → cloud ZIP backups for idle installed games and before uninstall. `0` disables both. Unchanged mods make no server request. Changed mods are staged and published to the same ZIP path; empty folders and existing archives without a known baseline are not overwritten. If the required backup fails or mods change during upload, uninstall stops without deleting the cached copy. Use one active editing PC per modpack; this is a snapshot, not a merge.

## Verification

The test suite exercises ZIP extraction guards and uses actual rclone `copyto` against a disposable filesystem source. It does not verify rendered Playnite UI or a live Nextcloud instance.
