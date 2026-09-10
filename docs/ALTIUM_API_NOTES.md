# Altium API notes (SDK25 / AD26, .NET 8 extensions)

Sources, in priority order: live experiments through the bridge, local working extensions
(`<ExtensionExamplesRoot>`), decompiled SDK and system extensions (`<DisasmRoot>`, e.g. `Altium.SDK.Interfaces`,
`Altium.PinsPanel`). Paths: `ENVIRONMENT.md`. Verified on AD 26.3.0 (sessions 1–2) and 26.9.1 (since 2026-09-08).

## Hosting model

- AD26 (`<AltiumExe>` = `X2.EXE`) runs .NET 8 extensions via `Altium.DotNetSupport.dll` (decompiled in
  `<DisasmRoot>\sources\Altium.DotNetSupport.dll-*`; the first machine also had a write-up
  `docs\Altium.DotNetSupport_Extension_Loading_Algorithm.md` that did not travel with the repo).
  Assemblies are loaded from a custom `AssemblyLoadContext`; they do not appear as OS modules, but the
  DLL file is locked while loaded (use that to check whether an extension was loaded).
- Extension folder: `<AltiumProfileRoot>\Extensions\<Name>\` containing `<Name>.dll`, `<Name>.ins`,
  optional `<Name>.rcs`, `deps.json`, `runtimeconfig.json`. On a fresh profile the folder alone is not enough:
  the extension must be listed in `<AltiumProfileRoot>\Extensions\ExtensionsRegistry.xml`
  (`Item HRID=... Path=...`; `tools\Register-Extension.ps1` writes it) or `-R<Server>:<Command>` starts nothing.
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

## SDK structure: `Internal_*` vs `*Helper`

Every interface in `Altium.SDK.Interfaces` declares raw COM members `Internal_Xxx()` returning `object`
(`[EditorBrowsable(Never)]`). The typed API is in sibling static extension classes `<IName>Helper.cs`
(`IClientHelper`, `IWorkspaceHelper`, `IProjectHelper`, `ISch_BasicContainerHelper`, `IPCB_BoardHelper`…).
Look there first; e.g. `IClient.GetCurrentView()` is `IClientHelper.GetCurrentView(this IClient)`.
Enum-set filters are built as `new TObjectSet(TObjectId.ePin)`.

Additional `IClient`/`IClientAPI_Interface` facts: `GetServerModuleByName(string)`, `StartServer(name)`,
`OpenDocument(kind, path)`, `IsDocumentOpen(path)`, `GetDocumentKindFromDocumentPath(path)`,
`RegisterNotificationHandler(INotificationHandler)` / `RegisterFilteredNotificationHandler(handler, filter)`;
special folders via `GlobalVars.ClientApi.SpecialFolder_AltiumExtensions()`, `..._AltiumSystem()`,
`..._AltiumApplicationData()`, `..._MyDesigns()`, etc. `IServerModule.GetDocuments(i)` enumerates only *that
module's* documents — use the workspace for the global view.

`IProject` extras: `DM_CompileEx(bool, ref bool cancelled)`, `DM_TopLevelLogicalDocument()`,
`DM_GetDocumentByDocumentId(string)`, `DM_CurrentProjectVariant()`, `DM_ConfigurationCount()/DM_Configurations(i)`,
`DM_ErrorLevels(TErrorKind)`, `DM_HierarchyMode()`. `IWorkspace` extras: `DM_OpenProject(path, show)`,
`DM_CloseProject(path)`, `DM_LoadProjectHidden(path)`, `DM_FreeDocumentsProject()`. `IDocument` extras:
`DM_UniqueComponentCount()`, `DM_PartCount()`, `DM_BusCount()`, `DM_NetClassCount()`, `DM_ComponentClassCount()`,
`DM_DifferentialPairCount()`, `DM_RuleCount()/DM_Rules(i)`, `DM_RoomCount()`, `DM_PhysicalInstancePath()`,
`DM_ChannelIndex()`, `DM_LoadDocument()`. `INet` extras: `DM_AllNetItemCount()/DM_AllNetItems(i)`,
`DM_SheetEntryCount()`, `DM_SignalType()`. `IParameter.DM_SetValue(string)` exists (phase 2 write path).

## Live findings from SCH/PCB verification (AD 26.9.1, 2026-09-08/10)

Schematic (`SCH`):
- `ISch_Document` first-level iteration never yields **sheet entries** or **harness entries**; they are children
  of `eSheetSymbol` / `eHarnessConnector` and must be iterated from the container (like pins/parameters from
  `eSchComponent`). `ISch_SheetEntry.GetState_IOType()/GetState_Side()`, `ISch_HarnessEntry.GetState_Side()` work.
- Iterating pins of a component returns **one pin set per display mode** (alternate symbols): a 2-pin capacitor
  yields 4 pins. Filter with `pin.GetState_OwnerPartDisplayMode() == component.GetState_DisplayMode()`.
- **Each placed part of a multi-part component is its own `ISch_Component`** with its own `UniqueId`, same
  designator text (`U2` → `NCXIUWJG` part 1, `YCVLBLXF` part 2; `GetState_CurrentPartID()` tells which).
  The compiled model keeps a single component whose hierarchical `DM_UniqueId()` ends with one of them.
  Each part object still reports all pins (all `partId`s).
- `ISch_Designator.GetState_PhysicalDesignator()` is **empty** on a hierarchical design at sheet level; the
  physical designator lives only in the compiled model (`IComponent.DM_PhysicalDesignator()`), mapped through
  `DM_OwnerDocumentFullPath()` == sheet path and the UniqueId tail (or logical designator for other parts).
- Compiled id ↔ sheet id: `\LMYISRGO\GJSCNDCC\SQQNJPYP` ↔ sheet `SQQNJPYP` (segments = sheet-symbol UniqueIds
  along the hierarchy + component UniqueId). Multi-channel: one sheet object ↔ N compiled ids.
- `LoadSchDocumentByPath` (hidden load) does not steal focus and does not create an editor tab;
  `sch.getSheet` on a hidden load ≈ 0.4 s, further calls 40–100 ms.
- Sheets that only wire sub-sheets (e.g. `Visual_LEDx6.SchDoc`) legitimately have 0 components; managed reused
  sheets live under `<project>\Managed\Sheets\{GUID}\` and appear as normal logical documents.
- **Selection**: `ISch_GraphicalObject.SetState_Selection(bool)` + `GraphicallyInvalidate()`, then
  `ISch_Document.UpdateDisplayForCurrentSheet()`. No document-level "deselect all" API → iterate all levels and
  clear `GetState_Selection()`. `ISch_Component.FullPartDesignator(partId)` did not match `U2A` live; compose
  `designator + (char)('A' + partId - 1)` as a fallback. `sch.select`.
- **Running editor processes from C#**: `(GlobalVars.Client as IProcessLauncher).SendMessage("Sch:Zoom", ref "Object=Selected",
  serverDocument.GetView(0))` — the pattern Altium's own managed servers use (`ActiveBomServerDocument`). Process
  ids/parameters are in `<AltiumProgramsHome>\System\advsch.rcs` / `AdvPcb.rcs` (`PCB:Zoom` `Action=Selected`,
  `Sch:DeSelect` `Action=All`, `PCB:DeSelect` `Scope=All`). Works live for zoom-to-selection.
- **No sheet-to-image API** in the .NET SDK: `ISch_ServerInterface.CreateDocumentPainter()` → `IDocumentPainterView`
  only repaints the editor; `CreateComponentMetafilePainter().DrawToMetafile(part, colorMode, scaleMode, file)` and
  `PaintLoadedComponentThumbnail(comp, part, w, h)` (HBITMAP) are per component. PCB does have
  `IPCB_Board.GetState_MainGraphicalView().RenderToDC(hdc, dpiX, dpiY, dest, src)` (+ `SetState_TemporaryWindow`).
  Rendering is deferred (ROADMAP).

PCB (`PCB`):
- `IPCB_Net.GetState_ConnectivelyInvalid()` is **true for every net** — on a hidden-loaded board and also after the
  board is opened in the editor. Useless as an "unrouted" signal. Count `eConnectionObject` primitives per net
  instead (ratsnest lines; 0 on the fully routed example).
- `IPCB_Rule.Priority()` works (`FanoutControl` 1..5, `PolygonConnectStyle` 1..3). `IPCB_ObjectClass` member
  iteration terminates on an empty name. `GetState_LayerStack_V7` `FirstLayer/NextLayer` returned only the four
  copper layers (no dielectrics) on the example board. `IPCB_Component.GetState_Name()` returns the designator
  `IPCB_Text`.
- `eViolationObject` primitives and `violationCount` are 0 until a DRC is run inside Altium.
- **Batch DRC from code works**: `IPCB_Board.RunBatchDesignRuleCheck(reportPath, TDRCReportFileFormat.eDRC_Text|eDRC_HTML,
  displayReport=false, publishToWeb=false)` returns true, runs on a **hidden-loaded** board too (1.6 s on the example),
  writes a `Rule Violations|<rule summary>|<count>` text report and refreshes `eViolationObject` primitives. Per
  violation: `IPCB_Violation.GetState_Rule()` (→ `IPCB_Rule`), `GetState_Description()`, `GetState_Primitive1/2()`
  (use `GetState_DescriptorString()`), bounds via `BoundingRectangle()`. No dialog appeared. `pcb.runDrc`.
- **Selection is editor state**: `board.SelectedObjects_BeginUpdate/Clear/Add/EndUpdate` + `prim.SetState_Selected(true)`,
  `SelectedObjectsCount()` / `GetState_SelectecObject(i)` (sic), then `ViewManager_FullUpdate()`; zoom with
  `GraphicalView_ZoomOnRect(x1,y1,x2,y2)` in absolute internal units + `GraphicalView_ZoomRedraw()`. Selecting a
  component also counts its designator/comment strings (`U1`+`C1` → 4 selected). The document is **not** marked
  modified. `pcb.select`.
- Timing: hidden-loaded board — first call ~6 s (load), then 1.0–1.5 s per call (the board iterator over 10k
  tracks dominates); board open in the editor — ~0.5 s. `pcb.listPrimitives` over all tracks ≈ 3.5 s.
- Layer names from the layer-name cache are the display names (`Top Layer`, `Mid-Layer 1`, `Top Overlay`…);
  `TV6_Layer` ids (`TopLayer`, `Mechanical1`) also match. A name matching nothing must be an error, not an empty list.

Workspace / client:
- `IClient.OpenDocumentShowOrHide(kind, path, showInProjectTree)` + `ShowDocument` / `ShowDocumentDontFocus` open
  or raise an editor tab from a bridge call (used by `workspace.openDocument`; PCB open ≈ 6 s). `GetDocumentKindFromDocumentPath`
  gives the kind. `IServerDocument.GetIsShown()` = has an editor tab.
- Extension loading on a fresh profile requires an `ExtensionsRegistry.xml` entry (see Hosting model).
- Altium single-instance forwarding: `X2.EXE <file>` opens the file in the running instance; `-R` only acts at cold start.

## Researched, not yet implemented

### Schematic object model (`SCH`)
- Entry: `SCH.GlobalVars.SchServer` (= `Client.GetServerModuleByName("SCH") as ISch_ServerInterface`).
  `GetCurrentSchDocument()`, `GetSchDocumentByPath(path)` (document must be open),
  `LoadSchDocumentByPath(path)` (loads hidden), `GetSchDocumentBySchDocID(id)`.
- Iteration (`ISch_BasicContainerHelper` / `ISch_IteratorHelper`) — works on a document, a component or any
  container:
  ```csharp
  ISch_Iterator it = doc.SchIterator_Create();
  try {
      it.AddFilter_ObjectSet(new TObjectSet(TObjectId.eSchComponent));
      it.SetState_IterationDepth(TIterationDepth.eIterateFirstLevel);   // or eIterateAllLevels
      for (var o = it.FirstSchObject(); o != null; o = it.NextSchObject()) { ... }
  } finally { doc.SchIterator_Destroy(ref it); }
  ```
  Real example: `<DisasmRoot>` → `Altium.PinsPanel.dll-*\Altium\PinsPanel\Infrastructure\Extension.cs` (iterator loop).
- `TObjectId` (SCH): `eWire`, `eNetLabel`, `eDesignator`, `eSchComponent`, `eParameter`, `ePin`, `ePort`,
  `ePowerObject`, `eSheetSymbol`, `eBus`, `eJunction`…
- Common: `GetState_ObjectId()`, `GetState_UniqueId()`, `GetState_Text()`, `GetState_Location()` → `DXP.Point {X,Y}`
  (internal units, see below), `GetState_OwnerSchDocument()`, `GetState_SchParameterByName(name)`.
- `ISch_Component`: `GetState_LibReference()`, `GetState_ComponentDescription()`, `GetState_SourceLibraryName()`,
  `GetState_SchDesignator().GetState_Text()` (no direct string designator), `GetState_DisplayMode()`,
  `GetState_CurrentPartID()`; parameters = iterate `eParameter` inside the component.
- `ISch_Pin`: `GetState_Name()`, `GetState_Designator()`, `GetState_Electrical()`, `GetState_Orientation()`,
  `GetState_PinLength()`, `OwnerSchComponent()`, `GetState_OwnerPartDisplayMode()`.
- `ISch_Parameter`: `GetState_Name()`, `GetState_Text()` (value), `GetState_Description()`.
- Plugin examples also use the parallel `Rt_Schematic`/`RT_PCB` interface set (from `Altium.Edp.Interfaces`);
  prefer the `SCH`/`PCB` namespaces of `Altium.SDK.Interfaces` for new code (same method names, `TObjectSet`
  passed by value instead of `ref`).

### PCB object model (`PCB`)
- Entry: `PCB.GlobalVars.PCBServer`; `GetCurrentPCBBoard()`, `GetPCBBoardByPath(path)`, `LoadPCBBoardByPath(path)`,
  `GetCurrentPCBLibrary()`.
- `IPCB_Board`: `GetState_FileName()`, `GetState_XOrigin()/YOrigin()`, `GetState_LayerStack()` /
  `GetState_LayerStack_V7()`, `GetState_BoardOutline()`, `BoardIterator_Create()` / `BoardIterator_Destroy(ref it)`.
- Iteration (`IPCB_AbstractIteratorHelper`): `AddFilter_ObjectSet(new TObjectSet(TObjectId.eComponentObject))`,
  `AddFilter_LayerSet(...)`, `AddFilter_Method(TIterationMethod)`, `FirstPCBObject()/NextPCBObject()`.
  Example: `Code\System\Altium.PCB.FullComponents\...\FullComponentListGenerator.cs:84-89`.
- `TObjectId` (PCB): `eArcObject`, `ePadObject`, `eViaObject`, `eTrackObject`, `eTextObject`, `eFillObject`,
  `eNetObject`, `eComponentObject`, `ePolyObject`, `eRegionObject`, `eBoardOutlineObject`.
- `IPCB_Component`: `GetState_Name().GetState_Text()` (designator), `GetState_Comment().GetState_Text()`,
  `GetState_Pattern()` (footprint), `GetState_Rotation()`, `GetState_SourceDesignator()`,
  `GetState_SourceLibReference()`; position from `IPCB_Group.GetState_XLocation()/YLocation()`; layer from
  `IPCB_Primitive.GetState_Layer()`. `IPCB_Primitive.GetState_Net()` / `GetState_Component()` give pad→net/component.
- `IPCB_Net`: `GetState_Name()`, `GetState_PinCount()`. `IPCB_Pad.GetState_Name()`. `IPCB_Track`: `X1/Y1/X2/Y2/Width`.
  `IPCB_Via`: `Size`, `HoleSize`, `LowLayer/HighLayer`. `IPCB_Polygon`: `Name`, `PointCount`, `Segments(i)`, `PolygonType`.
- Units: internal coord = 1/10000 mil. `EDP.Utils.CoordToMils(int)`, `CoordToMMs(int)`, `MilsToCoord(double)`,
  `MMsToCoord(double)` (`Altium.SDK\EDP\Utils.cs:65-80`). Same units apply to SCH locations.

### Altium 365 / On-Prem connection
- `EDP.Utils.GetDXPServerManager()` → `IEDMS_DXPServerManager`: `IsConnected()`, `GetSessionID()`,
  `LoadConnectionOptions(out address, out user, ...)`, `GetIsCloudServerOption()`, `LoginWithUI(server)`,
  `SilentLogin(...)`, `Logout()`. `EDP.Utils.GetVaultManager()` → `IEDMS_VaultManager`:
  `GetInstalledVaultCount()`, `Internal_GetInstalledVault(i)`, `Internal_GetEnterpriseVault()`.
  Loaded from `EDMSInterface.dll` export `API_GetEDMS_DXPServerManager`.

### Still open
- Libraries: `IntegratedLibrary` module, `IWorkspace.DM_InstalledLibraries(i)` (count returned 0 here), managed
  component item/revision links on `EDP.IComponent`/`IPart` (names not yet confirmed).
- Notifications: `ServerModule.ReceiveNotificationImpl(INotification)` — candidates for a change feed and for a
  shutdown hook (ProcessExit does not fire in X2.EXE).
- DRC/ERC invocation from .NET (`IPCB_Board` DRC entry points, `IProject.DM_CompileEx` for ERC).
