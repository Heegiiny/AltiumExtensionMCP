<#
.SYNOPSIS
  Start Altium Designer 26 with the MCP bridge extension loaded and wait until the bridge answers.

.EXAMPLE
  .\tools\Start-AltiumWithBridge.ps1
  .\tools\Start-AltiumWithBridge.ps1 -Project "E:\Projects\Foo\Foo.PrjPcb"
#>
[CmdletBinding()]
param(
    [string]$AltiumExe = 'C:\Program Files\Altium\AD26\X2.EXE',
    [string]$Project = '',
    [int]$TimeoutSec = 120
)

$ErrorActionPreference = 'Stop'
$discovery = Join-Path $env:LOCALAPPDATA 'AltiumMcp\bridge.json'

$running = Get-Process X2 -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $AltiumExe }
if ($running) {
    Write-Host "AD26 already running (pid $($running.Id -join ', ')). -R cannot load the extension into a running instance; if the bridge is down use DXP > Run Process > AltiumExtensionMCP:StartBridge in Altium."
} else {
    Write-Host "Starting $AltiumExe -RAltiumExtensionMCP:StartBridge"
    Start-Process $AltiumExe -ArgumentList '-RAltiumExtensionMCP:StartBridge'
}

$deadline = (Get-Date).AddSeconds($TimeoutSec)
$ok = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    if (Test-Path $discovery) {
        try {
            $d = Get-Content $discovery -Raw | ConvertFrom-Json
            if (Get-Process -Id $d.processId -ErrorAction SilentlyContinue) {
                $h = Invoke-RestMethod -Uri ($d.baseUrl + 'health') -TimeoutSec 3
                if ($h.status -eq 'ok') { $ok = $true; break }
            }
        } catch { }
    }
}

if (-not $ok) { throw "Bridge did not come up within $TimeoutSec s. See $env:LOCALAPPDATA\AltiumMcp\logs\" }
Write-Host "Bridge OK: $($d.baseUrl) (pid $($d.processId), Altium $($d.productVersion))"

if ($Project) {
    # X2.EXE forwards a document path to the already-running instance.
    Write-Host "Opening $Project"
    Start-Process $AltiumExe -ArgumentList "`"$Project`""
}
