using System.Collections;
using System.Linq;
using System.Reflection;
using Mafi;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core.Products;
using Mafi.Core.Prototypes;
using Mafi.Unity;
using Mafi.Unity.InputControl;
using Mafi.Unity.InputControl.ResVis;
using Mafi.Unity.UiToolkit.Component;
using Mafi.Unity.UiToolkit.Library;
using UnityEngine;
using UnityEngine.EventSystems;

namespace PlaceResourceMod;

/// <summary>
/// UI-side modal tool for placing virtual-resource deposits. Activated by the picker window
/// (PlaceResourceWindow), not by a direct hotkey — the window calls SetResource(...) before
/// inputManager.ActivateNewController(this) to pre-select what to place.
///
/// Shift+wheel = radius, Ctrl+wheel = quantity, Alt+wheel = cycle resource. Plain wheel passes
/// through to camera zoom. Left-click places, right-click exits.
///
/// After placement, refreshes the resource visualization layer (the bars cache doesn't notice
/// our reflection-injected resources on its own) and ensures the layers panel is open with the
/// placed resource enabled.
/// </summary>
[GlobalDependency(RegistrationMode.AsEverything, false, false)]
public sealed class PlaceResourceTool : IUnityInputController {

	private const int DEFAULT_QUANTITY = 50_000;
	private const int DEFAULT_RADIUS = 15;
	private const int QUANTITY_STEP = 1_000;
	private const int QUANTITY_MIN = 1_000;
	private const int QUANTITY_MAX = 1_000_000;
	private const int RADIUS_MIN = 1;
	private const int RADIUS_MAX = 200;

	private readonly IUnityInputMgr m_inputManager;
	private readonly TerrainCursor m_terrainCursor;
	private readonly PlaceResourceService m_service;
	private readonly ResVisBarsRenderer m_resBarsRenderer;
	private readonly DependencyResolver m_resolver;

	public ImmutableArray<VirtualResourceProductProto> Resources => m_resources;

	private readonly ImmutableArray<VirtualResourceProductProto> m_resources;

	private int m_resourceIndex;
	private int m_quantity = DEFAULT_QUANTITY;
	private int m_radius = DEFAULT_RADIUS;

	private GameObject m_previewGo;
	private PreviewCircleMb m_previewCircle;
	private InfoPanelMb m_infoPanel;
	private bool m_isActive;

	// Lazily resolved on first placement — see resolveLayersWiring().
	private bool m_layersWiringResolved;
	private IUnityInputController m_overlaysController;
	private FieldInfo m_overlaysController_activatorField;
	private FieldInfo m_overlaysController_panelField;
	private FieldInfo m_overlaysPanel_visibilityField;
	private FieldInfo m_activator_activatedProductsField;

	// ToolBlockingCamera (not Tool) so that returning true from InputUpdate also suppresses
	// the camera's wheel-zoom on the same frame. Camera still pans/orbits freely on frames
	// where we don't consume input.
	public ControllerConfig Config => ControllerConfig.ToolBlockingCamera;

	public PlaceResourceTool(
		IUnityInputMgr inputManager,
		TerrainCursor terrainCursor,
		ProtosDb protosDb,
		PlaceResourceService service,
		ResVisBarsRenderer resBarsRenderer,
		DependencyResolver resolver)
	{
		m_inputManager = inputManager;
		m_terrainCursor = terrainCursor;
		m_service = service;
		m_resBarsRenderer = resBarsRenderer;
		m_resolver = resolver;

		m_resources = protosDb.All<VirtualResourceProductProto>().ToImmutableArray();
		if (m_resources.Length == 0) {
			Log.Warning("PlaceResourceTool: no VirtualResourceProductProto registered — tool will be a no-op.");
		}

		// Default to crude oil if present, else first available. Used when the controller is
		// activated without a prior SetResource call (defensive — window normally sets it).
		for (int i = 0; i < m_resources.Length; i++) {
			if (m_resources[i].Id.Value == "Product_VirtualResource_CrudeOil") {
				m_resourceIndex = i;
				break;
			}
		}
	}

	/// <summary>
	/// Pre-select which virtual resource will be placed. Called by PlaceResourceWindow before
	/// activating this controller. Silently ignores unknown products.
	/// </summary>
	public void SetResource(VirtualResourceProductProto product) {
		for (int i = 0; i < m_resources.Length; i++) {
			if (ReferenceEquals(m_resources[i], product)) {
				m_resourceIndex = i;
				updateVisualState();
				return;
			}
		}
	}

	public void Activate() {
		if (m_resources.Length == 0) return;
		m_isActive = true;
		m_terrainCursor.Activate();

		m_previewGo = new GameObject("PlaceResourceMod.Preview");
		m_previewCircle = m_previewGo.AddComponent<PreviewCircleMb>();
		m_infoPanel = m_previewGo.AddComponent<InfoPanelMb>();

		updateVisualState();
	}

	public void Deactivate() {
		if (m_previewGo != null) {
			UnityEngine.Object.Destroy(m_previewGo);
			m_previewGo = null;
			m_previewCircle = null;
			m_infoPanel = null;
		}
		if (m_isActive) m_terrainCursor.Deactivate();
		m_isActive = false;
	}

	public bool InputUpdate() {
		if (!m_isActive) return false;

		// Don't place / exit / scroll if the cursor is over a UI element — let the UI handle it.
		// (Same check the game's own tool controllers use — see QuickEntityTransformInputController.)
		bool overUi = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

		// Right-click → exit.
		if (Input.GetMouseButtonDown(1) && !overUi) {
			m_inputManager.DeactivateController(this);
			return true;
		}

		// Wheel handling. Plain wheel (no modifier) is left to the camera so zoom keeps working.
		float wheel = overUi ? 0f : Input.mouseScrollDelta.y;
		if (wheel != 0f) {
			bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
			bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
			bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

			if (alt || ctrl || shift) {
				int dir = wheel > 0f ? 1 : -1;
				if (alt) {
					cycleResource(dir);
				} else if (ctrl) {
					m_quantity = clamp(m_quantity + dir * QUANTITY_STEP, QUANTITY_MIN, QUANTITY_MAX);
				} else /* shift */ {
					m_radius = clamp(m_radius + dir, RADIUS_MIN, RADIUS_MAX);
				}
				updateVisualState();
				return true; // suppress camera zoom on this notch only
			}
			// No modifier → fall through, camera handles zoom.
		}

		// Position the preview at the cursor.
		if (m_previewCircle != null) {
			if (m_terrainCursor.HasValue) {
				m_previewCircle.SetVisible(true);
				m_previewCircle.SetCenter(m_terrainCursor.Tile3f);
			} else {
				m_previewCircle.SetVisible(false);
			}
		}

		// Left-click → place.
		if (Input.GetMouseButtonDown(0) && !overUi && m_terrainCursor.HasValue) {
			var product = m_resources[m_resourceIndex];
			m_service.PlaceAt(product, m_terrainCursor.Tile2i, new Quantity(m_quantity), new RelTile1i(m_radius));
			ensureLayersShowingPlacedResource(product);
			return true;
		}

		return false;
	}

	private void cycleResource(int dir) {
		int n = m_resources.Length;
		if (n == 0) return;
		m_resourceIndex = ((m_resourceIndex + dir) % n + n) % n;
	}

	private void updateVisualState() {
		if (m_resources.Length == 0) return;
		var product = m_resources[m_resourceIndex];
		if (m_previewCircle != null) {
			m_previewCircle.SetRadius(m_radius);
			m_previewCircle.SetColor(product.Graphics.ResourcesVizColor.ToColor());
		}
		if (m_infoPanel != null) {
			m_infoPanel.SetLines(
				product.Strings.Name.TranslatedString,
				$"{m_quantity:N0} units",
				$"radius {m_radius} tiles");
		}
	}

	private static int clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

	// --- Layers integration -------------------------------------------------

	/// <summary>
	/// After a placement: (1) refresh the resource-bars cache, (2) force-enable the placed
	/// resource in the OverlaysLegendPanel state, (3) ensure the OverlaysController is active
	/// and has our resource in its activator. Best-effort — if any reflection step fails we
	/// log and continue (the placement itself already succeeded).
	/// </summary>
	private void ensureLayersShowingPlacedResource(VirtualResourceProductProto product) {
		// (1) Bars cache refresh — fixes the "stale visualization" bug regardless of panel state.
		try {
			m_resBarsRenderer.InvalidateAllResourceBars();
		} catch (System.Exception ex) {
			Log.Warning($"PlaceResourceTool: InvalidateAllResourceBars failed: {ex.Message}");
		}

		if (!resolveLayersWiring()) return;

		// (2) Force-enable the resource in the panel — both the checkbox-state dict (which
		// drives ShowExactly logic on activation) AND the LayerToggleButton's visual state
		// (otherwise the panel UI shows "off" while bars actually render).
		try {
			object panelInstance = m_overlaysController_panelField.GetValue(m_overlaysController);
			if (panelInstance != null) {
				IDictionary visibility = (IDictionary)m_overlaysPanel_visibilityField.GetValue(panelInstance);
				visibility[product] = true;
				syncToggleButtonVisual(panelInstance, product, visibility);
			}
		} catch (System.Exception ex) {
			Log.Warning($"PlaceResourceTool: updating panel state failed: {ex.Message}");
		}

		// (3a) If the layers panel isn't open, open it. ActivateNewController is idempotent
		// and triggers OverlaysController.Activate() which will read the dict we just touched
		// in step 2 and call ShowExactly(...) on its activator with our resource included.
		bool wasAlreadyActive = m_inputManager.ActiveControllers.Contains(m_overlaysController);
		if (!wasAlreadyActive) {
			m_inputManager.ActivateNewController(m_overlaysController);
			return; // ShowExactly during Activate() already added our resource to the activator.
		}

		// (3b) Panel was already active — ShowExactly won't re-run on its own. Reach into the
		// activator and call Show(product.Product) directly, but only if it's not already in
		// the activated set (Show asserts on duplicate adds).
		try {
			object activator = m_overlaysController_activatorField.GetValue(m_overlaysController);
			if (activator == null) return;
			// Mafi's Set<T> implements ICollection<T> + non-generic IEnumerable but NOT
			// non-generic ICollection. Iterate via IEnumerable to stay generic-agnostic.
			IEnumerable activatedProducts = (IEnumerable)m_activator_activatedProductsField.GetValue(activator);
			if (containsProductProto(activatedProducts, product.Product)) return;

			// Activator.Show is public; reflect the call to avoid a Mafi.Unity-internal cast.
			MethodInfo show = activator.GetType().GetMethod("Show", BindingFlags.Public | BindingFlags.Instance);
			show.Invoke(activator, new object[] { product.Product });
		} catch (System.Exception ex) {
			Log.Warning($"PlaceResourceTool: activator.Show failed: {ex.Message}");
		}
	}

	/// <summary>
	/// Lazily locate the (internal) OverlaysController in the controller list and cache the
	/// FieldInfos we need. Returns false if anything is missing — callers must handle gracefully.
	/// </summary>
	private bool resolveLayersWiring() {
		if (m_layersWiringResolved) return m_overlaysController != null;
		m_layersWiringResolved = true;

		// Find OverlaysController by type name. It's `internal class` (can't reference directly)
		// and IUnityInputController isn't [MultiDependency] (so AllImplementationsOf<T> doesn't
		// resolve). Walk every resolved instance instead — same trick the game's own
		// GameConsoleCommandsExecutor uses to scan for [ConsoleCommand] methods.
		foreach (object instance in m_resolver.AllResolvedInstances) {
			if (instance is IUnityInputController ctrl && instance.GetType().Name == "OverlaysController") {
				m_overlaysController = ctrl;
				break;
			}
		}
		if (m_overlaysController == null) {
			Log.Warning("PlaceResourceTool: OverlaysController not found in controller list — layers integration disabled.");
			return false;
		}

		var ctrlType = m_overlaysController.GetType();
		const BindingFlags privInst = BindingFlags.Instance | BindingFlags.NonPublic;
		m_overlaysController_activatorField = ctrlType.GetField("m_resBarsRendererActivator", privInst);
		m_overlaysController_panelField = ctrlType.GetField("m_overlaysPanel", privInst);
		if (m_overlaysController_activatorField == null || m_overlaysController_panelField == null) {
			Log.Warning("PlaceResourceTool: OverlaysController fields missing — game version drift?");
			m_overlaysController = null;
			return false;
		}

		object panelInstance = m_overlaysController_panelField.GetValue(m_overlaysController);
		if (panelInstance != null) {
			m_overlaysPanel_visibilityField = panelInstance.GetType().GetField("m_virtualResourceItemVisibility", privInst);
		}
		object activatorInstance = m_overlaysController_activatorField.GetValue(m_overlaysController);
		if (activatorInstance != null) {
			m_activator_activatedProductsField = activatorInstance.GetType().GetField("m_activatedProducts", privInst);
		}

		if (m_overlaysPanel_visibilityField == null || m_activator_activatedProductsField == null) {
			Log.Warning("PlaceResourceTool: OverlaysLegendPanel/Activator fields missing — layers integration partially disabled.");
		}

		return true;
	}

	/// <summary>
	/// The panel's LayerToggleButton instances are private and stored only as inline children
	/// of Body (no name-keyed lookup). The buttons are added in a fixed order:
	///   [4 fixed: grid, designators, trees, zones] [N terrain materials] [M virtual resources]
	/// where the virtual-resources slice's order matches m_virtualResourceItemVisibility's
	/// iteration order (insertion order — Dict preserves it). We find our product's index in
	/// the dict, then index into the matching tail of LayerToggleButton children.
	/// </summary>
	private static void syncToggleButtonVisual(object panelInstance, VirtualResourceProductProto product, IDictionary virtVisibility) {
		var panel = panelInstance as PanelWithHeader;
		if (panel == null) return;

		int virtIndex = -1;
		int i = 0;
		foreach (DictionaryEntry e in virtVisibility) {
			if (ReferenceEquals(e.Key, product)) { virtIndex = i; break; }
			i++;
		}
		if (virtIndex < 0) return;

		var buttons = panel.Body.AllChildren
			.Where(c => c.GetType().Name == "LayerToggleButton")
			.ToList();

		int virtCount = virtVisibility.Count;
		int btnIndex = buttons.Count - virtCount + virtIndex;
		if (btnIndex < 0 || btnIndex >= buttons.Count) return;

		UiComponent btn = buttons[btnIndex];
		MethodInfo setSelected = btn.GetType().GetMethod("SetSelected", BindingFlags.Public | BindingFlags.Instance);
		setSelected?.Invoke(btn, new object[] { true });
	}

	private static bool containsProductProto(IEnumerable set, ProductProto product) {
		foreach (var item in set) {
			if (ReferenceEquals(item, product)) return true;
		}
		return false;
	}
}
