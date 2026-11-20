using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Vegetation.Canopy;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>One resident tree as the forest lighting canopy raster needs it (main-thread copy; positions canonical, altitude above the floor as rendered).</summary>
public readonly record struct CanopyTreeRecord(ulong Id, double Around, double Across, float RootAltitude, float Width, float Height, TreeSpecies Species, int Variant);

/// <summary>Read-only access to the resident tree records for the ForestLighting node (docs/forest-lighting-plan.md section 5). Main thread only.</summary>
public partial class RingBiomeDecorationLayer
{
    /// <summary>Appends the resident trees within <paramref name="radiusMeters"/> (horizontal) of the focus to <paramref name="into"/>; returns the number appended.</summary>
    public int CopyCanopyTrees(RingCanonicalPosition focus, double radiusMeters, List<CanopyTreeRecord> into)
    {
        if (_grid == null) return 0;
        double circumference = _grid.GetCanonicalRingCircumferenceMeters64();
        VegetationLayout layout = CaptureVegetationLayout(circumference);
        double radiusSquared = radiusMeters * radiusMeters;
        float embed = RootEmbedMeters;
        int before = into.Count;
        foreach (VegetationTile tile in _vegetationTiles.Values)
        {
            if (TileDistanceSquared(tile.Key, focus, layout) > radiusSquared) continue;
            List<CachedTree> trees = tile.Trees;
            for (int i = 0; i < trees.Count; i++)
            {
                CachedTree tree = trees[i];
                if (tree.Variant < 0 || tree.Variant >= ImportedTreeCatalog.VariantCount) continue;
                double da = WrappedVegetationDelta(tree.Around - focus.AroundMeters, circumference);
                double dz = tree.Across - focus.AcrossMeters;
                if (da * da + dz * dz > radiusSquared) continue;
                into.Add(new CanopyTreeRecord(tree.Id, tree.Around, tree.Across, RootAltitude(tree.SurfaceHeight, embed), tree.Width, tree.Height, tree.Species, tree.Variant));
            }
        }
        return into.Count - before;
    }

    /// <summary>Cheap change detector for the resident tree set: tile count, tile keys, per-tile tree count and edit version, and the global edit version.</summary>
    public long CanopyTileSignature()
    {
        ulong hash = 1469598103934665603UL ^ (ulong)_vegetationTiles.Count;
        hash = Mix(hash, _vegetationEditVersion);
        ulong sum = 0;
        foreach ((VegetationTileKey key, VegetationTile tile) in _vegetationTiles)
            sum += Mix(Mix(Mix((ulong)key.Around, (ulong)key.Across), (ulong)tile.Trees.Count), tile.EditVersion); // commutative across tiles
        return (long)Mix(hash, sum);
    }

    /// <summary>SF-4: appends the understory layer's stand fillers within the radius as canopy records (Variant = the base catalog variant); 0 when the world has no understory layer.</summary>
    public int CopyStandFillTrees(RingCanonicalPosition focus, double radiusMeters, List<CanopyTreeRecord> into)
        => _understoryLayer != null && IsInstanceValid(_understoryLayer) ? _understoryLayer.CopyStandFillTrees(focus, radiusMeters, into) : 0;

    /// <summary>SF-4: identity of the current stand-filler set (0 without an understory layer or without fillers), folded into the canopy rebuild signature.</summary>
    public long StandFillSignature()
        => _understoryLayer != null && IsInstanceValid(_understoryLayer) ? _understoryLayer.StandFillSignature() : 0L;

    private static ulong Mix(ulong h, ulong v)
    {
        h ^= v + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2);
        h *= 0xFF51AFD7ED558CCDUL;
        return h ^ (h >> 32);
    }

    /// <summary>The current geometry snapshot and the camera's canonical position (the main camera, else the explorer's).</summary>
    public bool TryGetCanopyContext(out RingLodGeometrySnapshot geometry, out RingCanonicalPosition camera)
    {
        geometry = default; camera = default;
        if (_grid == null) return false;
        geometry = _grid.CaptureLodGeometrySnapshot();
        Camera3D? view = GetViewport()?.GetCamera3D();
        if (view != null && IsInstanceValid(view)) camera = _grid.WorldToCanonicalRingPosition64(view.GlobalPosition);
        else if (_explorer != null) camera = _explorer.CanonicalPosition;
        else return false;
        return true;
    }

    /// <summary>The tree sun-shadow distance in metres the shaders can rely on: 0 while tree sun shadows are suspended (fast travel, altitude), else the effective distance.</summary>
    public float CanopyTreeSunShadowDistanceMeters
        => _treeSunShadowsSuppressed ? 0f : MathF.Max(0f, EffectiveTreeSunShadowDistanceMeters);

    /// <summary>Crown hull instance uniforms for a renderer that draws exactly one variant of the imported tree (docs/forest-lighting-plan.md section 3). Call only for renderers using the imported_tree material.</summary>
    private static void ApplyTreeCrownHull(GeometryInstance3D renderer, int variant)
    {
        TreeCrownHull[] hulls = TreeCrownHullTable.Get();
        if (variant < 0 || variant >= hulls.Length) return;
        TreeCrownHullTable.Pack(hulls[variant], out Vector4 hull, out Vector4 extra, out Vector4 lad);
        renderer.SetInstanceShaderParameter("tree_crown_hull", hull);
        renderer.SetInstanceShaderParameter("tree_crown_hull_extra", extra);
        renderer.SetInstanceShaderParameter("tree_crown_lad", lad);
    }
}
