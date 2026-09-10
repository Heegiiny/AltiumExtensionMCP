# Workspace / On-Prem Server API notes (phase 2 research)

Status 2026-09-10: **research only, nothing verified live yet.** Phase 1 tools expose the managed-content
facts that are already on the project/component objects; the connection/session/revision APIs below come
from SDK helper signatures and the decompiled client assemblies in `<DisasmRoot>` (see `ENVIRONMENT.md`).
Rule for this file: mark every statement as *SDK-declared*, *decompiled*, or *verified live*.

## What phase 1 already exposes (verified live)

- `workspace.listProjects[].managedGuid` ← `IProject.DM_ManagedProjectGUID()` (empty for local projects;
  the Bluetooth Sentinel test copy is local).
- `sch.getComponent.managed{vaultGuid,itemGuid,revisionGuid,symbolItemGuid,symbolRevisionGuid}` ←
  `ISch_Component.GetState_VaultGUID()/ItemGUID()/RevisionGUID()/SymbolItemGUID()/SymbolRevisionGUID()`.
- `pcb.getComponent.managed{}` ← the corresponding `IPCB_Component` getters; `sourceDesignItemId`.
- `project.listComponents[].sourceLibraryName` = `Altium Content Vault` for managed parts.

## Connection / session (SDK-declared)

- `EDP.Utils.GetDXPServerManager()` → `IEDMS_DXPServerManager`: `IsConnected()`, `GetSessionID()`,
  `LoadConnectionOptions(out address, out user, ...)`, `GetIsCloudServerOption()`, `LoginWithUI(server)`,
  `SilentLogin(...)`, `Logout()`. Loaded from `EDMSInterface.dll` export `API_GetEDMS_DXPServerManager`.
- `EDP.Utils.GetVaultManager()` → `IEDMS_VaultManager`: `GetInstalledVaultCount()`,
  `Internal_GetInstalledVault(i)`, `Internal_GetEnterpriseVault()`.
- Planned first tool: `altium_get_workspace_server` = `{isConnected, serverAddress, user, isCloud, vaults[]}`
  (read-only; never calls `LoginWithUI`).

## Managed projects, revisions, collaboration (decompiled, to be mapped)

Assemblies worth reading in `<DisasmRoot>\sources\` (names are prefixes of the folder names):
- `Altium.WorkspaceManager.ProjectServices`, `Altium.WorkspaceManager.Changes`,
  `Altium.WorkspaceManager.Differences`, `Altium.WorkspaceManager.Comparators` — project history,
  local vs. server differences, compare/diff machinery used by *Project > History* and *Compare*.
- `Altium.Dxp.Edms.SessionManager`, `Altium.Dxp.Edms.UnifiedLogin`, `Altium.Dxp.Edms.ConnectivityMonitor` —
  session state.
- `Altium.Dxp.Edms.VaultClient`, `Altium.Dxp.Edms.VaultCachingClient` — item/revision access
  (managed components, lifecycle states).
- `Altium.Dxp.Edms.DesignReviewsManager`, `Altium.Dxp.Edms.ShareManager`, `Altium.Dxp.Edms.InUseManager` —
  reviews, sharing, "in use" locks.
- `Altium.Edp.SupplyChain.*`, `Altium.Edp.ComponentSearch.*`, `Altium.Edp.PartSearch.*` — managed
  component search (phase 1 item 5 / libraries).

Questions to answer with the decompiled code, then verify live against the real Workspace:
1. Which interface gives the current Workspace (name, URL) and the list of managed projects the user can see?
2. How does *Project > History* obtain commits/revisions for an open managed project (service calls, DTOs)?
3. Which API produces the *Compare* result (semantic diff) between two revisions, and can it run headless?
4. How are comments and design-review threads read (read-only)?
5. Which calls are safe (pure reads) vs. state-changing (checkout, lock, commit) — needed for the safety list.

## Collaboration model (target, from the project brief)

Human engineers ↔ Altium Workspace / On-Prem Server ↔ dedicated EE-agent workstation ↔ Altium Designer +
extension ↔ MCP/harness/agent. Edits in later phases go through project commits, revisions, compare/diff,
comments and review workflows; live read-only analysis works directly on the open project through the
extension and must not require Save-to-Server for every intermediate state.
