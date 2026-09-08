<#
.SYNOPSIS
  Start Altium Designer with the MCP bridge extension loaded and wait until the bridge answers.

.DESCRIPTION
  Paths come from Environment.local.props / registry via tools\AltiumEnvironment.ps1 (docs/ENVIRONMENT.md).
  If Altium is already running without the bridge, the -R switch cannot load the extension into it;
  use -Restart to close that instance first (WM_CLOSE, then kill after -RestartTimeoutSec), or start
  the bridge from inside Altium (File > MCP Bridge > Start, or DXP > Run Process > AltiumExtensionMCP:StartBridge).

.EXAMPLE
  .\tools\Start-AltiumWithBridge.ps1
  .\tools\Start-AltiumWithBridge.ps1 -Restart
  .\tools\Start-AltiumWithBridge.ps1 -Project "$env:USERPROFILE\AltiumMcpTestProjects\Bluetooth Sentinel\Bluetooth_Sentinel.PrjPcb"
#>
[CmdletBinding()]
param(
    [string]$AltiumExe = '',
    [string]$Project = '',
    [int]$TimeoutSec = 120,
    [switch]$Restart,
    [int]$RestartTimeoutSec = 60
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\AltiumEnvironment.ps1"
$envInfo = Get-AltiumEnvironment
if (-not $AltiumExe) { $AltiumExe = $envInfo.AltiumExe }
$discovery = $envInfo.BridgeDiscoveryFile

function Test-Bridge {
    if (-not (Test-Path $discovery)) { return $null }
    try {
        $d = Get-Content $discovery -Raw | ConvertFrom-Json
        if (-not (Get-Process -Id $d.processId -ErrorAction SilentlyContinue)) { return $null }
        $h = Invoke-RestMethod -Uri ($d.baseUrl + 'health') -TimeoutSec 3
        if ($h.status -eq 'ok') { return $d }
    } catch { }
    return $null
}

$running = @(Get-Process X2 -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $AltiumExe })
if ($running.Count -gt 0) {
    $live = Test-Bridge
    if ($live -and ($running.Id -contains $live.processId)) {
        Write-Host "Altium already running with bridge: $($live.baseUrl) (pid $($live.processId))"
    } elseif ($Restart) {
        Write-Host "Altium running without bridge (pid $($running.Id -join ', ')). Closing it..."
        foreach ($p in $running) { $p.CloseMainWindow() | Out-Null }
        $deadline = (Get-Date).AddSeconds($RestartTimeoutSec)
        while ((Get-Date) -lt $deadline -and (Get-Process -Id $running.Id -ErrorAction SilentlyContinue)) { Start-Sleep -Seconds 2 }
        $left = Get-Process -Id $running.Id -ErrorAction SilentlyContinue
        if ($left) { Write-Warning "Altium did not close in $RestartTimeoutSec s (modal dialog?). Killing."; $left | Stop-Process -Force; Start-Sleep -Seconds 3 }
        $running = @()
    } else {
        Write-Host "Altium already running (pid $($running.Id -join ', ')) but the bridge is down. -R cannot load the extension into a running instance."
        Write-Host "Either re-run with -Restart, or in Altium: File > MCP Bridge > Start  /  DXP > Run Process > AltiumExtensionMCP:StartBridge"
    }
}
if ($running.Count -eq 0) {
    Write-Host "Starting $AltiumExe -RAltiumExtensionMCP:StartBridge"
    Start-Process $AltiumExe -ArgumentList '-RAltiumExtensionMCP:StartBridge'
}

$deadline = (Get-Date).AddSeconds($TimeoutSec)
$d = $null
while ((Get-Date) -lt $deadline) {
    $d = Test-Bridge
    if ($d) { break }
    Start-Sleep -Seconds 2
}

if (-not $d) { throw "Bridge did not come up within $TimeoutSec s. See $($envInfo.BridgeLogDir)" }
Write-Host "Bridge OK: $($d.baseUrl) (pid $($d.processId), Altium $($d.productVersion))"

if ($Project) {
    # X2.EXE forwards a document path to the already-running instance.
    Write-Host "Opening $Project"
    Start-Process $AltiumExe -ArgumentList "`"$Project`""
}
