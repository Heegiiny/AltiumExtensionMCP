<#
.SYNOPSIS
  Full dev-loop step: close Altium, build + deploy the extension, restart Altium with the bridge, reopen a project.

.DESCRIPTION
  Altium locks the extension DLL while loaded, so redeploying always means a restart. This script does the
  whole cycle using the environment from Environment.local.props (docs/ENVIRONMENT.md):
    1. Close Altium gracefully (CloseMainWindow; force-kill after -CloseTimeoutSec, e.g. when a Save dialog blocks).
    2. dotnet build src\AltiumMcp.Extension (deploys into the Altium Extensions folder) and the MCP server.
    3. Register the extension in ExtensionsRegistry.xml if missing (Register-Extension.ps1).
    4. Start Altium with -RAltiumExtensionMCP:StartBridge and wait for /health (Start-AltiumWithBridge.ps1).
    5. Optionally open -Project (default: the Bluetooth Sentinel test copy if present; pass -NoProject to skip).

.EXAMPLE
  .\tools\Redeploy-Extension.ps1
  .\tools\Redeploy-Extension.ps1 -Project "C:\path\Foo.PrjPcb"
  .\tools\Redeploy-Extension.ps1 -NoProject -Configuration Release
#>
[CmdletBinding()]
param(
    [string]$Project = '',
    [switch]$NoProject,
    [string]$Configuration = 'Debug',
    [int]$CloseTimeoutSec = 60,
    [int]$BridgeTimeoutSec = 150
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\AltiumEnvironment.ps1"
$e = Get-AltiumEnvironment
if (-not $e.AltiumExtensionDeployDir) { throw 'Environment not detected. Run tools\Detect-AltiumEnvironment.ps1 first.' }

# 1. Close Altium.
$running = @(Get-Process X2 -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $e.AltiumExe })
if ($running.Count -gt 0) {
    Write-Host "Closing Altium (pid $($running.Id -join ', '))..."
    foreach ($p in $running) { $p.CloseMainWindow() | Out-Null }
    $deadline = (Get-Date).AddSeconds($CloseTimeoutSec)
    while ((Get-Date) -lt $deadline -and (Get-Process -Id $running.Id -ErrorAction SilentlyContinue)) { Start-Sleep -Seconds 2 }
    $left = Get-Process -Id $running.Id -ErrorAction SilentlyContinue
    if ($left) { Write-Warning "Altium did not exit in $CloseTimeoutSec s (modal dialog?). Killing."; $left | Stop-Process -Force }
    Start-Sleep -Seconds 3
}
Stop-Process -Name AltiumMcp.Server -Force -ErrorAction SilentlyContinue

# 2. Build + deploy.
Push-Location $e.RepoRoot
try {
    Write-Host "Building and deploying extension ($Configuration)..."
    dotnet build src\AltiumMcp.Extension\AltiumMcp.Extension.csproj -c $Configuration -p:DeployToAltium=true
    if ($LASTEXITCODE -ne 0) { throw 'Extension build failed.' }
    dotnet build src\AltiumMcp.Server\AltiumMcp.Server.csproj -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Server build failed.' }

    # 3. Registration (idempotent; only needed once per machine/profile).
    $registry = Join-Path $e.AltiumProfileRoot 'Extensions\ExtensionsRegistry.xml'
    if ((Test-Path $registry) -and -not (Select-String -Path $registry -Pattern 'HRID="AltiumExtensionMCP"' -Quiet)) {
        Write-Host 'Extension not registered in ExtensionsRegistry.xml; registering...'
        & "$PSScriptRoot\Register-Extension.ps1"
    }

    # 4. Start Altium with the bridge.
    & "$PSScriptRoot\Start-AltiumWithBridge.ps1" -TimeoutSec $BridgeTimeoutSec

    # 5. Open a project.
    if (-not $NoProject) {
        if (-not $Project) {
            $candidate = Join-Path $e.AltiumMcpTestProjectsRoot 'Bluetooth Sentinel\Bluetooth_Sentinel.PrjPcb'
            if (Test-Path $candidate) { $Project = $candidate }
        }
        if ($Project) {
            Write-Host "Opening $Project"
            Start-Process $e.AltiumExe -ArgumentList "`"$Project`""
        }
    }
} finally {
    Pop-Location
}
