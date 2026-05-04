using System;
using Mafi;
using Mafi.Core;
using Mafi.Core.Input;
using Mafi.Core.Products;
using Mafi.Core.Terrain;
using Mafi.Core.Terrain.Designation;
using Mafi.Unity;
using Mafi.Unity.InputControl;
using Mafi.Unity.InputControl.AreaTool;
using Mafi.Unity.Terrain;
using Mafi.Unity.Terrain.Designation;
using Mafi.Unity.Utils;
using UnityEngine;
using UnityEngine.EventSystems;

namespace PlaceResourceMod;

/// <summary>
/// UI-side modal tool. Activated by the picker window (PlaceResourceWindow); the window calls
/// SetProduct(...) before inputManager.ActivateNewController(this). While active, click-drag on
/// the terrain to select a rectangle area; on release, every dumping / leveling / mining
/// designation whose center falls inside that area is queued for instant fulfillment via
/// QuickFulfillDesignationsCmd, using the pre-selected product (None → default material).
/// Right-click (with no drag) exits.
///
/// This is the "sandbox quick-fulfill" cheat exposed in normal play. The user still places
/// designators with the game's existing toolbar tools (which work in non-sandbox mode); we
/// only short-circuit the truck logistics that would normally be required to fulfill them.
///
/// The cmd processor (TerrainDesignationsManager.IAction&lt;QuickFulfillDesignationsCmd&gt;.Invoke)
/// has no sandbox guard — gating is purely at the UI layer (FulfillDesignationsInputController
/// is only reachable through SandboxWindow.Controller, which only registers itself when
/// SandboxManager.CanCheat is true). Reusing that controller would also re-open the sandbox
/// window on exit (its Deactivate calls ActivateNewController(m_sandboxWindowController)),
/// which is why we run our own thin wrapper around AreaSelectionTool instead.
/// </summary>
[GlobalDependency(RegistrationMode.AsEverything, false, false)]
public sealed class FulfillDesignationsTool : IUnityInputController {

	private readonly IUnityInputMgr m_inputManager;
	private readonly IInputScheduler m_inputScheduler;
	private readonly AreaSelectionTool m_areaSelectionTool;
	// Activator for the designation overlay (combined with terrain grid lines), so existing
	// dumping/leveling/mining designations are visible while the user picks an area to fulfill.
	// Same pattern the sandbox FulfillDesignationsInputController uses.
	private readonly IActivator m_designationsActivator;

	// Live getter back to the picker UI. Read at fulfill-time so the user can change material
	// in the panel while the tool is active and the next click uses the new selection.
	private Func<Option<ProductProto>> m_productGetter = () => Option<ProductProto>.None;

	private GameObject m_panelGo;
	private InfoPanelMb m_panel;
	private bool m_isActive;

	public ControllerConfig Config => ControllerConfig.Tool;

	public FulfillDesignationsTool(
		IUnityInputMgr inputManager,
		IInputScheduler inputScheduler,
		AreaSelectionToolFactory areaSelectionToolFactory,
		NewInstanceOf<TerrainAreaOutlineRenderer> terrainOutlineRenderer,
		TerrainDesignationsRenderer designationsRenderer)
	{
		m_inputManager = inputManager;
		m_inputScheduler = inputScheduler;
		m_areaSelectionTool = areaSelectionToolFactory.CreateInstance(
			terrainOutlineRenderer.Instance,
			onSelectionChangedSync: (a, l) => { },
			onSelectionDone: onSelectionDone,
			onSelfDeactivated: onAreaToolDeactivated,
			onEmptyRightClick: onEmptyRightClick);
		m_areaSelectionTool.SetLeftClickColor(ColorRgba.Green);
		m_designationsActivator = designationsRenderer.CreateActivatorCombinedWithTerrainGrid();
	}

	/// <summary>
	/// Source for the product's terrain material to use when the dispatched fulfill cmd runs.
	/// Called once when the window activates this tool. The getter is invoked at every fulfill
	/// click so changes via the picker panel while the tool is active take effect immediately.
	/// </summary>
	public void SetProductGetter(Func<Option<ProductProto>> getter) {
		m_productGetter = getter ?? (() => Option<ProductProto>.None);
	}

	public void Activate() {
		m_isActive = true;
		m_areaSelectionTool.TerrainCursor.Activate();
		// Make existing designations visible while picking an area, so the user sees what will
		// be hit. Activator is no-op if already active (e.g. user opened the Layers panel first).
		m_designationsActivator.ActivateIfNotActive();

		m_panelGo = new GameObject("PlaceResourceMod.FulfillPanel");
		m_panel = m_panelGo.AddComponent<InfoPanelMb>();
		// Material is intentionally not displayed here, it can be changed live via the picker
		// panel and the cursor tooltip would only show a stale snapshot.
		m_panel.SetLines(
			"Quick fulfill designations",
			"Click+drag to select area",
			"Right-click to exit");
	}

	public void Deactivate() {
		if (m_panelGo != null) {
			UnityEngine.Object.Destroy(m_panelGo);
			m_panelGo = null;
			m_panel = null;
		}
		if (m_isActive) {
			m_areaSelectionTool.TerrainCursor.Deactivate();
			m_areaSelectionTool.Deactivate();
			m_designationsActivator.DeactivateIfActive();
		}
		m_isActive = false;
	}

	public bool InputUpdate() {
		if (!m_isActive) return false;

		bool overUi = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

		// Right-click while not actively dragging → exit. (When dragging, AreaSelectionTool
		// owns the right-click as either a "cancel selection" or "empty right click", which
		// fires our onEmptyRightClick callback.)
		if (Input.GetMouseButtonDown(1) && !overUi && !m_areaSelectionTool.IsActive) {
			m_inputManager.DeactivateController(this);
			return true;
		}

		// Begin a drag-selection on left-click. AreaSelectionTool then drives itself via
		// IGameLoopEvents until it auto-deactivates on mouse-up.
		if (Input.GetMouseButtonDown(0) && !overUi && !m_areaSelectionTool.IsActive) {
			m_areaSelectionTool.Activate(additionMode: true);
			return true;
		}

		// While the area tool is dragging, suppress lower-priority controllers.
		return m_areaSelectionTool.IsActive;
	}

	private void onSelectionDone(RectangleTerrainArea2i area, bool leftClick) {
		// Read the currently-selected product from the picker UI at fulfill-time, not snapshot.
		// None → terrain raises (or levels, or lowers) using whatever default material the
		// designation already implies. With a product set, simUpdate uses that product's
		// TerrainMaterial.SlimId (TerrainDesignationsManager.cs:703).
		Option<ProductProto> product = m_productGetter();
		m_inputScheduler.ScheduleInputCmd(new QuickFulfillDesignationsCmd(area, product));
	}

	private void onAreaToolDeactivated() {
		// AreaSelectionTool decided to self-deactivate (e.g., user pressed clear-designation
		// shortcut mid-drag). Stay in the tool — user may want to keep selecting.
	}

	private void onEmptyRightClick() {
		// Right-click after pressing left without moving (or right-click during drag) → exit.
		m_inputManager.DeactivateController(this);
	}
}
