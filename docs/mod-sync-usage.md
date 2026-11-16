# Using optional mod synchronization

Enable mod sync in the plugin settings and update the game library after adding or changing a game's `modPath`. Right-click one game owned by this library and choose **Personal Cloud Library → Sync mods**. Games without a configured `modPath` do not participate.

## Automatic restoration during installation

With Mod Sync enabled, the RcloneRemote installer restores optional cloud mods **after** the base game has downloaded successfully, and only for a game with a nonblank configured `modPath`. The imported roots are captured before the base download and compared with the live manifest again before any mod writes. No ordinary installation confirmation or separate global-progress window is required; the inline status control needs the [theme integration](inline-install-progress.md).

A genuinely new missing local mods folder is created for this installation, not interpreted as a request to delete cloud mods. A completed prior pairing at the exact same local/cloud identity is archived before a deliberate reinstall establishes a fresh merge baseline. An interrupted initial pairing is never reset automatically. Differing existing files and names differing only by case (ambiguous on Windows) are refused, not silently overwritten. A successful restore leaves a real rclone baseline, so the next ordinary sync does not repeat initial pairing/comparison.

If optional mods fail or are cancelled, the base game **stays installed**. The inline status offers **Retry mods**, without redownloading the game. An interrupted setup offers **Recover mods** instead; its actual archive/merge confirmation is still asked explicitly on the UI, outside the worker/lock. Dismissing recovery does not grant future permission. Once this installation has established a baseline, a mod-only retry uses ordinary synchronization rather than initializing again. Potential whole-folder clears still need the normal **Sync mods** confirmation.

The initial content preflight uses SHA-1/MD5 only when the particular overlapping objects expose a compatible nonempty hash; advertised backend support alone is insufficient. Missing/mixed native hashes fall back to actual-content comparison. This can avoid an extra cloud-content read during initial verification; it is not a guarantee that every bisync phase or every backend performs zero reads, nor a measurement of the user's Nextcloud.

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
