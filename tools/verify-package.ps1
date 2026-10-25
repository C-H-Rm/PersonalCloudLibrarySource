param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Read-EntryText {
    param([System.IO.Compression.ZipArchiveEntry]$Entry)
    $stream = $Entry.Open()
    $reader = [System.IO.StreamReader]::new($stream)
    try { return $reader.ReadToEnd() }
    finally { $reader.Dispose() }
}

function Read-ManifestField {
    param([string]$Text, [string]$Name)
    $matches = [regex]::Matches($Text, '(?m)^' + [regex]::Escape($Name) + ':[ \t]*([^\r\n]*?)[ \t]*\r?$')
    if ($matches.Count -ne 1) { throw "Manifest must contain exactly one $Name field." }
    $value = $matches[0].Groups[1].Value.Trim().Trim('"', "'")
    if (-not $value) { throw "Manifest contains an empty $Name field." }
    return $value
}

$fullPath = (Resolve-Path -LiteralPath $PackagePath).Path
$archive = [System.IO.Compression.ZipFile]::OpenRead($fullPath)
try {
    $names = @($archive.Entries | ForEach-Object { $_.FullName })
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName
        if (-not $seen.Add($name)) { throw "Duplicate package entry: $name" }
        $isRequired = $name -cin @('PersonalCloudLibrarySource.dll', 'extension.yaml', 'icon.png')
        if (-not $isRequired -and $name -cnotmatch '^Localization/[^/\\]+\.xaml$') {
            throw "Unexpected package entry (dependencies, secrets and nested package roots are not allowed): $name"
        }
        if ($entry.Length -eq 0) { throw "Empty package entry: $name" }
        # Read every member to detect decompression/truncation failures, not only headers.
        $stream = $entry.Open()
        try { $stream.CopyTo([System.IO.Stream]::Null) }
        finally { $stream.Dispose() }
    }
    foreach ($required in @('PersonalCloudLibrarySource.dll', 'extension.yaml', 'icon.png', 'Localization/en_US.xaml')) {
        if ($names -cnotcontains $required) { throw "Required package entry missing: $required" }
    }

    $manifest = Read-EntryText $archive.GetEntry('extension.yaml')
    $id = Read-ManifestField $manifest 'Id'
    $version = Read-ManifestField $manifest 'Version'
    $module = Read-ManifestField $manifest 'Module'
    $type = Read-ManifestField $manifest 'Type'
    $icon = Read-ManifestField $manifest 'Icon'
    $displayName = Read-ManifestField $manifest 'Name'
    if ($id -cne 'PersonalCloudLibrarySource_61993828-67a8-4468-93a2-293442e36328') { throw 'Unexpected Playnite add-on ID.' }
    if ($version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Version must have three or four numeric components.' }
    $null = [version]::Parse($version)
    if ($module -cne 'PersonalCloudLibrarySource.dll' -or $type -cne 'GameLibrary' -or $icon -cne 'icon.png' -or -not $displayName) {
        throw 'Invalid Playnite module/type/icon/name metadata.'
    }

    $dllStream = $archive.GetEntry($module).Open()
    $memory = [System.IO.MemoryStream]::new()
    try { $dllStream.CopyTo($memory); $dllBytes = $memory.ToArray() }
    finally { $dllStream.Dispose(); $memory.Dispose() }
    # Metadata/resource inspection only; this does not instantiate or run the plugin.
    $assembly = [System.Reflection.Assembly]::Load($dllBytes)
    if ($assembly.GetName().Name -cne 'PersonalCloudLibrarySource') { throw 'DLL assembly name does not match the plugin.' }
    $sdk = @($assembly.GetReferencedAssemblies() | Where-Object { $_.Name -ceq 'Playnite.SDK' })
    if ($sdk.Count -ne 1) { throw 'Plugin DLL must reference Playnite.SDK.' }
    $resourceStream = $assembly.GetManifestResourceStream('PersonalCloudLibrarySource.g.resources')
    if ($null -eq $resourceStream) { throw 'Compiled WPF resources are missing.' }
    $resourceReader = [System.Resources.ResourceReader]::new($resourceStream)
    $resourceNames = @()
    try {
        $enumerator = $resourceReader.GetEnumerator()
        while ($enumerator.MoveNext()) { $resourceNames += [string]$enumerator.Key }
    }
    finally { $resourceReader.Dispose(); $resourceStream.Dispose() }
    foreach ($required in @('personalcloudlibrarysourcesettingsview.baml', 'app.baml')) {
        if ($resourceNames -cnotcontains $required) { throw "Compiled WPF resource missing: $required" }
    }

    foreach ($entry in $archive.Entries | Where-Object { $_.FullName -clike 'Localization/*.xaml' }) {
        $settings = [System.Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $text = [System.IO.StringReader]::new((Read-EntryText $entry))
        $xml = [System.Xml.XmlReader]::Create($text, $settings)
        try { while ($xml.Read()) {} }
        finally { $xml.Dispose(); $text.Dispose() }
    }

    $sha256 = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "Verified Playnite package: $([System.IO.Path]::GetFileName($fullPath)) (version $version)"
    [pscustomobject]@{
        Version = $version
        AddonId = $id
        Module = $module
        Sha256 = $sha256
        Files = $names
        WpfResources = $resourceNames
    }
}
finally { $archive.Dispose() }
