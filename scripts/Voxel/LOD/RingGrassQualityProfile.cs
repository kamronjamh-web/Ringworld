using System;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>One bounded standing-grass streaming budget for a graphics preset.</summary>
[GlobalClass]
public partial class RingGrassQualityProfile : Resource
{
    [Export] public bool Enabled { get; set; } = true;
    [Export(PropertyHint.Range, "0,96,1")] public float NearDensity { get; set; } = 12;
    [Export(PropertyHint.Range, "0,96,1")] public float FarDensity { get; set; } = 3;
    [Export(PropertyHint.Range, "8,128,8")] public float TileMeters { get; set; } = 16;
    [Export(PropertyHint.Range, "8,128,4")] public float VisibilityRadiusMeters { get; set; } = 32;
    [Export(PropertyHint.Range, "4,64,1")] public float NearDistanceMeters { get; set; } = 16;
    [Export(PropertyHint.Range, "8,160,1")] public float FarDistanceMeters { get; set; } = 32;
    [Export(PropertyHint.Range, "0,32,0.5")] public float TransitionWidthMeters { get; set; } = 4;
    [Export(PropertyHint.Range, "1,256,1")] public int MaximumResidentTiles { get; set; } = 81;
    [Export(PropertyHint.Range, "100,200000,100")] public int MaximumInstancesPerTile { get; set; } = 3072;
    [Export(PropertyHint.Range, "1000,5000000,1000")] public int MaximumInstances { get; set; } = 250000;
    [Export(PropertyHint.Range, "1,8,1")] public int MaximumBuildsInFlight { get; set; } = 2;
    [Export(PropertyHint.Range, "1,16,1")] public int MaximumUploadsPerFrame { get; set; } = 2;
    [Export(PropertyHint.Range, "0.1,20,0.1")] public float UploadBudgetMilliseconds { get; set; } = 2;

    /// <summary>Applies only when the game graphics controller selects a preset.</summary>
    public void ApplyTo(RingStandingGrassLayer layer)
    {
        // The worker builds one canonical near-density root set. Distance LOD
        // only hides a deterministic prefix, so camera movement never rebuilds it.
        layer.BladesPerSquareMeter = NearDensity;
        layer.SpatialTileMeters = TileMeters;
        layer.VisibilityRadiusMeters = VisibilityRadiusMeters;
        layer.MaximumResidentTiles = MaximumResidentTiles;
        layer.MaximumInstancesPerTile = MaximumInstancesPerTile;
        layer.MaximumInstances = MaximumInstances;
        layer.MaximumTileBuildsInFlight = MaximumBuildsInFlight;
        layer.MaximumTileUploadsPerFrame = MaximumUploadsPerFrame;
        layer.MaximumTileUploadMilliseconds = UploadBudgetMilliseconds;
        float farRatio = NearDensity > 0 ? Mathf.Clamp(FarDensity / NearDensity, .05f, 1) : .05f;
        layer.ApplyGrassQuality(Enabled, NearDistanceMeters, FarDistanceMeters, TransitionWidthMeters, 1, farRatio);
    }

    /// <summary>Checks the resident cache and cap together; the scheduler never retains more tiles than this.</summary>
    public bool HasSufficientInstanceBudget()
    {
        return HasSufficientResidentWindow()
            && (long)MaximumResidentTiles * MaximumInstancesPerTile <= MaximumInstances;
    }

    /// <summary>Bounds intersecting tiles with the tile diagonal, rather than the scheduler's rejected square scan.</summary>
    public bool HasSufficientResidentWindow()
    {
        double radius = Math.Max(VisibilityRadiusMeters, FarDistanceMeters + TransitionWidthMeters * .5f);
        double expandedRadius = radius + Math.Sqrt(2) * Math.Max(16, TileMeters);
        long intersectingTileBound = (long)Math.Ceiling(Math.PI * expandedRadius * expandedRadius / (TileMeters * TileMeters));
        return MaximumResidentTiles >= intersectingTileBound;
    }

    public static RingGrassQualityProfile For(RingTerrainLodCoordinator.QualityPreset preset, string hardwareTier = "desktop_mid_high")
    {
        if (string.Equals(hardwareTier, "laptop_140v_baseline", StringComparison.OrdinalIgnoreCase))
            return Create(8, 2, 16, 28, 12, 28, 3, 49, 1500, 73500, 1, 1, 1.5f);
        return preset switch
        {
        RingTerrainLodCoordinator.QualityPreset.Low => Create(4, 1, 16, 28, 12, 28, 3, 49, 1500, 73500, 1, 1, 1.5f),
        RingTerrainLodCoordinator.QualityPreset.Medium => Create(8, 2, 16, 32, 16, 32, 4, 49, 3000, 147000, 2, 2, 2),
        // Per-tile caps bound the retained canonical root buffer. A dense source
        // grid is intentionally trimmed to 5,000 roots/tile under gameplay caps.
        RingTerrainLodCoordinator.QualityPreset.High => Create(24, 6, 16, 32, 18, 32, 4, 49, 5000, 245000, 3, 3, 3),
        RingTerrainLodCoordinator.QualityPreset.VeryHigh => Create(32, 8, 16, 32, 22, 32, 4, 49, 5000, 245000, 4, 3, 4),
        // Capture-only tiers explicitly opt into the larger retained CPU and two-pass GPU buffers.
        RingTerrainLodCoordinator.QualityPreset.UltraVideo => Create(48, 12, 16, 48, 28, 48, 6, 121, 12288, 1487000, 4, 4, 5),
        RingTerrainLodCoordinator.QualityPreset.Cinematic5090 => Create(96, 24, 16, 64, 32, 64, 8, 169, 24576, 4154000, 6, 6, 7),
        _ => For(RingTerrainLodCoordinator.QualityPreset.Medium, hardwareTier)
        };
    }

    private static RingGrassQualityProfile Create(float nearDensity, float farDensity, float tileMeters,
        float radius, float nearDistance, float farDistance, float transition, int residentTiles,
        int perTile, int total, int builds, int uploads, float uploadMilliseconds) => new()
    {
        NearDensity = nearDensity, FarDensity = farDensity, TileMeters = tileMeters,
        VisibilityRadiusMeters = radius, NearDistanceMeters = nearDistance, FarDistanceMeters = farDistance,
        TransitionWidthMeters = transition, MaximumResidentTiles = residentTiles,
        MaximumInstancesPerTile = perTile, MaximumInstances = total, MaximumBuildsInFlight = builds,
        MaximumUploadsPerFrame = uploads, UploadBudgetMilliseconds = uploadMilliseconds
    };
}
