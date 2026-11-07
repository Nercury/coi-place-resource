using Mafi;
using Mafi.Core.Mods;
using Mafi.Core.Prototypes;

namespace PlaceResourceMod;

/// <summary>
/// Entrypoint. Subclassing DataOnlyMod even though we add runtime behavior — all our
/// runtime types are [GlobalDependency]-marked and auto-register through the dep system.
/// </summary>
public sealed class PlaceResourceMod : DataOnlyMod {

	/// <summary>
	/// The live mod instance, needed as the owner when registering key-binding actions with
	/// IShortcutsRegistry (DataOnlyMod.Initialize is not overridable, so registration happens in
	/// PlaceResourceWindow.Controller's constructor instead).
	/// </summary>
	public static PlaceResourceMod Instance { get; private set; }

	public PlaceResourceMod(ModManifest manifest) : base(manifest) {
		Instance = this;
		Log.Info("PlaceResourceMod: constructed");
	}

	public override void RegisterPrototypes(ProtoRegistrator registrator) {
		// No prototypes to register; tool/service classes are auto-discovered via [GlobalDependency].
	}
}
