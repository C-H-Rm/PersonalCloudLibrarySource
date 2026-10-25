# Building the Playnite package with GitHub Actions

## What the workflow does

`.github/workflows/build-plugin.yml` runs on a Windows 2022 hosted runner:

1. Check out the event's source without persisting Git credentials.
2. Install a .NET 8 SDK (`global.json` keeps SDK selection on .NET 8).
3. Rebuild the .NET Framework 4.8/WPF plugin from source.
4. Package the built DLL, `extension.yaml`, icon and localization as `PersonalCloudLibrarySource-<Version>.pext`.
5. Inspect the actual archive, managed assembly, Playnite SDK reference and compiled settings/app BAML. Reject missing/empty/unexpected files, wrong add-on metadata and invalid localization XML.
6. Run the package-validator regression tests against the real build and deliberately broken copies.
7. Upload the single `.pext` directly as a downloadable artifact, plus a separate checksum/build-info artifact.

No GitHub Release is created, no tag is pushed, no Playnite add-on listing is changed, no Nextcloud credentials are needed and no real game library is accessed. Permissions are `contents: read`; action revisions are pinned to commit SHAs. Artifacts expire after 14 days.

The workflow has been prepared locally. A real GitHub-hosted run still requires the files to be pushed with CHR's approval. Do not describe local execution or actionlint validation as a green hosted CI run.

## Starting and downloading a build

Once the workflow is present on the repository's default branch:

1. Open **Actions → Build Playnite plugin → Run workflow**.
2. Select the branch and run it.
3. Open the successful run and its **Artifacts** section.
4. Download `PersonalCloudLibrarySource-<Version>.pext`. The pinned upload-artifact action supports direct single-file download, without wrapping the plugin in an extra ZIP.
5. Open/drag that `.pext` into Playnite and restart when prompted.

The workflow also runs for relevant pushes to `main` and pull requests targeting `main`. It does not run on arbitrary feature-branch pushes or publish releases automatically. Fork PRs use the ordinary unprivileged `pull_request` event, never `pull_request_target`.

A successful build proves that a real, structurally verified plugin package was produced. It does not prove runtime behavior in Playnite, rendered Windows dialogs, mod compatibility or the actual Nextcloud endpoint.

## One packaging entrypoint for local and CI use

In PowerShell, from the repository root:

```powershell
./tools/package-extension.ps1
```

The script discovers MSBuild on PATH, otherwise falls back to `dotnet msbuild`. It no longer assumes one hard-coded Visual Studio edition/version path. GitHub Actions explicitly selects `dotnet` so local verification can exercise the same entrypoint:

```powershell
./tools/package-extension.ps1 -MSBuildPath (Get-Command dotnet).Source
```

Prerequisites: desktop .NET Framework 4.8 targeting/WPF build tools; a .NET 8 SDK for the CI/dotnet route. The project explicitly imports the installed WinFX build targets so its XAML is compiled, rather than shipping a DLL without generated settings resources. The Playnite SDK reference currently lives in the repository's tracked `PersonalCloudLibrarySource/packages` directory; it is used to compile, not included in the plugin package.

Optional separate PDB ZIP:

```powershell
./tools/package-extension.ps1 -DebugSymbols
```

Build outputs:

```text
dist/PersonalCloudLibrarySource-<Version>.pext
dist/PersonalCloudLibrarySource-<Version>.pext.sha256
dist/PersonalCloudLibrarySource-<Version>.build.json
```

The version comes from the source `extension.yaml`; invalid or duplicate version declarations fail. A rebuild/validation failure stops before publishing a new package or GitHub output paths. A previous successful local package may remain in `dist`; a failed invocation must not be mistaken for a new successful build.

The build receipt records commit/worktree status when Git metadata is available, package checksum and members, compiled WPF resources, and `runtimeTested: false`. Extracted source archives without Git metadata use null commit/worktree fields rather than inventing provenance.

## Re-checking a package

```powershell
./tools/verify-package.ps1 -PackagePath ./dist/PersonalCloudLibrarySource-0.1.2.pext
./tools/test-package-extension.ps1 -PackagePath ./dist/PersonalCloudLibrarySource-0.1.2.pext
```

The regression suite includes missing module/manifest/icon/localization, bad managed DLL, accidentally bundled SDK/credentials, traversal/nested roots, case-duplicate entries, empty files, wrong ID/module, invalid/duplicate/overflow version, empty display name, malformed XML and prohibited DTDs. Test fixtures are disposable; no production cloud or user library is involved.

For Linux cross-build verification only, `-TargetFrameworkRootPath <reference-pack>/build/` can point to Microsoft's .NET Framework 4.8 reference pack. This is an explicit local toolchain input, not a change to the target framework. CI uses the Windows runner's installed targeting pack. Windows runner execution and Playnite runtime remain separate verification gates.

Maintainers can exercise the complete packaging entrypoint in a fresh local clone, including deliberate compiler/validation failures and GitHub output emission:

```text
python tools/test-build-pipeline.py --pwsh <pwsh> --dotnet <dotnet> --actionlint <actionlint>
```

Commit the local candidate first: the test clones that exact commit, not a dirty working tree. On Linux add `--framework-root <reference-pack>/build/`. The test preserves its disposable fixture for inspecting failures and copies the successful package/receipts plus `ci-local-verification.json` into `dist`. It makes no GitHub/Nextcloud requests or pushes.
