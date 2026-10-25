# Optional two-way mod folder sync (private 0.1.2 candidate)

This feature is off by default. It does not integrate Ludusavi, discover mods, install mod loaders, switch profiles, or modify the base game stored in the cloud.

## Layout and configuration

With `SourceProviderType = RcloneRemote` and `RcloneContentRoot = games`:

```text
games/
  GameNaam/           # existing base game source
  mods/
    GameNaam/         # contents of the separately configured local mod folder
```

Add these optional fields to an existing manifest item:

```json
{
  "id": "stable-game-id",
  "title": "GameNaam",
  "sourcePath": "GameNaam",
  "installDirectory": "GameNaam",
  "launchFile": "Game.exe",
  "modPath": "Mods",
  "modFolder": "GameNaam"
}
```

- `modPath`: local folder, relative to the installation directory, or an explicit absolute path such as `D:\\MyMods\\GameNaam`. Relative paths may not escape the installation directory. The mod folder may not contain the game launch file. No links/junctions are followed.
- `modFolder`: optional stable, single cloud folder name under `mods`. Defaults to the title with Windows-invalid characters replaced by underscores. Set it explicitly to avoid changing the cloud location when renaming a game, and to distinguish editions with the same title. Colliding destinations are refused.
- No `modPath`: existing import/download/launch behavior is unchanged; no mod sync.
- In plugin settings, enable **Optional two-way mod synchronization**. Its timeout is separate from the existing manifest/copy timeout; default 1800 seconds.
- Update the Playnite library after changing the manifest. Mod actions use the locally cached last-good imported manifest, avoiding network reads merely to open menus. Provider settings and game mappings must match the retained pairing.

## First link and normal use

1. Install/download the game normally.
2. Create its chosen local mod folder (it can be empty).
3. Right-click the game and choose **Personal Cloud Library → Sync mods**.
4. Review and confirm the actual local/cloud paths. Initial linkage merges files present on either side. Same-path files with different content are refused before bisync; neither is overwritten. Resolve those differences yourself, then retry.
5. Thereafter mods sync before game launch, after game stop, and on demand. Local additions, changes and deletions propagate to cloud; cloud changes and deletions propagate back on this or another sequentially used PC.

The local mod folder contains a reserved `.pcls-modsync-…` access marker, also present in cloud. Do not delete it. Sync listings and logs live in plugin user data under `mod-sync`, outside the game cache. Do not clear that state to "fix" a normal deletion: it is how bisync distinguishes deletions from additions.

This is event-based, not real-time background sync. Close the game before manual sync. Do not have another sync tool manage the same mod folder. In v1, run mod sync on one PC at a time; there is no distributed cross-PC lock. Concurrent independent file changes between completed runs are preserved as conflict copies, but simultaneous sync operations on different PCs are not a supported workflow.

## Deletions, conflicts and reinstall

- Removing an individual local mod file removes its cloud counterpart on the next sync. The reverse also works.
- Clearing all tracked user files while retaining the folder and marker requires confirmation before propagating that change. Cancelling leaves the other side untouched.
- Missing local folder/marker, missing cloud marker, missing launch file or offline errors pause sync instead of interpreting the problem as a deletion.
- Changes to the same file on both sides are preserved with `pcls-conflict` suffixes and reported. Inspect the files before playing: a conflict copy is not automatically a valid mod configuration.
- The plugin pauses mod sync before uninstall removes cached game files. Cloud mods are preserved.
- After reinstall, use **Relink mods after reinstall**, not a normal sync. Confirm the paths: old sync state is archived and cloud/local contents are merged into a new baseline. This also supports explicit recovery after failed initial linkage; it is never run automatically after an error.
- A failed pre-launch sync offers an explicit choice to start anyway or cancel. A failed post-play sync is reported and does not imply that cloud changes succeeded.

## Limits and safeguards

The implementation uses rclone bisync with access checks, local operation locking, retained state, conflict copies and recovery flags. Required flags are checked before linking. Tested with rclone 1.75.2; use a compatible current rclone binary. The feature does not change rclone credentials.

Normal bisync compares the metadata supported by rclone's default comparison; this is not a complete byte-for-byte verification of every mod on every run. Initial common-file comparison uses `rclone check --download`. Whole-folder deletion confirmation is additional to marker/installation checks; it is not a backup system. Use existing Nextcloud recovery/backup facilities as appropriate.

Files only: empty subdirectory structure is not guaranteed to replicate. Absolute external mod folders are intentionally supported, but must be a dedicated mod folder, not your whole user directory or a mod manager's shared multi-game store. Mod paths that overlap another configured game's mod directory or encompass any game launch file are refused. Bases whose cloud source overlaps the cloud mod destination are refused.

## Verified locally / still to check

- Real rclone tests use two isolated local endpoints, never CHR's Nextcloud or game library.
- The complete .NET Framework 4.8 plugin, including generated WPF XAML resources, is built against Playnite SDK 6.16.0.
- Windows Playnite runtime behavior, rendered settings/dialogs, and the real Nextcloud/WebDAV endpoint have not yet been exercised. Use a disposable mod test folder first; do not interpret a successful cross-build as a Windows UI test.

Test command (requires .NET 8 SDK and rclone on PATH, or `PCLS_RCLONE` set to its executable path):

```text
dotnet run --project tests/ModSync.Tests/ModSync.Tests.csproj
```

Windows acceptance checks: import a disposable item with/without modPath; enable the setting; manually link; add/update/delete mods both ways; clear contents and cancel/confirm; verify start waits for sync; verify stop triggers sync; cancel a failed launch; uninstall/reinstall and explicitly relink; confirm cloud base game stays unchanged. Exercise two PCs sequentially and a deliberately offline endpoint.
