using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Vegetation.Scatter;
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

    public static readonly string[] ScatterDressingPoses = { "steep_eye", "scree_eye" };
    const string DressingFixture = "res://tools/fixtures/landscape_poses_dressing_s1337.json";

    /// <summary>True when the spec's world (its generation's frozen features plus the review list) holds scatter.rocks.</summary>
    public static bool ScatterEnabled(LandscapeWorld.Spec spec)
        => WorldGenerationLedger.Production.FeatureSet(spec.Recipe.WorldGeneration, spec.Recipe.ReviewFeatures).Contains("scatter.rocks");

    /// <summary>T5 design A9: the eight T0 tile poses plus steep_eye and scree_eye, built with the production layout (Circ / ceil(Circ / 30)), every row and all 16 cobble blocks, a clean edit snapshot.</summary>
    public static void ComputeScatterSlot(LandscapeWorld.Spec spec, LandscapeWorld.Worker w, Dictionary<string, object?> row)
    {
        if (!ScatterEnabled(spec))
        {
            row["scatter"] = new Dictionary<string, object?> { ["count"] = 0, ["hash"] = "none" };
            return;
        }
        RingScatterClassTable table = RingScatterClassTable.Production;
        var poses = new List<LandscapePose>();
        var t0 = LandscapePoseFinder.ReadFixture(ProjectSettings.GlobalizePath(LandscapePoseFinder.FixturePath));
        foreach (string name in TilePoses) poses.Add(t0.Poses.First(p => p.Name == name));
        var dressing = LandscapePoseFinder.ReadFixture(ProjectSettings.GlobalizePath(DressingFixture));
        foreach (string name in ScatterDressingPoses) poses.Add(dressing.Poses.First(p => p.Name == name));
        double aroundCell = Circ / Math.Ceiling(Circ / TreeCell);
        var tiles = new List<object?>();
        foreach (LandscapePose p in poses)
        {
            long ta = (long)Math.Floor(Math.Floor(p.Around / aroundCell) / TreeTileCells), tz = (long)Math.Floor(p.Across / (TreeCell * TreeTileCells));
            var records = RingBiomeDecorationLayer.BuildScatterReviewTile(w.Sampler, ta, tz, LandscapeWorld.Seed, Circ, Width, table);
            var h = new Fnv();
            foreach (ScatterRecord r in records)
            {
                h.U64(r.Id); h.I64(r.AroundCm); h.I32(r.AcrossCm); h.F32(r.SurfaceHeight); h.I32(r.ClassId); h.I32(r.Mesh); h.F32(r.Size); h.F32(r.Yaw); h.U32(r.UpOct); h.U32(r.BaseTint);
            }
            tiles.Add(new Dictionary<string, object?> { ["pose"] = p.Name, ["tile_around"] = ta, ["tile_across"] = tz, ["count"] = records.Count, ["hash"] = Hex(h.Value) });
        }
        row["scatter"] = tiles;
        row["scatter_params"] = new Dictionary<string, object?>
        {
            ["class_table_hash"] = Hex(table.Hash), ["algorithm_version"] = RingBiomeDecorationLayer.ScatterAlgorithmVersion,
            ["rock_catalog_sha256"] = RockCatalog.ArtifactSha256, // T5-6: the manifest only, no Godot textures
            ["layout"] = "production"
        };
    }

    /// <summary>The <c>catalog</c> field: the 30 base variants (index, species, name, sizes, per-LOD surface and vertex counts). Its definition never changes (A6a); it hashes ImportedTreeCatalog.Load() only.</summary>
    public static Dictionary<string, object?> ComputeCatalogField(IReadOnlyList<ImportedTreeVariant> catalog)
    {
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
        return new Dictionary<string, object?> { ["version"] = ImportedTreeCatalog.CatalogVersion, ["variant_count"] = catalog.Count, ["hash"] = Hex(ch.Value) };
    }


    static void HashMeshes(Fnv h, IEnumerable<Mesh?> lods)
    {
        foreach (Mesh? lod in lods)
        {
            if (lod == null) { h.I32(-1); continue; }
            int surfaces = lod.GetSurfaceCount();
            h.I32(surfaces);
            for (int s = 0; s < surfaces; s++)
                h.I32(lod is ArrayMesh am ? am.SurfaceGetArrayLen(s) : lod.SurfaceGetArrays(s)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length);
        }
    }

    /// <summary>
    /// T6 design A6a: the rows of <c>table.RowsEnabled(featureSet)</c> with index 30 or more, in index order: the row's fields, its meshes from the owning catalog (per LOD slot: surface count and vertex count
    /// per surface, as the <c>catalog</c> field does; -1 for an unused slot, -2 when the owning catalog holds no mesh for the row) and each owning catalog's version. {count 0, hash none} when no appended row is enabled.
    /// The derived catalog is loaded only when a row needs it, so generation 1 never touches it. Main thread only.
    /// </summary>
    public static Dictionary<string, object?> ComputeCatalogAppended(IReadOnlySet<string> featureSet, TreeSourceTable table)
    {
        ImportedTreeSourceRow[] rows = table.RowsEnabled(featureSet).Where(r => r.Index >= ImportedTreeCatalog.VariantCount).OrderBy(r => r.Index).ToArray();
        if (rows.Length == 0) return new Dictionary<string, object?> { ["count"] = 0, ["hash"] = "none" };
        var h = new Fnv();
        var versions = new SortedDictionary<int, string>();
        foreach (ImportedTreeSourceRow r in rows)
        {
            h.I32(r.Index); h.I32((int)r.Species); h.I32((int)r.Form); h.I32((int)r.SizeClass); h.I32(r.AgeRank); h.I32((int)r.Role); h.I32((int)r.Catalog); h.I32(r.SourceBaseVariant);
            foreach (char c in r.EnablingFeature ?? "") h.U32(c);
            h.U32(0);
            switch (r.Catalog)
            {
                case TreeSourceCatalog.T6Derived:
                    versions[(int)r.Catalog] = $"{r.Catalog}={DerivedTreeCatalog.CatalogVersion}";
                    DerivedTreeForm? form = DerivedTreeCatalog.Load().FirstOrDefault(f => f.Index == r.Index);
                    if (form == null) h.I32(-2); else HashMeshes(h, form.Lods);
                    break;
                case TreeSourceCatalog.LolipopBase:
                    versions[(int)r.Catalog] = $"{r.Catalog}={ImportedTreeCatalog.CatalogVersion}";
                    IReadOnlyList<ImportedTreeVariant> baseCatalog = ImportedTreeCatalog.Load();
                    if (r.Index < baseCatalog.Count) HashMeshes(h, baseCatalog[r.Index].Lods); else h.I32(-2);
                    break;
                default:
                    versions[(int)r.Catalog] = $"{r.Catalog}=0";
                    h.I32(-2);
                    break;
            }
        }
        return new Dictionary<string, object?> { ["count"] = rows.Length, ["catalog_versions"] = string.Join(",", versions.Values), ["hash"] = Hex(h.Value) };
    }

    public static Dictionary<string, object?> Compute(LandscapeWorld.Spec spec, LandscapeWorld.Worker w, LandscapePoseFinder.Fixture fixture, string fixturePath)
    {
        // The tree catalog is not thread safe: load it here, on the main thread, before any tile builds.
        IReadOnlyList<ImportedTreeVariant> catalog = ImportedTreeCatalog.Load();
        var poses = new List<LandscapePose>();
        foreach (string name in TilePoses)
            poses.Add(fixture.Poses.FirstOrDefault(p => p.Name == name) ?? throw new InvalidOperationException("pose missing from fixture: " + name));

        var row = new Dictionary<string, object?>
        {
            ["generation"] = spec.Recipe.WorldGeneration,
            ["label"] = $"gen{spec.Recipe.WorldGeneration}-v{(spec.IsDefault ? 8 : 9)}-s{spec.Recipe.Seed}",
            ["generator_id"] = spec.Recipe.GeneratorId,
            ["seed"] = spec.Recipe.Seed,
            ["definition_hash"] = spec.DefinitionHashHex,
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

        // 5: scatter (T5, design A9): generation-keyed. Without scatter.rocks the slot stays {count 0, hash none}.
        ComputeScatterSlot(spec, w, row);

        // 6: tree catalog
        row["catalog"] = ComputeCatalogField(catalog);
        // 7: appended catalog entries (T6 design A6a): rows of this generation's feature set with index 30 or more; the `catalog` field above keeps its definition for good.
        row["catalog_appended"] = ComputeCatalogAppended(WorldGenerationLedger.Production.FeatureSet(spec.Recipe.WorldGeneration, spec.Recipe.ReviewFeatures), ImportedTreeSourceTable.Production);
        return row;
    }
}
