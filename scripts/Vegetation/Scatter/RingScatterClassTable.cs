using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using RingworldTechDemo.Terrain.Surface;

namespace RingworldTechDemo.Vegetation.Scatter;

/// <summary>T5 design A5: which meshes, LOD bands and draws a class feeds. Append-only; never reorder or remove.</summary>
public enum RingScatterRenderGroup : byte { Outcrops = 1, Boulders = 2, Cobbles = 3, Saplings = 4, Shrubs = 5, DeadWood = 6, StandFill = 7 }

/// <summary>
/// A5 response fields. Append-only. <c>NearestTreeM</c> is the distance to the nearest accepted tree root, capped at 50 m.
/// T6 design A5 fields (StandAge onward) are derived once per evaluated point by the scatter builder from the habitat sample: StandAge is the effective age of A2, ConiferShare the fir + pine habitat weights of A3,
/// Treeline SmoothStep(0.24, 0.40, T), Clearing the regional clearing field, and the eight memberships are RingPlantCommunities.Memberships; Dense = ClosedBroadleaf + ConiferStand.
/// </summary>
public enum RingHabitatField : byte
{
    Slope, Relief8, Relief64, Wetness, RockExposure, Scree, SoilDepth, Canopy, Moisture, Temperature, ForestWeight, GrassWeight, HeightAboveChannel, NearestTreeM,
    StandAge, ConiferShare, Treeline, Clearing, Dense, ClosedBroadleaf, ConiferStand, OpenWoodland, ForestEdge, ClearingCommunity, DryMeadow, WetMeadow, RockyFell
}

public enum RingScatterAnchorKind : byte { Free, NearTrees }
public enum RingScatterAlignment : byte { Upright, SlopeAligned, BeddingHorizontal, LyingAlongSlope }
public enum RingScatterCollision : byte { None, ConvexHull, TriMesh, Capsule }
public enum RingScatterFar : byte { None, RockSpeckle, CanopyTint }
/// <summary>Trees: mesh indices are ImportedTreeSourceTable indices. For this catalog a record's Size is a uniform scale multiplier of the source variant. T6 appends Shrubs.</summary>
public enum RingScatterMeshCatalog : byte { Rocks, Trees }
/// <summary>Uniform picks a hashed mesh. BySize and ByField are reserved and behave as Uniform. BySpecies (MeshCatalog.Trees rows): the species from the A3 weights at the point times the row's light factors, then a uniform Understory-role variant of that species.</summary>
public enum RingScatterVariantSelector : byte { Uniform, BySize, ByField, BySpecies }

/// <summary>Out0 + (Out1 - Out0) smoothstep(In0, In1, value). Reversed edges (In0 greater than In1) are allowed.</summary>
public readonly record struct RingScatterResponse(RingHabitatField Field, float In0, float In1, float Out0, float Out1);

/// <summary>One term of the density sum: Weight times the product of its factors (no factors = a constant).</summary>
public sealed record RingScatterTerm(float Weight, RingScatterResponse[] Factors);

/// <summary>Reject within ClearanceM + SizeFactor x (size a + size b) / 2 of an accepted record of an earlier class.</summary>
public readonly record struct RingScatterAvoid(ushort ClassId, float ClearanceM, float SizeFactor);

public readonly record struct RingScatterAnchor(RingScatterAnchorKind Kind, int SpeciesMask, float MinM, float MaxM);

/// <summary>One row of the class table (A5). Every column is part of <see cref="RingScatterClassTable.Hash"/>.</summary>
public sealed record RingScatterClass
{
    public ushort Id { get; init; }
    public string Key { get; init; } = "";
    public int FeatureBit { get; init; } = RingHabitatFieldGrid.ScatterRocksBit;
    public RingScatterRenderGroup RenderGroup { get; init; } = RingScatterRenderGroup.Boulders;
    public float ReachM { get; init; } = 100f;
    public float CellM { get; init; } = 8f;
    public int CandidatesPerCell { get; init; } = 1;
    public float BaseDensityPerHa { get; init; }
    public RingScatterTerm[] Terms { get; init; } = Array.Empty<RingScatterTerm>();
    public RingScatterResponse[] Multipliers { get; init; } = Array.Empty<RingScatterResponse>();
    public RingScatterAnchor Anchor { get; init; } = new(RingScatterAnchorKind.Free, 0, 0f, 0f);
    public float SpacingM { get; init; }
    public float SpacingSizeFactor { get; init; }
    public RingScatterAvoid[] Avoid { get; init; } = Array.Empty<RingScatterAvoid>();
    public float TreeClearanceM { get; init; }
    public float TreeClearanceSizeFactor { get; init; }
    public float SizeMinM { get; init; } = 1f;
    public float SizeMaxM { get; init; } = 1f;
    public float SizeExponent { get; init; } = 1f;
    public RingScatterMeshCatalog MeshCatalog { get; init; } = RingScatterMeshCatalog.Rocks;
    public int MeshFirst { get; init; }
    public int MeshCount { get; init; } = 1;
    public RingScatterVariantSelector VariantSelector { get; init; } = RingScatterVariantSelector.Uniform;
    public RingScatterAlignment Alignment { get; init; } = RingScatterAlignment.Upright;
    public float AlignFactor { get; init; }
    public float RandomTiltDeg { get; init; }
    public float EmbedMin { get; init; }
    public float EmbedMax { get; init; }
    public RingScatterCollision Collision { get; init; } = RingScatterCollision.None;
    public float CollisionMinSizeM { get; init; }
    public float ShadowMinSizeM { get; init; } = float.PositiveInfinity;
    public RingScatterFar Far { get; init; } = RingScatterFar.None;
    public int ResidentCap { get; init; }
    /// <summary>0 builds the whole tile; 60 builds per 60 m block (cobbles, A6).</summary>
    public int BlockM { get; init; }
    /// <summary>T5-10b buried-base rule: the mesh reaches this fraction of the size below the root (0.75 for outcrops, 0 = rule off). A candidate is not viable when the terrain under any of 17 points of its footprint (the centre and 16 on a ring of <see cref="BuriedFootprintRadius"/> x size) lies below the root height - fraction x size + <see cref="BuriedClearanceM"/>, because the buried column would come out of a cliff face there.</summary>
    public float BuriedDepthFraction { get; init; }
    /// <summary>Horizontal radius of the group's largest variant in unit space (checked against the prepared catalog by scatter_rock_mesh_validation_test).</summary>
    public float BuriedFootprintRadius { get; init; }
    public float BuriedClearanceM { get; init; } = 0.5f;

    internal bool UsesField(RingHabitatField field)
    {
        foreach (RingScatterTerm term in Terms) foreach (RingScatterResponse r in term.Factors) if (r.Field == field) return true;
        foreach (RingScatterResponse r in Multipliers) if (r.Field == field) return true;
        return false;
    }
}

/// <summary>
/// T5 design A5: the append-only class table. Rows are built in order of ReachM descending, ties by Id; a row may avoid only rows built before it.
/// <see cref="Production"/> holds the three rock rows of A5 (T5-5); tests build their own tables with <see cref="CreateForTests"/>.
/// </summary>
public sealed class RingScatterClassTable
{
    private static readonly RingScatterClassTable ProductionTable = new(ProductionRows());
    /// <summary>TEST ONLY (T5-10, tools/ScatterDebugBridge.cs): a table swapped in by the landscape harness before the streamer is created (`--scatter-a5-density`). Null in every normal run.</summary>
    internal static RingScatterClassTable? DebugOverride { get; set; }
    public static RingScatterClassTable Production => DebugOverride ?? ProductionTable;

    private static RingScatterResponse R(RingHabitatField f, float in0, float in1, float out0 = 0f, float out1 = 1f) => new(f, in0, in1, out0, out1);

    /// <summary>
    /// A5 rock rows (Ids 1-3) and the T6 rows of feature bit 6 (A7): shrub (Id 11), sapling (Id 12) and dead wood (snag 10, fallen tree 13, stump 14). Size factors use the schema's "factor x (size a + size b) / 2": the table's "0.45 x (a + b)" is 0.9 and "0.5 x (a + b)" is 1.0.
    /// Mesh indices are group-local (RenderGroup selects the group). BaseDensityPerHa values are the table's starting values (T5-5 calibration, see scatter_rock_placement_test).
    /// </summary>
    private static RingScatterClass[] ProductionRows() => new[]
    {
        new RingScatterClass
        {
            Id = 1, Key = "rock.outcrop", RenderGroup = RingScatterRenderGroup.Outcrops, ReachM = 2500f, CellM = 64f, CandidatesPerCell = 1, BaseDensityPerHa = 0.3f,
            Terms = new[]
            {
                new RingScatterTerm(1.0f, new[] { R(RingHabitatField.RockExposure, 0f, 1f), R(RingHabitatField.Relief64, -0.2f, 0.5f) }),
                new RingScatterTerm(1.0f, new[] { R(RingHabitatField.Relief64, 0.35f, 0.8f), R(RingHabitatField.Slope, 18f, 30f) }),
            },
            SpacingM = 30f, SpacingSizeFactor = 0.6f, TreeClearanceM = 2f, TreeClearanceSizeFactor = 0.5f, SizeMinM = 5f, SizeMaxM = 40f, SizeExponent = 2.2f,
            MeshFirst = 0, MeshCount = 8, Alignment = RingScatterAlignment.BeddingHorizontal, Collision = RingScatterCollision.TriMesh, ShadowMinSizeM = 0f,
            Far = RingScatterFar.None, ResidentCap = 150, BlockM = 0, BuriedDepthFraction = 1.25f, BuriedFootprintRadius = 0.55f
        },
        new RingScatterClass
        {
            Id = 2, Key = "rock.boulder", RenderGroup = RingScatterRenderGroup.Boulders, ReachM = 600f, CellM = 8f, CandidatesPerCell = 2, BaseDensityPerHa = 100f,
            Terms = new[]
            {
                // Design-A5 habitat weights (user, 2026-11-19: the A5 boulder density over the T5-5 calibration, which was these x 0.4); the lone-boulder constant is unchanged.
                new RingScatterTerm(1.0f, new[] { R(RingHabitatField.Scree, 0f, 1f) }),
                new RingScatterTerm(0.6f, new[] { R(RingHabitatField.Relief64, -0.15f, -0.6f), R(RingHabitatField.Slope, 4f, 15f) }),
                new RingScatterTerm(0.4f, new[] { R(RingHabitatField.RockExposure, 0f, 1f), R(RingHabitatField.Relief64, 0.2f, 0.7f, 1f, 0f) }),
                new RingScatterTerm(0.01f, Array.Empty<RingScatterResponse>()),
            },
            Multipliers = new[] { R(RingHabitatField.Canopy, 0f, 1f, 1f, 0.7f), R(RingHabitatField.Slope, 42f, 52f, 1f, 0f) }, // angle of repose: no boulders stand on near-vertical faces
            SpacingM = 0f, SpacingSizeFactor = 0.9f, Avoid = new[] { new RingScatterAvoid(1, 0f, 1.0f) }, TreeClearanceM = 1f, TreeClearanceSizeFactor = 0.5f,
            SizeMinM = 1f, SizeMaxM = 4f, SizeExponent = 1.6f, MeshFirst = 0, MeshCount = 16, Alignment = RingScatterAlignment.SlopeAligned, AlignFactor = 0.6f, RandomTiltDeg = 8f,
            EmbedMin = 0.20f, EmbedMax = 0.35f, Collision = RingScatterCollision.ConvexHull, CollisionMinSizeM = 0.8f, ShadowMinSizeM = 2f, Far = RingScatterFar.RockSpeckle,
            ResidentCap = 1500, BlockM = 0
        },
        new RingScatterClass
        {
            Id = 3, Key = "rock.cobble", RenderGroup = RingScatterRenderGroup.Cobbles, ReachM = 50f, CellM = 8f, CandidatesPerCell = 4, BaseDensityPerHa = 500f,
            Terms = new[]
            {
                new RingScatterTerm(1.0f, new[] { R(RingHabitatField.Scree, 0f, 1f) }),
                new RingScatterTerm(0.3f, new[] { R(RingHabitatField.RockExposure, 0f, 1f), R(RingHabitatField.Slope, 40f, 46f, 1f, 0f) }),
            },
            Multipliers = new[] { R(RingHabitatField.Canopy, 0f, 1f, 1f, 0.5f) },
            SpacingM = 0f, SpacingSizeFactor = 1.0f, Avoid = new[] { new RingScatterAvoid(1, 0f, 1.0f), new RingScatterAvoid(2, 0f, 1.0f) },
            TreeClearanceM = 0.6f, TreeClearanceSizeFactor = 0.5f, SizeMinM = 0.8f, SizeMaxM = 1.6f, SizeExponent = 1f, MeshFirst = 0, MeshCount = 6,
            Alignment = RingScatterAlignment.SlopeAligned, AlignFactor = 1f, RandomTiltDeg = 4f, EmbedMin = 0.30f, EmbedMax = 0.30f, Collision = RingScatterCollision.None,
            Far = RingScatterFar.RockSpeckle, ResidentCap = 1000, BlockM = 60
        },
        // T6-7 shrubs (A7, A8): built before the saplings (equal reach, smaller Id). A record's Size is the height in metres (the carrier is 1 unit tall, so the instance scale is the Size). BySpecies, with the builder's look overrides
        // (WetMeadow dominant: broadleaf; DryMeadow or RockyFell dominant: pine scrub).
        new RingScatterClass
        {
            Id = 11, Key = "veg.shrub", FeatureBit = 6, RenderGroup = RingScatterRenderGroup.Shrubs, ReachM = 150f, CellM = 8f, CandidatesPerCell = 6, BaseDensityPerHa = 100f,
            Terms = new[]
            {
                new RingScatterTerm(2.0f, new[] { R(RingHabitatField.ClosedBroadleaf, 0f, 1f) }),
                new RingScatterTerm(1.2f, new[] { R(RingHabitatField.ConiferStand, 0f, 1f) }),
                new RingScatterTerm(1.5f, new[] { R(RingHabitatField.OpenWoodland, 0f, 1f) }),
                new RingScatterTerm(7.0f, new[] { R(RingHabitatField.ForestEdge, 0f, 1f) }),
                new RingScatterTerm(0.6f, new[] { R(RingHabitatField.ClearingCommunity, 0f, 1f) }),
                new RingScatterTerm(0.25f, new[] { R(RingHabitatField.DryMeadow, 0f, 1f) }),
                new RingScatterTerm(0.6f, new[] { R(RingHabitatField.WetMeadow, 0f, 1f) }),
                new RingScatterTerm(0.4f, new[] { R(RingHabitatField.RockyFell, 0f, 1f) }),
                new RingScatterTerm(0.05f, Array.Empty<RingScatterResponse>()),
            },
            Multipliers = new[] { R(RingHabitatField.Treeline, 0f, 1f, 0.3f, 1f), R(RingHabitatField.Slope, 30f, 38f, 1f, 0f) },
            SpacingM = 0f, SpacingSizeFactor = 0.6f, Avoid = new[] { new RingScatterAvoid(10, 2f, 0f) }, TreeClearanceM = 1.0f, TreeClearanceSizeFactor = 0f, SizeMinM = 0.8f, SizeMaxM = 3.0f, SizeExponent = 1.4f,
            MeshCatalog = RingScatterMeshCatalog.Trees, MeshFirst = 43, MeshCount = 12, VariantSelector = RingScatterVariantSelector.BySpecies,
            Alignment = RingScatterAlignment.Upright, RandomTiltDeg = 4f, EmbedMin = 0.05f, EmbedMax = 0.05f, Collision = RingScatterCollision.None,
            ShadowMinSizeM = float.PositiveInfinity, Far = RingScatterFar.None, ResidentCap = 3000, BlockM = 0
        },
        new RingScatterClass
        {
            Id = 12, Key = "veg.sapling", FeatureBit = 6, RenderGroup = RingScatterRenderGroup.Saplings, ReachM = 150f, CellM = 8f, CandidatesPerCell = 2, BaseDensityPerHa = 100f,
            Terms = new[]
            {
                new RingScatterTerm(0.3f, new[] { R(RingHabitatField.Dense, 0f, 1f) }),
                new RingScatterTerm(1.1f, new[] { R(RingHabitatField.ForestEdge, 0f, 1f) }),
                new RingScatterTerm(0.6f, new[] { R(RingHabitatField.ClearingCommunity, 0f, 1f) }),
                new RingScatterTerm(0.4f, new[] { R(RingHabitatField.OpenWoodland, 0f, 1f) }),
                new RingScatterTerm(0.02f, Array.Empty<RingScatterResponse>()),
            },
            Multipliers = new[] { R(RingHabitatField.Treeline, 0f, 1f, 0.3f, 1f), R(RingHabitatField.Slope, 28f, 34f, 1f, 0f) },
            SpacingM = 2f, SpacingSizeFactor = 0f, Avoid = new[] { new RingScatterAvoid(10, 2f, 0f), new RingScatterAvoid(11, 0f, 0.5f) }, TreeClearanceM = 1.5f, TreeClearanceSizeFactor = 0f, SizeMinM = 0.8f, SizeMaxM = 1.2f, SizeExponent = 1f,
            MeshCatalog = RingScatterMeshCatalog.Trees, MeshFirst = 0, MeshCount = 27, VariantSelector = RingScatterVariantSelector.BySpecies,
            Alignment = RingScatterAlignment.Upright, RandomTiltDeg = 3f, EmbedMin = 0.02f, EmbedMax = 0.03f, Collision = RingScatterCollision.None,
            ShadowMinSizeM = float.PositiveInfinity, Far = RingScatterFar.None, ResidentCap = 1500, BlockM = 0
        },
        // T6-6 dead wood. For MeshCatalog.Trees rows a record's Size is a scale multiplier of the source variant (a log's length is 0.55 x size x the source height). Embeds are fractions of the instance height
        // (0.1 m of a snag or log of about 10 m, 0.05 m of the stump's source height). Spacing and clearances that the table gives "x length" are measured with the log's length (the builder's Extent).
        new RingScatterClass
        {
            Id = 10, Key = "veg.snag", FeatureBit = 6, RenderGroup = RingScatterRenderGroup.DeadWood, ReachM = 300f, CellM = 32f, CandidatesPerCell = 1, BaseDensityPerHa = 10f,
            Terms = new[]
            {
                new RingScatterTerm(0.8f, new[] { R(RingHabitatField.Dense, 0f, 1f), R(RingHabitatField.StandAge, 0.55f, 0.95f) }),
                new RingScatterTerm(0.15f, new[] { R(RingHabitatField.OpenWoodland, 0f, 1f) }),
                new RingScatterTerm(0.05f, Array.Empty<RingScatterResponse>()),
            },
            Multipliers = new[] { R(RingHabitatField.Treeline, 0f, 1f), R(RingHabitatField.Slope, 28f, 34f, 1f, 0f) },
            SpacingM = 6f, SpacingSizeFactor = 0.3f, TreeClearanceM = 3f, TreeClearanceSizeFactor = 0.5f, SizeMinM = 0.85f, SizeMaxM = 1.15f, SizeExponent = 1f,
            MeshCatalog = RingScatterMeshCatalog.Trees, MeshFirst = 30, MeshCount = 6, VariantSelector = RingScatterVariantSelector.BySpecies,
            Alignment = RingScatterAlignment.Upright, EmbedMin = 0.01f, EmbedMax = 0.01f, Collision = RingScatterCollision.Capsule, ShadowMinSizeM = 0f, Far = RingScatterFar.None, ResidentCap = 200, BlockM = 0
        },
        new RingScatterClass
        {
            Id = 13, Key = "veg.fallen", FeatureBit = 6, RenderGroup = RingScatterRenderGroup.DeadWood, ReachM = 150f, CellM = 16f, CandidatesPerCell = 1, BaseDensityPerHa = 10f,
            Terms = new[]
            {
                new RingScatterTerm(0.6f, new[] { R(RingHabitatField.ClosedBroadleaf, 0f, 1f), R(RingHabitatField.StandAge, 0f, 1f, 0.4f, 1f) }),
                new RingScatterTerm(0.3f, new[] { R(RingHabitatField.ConiferStand, 0f, 1f) }),
                new RingScatterTerm(0.2f, new[] { R(RingHabitatField.ForestEdge, 0f, 1f) }),
            },
            Multipliers = new[] { R(RingHabitatField.Slope, 15f, 25f, 1f, 0f) },
            SpacingM = 0f, SpacingSizeFactor = 0.5f, Avoid = new[] { new RingScatterAvoid(10, 0f, 1f), new RingScatterAvoid(12, 0f, 1f) }, TreeClearanceM = 0f, TreeClearanceSizeFactor = 0.5f,
            SizeMinM = 0.85f, SizeMaxM = 1.15f, SizeExponent = 1f,
            MeshCatalog = RingScatterMeshCatalog.Trees, MeshFirst = 36, MeshCount = 4, VariantSelector = RingScatterVariantSelector.BySpecies,
            Alignment = RingScatterAlignment.LyingAlongSlope, EmbedMin = 0.01f, EmbedMax = 0.01f, Collision = RingScatterCollision.Capsule, ShadowMinSizeM = 0f, Far = RingScatterFar.None, ResidentCap = 400, BlockM = 0
        },
        new RingScatterClass
        {
            Id = 14, Key = "veg.stump", FeatureBit = 6, RenderGroup = RingScatterRenderGroup.DeadWood, ReachM = 80f, CellM = 16f, CandidatesPerCell = 1, BaseDensityPerHa = 5f,
            Terms = new[]
            {
                new RingScatterTerm(0.2f, new[] { R(RingHabitatField.Dense, 0f, 1f) }),
                new RingScatterTerm(1.2f, new[] { R(RingHabitatField.ClearingCommunity, 0f, 1f) }),
                new RingScatterTerm(0.2f, new[] { R(RingHabitatField.ForestEdge, 0f, 1f) }),
            },
            Multipliers = new[] { R(RingHabitatField.Slope, 25f, 32f, 1f, 0f) },
            SpacingM = 4f, SpacingSizeFactor = 0f, Avoid = new[] { new RingScatterAvoid(10, 1f, 0f), new RingScatterAvoid(12, 1f, 0f), new RingScatterAvoid(13, 1f, 0f) }, TreeClearanceM = 1f, TreeClearanceSizeFactor = 0f,
            SizeMinM = 0.9f, SizeMaxM = 1.2f, SizeExponent = 1f,
            MeshCatalog = RingScatterMeshCatalog.Trees, MeshFirst = 40, MeshCount = 3, VariantSelector = RingScatterVariantSelector.BySpecies,
            Alignment = RingScatterAlignment.Upright, EmbedMin = 0.006f, EmbedMax = 0.006f, Collision = RingScatterCollision.Capsule, ShadowMinSizeM = float.PositiveInfinity, Far = RingScatterFar.None, ResidentCap = 200, BlockM = 0
        },
        // T6 stand filler (design T6-stand-filler-design.md section 2): near-only sub-canopy stems of the small maple and pine variants in dense forest, preferring canopy gaps (NearestTreeM). Records only until SF-3 draws and collides them.
        new RingScatterClass
        {
            Id = 15, Key = "veg.standfill", FeatureBit = 6, RenderGroup = RingScatterRenderGroup.StandFill, ReachM = 150f, CellM = 8f, CandidatesPerCell = 4, BaseDensityPerHa = 255f,
            Terms = new[]
            {
                new RingScatterTerm(1.0f, new[] { R(RingHabitatField.Dense, 0f, 1f), R(RingHabitatField.StandAge, 0f, 1f, 1f, 0.55f) }),
                new RingScatterTerm(0.25f, new[] { R(RingHabitatField.OpenWoodland, 0f, 1f) }),
            },
            Multipliers = new[] { R(RingHabitatField.Treeline, 0f, 1f, 0.3f, 1f), R(RingHabitatField.Slope, 28f, 34f, 1f, 0f), R(RingHabitatField.NearestTreeM, 2.5f, 6f, 0.3f, 1f) },
            SpacingM = 3f, SpacingSizeFactor = 0f, Avoid = new[] { new RingScatterAvoid(10, 2f, 0f), new RingScatterAvoid(12, 1f, 0f) }, TreeClearanceM = 2.5f, TreeClearanceSizeFactor = 0f, SizeMinM = 0.85f, SizeMaxM = 1.15f, SizeExponent = 1f,
            MeshCatalog = RingScatterMeshCatalog.Trees, MeshFirst = 9, MeshCount = 21, VariantSelector = RingScatterVariantSelector.BySpecies,
            Alignment = RingScatterAlignment.Upright, RandomTiltDeg = 2f, EmbedMin = 0.02f, EmbedMax = 0.03f, Collision = RingScatterCollision.None,
            ShadowMinSizeM = float.PositiveInfinity, Far = RingScatterFar.None, ResidentCap = 1500, BlockM = 0
        },
    };

    public static RingScatterClassTable CreateForTests(IEnumerable<RingScatterClass> rows) => new(rows);

    public IReadOnlyList<RingScatterClass> Rows { get; }
    /// <summary>Rows in build order: ReachM descending, then Id.</summary>
    public IReadOnlyList<RingScatterClass> BuildOrder { get; }
    public ulong Hash { get; }

    private readonly ConcurrentDictionary<int, RingScatterClassTable> _views = new();

    private RingScatterClassTable(IEnumerable<RingScatterClass> rows)
    {
        Rows = Array.AsReadOnly(rows.ToArray());
        BuildOrder = Array.AsReadOnly(Rows.OrderByDescending(r => r.ReachM).ThenBy(r => r.Id).ToArray());
        Hash = ComputeHash(Rows);
    }

    /// <summary>
    /// T6 A7: a view over the rows whose FeatureBit is set in the world feature mask, build order recomputed over them. An Avoid entry pointing at a row outside the view is dropped.
    /// Returns this table when every row is in the mask. For a rocks-only mask (bit 4) the view's hash equals the three-row rock table's hash bit for bit.
    /// </summary>
    public RingScatterClassTable ForMask(int mask)
    {
        int used = 0;
        foreach (RingScatterClass r in Rows) used |= 1 << r.FeatureBit;
        return _views.GetOrAdd(mask & used, m =>
        {
            RingScatterClass[] kept = Rows.Where(r => (m & (1 << r.FeatureBit)) != 0).ToArray();
            if (kept.Length == Rows.Count) return this;
            var ids = new HashSet<ushort>(kept.Select(r => r.Id));
            return new RingScatterClassTable(kept.Select(r => r.Avoid.All(a => ids.Contains(a.ClassId)) ? r : r with { Avoid = r.Avoid.Where(a => ids.Contains(a.ClassId)).ToArray() }));
        });
    }

    /// <summary>The hash of <see cref="ForMask"/> (the scatter cache identity and the fingerprint's class_table_hash).</summary>
    public ulong HashFor(int mask) => ForMask(mask).Hash;

    public int BuildIndexOf(ushort id)
    {
        for (int i = 0; i < BuildOrder.Count; i++) if (BuildOrder[i].Id == id) return i;
        return -1;
    }

    public RingScatterClass? Find(ushort id) => Rows.FirstOrDefault(r => r.Id == id);

    private struct Fnv
    {
        public ulong H;
        public void Byte(byte b) { H ^= b; H *= 0x100000001B3UL; }
        public void U64(ulong v) { for (int i = 0; i < 8; i++) Byte((byte)(v >> (8 * i))); }
        public void I32(int v) { uint u = (uint)v; for (int i = 0; i < 4; i++) Byte((byte)(u >> (8 * i))); }
        public void F32(float v) => I32(BitConverter.SingleToInt32Bits(v));
        public void F64(double v) => U64((ulong)BitConverter.DoubleToInt64Bits(v));
        public void Str(string s) { I32(s.Length); foreach (char c in s) I32(c); }
    }

    private static void Mix(ref Fnv h, in RingScatterResponse r)
    {
        h.I32((int)r.Field); h.F32(r.In0); h.F32(r.In1); h.F32(r.Out0); h.F32(r.Out1);
    }

    private static ulong ComputeHash(IReadOnlyList<RingScatterClass> rows)
    {
        var h = new Fnv { H = 0xCBF29CE484222325UL };
        // The ground model's constants behind RockExposure, ScreeWeight and Relief, and the grid's lattice constants: a change to any of them changes every habitat field.
        h.F64(RingGroundSurfaceModel.RockRampDeg); h.F64(RingGroundSurfaceModel.ConvexShiftDeg); h.F64(RingGroundSurfaceModel.ForestShiftDeg);
        h.F64(RingGroundSurfaceModel.SlopeRefBaselineM); h.F64(RingGroundSurfaceModel.SlopeBiasPerOctaveDeg); h.F64(RingGroundSurfaceModel.SlopeBiasMaxDeg);
        h.F64(RingGroundSurfaceModel.DefaultRockStartDeg);
        h.F64(RingGroundSurfaceModel.ScreeInStart); h.F64(RingGroundSurfaceModel.ScreeInEnd); h.F64(RingGroundSurfaceModel.ScreeOutStart); h.F64(RingGroundSurfaceModel.ScreeOutEnd);
        h.F64(RingGroundSurfaceModel.ScreeConvexFadeStart); h.F64(RingGroundSurfaceModel.ScreeConvexFadeEnd); h.F64(RingGroundSurfaceModel.ScreeVegetated);
        h.F64(RingGroundSurfaceModel.ReliefRefM);
        h.F64(RingHabitatFieldGrid.FineM); h.F64(RingHabitatFieldGrid.CoarseM); h.F64(RingHabitatFieldGrid.HabitatFineSlopeDeg);
        h.I32(rows.Count);
        foreach (RingScatterClass r in rows.OrderBy(x => x.Id))
        {
            h.I32(r.Id); h.Str(r.Key); h.I32(r.FeatureBit); h.I32((int)r.RenderGroup); h.F32(r.ReachM); h.F32(r.CellM); h.I32(r.CandidatesPerCell); h.F32(r.BaseDensityPerHa);
            h.I32(r.Terms.Length);
            foreach (RingScatterTerm t in r.Terms) { h.F32(t.Weight); h.I32(t.Factors.Length); foreach (RingScatterResponse f in t.Factors) Mix(ref h, f); }
            h.I32(r.Multipliers.Length);
            foreach (RingScatterResponse m in r.Multipliers) Mix(ref h, m);
            h.I32((int)r.Anchor.Kind); h.I32(r.Anchor.SpeciesMask); h.F32(r.Anchor.MinM); h.F32(r.Anchor.MaxM);
            h.F32(r.SpacingM); h.F32(r.SpacingSizeFactor);
            h.I32(r.Avoid.Length);
            foreach (RingScatterAvoid a in r.Avoid) { h.I32(a.ClassId); h.F32(a.ClearanceM); h.F32(a.SizeFactor); }
            h.F32(r.TreeClearanceM); h.F32(r.TreeClearanceSizeFactor); h.F32(r.SizeMinM); h.F32(r.SizeMaxM); h.F32(r.SizeExponent);
            h.I32((int)r.MeshCatalog); h.I32(r.MeshFirst); h.I32(r.MeshCount); h.I32((int)r.VariantSelector);
            h.I32((int)r.Alignment); h.F32(r.AlignFactor); h.F32(r.RandomTiltDeg); h.F32(r.EmbedMin); h.F32(r.EmbedMax);
            h.I32((int)r.Collision); h.F32(r.CollisionMinSizeM); h.F32(r.ShadowMinSizeM); h.I32((int)r.Far); h.I32(r.ResidentCap); h.I32(r.BlockM);
            h.F32(r.BuriedDepthFraction); h.F32(r.BuriedFootprintRadius); h.F32(r.BuriedClearanceM);
        }
        return h.H;
    }

    /// <summary>Returns every violated rule; an empty list means the table is valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var ids = new HashSet<ushort>();
        foreach (RingScatterClass r in Rows)
        {
            string n = $"row {r.Id} ({r.Key})";
            if (r.Id < 1) errors.Add($"{n}: id must be at least 1");
            if (!ids.Add(r.Id)) errors.Add($"{n}: duplicate id");
            if (r.CellM != 8f && r.CellM != 16f && r.CellM != 32f && r.CellM != 64f) errors.Add($"{n}: CellM {r.CellM} is not 8, 16, 32 or 64");
            if (r.CandidatesPerCell < 1 || r.CandidatesPerCell > 16) errors.Add($"{n}: CandidatesPerCell out of 1-16");
            if (r.BlockM != 0 && r.BlockM != 60) errors.Add($"{n}: BlockM {r.BlockM} is not 0 or 60");
            if (!Enum.IsDefined(r.RenderGroup)) errors.Add($"{n}: unknown render group");
            if (!Enum.IsDefined(r.Alignment)) errors.Add($"{n}: unknown alignment");
            if (!Enum.IsDefined(r.Collision)) errors.Add($"{n}: unknown collision");
            if (!Enum.IsDefined(r.Far)) errors.Add($"{n}: unknown far mode");
            if (!Enum.IsDefined(r.MeshCatalog)) errors.Add($"{n}: unknown mesh catalog");
            if (!Enum.IsDefined(r.VariantSelector)) errors.Add($"{n}: unknown variant selector");
            if (!Enum.IsDefined(r.Anchor.Kind)) errors.Add($"{n}: unknown anchor kind");
            if (!(float.IsFinite(r.ReachM) && r.ReachM > 0f)) errors.Add($"{n}: ReachM must be finite and positive");
            if (!(float.IsFinite(r.BaseDensityPerHa) && r.BaseDensityPerHa >= 0f)) errors.Add($"{n}: BaseDensityPerHa must be finite and not negative");
            if (!(float.IsFinite(r.SizeMinM) && float.IsFinite(r.SizeMaxM) && r.SizeMinM > 0f && r.SizeMaxM >= r.SizeMinM)) errors.Add($"{n}: size range invalid");
            if (!(float.IsFinite(r.SizeExponent) && r.SizeExponent > 0f)) errors.Add($"{n}: SizeExponent must be finite and positive");
            if (!(float.IsFinite(r.SpacingM) && r.SpacingM >= 0f && float.IsFinite(r.SpacingSizeFactor) && r.SpacingSizeFactor >= 0f)) errors.Add($"{n}: spacing invalid");
            if (!(float.IsFinite(r.TreeClearanceM) && r.TreeClearanceM >= 0f && float.IsFinite(r.TreeClearanceSizeFactor) && r.TreeClearanceSizeFactor >= 0f)) errors.Add($"{n}: tree clearance invalid");
            if (!(float.IsFinite(r.BuriedDepthFraction) && r.BuriedDepthFraction >= 0f && float.IsFinite(r.BuriedFootprintRadius) && r.BuriedFootprintRadius >= 0f && float.IsFinite(r.BuriedClearanceM) && r.BuriedClearanceM >= 0f)) errors.Add($"{n}: buried-base values invalid");
            if (r.MeshFirst < 0 || r.MeshCount < 1 || r.MeshFirst + r.MeshCount > ushort.MaxValue) errors.Add($"{n}: mesh range invalid");
            foreach (RingScatterTerm t in r.Terms)
            {
                if (!float.IsFinite(t.Weight)) errors.Add($"{n}: non-finite term weight");
                foreach (RingScatterResponse f in t.Factors) CheckResponse(errors, n, f);
            }
            foreach (RingScatterResponse m in r.Multipliers) CheckResponse(errors, n, m);
        }
        for (int i = 0; i < BuildOrder.Count; i++)
        {
            RingScatterClass r = BuildOrder[i];
            foreach (RingScatterAvoid a in r.Avoid)
            {
                int at = BuildIndexOf(a.ClassId);
                if (at < 0) errors.Add($"row {r.Id}: avoids unknown class {a.ClassId}");
                else if (at >= i) errors.Add($"row {r.Id}: avoids class {a.ClassId}, which is not built earlier");
                if (!(float.IsFinite(a.ClearanceM) && a.ClearanceM >= 0f && float.IsFinite(a.SizeFactor) && a.SizeFactor >= 0f)) errors.Add($"row {r.Id}: avoid values invalid");
            }
        }
        return errors;
    }

    private static void CheckResponse(List<string> errors, string n, in RingScatterResponse r)
    {
        if (!Enum.IsDefined(r.Field)) errors.Add($"{n}: unknown response field");
        if (!(float.IsFinite(r.In0) && float.IsFinite(r.In1) && float.IsFinite(r.Out0) && float.IsFinite(r.Out1)) || r.In0 == r.In1) errors.Add($"{n}: response values invalid");
    }
}
