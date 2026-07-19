using System;
using System.Reflection;
using Mafi;
using Mafi.Collections;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core.Map;
using Mafi.Core.Products;
using Mafi.Core.Terrain;
using Mafi.Core.Terrain.Generation;

namespace PlaceResourceMod;

/// <summary>
/// Sim-side service that surgically inserts a SimpleVirtualResource into the live game state.
/// Mutates VirtualResourceManager's private fields directly via reflection. No regenerate, no
/// InputCommand round-trip — single-player, main-thread invocation is safe and preserves all
/// existing depleted-deposit state.
///
/// We deliberately do NOT touch IslandMap.VirtualResources — it's only read by the map editor
/// and the debug DrawMap overlay; runtime queries hit VirtualResourceManager.
/// </summary>
[GlobalDependency(RegistrationMode.AsSelf, false, false)]
public sealed class PlaceResourceService {

	private readonly VirtualResourceManager m_manager;
	private readonly FieldInfo m_resourcesField;
	private readonly FieldInfo m_resourcesMapField;

	public PlaceResourceService(VirtualResourceManager manager) {
		m_manager = manager;

		var t = typeof(VirtualResourceManager);
		const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
		m_resourcesField = t.GetField("m_virtualResources", flags)
			?? throw new MissingFieldException("VirtualResourceManager.m_virtualResources not found — game version mismatch?");
		m_resourcesMapField = t.GetField("m_virtualResourcesMap", flags)
			?? throw new MissingFieldException("VirtualResourceManager.m_virtualResourcesMap not found — game version mismatch?");
	}

	/// <summary>
	/// True when a deposit of <paramref name="product"/> with the given footprint would overlap an
	/// existing deposit of the same product. The game does not support overlapping deposits: a
	/// WellPump binds to exactly one deposit per tile and raises "Multiple virtual resources not
	/// supported!" when it finds more, and other mods patch deposit behavior assuming the same
	/// (Cheat++ disables its water/oil cheats entirely when we are loaded because of this).
	/// </summary>
	public bool OverlapsExisting(VirtualResourceProductProto product, Tile2i pos, RelTile1i radius) {
		var arr = (ImmutableArray<IVirtualTerrainResource>)m_resourcesField.GetValue(m_manager);
		foreach (var r in arr) {
			if (r.Product != product) continue;
			if (r is SimpleVirtualResource simple) {
				// Two circular footprints intersect iff center distance <= sum of radii.
				long reach = (long)radius.Value + simple.MaxRadius.Value;
				if (pos.DistanceSqrTo(simple.Position.Tile2i) <= reach * reach) return true;
			} else if (r.IsAt(pos)) {
				// Unknown IVirtualTerrainResource implementation — its footprint isn't readable,
				// so only the center tile can be checked.
				return true;
			}
		}
		return false;
	}

	/// <summary>Places a new deposit. Returns false (placing nothing) on overlap — see OverlapsExisting.</summary>
	public bool PlaceAt(VirtualResourceProductProto product, Tile2i pos, Quantity quantity, RelTile1i radius) {
		if (OverlapsExisting(product, pos, radius)) {
			Log.Info($"PlaceResourceService: refused {product.Id} at {pos} radius {radius} — overlaps an existing deposit");
			return false;
		}

		var resource = new SimpleVirtualResource(product, quantity, new Tile3i(pos.X, pos.Y, 0), radius);

		// Append to flat array. Both fields are serialized independently and not auto-rebuilt
		// on load, so both must stay in sync.
		var arr = (ImmutableArray<IVirtualTerrainResource>)m_resourcesField.GetValue(m_manager);
		m_resourcesField.SetValue(m_manager, arr.Add(resource));

		// Append to per-product dict.
		var map = (Dict<VirtualResourceProductProto, ImmutableArray<IVirtualTerrainResource>>)m_resourcesMapField.GetValue(m_manager);
		var existing = map.TryGetValue(product, out var bucket) ? bucket : ImmutableArray<IVirtualTerrainResource>.Empty;
		map[product] = existing.Add(resource);

		Log.Info($"PlaceResourceService: placed {quantity} of {product.Id} at {pos} radius {radius}");
		return true;
	}
}
