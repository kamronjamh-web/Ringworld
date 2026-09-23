using System;
using System.IO;
using Godot;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Biomes;

/// <summary>
/// A saved, isolated biome recipe for the authoring preview. It deliberately
/// does not assign a TerrainBiomeId to a world or alter terrain persistence.
/// </summary>
[Tool]
[GlobalClass]
public partial class BiomeAuthoringPreset : Resource
{
    public const int CurrentVersion = 1;
    [Export] public int Version { get; set; } = CurrentVersion;
    [Export] public string Identity { get; set; } = string.Empty;
    [Export] public string DisplayName { get; set; } = "New biome";
    [Export] public int Seed { get; set; } = 1337;
    [Export] public TerrainBiomeId BaseBiome { get; set; } = TerrainBiomeId.Grassland;

    [ExportGroup("Terrain")]
    [Export(PropertyHint.Range, "-1000,8000,1")] public float MeanElevationMeters { get; set; } = 120;
    [Export(PropertyHint.Range, "0,1000,1")] public float ElevationVariationMeters { get; set; } = 80;
    [Export(PropertyHint.Range, "100,10000,10")] public float BroadFeatureScaleMeters { get; set; } = 2400;
    [Export(PropertyHint.Range, "0,500,1")] public float DetailAmplitudeMeters { get; set; } = 18;
    [Export(PropertyHint.Range, "20,2000,1")] public float DetailFeatureScaleMeters { get; set; } = 260;
    [Export(PropertyHint.Range, "0,1,0.01")] public float Humidity { get; set; } = .58f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float Temperature { get; set; } = .55f;

    [ExportGroup("Trees")]
    [Export(PropertyHint.Range, "0,1,0.01")] public float TreeDensity { get; set; } = .45f;
    [Export(PropertyHint.Range, "2,64,0.5")] public float MinimumTreeSpacingMeters { get; set; } = 7;
    [Export(PropertyHint.Range, "0,1,0.01")] public float TreeClustering { get; set; } = .45f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float ClearingStrength { get; set; } = .18f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float MapleWeight { get; set; } = .70f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float FirWeight { get; set; } = .15f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float PineWeight { get; set; } = .15f;
    [Export(PropertyHint.Range, "1,80,0.1")] public float MinimumTreeHeightMeters { get; set; } = 5;
    [Export(PropertyHint.Range, "1,80,0.1")] public float MaximumTreeHeightMeters { get; set; } = 18;
    [Export(PropertyHint.Range, "0,60,1")] public float MaximumTreeSlopeDegrees { get; set; } = 24;

    [ExportGroup("Grass")]
    [Export(PropertyHint.Range, "0,2,0.01")] public float GrassDensity { get; set; } = 1;
    [Export(PropertyHint.Range, "0.1,4,0.01")] public float GrassHeightMeters { get; set; } = 1;
    [Export] public Color GrassTint { get; set; } = new(.22f, .42f, .16f, 1);

    [ExportGroup("Ground")]
    [Export] public BiomeGroundMaterial GroundMaterial { get; set; } = BiomeGroundMaterial.GrassSoil;
    [Export] public Color GroundTint { get; set; } = Colors.White;
    [Export(PropertyHint.Range, "0.1,32,0.1")] public float GroundTextureScale { get; set; } = 4;

    public string EnsureIdentity()
    {
        if (!Guid.TryParse(Identity, out _)) Identity = Guid.NewGuid().ToString("D");
        return Identity;
    }

    public bool TryValidate(out string error)
    {
        if (Version != CurrentVersion)
        { error = $"Unsupported preset version {Version}."; return false; }
        if (!Guid.TryParse(Identity, out _))
        { error = "Preset identity is missing or invalid."; return false; }
        if (string.IsNullOrWhiteSpace(DisplayName))
        { error = "Preset display name is required."; return false; }
        if (!Enum.IsDefined(BaseBiome) || BaseBiome is TerrainBiomeId.Ocean or TerrainBiomeId.Lake or TerrainBiomeId.River)
        { error = "Base biome must be a dry existing TerrainBiomeId."; return false; }
        if (MaximumTreeHeightMeters < MinimumTreeHeightMeters)
        { error = "Maximum tree height must be at least the minimum height."; return false; }
        if (MapleWeight + FirWeight + PineWeight <= 0.0001f && TreeDensity > 0)
        { error = "At least one tree species weight is required when trees are enabled."; return false; }
        if (!Enum.IsDefined(GroundMaterial)) { error = "Ground material is invalid."; return false; }
        if (!FiniteColor(GrassTint) || !FiniteColor(GroundTint)) { error = "Preset contains a non-finite color."; return false; }
        if (!Finite(MeanElevationMeters, -1000, 8000) || !Finite(ElevationVariationMeters, 0, 1000)
            || !Finite(BroadFeatureScaleMeters, 100, 10000) || !Finite(DetailAmplitudeMeters, 0, 500)
            || !Finite(DetailFeatureScaleMeters, 20, 2000) || !Finite(Humidity, 0, 1) || !Finite(Temperature, 0, 1)
            || !Finite(TreeDensity, 0, 1) || !Finite(MinimumTreeSpacingMeters, 2, 64) || !Finite(TreeClustering, 0, 1)
            || !Finite(ClearingStrength, 0, 1) || !Finite(MapleWeight, 0, 1) || !Finite(FirWeight, 0, 1)
            || !Finite(PineWeight, 0, 1) || !Finite(MinimumTreeHeightMeters, 1, 80)
            || !Finite(MaximumTreeHeightMeters, 1, 80) || !Finite(MaximumTreeSlopeDegrees, 0, 60)
            || !Finite(GrassDensity, 0, 2) || !Finite(GrassHeightMeters, .1f, 4) || !Finite(GroundTextureScale, .1f, 32))
        { error = "Preset contains a non-finite or out-of-range value."; return false; }
        error = string.Empty;
        return true;
    }

    static bool Finite(float value, float minimum, float maximum) => float.IsFinite(value) && value >= minimum && value <= maximum;
    static bool FiniteColor(Color value) => float.IsFinite(value.R) && float.IsFinite(value.G) && float.IsFinite(value.B) && float.IsFinite(value.A);

    /// <summary>Creates a new authoring resource with an identity. Loading never invents one.</summary>
    public static BiomeAuthoringPreset CreateNew(string displayName = "New biome")
    {
        var preset = new BiomeAuthoringPreset { DisplayName = displayName, Identity = Guid.NewGuid().ToString("D") };
        preset.Normalize();
        return preset;
    }

    public void Normalize()
    {
        Version = CurrentVersion;
        EnsureIdentity();
        DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? "New biome" : DisplayName.Trim();
        BroadFeatureScaleMeters = Mathf.Clamp(BroadFeatureScaleMeters, 100, 10000);
        DetailFeatureScaleMeters = Mathf.Clamp(DetailFeatureScaleMeters, 20, 2000);
        ElevationVariationMeters = Mathf.Clamp(ElevationVariationMeters, 0, 1000);
        DetailAmplitudeMeters = Mathf.Clamp(DetailAmplitudeMeters, 0, 500);
        Humidity = Mathf.Clamp(Humidity, 0, 1); Temperature = Mathf.Clamp(Temperature, 0, 1);
        TreeDensity = Mathf.Clamp(TreeDensity, 0, 1); MinimumTreeSpacingMeters = Mathf.Clamp(MinimumTreeSpacingMeters, 2, 64);
        TreeClustering = Mathf.Clamp(TreeClustering, 0, 1); ClearingStrength = Mathf.Clamp(ClearingStrength, 0, 1);
        MapleWeight = Mathf.Max(0, MapleWeight); FirWeight = Mathf.Max(0, FirWeight); PineWeight = Mathf.Max(0, PineWeight);
        MinimumTreeHeightMeters = Mathf.Clamp(MinimumTreeHeightMeters, 1, 80);
        MaximumTreeHeightMeters = Mathf.Clamp(MaximumTreeHeightMeters, MinimumTreeHeightMeters, 80);
        MaximumTreeSlopeDegrees = Mathf.Clamp(MaximumTreeSlopeDegrees, 0, 60);
        GrassDensity = Mathf.Clamp(GrassDensity, 0, 2); GrassHeightMeters = Mathf.Clamp(GrassHeightMeters, .1f, 4);
        GroundTextureScale = Mathf.Clamp(GroundTextureScale, .1f, 32);
    }

    public BiomeAuthoringPreset CreateDuplicateWithNewIdentity()
    {
        var duplicate = (BiomeAuthoringPreset)Duplicate(true);
        duplicate.Identity = Guid.NewGuid().ToString("D");
        duplicate.DisplayName = DisplayName + " copy";
        duplicate.Normalize();
        return duplicate;
    }

    public BiomeAuthoringSnapshot CreateSnapshot()
    {
        if (!TryValidate(out string error)) throw new InvalidOperationException(error);
        return new BiomeAuthoringSnapshot(Identity, DisplayName, Seed, BaseBiome, MeanElevationMeters, ElevationVariationMeters,
            BroadFeatureScaleMeters, DetailAmplitudeMeters, DetailFeatureScaleMeters, Humidity, Temperature, TreeDensity,
            MinimumTreeSpacingMeters, TreeClustering, ClearingStrength, MapleWeight, FirWeight, PineWeight,
            MinimumTreeHeightMeters, MaximumTreeHeightMeters, MaximumTreeSlopeDegrees, GrassDensity, GrassHeightMeters,
            GrassTint, GroundMaterial, GroundTint, GroundTextureScale);
    }

    /// <summary>Writes a fully serialized, uniquely named temporary resource, then atomically replaces the target.</summary>
    public Error SaveAtomic(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".tres", StringComparison.OrdinalIgnoreCase)) return Error.InvalidParameter;
        if (!TryValidate(out _)) return Error.InvalidData;
        string temporaryPath = string.Empty;
        try
        {
            string absolute = ProjectSettings.GlobalizePath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp.tres";
            Error saved = ResourceSaver.Save(this, temporaryPath);
            if (saved != Error.Ok) return saved;
            File.Move(ProjectSettings.GlobalizePath(temporaryPath), absolute, true);
            temporaryPath = string.Empty;
            return Error.Ok;
        }
        catch { return Error.CantCreate; }
        finally { if (temporaryPath.Length > 0) try { File.Delete(ProjectSettings.GlobalizePath(temporaryPath)); } catch { } }
    }

    public static bool TryLoad(string path, out BiomeAuthoringPreset? preset, out string error)
    {
        preset = null;
        BiomeAuthoringPreset? loaded = ResourceLoader.Load<BiomeAuthoringPreset>(path, cacheMode: ResourceLoader.CacheMode.Ignore);
        if (loaded == null) { error = "Preset could not be loaded."; return false; }
        if (!loaded.TryValidate(out error)) return false;
        preset = loaded.Duplicate(true) as BiomeAuthoringPreset;
        if (preset == null) { error = "Preset could not be cloned."; return false; }
        return true;
    }

    // GDScript-safe entry points for the dock. The C# methods above keep detailed errors for tests.
    public bool ValidateForUi() => TryValidate(out _);
    public string GetValidationError() => TryValidate(out string error) ? string.Empty : error;
    public string InitializeNewIdentity() => EnsureIdentity();
    public BiomeAuthoringPreset DuplicateWithNewIdentity() => CreateDuplicateWithNewIdentity();
    public int SavePreset(string path) => (int)SaveAtomic(path);
}

public enum BiomeGroundMaterial { GrassSoil, ForestFloor, DesertSand, TundraGround, AlpineRock }

public readonly record struct BiomeAuthoringSnapshot(
    string Identity, string DisplayName, int Seed, TerrainBiomeId BaseBiome, float MeanElevationMeters,
    float ElevationVariationMeters, float BroadFeatureScaleMeters, float DetailAmplitudeMeters, float DetailFeatureScaleMeters,
    float Humidity, float Temperature, float TreeDensity, float MinimumTreeSpacingMeters, float TreeClustering,
    float ClearingStrength, float MapleWeight, float FirWeight, float PineWeight, float MinimumTreeHeightMeters,
    float MaximumTreeHeightMeters, float MaximumTreeSlopeDegrees, float GrassDensity, float GrassHeightMeters,
    Color GrassTint, BiomeGroundMaterial GroundMaterial, Color GroundTint, float GroundTextureScale)
{
    public string GroundTexturePath => GroundMaterial switch
    {
        BiomeGroundMaterial.ForestFloor => "res://materials/biomes/forest_floor_albedo.png",
        BiomeGroundMaterial.DesertSand => "res://materials/biomes/desert_sand_albedo.png",
        BiomeGroundMaterial.TundraGround => "res://materials/biomes/tundra_ground_albedo.png",
        BiomeGroundMaterial.AlpineRock => "res://materials/biomes/alpine_rock_albedo.png",
        _ => "res://materials/third_party/voxelgame/grass_albedo.png"
    };
}
