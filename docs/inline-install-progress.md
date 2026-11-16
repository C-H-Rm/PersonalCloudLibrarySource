# Inline installation progress

The plugin offers the supported Playnite custom element **`PCLS_InstallProgress`**. It belongs above the install/context-button row in the selected game's theme. This is an inline game-details control, not a separate global progress dialog.

## Transfer display

- Preparation is labelled as inventory/checking; no percentage is invented before the required transfer total is known.
- The game-download percentage uses a fixed total after preparation, not the partial total discovered so far. Speed and byte counts come from the actual running transfer.
- A metadata/check-first pass must not download whole files merely to calculate their sizes. Already cached/skipped files must not inflate the remaining download.
- Only successful transfer completion can reach 100%; final executable/install-state validation remains a separately labelled finishing step.
- A retry counter reset keeps the original total but shows an explicit retry phase without an unreliable overall percentage; it does not disguise retransferred bytes behind a high-water percentage.
- Optional mod restoration uses the same status area as a separate named phase. It must not silently redefine a game download's denominator or present an invented combined percentage for unequal stages.
- Routine results do not need an extra modal window. A base-download failure remains visible with its error; use Playnite's installation action to retry. Completed status is removed.

## Required theme integration

A plugin cannot place a supported custom control at an arbitrary coordinate in every theme. The theme needs this host **immediately before its horizontal installation/action-button row**, inside the surrounding vertical container:

```xml
<ContentControl x:Name="PCLS_InstallProgress" />
```

The SDK binds the selected game through `PluginUserControl.GameContext`; the control hides for unrelated games or when there is no installation/status to show. It unsubscribes when unloaded and marshals transfer updates onto its WPF dispatcher.

`tools/add-install-progress-to-theme.ps1` prepares a separate XAML copy for the standard-style layout (`PART_ButtonContextAction` in a horizontal StackPanel inside a vertical StackPanel):

```powershell
./tools/add-install-progress-to-theme.ps1 -InputFile './DetailsViewGameOverview.xaml' -OutputFile './prepared/DetailsViewGameOverview.xaml'
```

The helper never overwrites its input, refuses an existing different output, detects duplicate/unsupported layouts and prohibits DTD parsing. Review the generated copy and retain a backup/custom copy of the original theme before applying it. Do not assume editing bundled default-theme files survives a Playnite update. Other layouts need an explicitly reviewed placement of the same host; the helper refuses rather than guessing.

The actual current theme and its rendered Windows layout must be verified during deployment. Registering the control or passing an SDK build alone does **not** prove that an unmodified theme displays it.

## Local verification

- `dotnet run --project tests/InstallProgress.Tests/InstallProgress.Tests.csproj -c Release`
- `dotnet run --project tests/CacheControllers.Tests/CacheControllers.Tests.csproj -c Release`
- `python -B tools/test-install-progress-theme.py --pwsh <pwsh executable>`
- Native transfer regressions in `tests/RcloneTransfers.Tests` plus full .NET Framework 4.8/SDK/WPF/package validation.

State/controller fixtures are explicitly labelled doubles; they do not pretend to be a rendered Playnite UI test. Native rclone checks use disposable local files/endpoints, not the user's real Nextcloud.
