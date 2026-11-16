# Using optional mod synchronization

Enable mod sync in the plugin settings and update the game library after adding or changing a game's `modPath`. Right-click one game owned by this library and choose **Personal Cloud Library → Sync mods**. Games without a configured `modPath` do not participate.

## Progress and cancellation

The progress dialog shows the current phase and, when rclone supplies them, transferred bytes, speed and checked-file counts. A phase with no known total stays indeterminate rather than inventing a percentage. Verification can read files without changing them; a busy verification phase is not proof that all mods are being uploaded again.

**Cancel** stops the current operation, including its rclone process and current-manifest lookup. Already completed transfers are retained. Cancellation does not disable mod sync or delete its pairing state. If cancelling the sync started before a game launch, that particular launch is cancelled too; the game is never silently launched with unverified mods. Click Play again or use Sync mods when ready.

Successful manual operations no longer need a success dialog to be dismissed. Results and actionable errors use a per-game Playnite notification instead; failures include a diagnostic path where available. Clicking a retry notification validates the current configuration again, rather than blindly replaying an old command.

## Retrying and recovery

- After cancelling or declining a question, use **Sync mods** again. Approvals belong only to the individual operation and are not stored as blanket permission.
- If an interrupted first linkage or uninstall requires recovery, ordinary **Sync mods** offers the existing explicit recovery confirmation. The separate menu action is called **Recover or relink mods**.
- Recovery archives the previous state and deliberately establishes a new pairing after confirmation. It does not silently choose local-wins/cloud-wins or automatically run `--resync` after a normal error.
- A failed recovery is not retried indefinitely. Diagnose the actual connectivity/conflict/path problem using the reported log, then retry.
- Missing folders/markers, changed live mappings, initial conflicts and potentially destructive whole-folder clears still pause the operation. Do not manually delete state or access markers to make a warning disappear.

This is sequential-PC synchronization, not a distributed lock. Close the game and finish synchronization on one PC before continuing on another.

## Verification scope

Local real-rclone tests and a full SDK/WPF build validate the implementation and package. They do not replace testing the rendered Playnite dialogs on Windows or prove performance against a particular Nextcloud backend. Generic loopback WebDAV timing/content-read measurements must be labelled separately from actual Nextcloud measurements.
