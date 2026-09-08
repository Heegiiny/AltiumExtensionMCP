<#
.SYNOPSIS
  Call a bridge method through the debug CLI without PowerShell JSON-quoting problems.

.DESCRIPTION
  Params are passed as a hashtable (converted to JSON and fed via stdin), so backslashes in paths and
  quotes need no escaping. Dot-source for the helper functions, or run directly.

.EXAMPLE
  . .\tools\Invoke-Bridge.ps1
  Invoke-Bridge system.ping
  Invoke-Bridge sch.getSheet @{ documentPath = "$(Get-TestProjectDir)\Bluetooth_Sentinel.SchDoc" }
  (Invoke-Bridge pcb.listNets @{ filter = 'GND' } -Raw) | ConvertFrom-Json

.EXAMPLE
  .\tools\Invoke-Bridge.ps1 project.listComponents @{ filter = 'U*'; limit = 5 }
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Method,
    [Parameter(Position = 1)][hashtable]$Params,
    [switch]$Raw
)

. "$PSScriptRoot\AltiumEnvironment.ps1"

function Get-TestProjectDir {
    param([string]$Name = 'Bluetooth Sentinel')
    $e = Get-AltiumEnvironment
    return (Join-Path $e.AltiumMcpTestProjectsRoot $Name)
}

function Invoke-Bridge {
    <# Returns the pretty JSON text from the CLI (or raw text on error). Throws if the CLI is missing. #>
    param(
        [Parameter(Mandatory, Position = 0)][string]$Method,
        [Parameter(Position = 1)][hashtable]$Params,
        [switch]$Raw
    )
    $e = Get-AltiumEnvironment
    if (-not (Test-Path $e.McpServerExe)) { throw "MCP server not built: $($e.McpServerExe). Run: dotnet build AltiumMcp.slnx -c Debug -p:DeployToAltium=false" }
    $prevEnc = [Console]::OutputEncoding
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    try {
        if ($Params) {
            $json = $Params | ConvertTo-Json -Depth 10 -Compress
            $out = $json | & $e.McpServerExe --call $Method - 2>&1
        } else {
            $out = & $e.McpServerExe --call $Method 2>&1
        }
        $text = ($out | ForEach-Object { "$_" }) -join "`n"
        if ($Raw) { return $text }
        Write-Output $text
    } finally {
        [Console]::OutputEncoding = $prevEnc
    }
}

if ($Method) {
    if ($Raw) { Invoke-Bridge -Method $Method -Params $Params -Raw } else { Invoke-Bridge -Method $Method -Params $Params }
}
