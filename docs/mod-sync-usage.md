# Using optional mod synchronization

Enable mod sync in the plugin settings and update the game library after adding or changing a game's `modPath`. Right-click one game owned by this library and choose **Personal Cloud Library → Sync mods**. Games without a configured `modPath` do not participate.

## Download mods first; synchronize after installation

With Mod Sync enabled and a nonblank per-game `modPath`, installation **only downloads cloud mods to the local mod folder**. It does not upload local files, delete files, perform an initial content comparison, or run bisync/relink on the installation critical path. Filename/size metadata inventory and transferred-file integrity checks remain; those are not a two-way comparison. Existing local files are preserved with `--ignore-existing`; any differences are reviewed later, not silently overwritten. Reserved synchronization markers are not downloaded as ordinary mods.

The imported mapping is captured before the base-game copy and compared with a freshly loaded live manifest before writing mods. Path/link/name-collision protections and the local operation lock remain. A failed or cancelled mod download keeps the base game installed and offers a **download-only Retry mods** action. This action never authorizes recovery or baseline reset.

After the download finishes, the controller clears `IsInstalling` and emits the SDK **Installed** event. Only then does a separate background queue begin the two-way work. Installation does not await that queue. Background status has an actionable cancellation notification; it is not a separate global-progress popup. Theme-host integration is still required for the optional inline status control.

Completed matching old pairing state is retained/paused during download so removed cache files cannot be propagated as cloud deletions. The later background initializer may archive that completed state and establish a real new bisync baseline. Interrupted initial state is retained for explicit recovery; no `ready` file or baseline is fabricated by downloading. Background conflicts/failures do not uninstall the game or trigger an automatic resync loop; review the normal Sync mods action when needed.

Launching the just-installed game defers pending background work or cancels/drains an active background worker before launch, without restarting the initial comparison in the foreground. Pending work resumes after the game stops. The queue is local to the running plugin, not a persistent external scheduler; stopping Playnite cancels its work.

The native per-object hash optimization applies to the later initial-sync comparison, not to the installation download. The background phase can still be expensive on a backend without native hashes. No particular Nextcloud throughput is promised by local fixture tests.

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
