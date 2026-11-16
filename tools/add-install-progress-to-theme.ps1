param(
    [Parameter(Mandatory = $true)][string]$InputFile,
    [Parameter(Mandatory = $true)][string]$OutputFile
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$source = [IO.Path]::GetFullPath($InputFile)
$target = [IO.Path]::GetFullPath($OutputFile)
if ($source -eq $target) { throw 'Choose a separate output file. The active theme is never overwritten by this helper.' }
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw 'Input XAML file does not exist.' }

# Parse safely and preserve the theme's namespace declarations and whitespace.
$settings = [Xml.XmlReaderSettings]::new()
$settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
$settings.XmlResolver = $null
$document = [Xml.XmlDocument]::new()
$document.PreserveWhitespace = $true
$document.XmlResolver = $null
$reader = [Xml.XmlReader]::Create($source, $settings)
try { $document.Load($reader) } finally { $reader.Dispose() }
$presentation = 'http://schemas.microsoft.com/winfx/2006/xaml/presentation'
$xaml = 'http://schemas.microsoft.com/winfx/2006/xaml'
$namespaces = [Xml.XmlNamespaceManager]::new($document.NameTable)
$namespaces.AddNamespace('w', $presentation)
$namespaces.AddNamespace('x', $xaml)
$buttons = @($document.SelectNodes("//w:Button[@Name='PART_ButtonContextAction' or @x:Name='PART_ButtonContextAction']", $namespaces))
if ($buttons.Count -ne 1) { throw 'Expected one install/context button in this view. No theme changes made.' }
$row = $buttons[0].ParentNode
$column = $row.ParentNode
if ($row.LocalName -ne 'StackPanel' -or $row.GetAttribute('Orientation') -ne 'Horizontal' -or
    $column.LocalName -ne 'StackPanel' -or $column.GetAttribute('Orientation') -eq 'Horizontal') {
    throw 'Unsupported button layout: needs a horizontal button row inside a vertical StackPanel. Add the documented host manually; no theme changes made.'
}
$slots = @($document.SelectNodes("//w:ContentControl[@Name='PCLS_InstallProgress' or @x:Name='PCLS_InstallProgress']", $namespaces))
if ($slots.Count -gt 1) { throw 'Duplicate PCLS install-progress hosts found. No theme changes made.' }
if ($slots.Count -eq 1) {
    $next = $slots[0].NextSibling
    while ($next -and ($next.NodeType -eq [Xml.XmlNodeType]::Whitespace -or $next.NodeType -eq [Xml.XmlNodeType]::SignificantWhitespace)) { $next = $next.NextSibling }
    if ($slots[0].ParentNode -ne $column -or $next -ne $row) { throw 'Existing progress host is not immediately above the button row.' }
} else {
    $slot = $document.CreateElement('ContentControl', $presentation)
    $name = $document.CreateAttribute('x', 'Name', $xaml)
    $name.Value = 'PCLS_InstallProgress'
    [void]$slot.Attributes.Append($name)
    $indent = if ($row.PreviousSibling -and $row.PreviousSibling.NodeType -eq [Xml.XmlNodeType]::Whitespace) { $row.PreviousSibling.CloneNode($true) } else { $null }
    [void]$column.InsertBefore($slot, $row)
    if ($indent) { [void]$column.InsertBefore($indent, $row) }
}
$writerSettings = [Xml.XmlWriterSettings]::new()
$writerSettings.Encoding = [Text.UTF8Encoding]::new($false)
$writerSettings.Indent = $false
$writerSettings.OmitXmlDeclaration = -not ($document.FirstChild -is [Xml.XmlDeclaration])
$buffer = [IO.MemoryStream]::new()
$writer = [Xml.XmlWriter]::Create($buffer, $writerSettings)
try { $document.Save($writer); $writer.Flush(); $bytes = $buffer.ToArray() } finally { $writer.Dispose(); $buffer.Dispose() }
if (Test-Path -LiteralPath $target) {
    $existing = [IO.File]::ReadAllBytes($target)
    if ([Convert]::ToBase64String($existing) -cne [Convert]::ToBase64String($bytes)) { throw 'Output already exists with different content. Choose a new output file.' }
} else {
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
    $stream = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
}
Write-Output "Prepared theme copy: $target"
Write-Output 'Original theme unchanged. Review/apply this copy to the corresponding view before restarting Playnite.'
