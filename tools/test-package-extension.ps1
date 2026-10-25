param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem
$verify = Join-Path $PSScriptRoot 'verify-package.ps1'
$sourcePackage = (Resolve-Path -LiteralPath $PackagePath).Path
$tempRoot = if ($env:TMPDIR) { $env:TMPDIR } else { [System.IO.Path]::GetTempPath() }
$fixtureRoot = Join-Path $tempRoot ('pcls-package-tests-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $fixtureRoot
$script:passed = 0
$script:failed = 0

function Set-ZipText {
    param($Archive, [string]$Name, [string]$Text)
    $old = $Archive.GetEntry($Name)
    if ($null -ne $old) { $old.Delete() }
    $entry = $Archive.CreateEntry($Name)
    $stream = $entry.Open()
    $writer = [System.IO.StreamWriter]::new($stream, [System.Text.UTF8Encoding]::new($false))
    try { $writer.Write($Text) }
    finally { $writer.Dispose() }
}

function Get-ZipText {
    param($Archive, [string]$Name)
    $stream = $Archive.GetEntry($Name).Open()
    $reader = [System.IO.StreamReader]::new($stream)
    try { return $reader.ReadToEnd() }
    finally { $reader.Dispose() }
}

function Test-RejectedPackage {
    param([string]$Name, [scriptblock]$Mutate, [string]$ExpectedError)
    $fixture = Join-Path $fixtureRoot ($Name + '.pext')
    Copy-Item -LiteralPath $sourcePackage -Destination $fixture
    $zip = [System.IO.Compression.ZipFile]::Open($fixture, [System.IO.Compression.ZipArchiveMode]::Update)
    try { & $Mutate $zip }
    finally { $zip.Dispose() }
    try {
        $null = & $verify -PackagePath $fixture
        Write-Host "FAIL $Name : invalid package was accepted"
        $script:failed++
    }
    catch {
        if ($_.Exception.Message -notmatch $ExpectedError) {
            Write-Host "FAIL $Name : unexpected rejection: $($_.Exception.Message)"
            $script:failed++
        }
        else { Write-Host "PASS $Name"; $script:passed++ }
    }
}

try {
    $verified = & $verify -PackagePath $sourcePackage
    if (-not $verified.Sha256 -or $verified.WpfResources.Count -lt 2) { throw 'Valid package failed validation.' }
    Write-Host 'PASS valid built plugin with compiled WPF resources'
    $script:passed++

    Test-RejectedPackage 'missing-module' { param($z) $z.GetEntry('PersonalCloudLibrarySource.dll').Delete() } 'Required package entry missing'
    Test-RejectedPackage 'missing-manifest' { param($z) $z.GetEntry('extension.yaml').Delete() } 'Required package entry missing'
    Test-RejectedPackage 'missing-icon' { param($z) $z.GetEntry('icon.png').Delete() } 'Required package entry missing'
    Test-RejectedPackage 'missing-localization' { param($z) $z.GetEntry('Localization/en_US.xaml').Delete() } 'Required package entry missing'
    Test-RejectedPackage 'bad-module' { param($z) Set-ZipText $z 'PersonalCloudLibrarySource.dll' 'Not a managed DLL' } 'Bad IL format|BadImageFormat|invalid|bad format|format of the file'
    Test-RejectedPackage 'unexpected-sdk' { param($z) Set-ZipText $z 'Playnite.SDK.dll' 'must not be bundled' } 'Unexpected package entry'
    Test-RejectedPackage 'unexpected-credentials' { param($z) Set-ZipText $z 'auth.json' '{"test":"not a secret"}' } 'Unexpected package entry'
    Test-RejectedPackage 'traversal' { param($z) Set-ZipText $z '../outside.txt' 'test' } 'Unexpected package entry'
    Test-RejectedPackage 'duplicate-case' { param($z) Set-ZipText $z 'ICON.PNG' 'duplicate' } 'Duplicate package entry'
    Test-RejectedPackage 'nested-root' { param($z) Set-ZipText $z 'payload/extension.yaml' 'test' } 'Unexpected package entry'
    Test-RejectedPackage 'empty-icon' { param($z) Set-ZipText $z 'icon.png' '' } 'Empty package entry'
    Test-RejectedPackage 'wrong-addon-id' { param($z)
        Set-ZipText $z 'extension.yaml' ((Get-ZipText $z 'extension.yaml') -replace '(?m)^Id:.*$', 'Id: OtherPlugin')
    } 'Unexpected Playnite add-on ID'
    Test-RejectedPackage 'wrong-module-name' { param($z)
        Set-ZipText $z 'extension.yaml' ((Get-ZipText $z 'extension.yaml') -replace '(?m)^Module:.*$', 'Module: Missing.dll')
    } 'Invalid Playnite'
    Test-RejectedPackage 'duplicate-version' { param($z)
        Set-ZipText $z 'extension.yaml' ((Get-ZipText $z 'extension.yaml') + "`nVersion: 9.9.9`n")
    } 'exactly one Version'
    Test-RejectedPackage 'invalid-version' { param($z)
        Set-ZipText $z 'extension.yaml' ((Get-ZipText $z 'extension.yaml') -replace '(?m)^Version:.*$', 'Version: 0.1.bad')
    } 'Version must'
    Test-RejectedPackage 'overflow-version' { param($z)
        Set-ZipText $z 'extension.yaml' ((Get-ZipText $z 'extension.yaml') -replace '(?m)^Version:.*$', 'Version: 99999999999999.1.2')
    } 'Version must|too large|Int32|parse|format'
    Test-RejectedPackage 'empty-name' { param($z)
        Set-ZipText $z 'extension.yaml' ((Get-ZipText $z 'extension.yaml') -replace '(?m)^Name:.*$', 'Name:')
    } 'empty Name|Invalid Playnite|exactly one Name'
    Test-RejectedPackage 'broken-localization' { param($z) Set-ZipText $z 'Localization/en_US.xaml' '<broken>' } 'XML|end of file|closed|Unexpected'
    Test-RejectedPackage 'dtd-localization' { param($z)
        Set-ZipText $z 'Localization/en_US.xaml' '<!DOCTYPE ResourceDictionary SYSTEM "file:///not-read"><ResourceDictionary/>'
    } 'DTD'

    Write-Host "RESULT packaging: $script:passed passed, $script:failed failed"
    if ($script:failed -ne 0) { throw 'Package validation guard tests failed.' }
}
finally {
    # Disposable fixtures created above only; never remove arbitrary caller paths.
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
}
