# Environment (machine-specific facts)

**This is the single source of truth for machine-specific paths and environment facts.**
Other documents must not copy absolute paths; they link here or use the symbolic names below.
When the project moves to another machine, update this file and `Environment.local.props` **first**.

Last verified: 2026-09-10 on `SOLDERING01`.

## How paths are resolved (no hard-coding elsewhere)

| Consumer | Source of paths |
|----------|-----------------|
| `dotnet build` (MSBuild) | `Directory.Build.props` imports `Environment.local.props` (git-ignored) and derives everything else (`AltiumExe`, `AltiumSdkHome`, `AltiumExtensionDeployDir`). `Directory.Build.targets` fails fast with a hint when the SDK is not found. Command-line `-p:` overrides win. |
| `tools\*.ps1` | Dot-source `tools\AltiumEnvironment.ps1` → `Get-AltiumEnvironment` (props file → registry `HKLM\SOFTWARE\Altium\Builds` → defaults). |
| Extension / MCP server at run time | No configured paths. Bridge discovery file and logs live under `%LOCALAPPDATA%\AltiumMcp\`; env overrides `ALTIUM_MCP_BRIDGE_URL` / `ALTIUM_MCP_BRIDGE_PORT`. |
| Documentation | Symbolic names from the table below (e.g. `<ProjectRoot>`, `<DisasmRoot>`) or a link to this file. |

Generate/refresh `Environment.local.props`:

```powershell
powershell -File tools\Detect-AltiumEnvironment.ps1            # detect + write + check
powershell -File tools\Detect-AltiumEnvironment.ps1 -Print     # dry run
powershell -File tools\Detect-AltiumEnvironment.ps1 -CopyExamples   # also copy 'Bluetooth Sentinel' into the test root
```

Optional roots (`AltiumMcpTestProjectsRoot`, `AltiumExtensionExamplesRoot`, `AltiumDisasmRoot`) are kept from an
existing props file, so hand edits survive re-detection; pass `-DisasmRoot`/`-ExtensionExamplesRoot`/`-TestProjectsRoot`
to change them.

## Current machine: `SOLDERING01` (dedicated EE-agent workstation, since 2026-09-08)

| Symbolic name | Value | Notes |
|---------------|-------|-------|
| `<ProjectRoot>` | `C:\Users\minat\AltiumExtensionMCP` | git repo, branch `master`, remote `origin` |
| User account | `altiumserver0\minat` (domain account; profile dir `C:\Users\minat`) | `%LOCALAPPDATA%` = `C:\Users\minat\AppData\Local` |
| `<AltiumProgramsHome>` | `C:\Program Files\Altium\AD26` | **Altium Designer 26.9.1.10** (Enterprise). The only installed build; registry also lists 26.5.0.11 (`AD25`) with `Installed=False` — ignore it. |
| `<AltiumExe>` | `<AltiumProgramsHome>\X2.EXE` | |
| `<AltiumProfileGuid>` | `{C74E7F47-1ECF-4B15-A90E-197100278073}` | from `HKLM\SOFTWARE\Altium\Builds` |
| `<AltiumProfileRoot>` | `C:\ProgramData\Altium\Altium Designer {C74E7F47-1ECF-4B15-A90E-197100278073}` | |
| `<AltiumSdkHome>` | `<AltiumProfileRoot>\Extensions\Altium Developer\SDK25` | `CSharp\Altium.SDK.dll`; `Altium.SDK.Interfaces.dll` is in `<AltiumProgramsHome>\System` |
| `<ExtensionDeployDir>` | `<AltiumProfileRoot>\Extensions\AltiumExtensionMCP` | written by `dotnet build` (Debug, `DeployToAltium=true`); Altium locks the DLL while loaded |
| Extension registry | `<AltiumProfileRoot>\Extensions\ExtensionsRegistry.xml` | must contain `HRID="AltiumExtensionMCP"` — `tools\Register-Extension.ps1` (idempotent, Altium closed) |
| `<AltiumDocumentsHome>` | `C:\Users\Public\Documents\Altium\AD26` | Altium examples under `Examples\` |
| `<TestProjectsRoot>` | `C:\Users\minat\AltiumMcpTestProjects` | scratch copies safe to compile/modify. Present: `Bluetooth Sentinel\Bluetooth_Sentinel.PrjPcb` (sheets `Bluetooth_Sentinel.SchDoc`, `Bluetooth.SchDoc`, `Host_Controller.SchDoc`, `Microcontroller_STM32F101.SchDoc`, `Visual_LEDx6.SchDoc`; board `Bluetooth_Sentinel.PcbDoc`) |
| `<ExtensionExamplesRoot>` | `C:\Users\minat\AltiumExtensions` | working reference extensions (`TestExtension2026`, `ReplicationBOM`, `ImageViewer`, `ExtensionPublisher`), `.epd` files, `tools\`. Its own `AGENTS.md` still cites the old machine's `D:\AD_Disasm`; not maintained by this repo. |
| `<DisasmRoot>` | `C:\Users\minat\ADAgile-2e708c88\ADAgile-2e708c88` | ILSpy dump of **Altium 26.9.1** (made 2026-09-09 from the old machine's `ADAgile` install + its ProgramData Extensions). Layout below. The older `D:\AD_Disasm` / `C:\Users\minat\AD_Disasm` trees referenced in earlier docs **do not exist here**. |
| Bridge discovery | `%LOCALAPPDATA%\AltiumMcp\bridge.json` | `http://127.0.0.1:47120/` by default |
| Bridge logs | `%LOCALAPPDATA%\AltiumMcp\logs\bridge-YYYYMMDD.log` | |
| MCP server exe | `<ProjectRoot>\src\AltiumMcp.Server\bin\Debug\net8.0\AltiumMcp.Server.exe` | what MCP clients spawn |
| .NET SDK | 10.0.400 (`dotnet --version`) | projects target `net8.0` because AD26 hosts .NET 8 |
| Shell | Windows PowerShell 5.1 by default in Cursor | quoting JSON inline is painful → use `tools\Invoke-Bridge.ps1` or `--call m @file.json` / `-` (stdin) |

Machine policy: the workstation is dedicated to the agent. Restarting Altium, UI automation, background
services and long experiments are allowed. Still do not touch production Workspace data (see prompt/safety).

### Decompiled sources layout (`<DisasmRoot>`)

- `REPORT.md`, `summary.json` — run status (2347 binaries scanned, 1183 source folders, 234k `.cs`).
- `assemblies.csv` — maps each source folder to the original DLL path and SHA-256. Use it to pick the right copy
  when a DLL name appears several times (there are 19 `Altium.SDK.dll-*` and 12 `Altium.SDK.Interfaces.dll-*`
  folders from different SDK versions/extensions).
- `types.csv`, `members.csv` — grep-able type/member index.
- `sources\<Assembly>.dll-<hash>--<hash>\` — C# per assembly. Key folders:
  - `Altium.SDK.Interfaces.dll-bb0e675db1b6--d0c00c76affcd206` = `System\Altium.SDK.Interfaces.dll` of 26.9.1
    (`DXP\`, `EDP\`, `SCH\`, `PCB\` with `*Helper.cs` typed wrappers).
  - `Altium.DotNetSupport.dll-30c105676d8f--*` — extension loader.
  - `Altium.PinsPanel.dll-*`, `Altium.PCB.FullComponents.dll-*` — real in-process plugins (iteration patterns).
  - `Altium.WorkspaceManager.*`, `Altium.Dxp.Edms.*`, `Altium.Edp.*` — Workspace/365 client side (phase 2 research).
- Caveat: the dump is not byte-identical to the installed AD26 build (different SHA of `Altium.SDK.Interfaces.dll`),
  so treat it as the same API generation, and verify live when in doubt.

## Migration checklist (next move)

1. Clone the repo; install Altium Designer 26.x and the *Altium Developer* extension (SDK25).
2. `powershell -File tools\Detect-AltiumEnvironment.ps1 -CopyExamples` → fix any `[!!]` line by editing
   `Environment.local.props` (optional roots: extension examples, decompiled sources, test projects).
3. Close Altium, `dotnet build AltiumMcp.slnx -c Debug` (deploys), `powershell -File tools\Register-Extension.ps1`.
4. `powershell -File tools\Start-AltiumWithBridge.ps1 -Project "<TestProjectsRoot>\Bluetooth Sentinel\Bluetooth_Sentinel.PrjPcb"`
   then `AltiumMcp.Server.exe --health`. (Or `tools\Redeploy-Extension.ps1` which does steps 3–4.)
5. Point the MCP client at the new `AltiumMcp.Server.exe` path.
6. Update the table above (machine name, versions, GUID, roots) and `docs/SESSION_HANDOFF.md`.

Nothing else in the repository should need editing. If a step required touching another file, that file has a
hard-coded path — move it into `Environment.local.props`/`AltiumEnvironment.ps1` and note it here.

Known exceptions (intentional):
- `AltiumExtensionMCP.epd` — Altium Developer (Extension Publisher) project file; contains
  `ExtensionSourceLocation` / `ExtensionTopLevelFolder` / SDK ticket paths of the machine it was created on.
  It is only needed to open the extension in Altium's publisher wizard; the build does not use it. Regenerate
  or edit by hand after a move.
- Fallback defaults in `Directory.Build.props` and `tools\AltiumEnvironment.ps1` (`C:\Program Files\Altium\AD26`,
  `%USERPROFILE%\...`) apply only when neither the props file nor the registry answers.

## Machine history

| Period | Machine | Facts (historical; paths no longer valid) |
|--------|---------|------------------------------------------|
| 2026-09-07 … 09-08 (sessions 1–2) | `ALTIUMSERVER0` (shared with the user) | repo `E:\AltiumExtensionMCP`; AD26 **26.3.0** (profile `{295B10CB-19D8-40A7-B7A6-BA8034539C1F}`) plus the user's `ADAgile` 26.9.1 that must not be closed; decompiled code `D:\AD_Disasm` (`Code\System\`, `Altium Developer\`, `docs\`); test projects `E:\AltiumMcpTestProjects`; `%LOCALAPPDATA%` = `C:\Users\minat.ALTIUMSERVER0\AppData\Local`. Constraints of that period (no UI automation, no Altium restarts) no longer apply. |
| 2026-09-08 → | `SOLDERING01` (dedicated) | this file |
