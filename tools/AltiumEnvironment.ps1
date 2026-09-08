<#
.SYNOPSIS
  Shared environment resolution for the AltiumExtensionMCP tool scripts. Dot-source it:

    . "$PSScriptRoot\AltiumEnvironment.ps1"
    $envInfo = Get-AltiumEnvironment

.DESCRIPTION
  Single place where machine-specific facts (Altium install, profile GUID, SDK, deploy folder,
  test projects) are resolved. Resolution order, highest priority first:

    1. <repo root>\Environment.local.props   (git-ignored; written by Detect-AltiumEnvironment.ps1)
    2. Windows registry  HKLM\SOFTWARE\Altium\Builds\*   (Installed=True, newest FullBuild wins)
    3. Built-in defaults (AD26 in Program Files)

  Nothing else in the repository should hard-code these paths. See docs/ENVIRONMENT.md.
#>

Set-StrictMode -Version Latest

function Get-AltiumMcpRepoRoot {
    # tools\AltiumEnvironment.ps1 -> repo root
    return (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

function Get-AltiumBuildsFromRegistry {
    <# Returns installed Altium Designer builds from HKLM\SOFTWARE\Altium\Builds, newest first. #>
    $key = 'HKLM:\SOFTWARE\Altium\Builds'
    if (-not (Test-Path $key)) { return @() }
    $builds = foreach ($sub in Get-ChildItem $key -ErrorAction SilentlyContinue) {
        $p = Get-ItemProperty $sub.PSPath -ErrorAction SilentlyContinue
        if (-not $p) { continue }
        $installed = "$($p.Installed)" -match '^(1|True)$'
        if (-not $installed) { continue }
        if ("$($p.Name)" -ne 'Altium Designer') { continue }
        $ver = $null
        try { $ver = [version]$p.FullBuild } catch { $ver = [version]'0.0' }
        [pscustomobject]@{
            ProfileGuid   = "$($p.UniqueID)"
            FullBuild     = "$($p.FullBuild)"
            Version       = $ver
            ProgramsHome  = "$($p.ProgramsInstallPath)"
            DocumentsHome = "$($p.DocumentsInstallPath)"
        }
    }
    return @($builds | Sort-Object Version -Descending)
}

function Read-AltiumEnvironmentProps {
    param([string]$Path)
    $result = @{}
    if (-not (Test-Path $Path)) { return $result }
    try {
        [xml]$xml = Get-Content $Path -Raw
        foreach ($pg in $xml.Project.PropertyGroup) {
            foreach ($node in $pg.ChildNodes) {
                if ($node.NodeType -eq 'Element') { $result[$node.Name] = $node.InnerText.Trim() }
            }
        }
    } catch {
        Write-Warning "Could not parse $Path : $_"
    }
    return $result
}

function Get-AltiumEnvironment {
    <#
    .SYNOPSIS  Resolve the environment (props file > registry > defaults) into one object.
    .PARAMETER IgnoreLocalProps  Skip Environment.local.props (used by the detect script itself).
    #>
    param([switch]$IgnoreLocalProps)

    $repoRoot  = Get-AltiumMcpRepoRoot
    $propsPath = Join-Path $repoRoot 'Environment.local.props'
    $props = if ($IgnoreLocalProps) { @{} } else { Read-AltiumEnvironmentProps $propsPath }

    $reg = @(Get-AltiumBuildsFromRegistry) | Select-Object -First 1

    $profileGuid   = if ($props['AltiumProfileGuid'])   { $props['AltiumProfileGuid'] }   elseif ($reg) { $reg.ProfileGuid }   else { '' }
    $programsHome  = if ($props['AltiumProgramsHome'])  { $props['AltiumProgramsHome'] }  elseif ($reg) { $reg.ProgramsHome }  else { 'C:\Program Files\Altium\AD26' }
    $documentsHome = if ($props['AltiumDocumentsHome']) { $props['AltiumDocumentsHome'] } elseif ($reg) { $reg.DocumentsHome } else { 'C:\Users\Public\Documents\Altium\AD26' }
    $version       = if ($props['AltiumProductVersion']){ $props['AltiumProductVersion'] }elseif ($reg) { $reg.FullBuild }     else { '' }
    $profileRoot   = if ($props['AltiumProfileRoot'])   { $props['AltiumProfileRoot'] }   elseif ($profileGuid) { "C:\ProgramData\Altium\Altium Designer $profileGuid" } else { '' }

    $testRoot   = if ($props['AltiumMcpTestProjectsRoot'])   { $props['AltiumMcpTestProjectsRoot'] }   else { Join-Path $env:USERPROFILE 'AltiumMcpTestProjects' }
    $extExRoot  = if ($props['AltiumExtensionExamplesRoot']) { $props['AltiumExtensionExamplesRoot'] } else { Join-Path $env:USERPROFILE 'AltiumExtensions' }
    $disasmRoot = if ($props['AltiumDisasmRoot'])            { $props['AltiumDisasmRoot'] }            else { Join-Path $env:USERPROFILE 'AD_Disasm' }

    [pscustomobject]@{
        RepoRoot                = $repoRoot
        LocalPropsPath          = $propsPath
        LocalPropsExists        = (Test-Path $propsPath)
        ResolvedFrom            = if ($props.Count -gt 0) { 'Environment.local.props' } elseif ($reg) { 'registry' } else { 'defaults' }
        AltiumProductVersion    = $version
        AltiumProfileGuid       = $profileGuid
        AltiumProfileRoot       = $profileRoot
        AltiumProgramsHome      = $programsHome
        AltiumDocumentsHome     = $documentsHome
        AltiumExe               = if ($props['AltiumExe'])        { $props['AltiumExe'] }        else { Join-Path $programsHome 'X2.EXE' }
        AltiumSystemHome        = if ($props['AltiumSystemHome']) { $props['AltiumSystemHome'] } else { Join-Path $programsHome 'System' }
        AltiumSdkHome           = if ($props['AltiumSdkHome'])    { $props['AltiumSdkHome'] }    elseif ($profileRoot) { Join-Path $profileRoot 'Extensions\Altium Developer\SDK25' } else { '' }
        AltiumExtensionDeployDir= if ($props['AltiumExtensionDeployDir']) { $props['AltiumExtensionDeployDir'] } elseif ($profileRoot) { Join-Path $profileRoot 'Extensions\AltiumExtensionMCP' } else { '' }
        AltiumExamplesRoot      = Join-Path $documentsHome 'Examples'
        AltiumMcpTestProjectsRoot   = $testRoot
        AltiumExtensionExamplesRoot = $extExRoot
        AltiumDisasmRoot            = $disasmRoot
        BridgeDataDir           = Join-Path $env:LOCALAPPDATA 'AltiumMcp'
        BridgeDiscoveryFile     = Join-Path $env:LOCALAPPDATA 'AltiumMcp\bridge.json'
        BridgeLogDir            = Join-Path $env:LOCALAPPDATA 'AltiumMcp\logs'
        McpServerExe            = Join-Path $repoRoot 'src\AltiumMcp.Server\bin\Debug\net8.0\AltiumMcp.Server.exe'
    }
}

function Test-AltiumEnvironment {
    <# Prints a check list; returns $true when all required paths exist. #>
    param([Parameter(Mandatory)]$Environment)
    $checks = [ordered]@{
        'Altium executable'        = $Environment.AltiumExe
        'Altium System (SDK.Interfaces)' = (Join-Path $Environment.AltiumSystemHome 'Altium.SDK.Interfaces.dll')
        'SDK25 Altium.SDK.dll'     = (Join-Path $Environment.AltiumSdkHome 'CSharp\Altium.SDK.dll')
        'Profile Extensions folder'= (Join-Path $Environment.AltiumProfileRoot 'Extensions')
    }
    $optional = [ordered]@{
        'Deployed extension DLL'   = (Join-Path $Environment.AltiumExtensionDeployDir 'AltiumExtensionMCP.dll')
        'Test projects root'       = $Environment.AltiumMcpTestProjectsRoot
        'Extension examples root'  = $Environment.AltiumExtensionExamplesRoot
        'Decompiled Altium root'   = $Environment.AltiumDisasmRoot
        'Bridge discovery file'    = $Environment.BridgeDiscoveryFile
        'MCP server exe (built)'   = $Environment.McpServerExe
    }
    $ok = $true
    foreach ($k in $checks.Keys) {
        $exists = ($checks[$k]) -and (Test-Path $checks[$k])
        if (-not $exists) { $ok = $false }
        Write-Host ("  [{0}] {1}: {2}" -f ($(if ($exists) { 'OK' } else { '!!' })), $k, $checks[$k])
    }
    foreach ($k in $optional.Keys) {
        $exists = ($optional[$k]) -and (Test-Path $optional[$k])
        Write-Host ("  [{0}] {1}: {2}" -f ($(if ($exists) { 'ok' } else { '--' })), $k, $optional[$k])
    }
    return $ok
}

