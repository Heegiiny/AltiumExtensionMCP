<#
.SYNOPSIS
  Register (or unregister) the AltiumExtensionMCP extension in the Altium profile's ExtensionsRegistry.xml.

.DESCRIPTION
  Altium Designer only loads extension servers that are listed in
  <profile>\Extensions\ExtensionsRegistry.xml (this is what "Extensions & Updates" shows as installed).
  Copying the DLL/.ins into the Extensions folder is not enough on a fresh machine. The Altium Developer
  wizard normally writes this entry; this script does the same thing idempotently so that a migration is
  just: Detect-AltiumEnvironment -> dotnet build (deploys) -> Register-Extension -> Start-AltiumWithBridge.

  Altium must be closed while the file is edited (it rewrites the registry on exit).

.EXAMPLE
  .\tools\Register-Extension.ps1
  .\tools\Register-Extension.ps1 -Unregister
#>
[CmdletBinding()]
param(
    [switch]$Unregister,
    [string]$Hrid = 'AltiumExtensionMCP',
    # Stable GUIDs so re-registration does not create duplicates / new identities.
    [string]$ExtensionGuid = '6F1E0C5A-3B7D-4C2E-9A61-2D4B8E7F1A10',
    [string]$VersionGuid = 'B2A7C9D1-4E6F-4A8B-8C3D-5E1F2A6B7C90',
    [string]$Version = '0.1.0',
    [string]$Title = 'Altium Vibe-EE MCP bridge',
    [string]$Description = 'MCP bridge: exposes read-only Altium project/schematic/PCB data to LLM agents over a local HTTP API.'
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\AltiumEnvironment.ps1"
$e = Get-AltiumEnvironment
if (-not $e.AltiumProfileRoot) { throw 'Altium profile unknown. Run tools\Detect-AltiumEnvironment.ps1 first.' }

$registryPath = Join-Path $e.AltiumProfileRoot 'Extensions\ExtensionsRegistry.xml'
if (-not (Test-Path $registryPath)) { throw "ExtensionsRegistry.xml not found: $registryPath" }

$running = @(Get-Process X2 -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $e.AltiumExe })
if ($running.Count -gt 0) { throw "Altium is running (pid $($running.Id -join ', ')). Close it first; it rewrites ExtensionsRegistry.xml on exit." }

$deployDir = $e.AltiumExtensionDeployDir
if (-not $Unregister -and -not (Test-Path (Join-Path $deployDir 'AltiumExtensionMCP.ins'))) {
    throw "Extension not deployed at $deployDir. Run: dotnet build AltiumMcp.slnx -c Debug"
}

Copy-Item $registryPath "$registryPath.bak" -Force
[xml]$xml = Get-Content $registryPath -Raw
$root = $xml.DocumentElement
$existing = @($root.SelectNodes("Item[@HRID='$Hrid']"))

if ($Unregister) {
    foreach ($n in $existing) { [void]$root.RemoveChild($n) }
    $xml.Save($registryPath)
    Write-Host "Unregistered '$Hrid' ($($existing.Count) entr$(if ($existing.Count -eq 1) {'y'} else {'ies'}) removed) in $registryPath"
    return
}

# Template: clone the first existing item to keep every field Altium expects, then overwrite ours.
$template = $root.SelectSingleNode('Item')
if (-not $template) { throw 'ExtensionsRegistry.xml has no Item to use as a template.' }

$item = $template.CloneNode($true)
$item.SetAttribute('HRID', $Hrid)
$item.SetAttribute('Guid', $ExtensionGuid)
function Set-Child([System.Xml.XmlElement]$el, [string]$name, [string]$value) {
    $c = $el.SelectSingleNode($name)
    if (-not $c) { $c = $el.OwnerDocument.CreateElement($name); [void]$el.AppendChild($c) }
    $c.InnerText = $value
}
$oleNow = [string](Get-Date).ToOADate()
Set-Child $item 'Path' $deployDir
Set-Child $item 'Status' '0'
Set-Child $item 'VaultGuid' ''
Set-Child $item 'CreatedBy' 'AltiumExtensionMCP project'
Set-Child $item 'CategoryGuid' '793A1F67-0B22-4E01-A5DE-3176A1E8C60D'   # "Extensions" category (from the .epd)
Set-Child $item 'CategoryName' ''
Set-Child $item 'ReadMe' ''
Set-Child $item 'Help' ''
Set-Child $item 'Requirements' ''
Set-Child $item 'Title' $Title
Set-Child $item 'ShortDescription' $Description
Set-Child $item 'LongDescription' $Description
Set-Child $item 'SmallImage' ''
Set-Child $item 'LargeImage' ''
Set-Child $item 'Version' $Version
Set-Child $item 'VersionGuid' $VersionGuid
Set-Child $item 'ReleasedDate' $oleNow
Set-Child $item 'ReleaseNotes' ''
Set-Child $item 'DateInstalled' $oleNow

foreach ($n in $existing) { [void]$root.RemoveChild($n) }
[void]$root.AppendChild($item)
$xml.Save($registryPath)
Write-Host "Registered '$Hrid' -> $deployDir in $registryPath (backup: $registryPath.bak)"
