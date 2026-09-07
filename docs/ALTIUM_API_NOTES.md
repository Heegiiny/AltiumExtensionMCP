# Altium API notes (SDK25 / AD26, .NET 8 extensions)

Sources, in priority order: local working extensions (`C:\Users\minat.ALTIUMSERVER0\AltiumExtensions`),
decompiled SDK (`D:\AD_Disasm\Altium Developer\Altium.SDK*`), decompiled system extensions
(`D:\AD_Disasm\Code\System\*`, e.g. `Altium.PinsPanel`), and live experiments through the bridge.

## Hosting model

- AD26 (`C:\Program Files\Altium\AD26\X2.EXE`) runs .NET 8 extensions via `Altium.DotNetSupport.dll`
  (algorithm documented in `D:\AD_Disasm\docs\Altium.DotNetSupport_Extension_Loading_Algorithm.md`).
  Assemblies are loaded from a custom `AssemblyLoadContext`; they do not appear as OS modules, but the
  DLL file is locked while loaded (use that to check whether an extension was loaded).
- Extension folder: `C:\ProgramData\Altium\Altium Designer {295B10CB-...}\Extensions\<Name>\`
  containing `<Name>.dll`, `<Name>.ins`, optional `<Name>.rcs`, `deps.json`, `runtimeconfig.json`.
- Entry point: a public class implementing `CSharpPlugin.IPluginFactory` with
  `IServerModule CreateServerModule(IClient client, string moduleName)` returning a `DXP.ServerModule` subclass.
- Project settings that work: `net8.0-windows`, `UseWindowsForms=true`, `UseWPF=true` (Altium.SDK.dll
  references WindowsBase), SDK refs with `Private=false` (Altium supplies them at runtime),
  `EnableDynamicLoading=true`.
- `.ins` file (ClientInsFile 1.0): `Server` block (`EditorName` must equal the DLL base name and the
  module name), `PanelInfo` blocks (Name/Category/Bitmap/CanDock*), `Command Name='X' ... End` lines.
  Commands map to `Command_X(IServerDocumentView view, ref string parameters)` methods registered in
  `InitializeCommands()` via `AddCommand`.

## Loading behaviour (measured)

- Lazy by default: a module is loaded on first command (`DXP > Run Process <Server>:<Command>`), when a
  document kind it registers is opened, or when a panel it registers is restored/shown.
- `X2.EXE -R<Server>:<Command>` at **cold start** loads the module and runs the command (bridge ready ~5 s after
  launch, before the main window). The same switch against a **running** instance does nothing observable.
- Passing a file path to `X2.EXE` while an instance runs forwards the open to that instance (used by
  `AltiumSession.ps1 open`).
- Extension DLL cannot be overwritten while Altium runs (redeploy requires closing Altium).

## Threading

- All SDK/COM calls must run on the UI thread. `System.Windows.Forms.Control` created in the module
  constructor (which runs on the UI thread) gives a reliable `BeginInvoke` target; force handle creation
  with `CreateHandle()`/`Handle`.
- Handlers taking ~10–25 ms do not visibly affect the UI.

## Global entry points (`DXP.GlobalVars`)

- `GlobalVars.Client : IClient` — `GetProductName()`, `GetProductVersion()` (e.g. `26.3.0.6`),
  `GetVersion()` (platform, `Version 1.0.0.30000`), `IsInitialized()`, `GetCount()/GetServerModule(i)`
  (loaded modules → `GetModuleName()`), `GetCurrentView()`, `GetDocumentByPath()`, `GetDocumentCount()/GetDocuments(i)`
  (open `IServerDocument`s: `GetFileName()`, `GetKind()`, `GetModified()`, `GetIsShown()`), `GetGUIManager()`,
  `GetServerViewFromName()`, `AddServerView()`, `GetTechnologySets(ref uint)` (203 entries on this install).
  Executable path: `Process.GetCurrentProcess().MainModule`.
- `GlobalVars.DXPWorkSpace` → cast to `EDP.IWorkspace` (workspace manager). This cast is the pattern used
  by Altium's own PinsPanel; works once the workspace manager module is initialised.
- `GlobalVars.ServerModule` — our module instance.
- `Utils.ShowMessage(string)` — modal message box.

## Workspace manager (`EDP` namespace)

`IWorkspace`
- `DM_WorkspaceFileName()`, `DM_WorkspaceFullPath()` (e.g. `Project Group 1.DsnWrk`).
- `DM_ProjectCount()`, `DM_Projects(i)`, `DM_FocusedProject()`, `DM_FocusedDocument()`.
- `DM_InstalledLibraryCount()` (returned 0 on this machine even with content vault — installed libs are
  managed elsewhere; revisit).
- `DM_GetDocumentFromPath(path)` / `DM_GetProjectFromPath(path)`.

`IProject`
- Identity: `DM_ProjectFullPath()`, `DM_ProjectFileName()`, `DM_ObjectKindString()` (`PrjPcb`,
  `FreeDocuments`…), `DM_ManagedProjectGUID()` (managed project GUID, empty for local projects).
- Documents: `DM_LogicalDocumentCount()/DM_LogicalDocuments(i)` (source documents incl. reused managed sheets
  under `Managed\Sheets\{GUID}\`), `DM_PhysicalDocumentCount()/DM_PhysicalDocuments(i)` (compiled
  sheet instances), `DM_GeneratedDocumentCount()`, `DM_TopLevelPhysicalDocument()`,
  `DM_PrimaryImplementationDocument()` (the PcbDoc).
- Compile: `DM_NeedsCompile()`, `DM_InCompilation()`, `DM_Compile()`, `DM_DocumentFlattened()` (**null until
  compiled**; the single source for flattened components and nets), `DM_ViolationCount()/DM_Violations(i)`.
- Misc: `DM_ProjectVariantCount()/DM_ProjectVariants(i)`, `DM_ParameterCount()/DM_Parameters(i)`,
  `DM_GetOutputPath()`.

`IDocument`
- `DM_FullPath()`, `DM_FileName()`, `DM_DocumentKind()` (`SCH`, `PCB`, `OUTPUTJOB`, `Harness`, …),
  `DM_DocumentIsLoaded()`, `DM_IsOpen`/is shown, `DM_Modified()`, `DM_IndentLevel()`,
  `DM_ComponentCount()/DM_Components(i)`, `DM_NetCount()/DM_Nets(i)`, `DM_SheetSymbolCount()`,
  `DM_PortCount()`, `DM_ChildDocumentCount()/DM_ChildDocuments(i)` (physical hierarchy, from
  `IProject.DM_TopLevelPhysicalDocument()`), `DM_PhysicalInstanceName()` (channel/sheet-symbol instance
  name), `DM_IsPrimaryImplementationDocument()`, `DM_Project()` (owner).

`IComponent` (from flattened document)
- `DM_UniqueId()` — hierarchical `\A\B\C` id (stable; **use as component id**).
- `DM_LogicalDesignator()` (as drawn) vs `DM_PhysicalDesignator()` (after annotation/channel expansion).
  In *Bluetooth Sentinel* they differ (U4 logical → U1 physical). Prefer physical for users.
- `DM_Comment()`, `DM_Description()`, `DM_LibraryReference()`, `DM_SourceLibraryName()`
  (`Altium Content Vault` for managed parts), `DM_Footprint()`, `DM_PartType()`, `DM_OwnerDocumentFullPath()`.
- `DM_PinCount()/DM_Pins(i)` → `IPin` (`DM_PinNumber()`, `DM_PinName()`, `DM_ElectricalString()`,
  `DM_FlattenedNetName()`, `DM_PartID()`, `DM_IsHidden()`), `DM_SubPartCount()`.
- `DM_ImplementationCount()/DM_Implementations(i)` → `IComponentImplementation` (`DM_ModelType()` =
  `PCBLIB`/`SIM`/`PCB3DLib`, `DM_ModelName()`, `DM_Description()`, `DM_IsCurrent()`). Multi-part
  components repeat each implementation once per sub-part — dedupe.
- `DM_ParameterCount()/DM_Parameters(i)` → `IParameter` (`DM_Name()`, `DM_Value()`); includes system-ish
  ones (`Comment`, `Description`, `Footprint`, `Library Reference`, `ComponentLinkNURL`…).

`INet` (flattened)
- `DM_NetName()`, `DM_PinCount()/DM_Pins(i)` → `INetItem` (`DM_PhysicalPartDesignator()`,
  `DM_LogicalPartDesignator()`, `DM_PinNumber()`, `DM_PinName()`, `DM_ElectricalString()`),
  `DM_PortCount()`, `DM_NetLabelCount()`, `DM_PowerObjectCount()`, `DM_IsLocal()`, `DM_IsAutoGenerated()`
  (names like `NetU1_11`), `DM_ElectricalString()`, `DM_OwnerDocumentFullPath()`.

`IViolation`
- `DM_ErrorKind()` (enum, e.g. `eError_FloatingInputPinsOnNet`, `eError_PowerObjectScopeChange`),
  `DM_ErrorLevel()` (`eErrorLevelWarning/Error/Fatal`), `DM_DescriptorString()`,
  `DM_RelatedObjectCount()/DM_RelatedObjects(i)` → `IDMObject.DM_OwnerDocumentFullPath()`.

## Panels

- Subclass `DXP.ServerPanelView` (constructor `(IServerModule, string viewName)`), host a WinForms
  control; register in `.ins` `PanelInfo`; create in `CreateServerViewImpl(name)`; show with
  `Client.AddServerView(view)` + `GetGUIManager().SetPanelVisibleInCurrentForm(name, true)`.
- `ServerPanelView.Show()` is **not virtual** (do not override).

## Not yet explored (next targets)

- Schematic object model: `SCH.SchServer`, `ISch_Document`, `ISch_Iterator`, `ISch_Component`, `ISch_Pin`,
  `ISch_Wire`, `ISch_NetLabel`, `ISch_Port`, `ISch_PowerObject`, `ISch_Parameter` — needs the document
  to be open (`Client.OpenDocument("SCH", path)` returns `IServerDocument`; hidden open is possible).
- PCB object model: `PCB.PCBServer`, `IPCB_Board` (`GetCurrentPCBBoard`, `GetPCBBoardByPath`),
  `IPCB_BoardIterator`, `IPCB_Component`, `IPCB_Net`, `IPCB_LayerStack`, `IPCB_Rule`, DRC via
  `IPCB_Board.RunDRC`/`DesignRuleChecker`.
- Libraries: `IntegratedLibrary` module, `IWorkspace.DM_InstalledLibraries`, managed component links
  (`IComponent.DM_VaultGUID/DM_ItemGUID/DM_RevisionGUID` — verify names in `EDP.IComponent`).
- Notifications: `ServerModule.ReceiveNotificationImpl(INotification)` — could push
  document-open/close/compile events (future: change feed / cache invalidation).
- On-Prem / 365 Workspace connection state: `EDMSInterface` / `VaultExplorer` modules.
