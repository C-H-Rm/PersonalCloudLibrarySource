param(
    [switch]$DebugSymbols,
    [string]$MSBuildPath,
    [string]$TargetFrameworkRootPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'PersonalCloudLibrarySource/PersonalCloudLibrarySource.csproj'
$projectOutput = Join-Path $repoRoot 'PersonalCloudLibrarySource/bin/Release'
$distRoot = Join-Path $repoRoot 'dist'
$extensionManifest = Join-Path $repoRoot 'PersonalCloudLibrarySource/extension.yaml'
$versionMatches = [regex]::Matches((Get-Content -LiteralPath $extensionManifest -Raw), '(?m)^Version:[ \t]*([0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?)[ \t]*\r?$')
if ($versionMatches.Count -ne 1) { throw 'Extension manifest must contain exactly one numeric Version.' }
$version = $versionMatches[0].Groups[1].Value
$null = [version]::Parse($version)

if (-not $MSBuildPath) {
    $command = Get-Command msbuild -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $command) { $command = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1 }
    if (-not $command) { throw 'MSBuild or dotnet was not found. Use Visual Studio desktop build tools, or pass -MSBuildPath.' }
    $MSBuildPath = $command.Source
}
$buildCommand = Get-Command $MSBuildPath -CommandType Application -ErrorAction Stop | Select-Object -First 1
$buildArguments = @()
if ([System.IO.Path]::GetFileNameWithoutExtension($buildCommand.Source) -ieq 'dotnet') { $buildArguments += 'msbuild' }
$buildArguments += @($project, '/t:Rebuild', '/p:Configuration=Release', '/p:Platform=AnyCPU', '/nologo', '/v:minimal')
if ($TargetFrameworkRootPath) {
    $buildArguments += "/p:TargetFrameworkRootPath=$TargetFrameworkRootPath"
}

& $buildCommand.Source @buildArguments | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Release rebuild failed with exit code $LASTEXITCODE. No new package was created." }

$null = New-Item -ItemType Directory -Path $distRoot -Force
$stageRoot = Join-Path $distRoot ('.package-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $stageRoot 'payload'
$null = New-Item -ItemType Directory -Path $payload
$packageName = "PersonalCloudLibrarySource-$version.pext"
$packagePath = Join-Path $distRoot $packageName
try {
    foreach ($fileName in @('PersonalCloudLibrarySource.dll', 'extension.yaml', 'icon.png')) {
        $sourcePath = Join-Path $projectOutput $fileName
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw "Required extension file missing: $sourcePath" }
        Copy-Item -LiteralPath $sourcePath -Destination $payload
    }
    $localizationPath = Join-Path $projectOutput 'Localization'
    if (-not (Test-Path -LiteralPath $localizationPath -PathType Container)) { throw 'Built localization folder is missing.' }
    Copy-Item -LiteralPath $localizationPath -Destination $payload -Recurse

    $stagedPackage = Join-Path $stageRoot $packageName
    [System.IO.Compression.ZipFile]::CreateFromDirectory($payload, $stagedPackage, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    $verified = & (Join-Path $PSScriptRoot 'verify-package.ps1') -PackagePath $stagedPackage
    if ($verified.Version -cne $version) { throw 'Built manifest version does not match the source manifest.' }

    $sourceCommit = $null
    $sourceDirty = $null
    if ((Test-Path -LiteralPath (Join-Path $repoRoot '.git')) -and (Get-Command git -CommandType Application -ErrorAction SilentlyContinue)) {
        $sourceCommit = (& git -C $repoRoot rev-parse HEAD | Out-String).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Could not determine source commit.' }
        $status = @(& git -C $repoRoot status --porcelain)
        if ($LASTEXITCODE -ne 0) { throw 'Could not determine source worktree status.' }
        $sourceDirty = $status.Count -gt 0
    }
    $receipt = [ordered]@{
        version = $version
        addonId = $verified.AddonId
        module = $verified.Module
        sourceCommit = $sourceCommit
        sourceDirty = $sourceDirty
        package = $packageName
        sha256 = $verified.Sha256
        files = @($verified.Files)
        wpfResources = @($verified.WpfResources)
        runtimeTested = $false
    }
    $receiptName = "PersonalCloudLibrarySource-$version.build.json"
    $checksumName = "$packageName.sha256"
    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText((Join-Path $stageRoot $receiptName), ($receipt | ConvertTo-Json -Depth 5), $encoding)
    [System.IO.File]::WriteAllText((Join-Path $stageRoot $checksumName), "$($verified.Sha256)  $packageName`n", $encoding)

    if ($DebugSymbols) {
        $pdbPath = Join-Path $projectOutput 'PersonalCloudLibrarySource.pdb'
        if (-not (Test-Path -LiteralPath $pdbPath -PathType Leaf)) { throw 'Debug symbols were requested but the PDB is missing.' }
        $symbols = Join-Path $stageRoot 'symbols'
        $null = New-Item -ItemType Directory -Path $symbols
        Copy-Item -LiteralPath $pdbPath -Destination $symbols
        $symbolsName = "PersonalCloudLibrarySource-$version-debug-symbols.zip"
        [System.IO.Compression.ZipFile]::CreateFromDirectory($symbols, (Join-Path $stageRoot $symbolsName))
        Move-Item -LiteralPath (Join-Path $stageRoot $symbolsName) -Destination (Join-Path $distRoot $symbolsName) -Force
    }
    foreach ($name in @($packageName, $receiptName, $checksumName)) {
        Move-Item -LiteralPath (Join-Path $stageRoot $name) -Destination (Join-Path $distRoot $name) -Force
    }

    # No shell interpolation of branch names or PR titles. Only validated version-derived paths.
    if ($env:GITHUB_OUTPUT) {
        $outputs = @(
            "package_path=dist/$packageName",
            "package_name=$packageName",
            "receipt_path=dist/$receiptName",
            "checksum_path=dist/$checksumName",
            "version=$version"
        ) -join "`n"
        [System.IO.File]::AppendAllText($env:GITHUB_OUTPUT, $outputs + "`n", $encoding)
    }
    Write-Host "Created verified package: $packagePath"
    [pscustomobject]@{ PackagePath = $packagePath; Version = $version; Sha256 = $verified.Sha256 }
}
finally {
    # Only remove the unique temporary directory made by this invocation.
    if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
}
