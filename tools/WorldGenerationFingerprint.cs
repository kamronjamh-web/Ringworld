using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Godot;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// The generation fingerprint row (docs/landscape-audit-t0-spec.md WO T0-6, docs/terrain-vegetation-revamp-plan.md 2.2): hashes of generation 1 heights, terrain samples,
/// tree placement, card-grass placement and the tree catalog, all computed from <see cref="LandscapeWorld"/> and the committed pose fixture (nothing reads user://).
/// Grass hashes cover the Cards placement builder at the layer's default card settings; they do not depend on the render style setting, which only chooses whether
/// the layer calls this builder (Blades uses a different builder path and is not hashed). Main thread only (the tree catalog is not thread safe).
/// </summary>
public static class WorldGenerationFingerprint
{
    public const int Generation = 1;
    public const string Label = "gen1-v8-s1337";
    public const int HeightPoints = 4096;
    public const ulong LcgSeed = 0x5EED_1337UL;
    public static readonly string[] TilePoses =
    {
        "plains_eye", "hills_eye", "coast_eye", "forest_interior_eye", "forest_edge_eye", "forest_clearing_eye", "v0_ridge_eye", "v0_centre_eye"
    };
    const double TreeCell = 30d;
    const int TreeTileCells = 8;
    const float GrassTile = 32f;
    const long Circ = LandscapeWorld.Circumference;
    const int Width = LandscapeWorld.Width;

    public static string Hex(ulong v) => "0x" + v.ToString("X16", CultureInfo.InvariantCulture);

    /// <summary>FNV-1a 64 over a stream of 64-bit and 32-bit words (little endian bytes).</summary>
    sealed class Fnv
    {
        ulong _h = 0xCBF29CE484222325UL;
        public void Byte(byte b) { _h ^= b; _h *= 0x100000001B3UL; }
        public void U64(ulong v) { for (int i = 0; i < 8; i++) Byte((byte)(v >> (8 * i))); }
        public void U32(uint v) { for (int i = 0; i < 4; i++) Byte((byte)(v >> (8 * i))); }
        public void I32(int v) => U32((uint)v);
        public void I64(long v) => U64((ulong)v);
        public void F64(double v) => U64((ulong)BitConverter.DoubleToInt64Bits(v));
        public void F32(float v) => U32((uint)BitConverter.SingleToInt32Bits(v));
        public ulong Value => _h;
    }

    /// <summary>The 64-bit LCG (Knuth MMIX constants) behind the height points: around in [0, C), across in [0, 318,000].</summary>
    public static IEnumerable<(double Around, double Across)> HeightPointsSequence()
    {
        ulong state = LcgSeed;
        double Next()
        {
            state = state * 6364136223846793005UL + 1442695040888963407UL;
            return (state >> 11) * (1.0 / 9007199254740992.0);
        }
        for (int i = 0; i < HeightPoints; i++)
        {
            double around = Next() * Circ;
            double across = Next() * Width;
            yield return (around, across);
        }
    }

    public static Dictionary<string, object?> Compute(LandscapeWorld.Worker w, LandscapePoseFinder.Fixture fixture, string fixturePath)
    {
        // The tree catalog is not thread safe: load it here, on the main thread, before any tile builds.
        IReadOnlyList<ImportedTreeVariant> catalog = ImportedTreeCatalog.Load();
        var poses = new List<LandscapePose>();
        foreach (string name in TilePoses)
            poses.Add(fixture.Poses.FirstOrDefault(p => p.Name == name) ?? throw new InvalidOperationException("pose missing from fixture: " + name));

        var row = new Dictionary<string, object?>
        {
            ["generation"] = Generation,
            ["label"] = Label,
            ["generator_id"] = LandscapeWorld.GeneratorId,
            ["seed"] = LandscapeWorld.Seed,
            ["definition_hash"] = LandscapeWorld.DefinitionHashHex,
            ["pose_fixture"] = fixturePath,
        };

        // 1 + 2: heights and terrain samples at the LCG points
        var heightHash = new Fnv();
        var sampleHash = new Fnv();
        foreach ((double around, double across) in HeightPointsSequence())
        {
            heightHash.F64(w.Shaped.CalculateHeight(around, across));
            TerrainSample s = w.Sampler.GetTerrainSample((long)Math.Floor(around), (int)Math.Floor(across));
            sampleHash.I32(s.SurfaceHeight); sampleHash.I32((int)s.WaterKind); sampleHash.I32(s.WaterSurfaceHeight);
            sampleHash.I32((int)s.Biome); sampleHash.I32((int)s.Region); sampleHash.F32(s.Moisture); sampleHash.F32(s.Temperature);
        }
        row["heights"] = new Dictionary<string, object?> { ["count"] = HeightPoints, ["lcg_seed"] = Hex(LcgSeed), ["hash"] = Hex(heightHash.Value) };
        row["sampler"] = new Dictionary<string, object?> { ["count"] = HeightPoints, ["lcg_seed"] = Hex(LcgSeed), ["hash"] = Hex(sampleHash.Value) };

        // 3: trees, eight 240 m production tiles
        long cells = (long)Math.Ceiling(Circ / TreeCell);
        var trees = new List<object?>();
        foreach (LandscapePose p in poses)
        {
            long tx = (long)Math.Floor(p.Around / TreeCell) / TreeTileCells, tz = (long)Math.Floor(p.Across / TreeCell) / TreeTileCells;
            var records = RingBiomeDecorationLayer.BuildImportedForestReviewTile(w.Sampler, tx, tz, cells, TreeCell, LandscapeWorld.Seed, Circ, Width);
            var h = new Fnv();
            foreach (var r in records)
            {
                h.U64(r.Id); h.I64(r.Around); h.I32(r.Across); h.F32(r.SurfaceHeight); h.F32(r.Width); h.F32(r.Height); h.F32(r.Yaw); h.I32(r.Variant); h.I32((int)r.Species);
            }
            trees.Add(new Dictionary<string, object?> { ["pose"] = p.Name, ["tile_around"] = tx, ["tile_across"] = tz, ["count"] = records.Count, ["hash"] = Hex(h.Value) });
        }
        row["trees"] = trees;

        // 4: grass, eight 32 m card tiles through RingGrassTileBuilder.Build with the production call's arguments (RingStandingGrassLayer.BuildWorker)
        var tuning = RingGrassCardTuning.Default;
        using (var layer = new RingStandingGrassLayer())
        {
            if (layer.CardTuningForTests != tuning) throw new InvalidOperationException("layer default card tuning differs from RingGrassCardTuning.Default");
            if (layer.CellSizeMeters != 8 || layer.CandidatesPerCell != 9 || layer.BladesPerSquareMeter != 0 || layer.MaximumSlopeDegrees != 25
                || layer.GrassMinimumHeightMeters != .10f || layer.GrassMaximumHeightMeters != .20f || layer.GrassBladesPerSource != 1 || layer.GrassModifierDensityStrength != 1
                || layer.MeadowTuftSpread != 1f || layer.GrassCardTileMeters != GrassTile || layer.GrassCardReachMeters != 400f)
                throw new InvalidOperationException("layer default grass settings no longer match the fingerprint's grass_params");
        }
        var style = RingGrassStyleProfile.Build(new RingGrassStyleProfile.Inputs(RingStandingGrassLayer.GrassRenderStyleKind.Cards, 0, GrassTile, 400f, 0, 256, 0, tuning));
        RingGrassCardPlacement placement = style.PlacementForTier(0);
        int mainCap = style.MainCapPerTile;
        row["grass_params"] = new Dictionary<string, object?>
        {
            ["builder"] = "RingGrassTileBuilder.Build, Cards placement, tier 0 (RingStandingGrassLayer.BuildWorker arguments)",
            ["render_style_dependence"] = "none: the hash calls the Cards builder directly with layer defaults; the style setting only selects the builder",
            ["tile_meters"] = GrassTile, ["cell_size_meters"] = 8, ["candidates_per_cell"] = 9, ["blades_per_square_meter"] = 0, ["max_instances_main_cap"] = mainCap,
            ["max_slope_degrees"] = 25, ["source_fraction"] = 1, ["source_tier"] = 0, ["use_grass_clumps"] = false, ["modifier_density_strength"] = 1,
            ["meadow_tufts"] = false, ["meadow_spread"] = 1, ["minimum_height_m"] = .10f, ["maximum_height_m"] = .20f, ["use_configured_height_range"] = true,
            ["appearance"] = "RingGrassAppearanceSnapshot.Default", ["exclusions"] = "none", ["modifiers"] = "none", ["edit_snapshot"] = "empty",
            ["reach_m"] = 400, ["resident_tile_setting"] = 256, ["lookahead_m"] = 0, ["hysteresis_override_m"] = 0,
            ["card_tuning"] = "RingGrassCardTuning.Default (equal to a default RingStandingGrassLayer; the Medium preset does not set any card value)",
            ["placement_near_density"] = placement.NearDensity, ["placement_include_low_layer"] = placement.IncludeLowLayer, ["placement_low_density"] = placement.LowLayerDensity,
            ["placement_low_max_instances"] = placement.LowLayerMaxInstances, ["placement_tall_m"] = placement.TallHeight, ["placement_short_m"] = placement.ShortHeight,
            ["placement_lean"] = placement.Lean, ["placement_biome_grid_m"] = placement.BiomeGridSpacingMeters,
            ["placement_fingerprint"] = Hex(style.PlacementFingerprint),
        };
        var clean = new RingLodSurfaceEditSnapshot(1, Circ, new Dictionary<RingTerrainColumnCoordinate, int>());
        var grass = new List<object?>();
        foreach (LandscapePose p in poses)
        {
            long ta = (long)Math.Floor(p.Around / GrassTile), tz = (long)Math.Floor(p.Across / GrassTile);
            var key = new RingGrassTileKey(ta, tz);
            RingGrassTileBounds bounds = RingGrassCoordinates.Bounds(ta, tz, GrassTile, Circ, Width);
            var geometry = new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero, Circ / Math.Tau, 0, 0, Circ, -1000, 1000,
                new RingCanonicalPosition(bounds.AroundCenter, 0, bounds.AcrossCenter));
            RingGrassTileData data = RingGrassTileBuilder.Build(key, bounds, 8f, 9, 0f, mainCap, 25f, w.Sampler, clean, geometry, CancellationToken.None,
                exclusions: null, appearance: RingGrassAppearanceSnapshot.Default, sourceFraction: 1f, sourceTier: 0, useGrassClumps: false, modifiers: null,
                modifierDensityStrength: 1f, meadowTufts: false, meadowSpread: 1f, minimumHeight: .10f, maximumHeight: .20f, useConfiguredHeightRange: true, cards: placement);
            var h = new Fnv();
            foreach (float f in data.Buffer) h.F32(f);
            grass.Add(new Dictionary<string, object?>
            {
                ["pose"] = p.Name, ["tile_around"] = ta, ["tile_across"] = tz, ["count"] = data.InstanceCount, ["low_count"] = data.LowLayerInstanceCount, ["hash"] = Hex(h.Value)
            });
        }
        row["grass"] = grass;

        // 5: reserved for T5
        row["scatter"] = new Dictionary<string, object?> { ["count"] = 0, ["hash"] = "none" };

        // 6: tree catalog
        var ch = new Fnv();
        foreach (ImportedTreeVariant v in catalog)
        {
            ch.I32(v.Index); ch.I32((int)v.Species);
            foreach (char c in v.Name) ch.U32(c);
            ch.F32(v.HeightMeters); ch.F32(v.WidthMeters); ch.F32(v.TrunkRadiusMeters);
            foreach (Mesh lod in v.Lods)
            {
                int surfaces = lod.GetSurfaceCount();
                ch.I32(surfaces);
                for (int s = 0; s < surfaces; s++)
                    ch.I32(lod is ArrayMesh am ? am.SurfaceGetArrayLen(s) : lod.SurfaceGetArrays(s)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length);
            }
        }
        row["catalog"] = new Dictionary<string, object?> { ["version"] = ImportedTreeCatalog.CatalogVersion, ["variant_count"] = catalog.Count, ["hash"] = Hex(ch.Value) };
        return row;
    }
}
