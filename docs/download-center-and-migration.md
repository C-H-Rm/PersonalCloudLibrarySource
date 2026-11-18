# Download center and standalone migration

## Standalone identity

**Cloud Storage Integration** is a separate build from the upstream add-on:

- Add-on ID: `CloudStorageIntegration_402561c2-ea33-4c60-a8f8-ec25e997a1d5`.
- Runtime plugin ID: `402561c2-ea33-4c60-a8f8-ec25e997a1d5`.
- Assembly/module: `CloudStorageIntegration.dll`. The source namespace is retained, not its old CLR assembly identity.

- Own data directory under Playnite's extension-data root, keyed by the new runtime UUID.

The upstream add-on catalog identifies the original extension. This standalone build has a distinct add-on ID and assembly; an upstream update does not overwrite it. This does not add an automatic update feed or publish a release.

Adoption preserves the copied configuration bytes and last-write time. The historical default library name is mapped to the standalone name for display only; custom library names are preserved. Changing settings later is a separate explicit user action.

## Switching an existing library

1. Finish downloads, stop running cloud games, and disable the original **Personal Cloud Library Source** extension. Restart Playnite. Do not delete its games or data.
2. Install the standalone `CloudStorageIntegration-0.2.0.pext`; restart as requested.
3. Use the explicit adoption prompt to copy settings and safety state and transfer ownership of existing game records. Their Playnite database IDs must be preserved: do not delete/reimport them. A separate fresh setup is also possible; it is not implicit migration.
4. Check existing games, settings and cached installation paths before removing the disabled old extension. Keep the original data for rollback.

Adoption does not download, upload, bisync, clear folders or change game-install directories. It must refuse active legacy ownership, in-flight/running games, duplicate ownership and conflicting destination state rather than guessing. Existing mod-sync history/baselines are not reset or marked ready to make migration pass. Original source data remains intact. The migration menu provides a deliberate retry route if the initial prompt was cancelled.

## Download center

The Desktop top-panel down-arrow item opens one modeless window. It observes the same per-operation state as the inline game-details bar; it is not another download engine or a queue scheduler. Live operations are visible across games, without selecting each one. Optional mod ZIP downloads are identified separately from base-game downloads.

Active rows show available real progress and transfer metrics. Unknown totals are indeterminate. History distinguishes successful, failed, cancelled and interrupted attempts; a retry is another operation, not a rewrite of the previous result. A bounded history survives restarts. An operation left active at shutdown is shown as interrupted on the next start, not falsely resumed. History begins with this version; earlier builds did not record it.

The top-panel item uses Playnite's supported host and follows the user's Appearance / Top Panel alignment preference. It does not move other extensions or modify a global appearance setting. The download center does not require a custom theme.

## Verification boundary

Linked-source fixtures test operation identity, lifecycle/history/persistence and adoption safety. The full .NET Framework 4.8 build uses the actual Playnite SDK and compiled WPF resources, and the package verifier checks the actual payload. These are not a running Windows Playnite or real Nextcloud test. Confirm physical toolbar placement, window rendering, theme layout and adoption on the user's Windows installation before claiming those deployed effects.
