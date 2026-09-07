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
  Real example: `D:\AD_Disasm\Code\System\Altium.PinsPanel\Altium\PinsPanel\Infrastructure\Extension.cs:35-61`.
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
