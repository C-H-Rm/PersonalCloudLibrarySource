# Private ZIP mod install prototype

This test-only package changes optional post-install mod restoration to download one archive using the configured rclone executable and remote. It does not alter the regular game-file transfer or mutate a live cloud service as part of its build/tests.

## Exact archive contract

For a configured `RcloneContentRoot` of `games`, literal game ID `a1b2`, and `modPath` resolving to `D:\\Games\\Example\\Mods`, the archive object is:

```text
<RcloneContentRoot>/mods/<literal game id>.zip
=> <rclone remote>:games/mods/a1b2.zip
```

The ZIP archive root is the content of `modPath`: `mod.dll` installs as `D:\\Games\\Example\\Mods\\mod.dll`, and `nested\\settings.ini` installs beneath `Mods\\nested`. Do not put a wrapping `mods/`, game-ID, or other top-level directory inside the ZIP. The game ID is used literally; this prototype does not derive a name from the title.

The optional download runs only for the existing enabled RcloneRemote mod setting and nonblank live `modPath`; the live manifest must still match the imported mapping. An absent/unreadable archive or invalid ZIP reports an optional mod failure while leaving the successfully installed game installed. There is no fallback to loose-file mod folders.

Before extraction, the destination must not exist or must be an empty directory. Existing populated mod paths are refused, never merged or overwritten. Extraction occurs in an owned sibling stage and is committed only after the whole ZIP validates and extracts. Cancellation/corruption/unsafe names do not commit staged files. ZIP paths reject traversal, rooted/drive paths, backslashes, Windows-invalid/device names, duplicate and case-insensitive collisions, and symlink/reparse entries. Temporary archive/stage files are cleaned up; pre-existing user data is never automatically cleaned up.

## Deliberately disabled for this prototype

- No ordinary loose-file mod inventory/listing is run for the initial restore.
- Automatic post-install two-way bisync is not enqueued. This prevents uploading the ZIP or applying a stale folder baseline/deletions. Manual mod menu operations otherwise remain the existing loose-folder behavior; do not use those against this prototype archive mapping.
- No cloud ZIP creation or migration is performed. Prepare the ZIP manually in an isolated test remote.

## Test safety and use

Use only an isolated local rclone remote. Do not point this prototype at real Nextcloud or a production mod folder. Install only in a disposable Playnite profile/library where the resolved modPath is absent/empty. A populated target intentionally fails instead of risking data loss.

The test suite exercises ZIP extraction guards and invokes actual rclone `copyto` against a disposable local filesystem source, then installs the downloaded bytes. It is not a Windows Playnite runtime/UI test, a Nextcloud test, or a verification of custom theme rendering. The private `.pext` is a candidate test artifact, not a published release.
