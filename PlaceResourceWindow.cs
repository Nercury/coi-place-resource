using System;
using System.Collections.Generic;
using System.Linq;
using Mafi;
using Mafi.Core.Products;
using Mafi.Core.Prototypes;
using Mafi.Localization;
using Mafi.Unity;
using Mafi.Unity.InputControl;
using Mafi.Unity.Ui;
using Mafi.Unity.Ui.Hud;
using Mafi.Unity.Ui.Library;
using Mafi.Unity.UiStatic.Toolbar;
using Mafi.Unity.UiToolkit.Component;
using Mafi.Unity.UiToolkit.Library;
using UnityEngine;

namespace PlaceResourceMod;

/// <summary>
/// Picker panel opened by F9. Sandbox-styled window with two action sections:
///  - "Place virtual resource": one button per VirtualResourceProductProto. Clicking a button
///    pre-selects that resource on PlaceResourceTool and activates it as a modal placement tool.
///  - "Fulfill designations": SingleProductPickerUi to pick a dumpable material (or none for
///    default), plus a "Fulfill" button that activates FulfillDesignationsTool with the choice.
///
/// Pinnable via the standard Window pin button — pinned panel stays open while the placement /
/// fulfill tools are active, letting the user kick off several actions from one panel session.
///
/// Layout patterns mirror decompiled/Mafi.Unity/Mafi.Unity.Ui/SandboxWindow.cs:345-460.
/// </summary>
public sealed class PlaceResourceWindow : Window {

	private Option<ProductProto> m_selectedProduct = Option<ProductProto>.None;

	public PlaceResourceWindow(
		UiContext context,
		PlaceResourceTool placeTool,
		FulfillDesignationsTool fulfillTool)
		: base("Place / Fulfill".AsLoc())
	{
		ShortcutToShow(KeyBindings.FromKey(KbCategory.Tools, ShortcutMode.Game, KeyCode.F9));
		WindowSize(384.px(), Px.Auto).MakeMovable().EnablePinning();

		// Section 1: virtual resources (oil, groundwater, ...). One button per resource showing
		// its product icon. Click → PlaceResourceTool pre-set with that resource and activated.
		var placeRow = new Row(2.pt());
		foreach (var vrp in placeTool.Resources) {
			var captured = vrp;
			placeRow.Add(
				new ButtonIcon(Button.General, (IProtoWithIcon)captured.Product, () => {
					placeTool.SetResource(captured);
					context.InputMgr.ActivateNewController(placeTool);
				})
				.Tooltip($"Place {captured.Strings.Name.TranslatedString} deposit at cursor (Shift/Ctrl/Alt+wheel adjusts)".AsLoc()));
		}

		// Section 2: fulfill designations with optional material. Picker pulls the same dumpable-
		// product set the sandbox uses (SandboxWindow.cs:382-385): every ProductProto's
		// .DumpableProduct.ValueOrNull, distinct, sorted by name.
		var picker = new SingleProductPickerUi(
			() => dumpableProducts(context.ProtosDb),
			p => m_selectedProduct = p,
			() => m_selectedProduct,
			() => m_selectedProduct = Option<ProductProto>.None);

		// Wire the live picker selection into the tool ONCE at window construction. The lambda
		// closes over m_selectedProduct so the tool reads the current value at every fulfill
		// click, even if the user changes the picker mid-session.
		fulfillTool.SetProductGetter(() => m_selectedProduct);

		var fulfillRow = new Row(2.pt()) {
			new ButtonIcon(Button.General, "Assets/Unity/UserInterface/Toolbar/Flatten.svg", () => {
				context.InputMgr.ActivateNewController(fulfillTool);
			})
			.Medium()
			.Tooltip("Activate area-select; on release fulfills designations using selected material".AsLoc()),
			new Icon("Assets/Unity/UserInterface/General/ArrowRight.svg").Small().Scale(-1f),
			picker,
		};

		AddBodySingle(c => c.Gap(2.pt()),
			new Title("Place virtual resource".AsLoc()).NoShrink(),
			placeRow,
			new Title("Fulfill designations".AsLoc()).NoShrink(),
			fulfillRow);
	}

	private static IEnumerable<ProductProto> dumpableProducts(ProtosDb protosDb) {
		return protosDb.All<ProductProto>()
			.Select(x => x.DumpableProduct.ValueOrNull)
			.Where(x => x != null)
			.Distinct()
			.OrderBy(x => x.Strings.Name.TranslatedString);
	}

	[GlobalDependency(RegistrationMode.AsEverything, false, false)]
	public sealed class Controller : WindowController<PlaceResourceWindow>, IToolbarItemController {

		private readonly KeyBindings m_binding =
			KeyBindings.FromKey(KbCategory.Tools, ShortcutMode.Game, KeyCode.F9);

		// IToolbarItemController members. Always visible (no gating logic), no-op event.
		public bool IsVisible => true;
		public bool DeactivateShortcutsIfNotVisible => false;
		public event Action<IToolbarItemController> VisibilityChanged { add { } remove { } }

		// LayersPanel config (Group=AlwaysActive) so activating a Tool-config controller from a
		// panel button doesn't close us. Tool's GroupToCloseOnActivation includes Window/Inspector
		// but not AlwaysActive, same trick the game's Layers panel uses. Window still closes on
		// F9 toggle, on the X button, and on Esc (LayersPanel.IgnoreEscapeKey is false).
		public Controller(ControllerContext ctx, ToolbarHud toolbar) : base(ctx, ControllerConfig.LayersPanel) {
			ctx.InputManager.RegisterGlobalShortcut(_ => m_binding, this);
			// Tool-section button. Order 1080f puts us right after Upgrade (1070f) in the same
			// bin (10) as the other tool buttons (Sweep, Demolish, Cut, Copy, Clone, Unity, Pause,
			// Upgrade at 1000-1070f). Lambda closes over our local binding so the tooltip displays
			// "F9" without needing a registered ShortcutsManager entry.
			toolbar.AddToolButton(
				"Place / Fulfill".AsLoc(),
				this,
				"Assets/PlaceResourceMod/Toolbar.png",
				1080f,
				_ => m_binding);
			Log.Info("PlaceResourceWindow.Controller: registered F9 toggle and toolbar button");
		}
	}
}
