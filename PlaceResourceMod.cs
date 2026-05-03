using Mafi;
using Mafi.Core.Mods;
using Mafi.Core.Prototypes;

namespace PlaceResourceMod;

/// <summary>
/// Entrypoint. Subclassing DataOnlyMod even though we add runtime behavior — all our
/// runtime types are [GlobalDependency]-marked and auto-register through the dep system.
/// </summary>
public sealed class PlaceResourceMod : DataOnlyMod {

	public PlaceResourceMod(ModManifest manifest) : base(manifest) {
		Log.Info("PlaceResourceMod: constructed");
	}

	public override void RegisterPrototypes(ProtoRegistrator registrator) {
		// No prototypes to register; tool/service classes are auto-discovered via [GlobalDependency].
	}
}
