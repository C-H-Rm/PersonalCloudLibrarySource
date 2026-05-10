param(
    [switch]$DebugSymbols
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$msbuild = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
$solution = Join-Path $repoRoot "PersonalCloudLibrarySource\PersonalCloudLibrarySource.sln"
$configuration = "Release"
$projectOutput = Join-Path $repoRoot "PersonalCloudLibrarySource\bin\$configuration"
$distRoot = Join-Path $repoRoot "dist"
$packageFolder = Join-Path $distRoot "PersonalCloudLibrarySource"
$packagePath = Join-Path $distRoot "PersonalCloudLibrarySource-0.1.1.pext"
$debugPackagePath = Join-Path $distRoot "PersonalCloudLibrarySource-0.1.1-debug-symbols.zip"

if (-not (Test-Path -LiteralPath $msbuild)) {
    throw "MSBuild was not found at $msbuild"
}

& $msbuild $solution /p:Configuration=$configuration /p:Platform="Any CPU"
if ($LASTEXITCODE -ne 0) {
    throw "Release build failed with exit code $LASTEXITCODE"
}

if (Test-Path -LiteralPath $packageFolder) {
    Remove-Item -LiteralPath $packageFolder -Recurse -Force
}

New-Item -ItemType Directory -Path $packageFolder -Force | Out-Null

$requiredFiles = @(
    "PersonalCloudLibrarySource.dll",
    "extension.yaml",
    "icon.png"
)

foreach ($fileName in $requiredFiles) {
    $sourcePath = Join-Path $projectOutput $fileName
    if (-not (Test-Path -LiteralPath $sourcePath)) {
        throw "Required extension file missing: $sourcePath"
    }

    Copy-Item -LiteralPath $sourcePath -Destination $packageFolder -Force
}

$localizationPath = Join-Path $projectOutput "Localization"
if (Test-Path -LiteralPath $localizationPath) {
    Copy-Item -LiteralPath $localizationPath -Destination $packageFolder -Recurse -Force
}

if (Test-Path -LiteralPath $packagePath) {
    Remove-Item -LiteralPath $packagePath -Force
}

$packageZipPath = "$packagePath.zip"
if (Test-Path -LiteralPath $packageZipPath) {
    Remove-Item -LiteralPath $packageZipPath -Force
}

Compress-Archive -Path (Join-Path $packageFolder "*") -DestinationPath $packageZipPath -Force
Move-Item -LiteralPath $packageZipPath -Destination $packagePath -Force

if ($DebugSymbols) {
    $debugFolder = Join-Path $distRoot "PersonalCloudLibrarySource-debug-symbols"
    if (Test-Path -LiteralPath $debugFolder) {
        Remove-Item -LiteralPath $debugFolder -Recurse -Force
    }

    New-Item -ItemType Directory -Path $debugFolder -Force | Out-Null
    $pdbPath = Join-Path $projectOutput "PersonalCloudLibrarySource.pdb"
    if (Test-Path -LiteralPath $pdbPath) {
        Copy-Item -LiteralPath $pdbPath -Destination $debugFolder -Force
    }

    if (Test-Path -LiteralPath $debugPackagePath) {
        Remove-Item -LiteralPath $debugPackagePath -Force
    }

    Compress-Archive -Path (Join-Path $debugFolder "*") -DestinationPath $debugPackagePath -Force
}

Write-Host "Created package: $packagePath"
