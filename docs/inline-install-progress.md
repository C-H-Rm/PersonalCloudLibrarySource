# Inline installation progress

The plugin offers the supported Playnite custom element **`PCLS_InstallProgress`**. It belongs above the install/context-button row in the selected game's theme. This is an inline game-details control, not a separate global progress dialog.

## Transfer display

- Preparation is labelled as inventory/checking; no percentage is invented before the required transfer total is known.
- The game-download percentage uses a fixed total after preparation, not the partial total discovered so far. Speed and byte counts come from the actual running transfer.
- A metadata/check-first pass must not download whole files merely to calculate their sizes. Already cached/skipped files must not inflate the remaining download.
- Only successful transfer completion can reach 100%; final executable/install-state validation remains a separately labelled finishing step.
- A retry counter reset keeps the original total but shows an explicit retry phase without an unreliable overall percentage; it does not disguise retransferred bytes behind a high-water percentage.
- The optional cloud-to-local mod download uses the same area as a separately named phase. It performs no two-way initialization on the install critical path. Only after the SDK Installed event and cleared installation state may separately cancellable background synchronization begin; its phase must not redefine the game's denominator or keep the game marked as installing.
- Routine results do not need an extra modal window. A base-download failure remains visible with its error; use Playnite's installation action to retry. Completed status is removed.

## Required theme integration

A plugin cannot place a supported custom control at an arbitrary coordinate in every theme. The theme needs this host **immediately before its horizontal installation/action-button row**, inside the surrounding vertical container:

```xml
<ContentControl x:Name="PCLS_InstallProgress" />
```

The SDK binds the selected game through `PluginUserControl.GameContext`; the control hides for unrelated games or when there is no installation/status to show. It unsubscribes when unloaded and marshals transfer updates onto its WPF dispatcher.

`tools/add-install-progress-to-theme.ps1` prepares a separate XAML copy for the standard-style layout (`PART_ButtonContextAction` in a horizontal StackPanel inside a vertical StackPanel). It also supports Daze's single-row Grid by inserting a vertical wrapper and retaining grid placement. Grids with additional elements/definitions are refused rather than rearranged:

```powershell
./tools/add-install-progress-to-theme.ps1 -InputFile './DetailsViewGameOverview.xaml' -OutputFile './prepared/DetailsViewGameOverview.xaml'
```

The helper never overwrites its input, refuses an existing different output, detects duplicate/unsupported layouts and prohibits DTD parsing. Review the generated copy and retain a backup/custom copy of the original theme before applying it. Do not assume editing bundled default-theme files survives a Playnite update. Other layouts need an explicitly reviewed placement of the same host; the helper refuses rather than guessing.

The actual current theme and its rendered Windows layout must be verified during deployment. Registering the control or passing an SDK build alone does **not** prove that an unmodified theme displays it.

## Installable Daze integration

The private **Daze - Cloud Progress** theme is a separate copy of the official Daze 2.3.1 release. Install its `.pthm`, select **Daze - Cloud Progress** in Playnite Desktop's theme settings, and restart as requested by Playnite. Keep the Personal Cloud Library plugin build with `PCLS_InstallProgress` enabled. A `.pthm` is a theme, not a replacement plugin `.pext`.

Both Details and Grid game overviews include the host directly above their installation/action row. The status is only visible for the selected cloud-library game while its download/background status exists; no idle progress bar is shown. The original Daze remains installed and selectable. Choosing the original Daze reverses this UI change. Existing ThemeModifier choices are not automatically migrated to the new theme identity.

Reproduce from the pinned official input (download separately; the tool does not access the network):

```text
python tools/prepare-daze-progress-theme.py --input Daze_27790ca9-d3a4-480f-bffe-914ec6768363_2_3_1.pthm --output Daze-Cloud-Progress-2.3.1.2.pthm --pwsh <pwsh executable>
```

The source archive SHA-256 is pinned in the tool. All original resources remain byte-identical except `theme.yaml` and the two overview XAMLs. A new ID/name prevents replacing upstream Daze; its MIT license and an integration note are included. The package format preserves the original Playnite theme ZIP structure. Upstream source: https://github.com/dariolupo/Daze_Theme/releases/tag/v2.3.1 . Native manifest and Toolbox packaging format inspected at Playnite commit `e2e6efcda311fea38fcd92f61c8ca90811d97f28`.

Package/layout checks do not prove rendered Windows operation. Verify on the user's real Playnite installation before calling the visible placement runtime-tested.

## Local verification

- `dotnet run --project tests/InstallProgress.Tests/InstallProgress.Tests.csproj -c Release`
- `dotnet run --project tests/CacheControllers.Tests/CacheControllers.Tests.csproj -c Release`
- `python -B tools/test-install-progress-theme.py --pwsh <pwsh executable>`
- `python -B tools/test-daze-progress-theme.py --input <official Daze 2.3.1 .pthm> --pwsh <pwsh executable>`
- Native transfer regressions in `tests/RcloneTransfers.Tests` plus full .NET Framework 4.8/SDK/WPF/package validation.

State/controller fixtures are explicitly labelled doubles; they do not pretend to be a rendered Playnite UI test. Native rclone checks use disposable local files/endpoints, not the user's real Nextcloud.
