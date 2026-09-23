using Godot;

namespace RingworldTechDemo.Voxel.Phase3;

[GlobalClass]
public partial class VoxelTerrainSettings : Resource
{
    [Export] public int Seed { get; set; } = 1337;
    [ExportGroup("Designed Cave Generation")]
    // This resource contains cave contract settings only. Designed caves always
    // derive their deterministic key from Seed above; no separate cave seed is
    // accepted for a current world.
    [Export] public CaveGenerationSettings? DesignedCaveGenerationContractSettings { get; set; }
    // Opt-in until cave distribution, depth placement, exclusion audits, and
    // human traversal certification are complete.
    // Smooth density composes this layer before the ordered player edit journal.
    [Export] public bool EnableDesignedCaveDensityComposition { get; set; } = false;
    [ExportGroup("Biomes")]
    [Export] public TerrainBiomeCatalog? BiomeCatalog { get; set; }
    [ExportGroup("Extreme Scale")]
    // Mega Ring only. Certified and custom rings retain the original seamless
    // cylindrical noise domain and its existing cache identity.
    [Export] public bool UsePrecisionSafePeriodicNoiseTiles { get; set; }
    [ExportGroup("Base Terrain")]
    [Export(PropertyHint.Range, "1,8000,1")] public int BaseSurfaceHeightVoxels { get; set; } = 16;
    [Export(PropertyHint.Range, "0,8000,1")] public int HeightAmplitudeVoxels { get; set; } = 180;
    [Export(PropertyHint.Range, "0.0001,1.0,0.0001")] public float BaseFrequency { get; set; } = 0.0008f;
    [Export(PropertyHint.Range, "0,2000,1")] public int DetailAmplitudeVoxels { get; set; } = 30;
    [Export(PropertyHint.Range, "0.0001,1.0,0.0001")] public float DetailFrequency { get; set; } = 0.006f;
    [Export(PropertyHint.Range, "1,16,1")] public int SoilDepthVoxels { get; set; } = 3;
    [Export] public int BedrockY { get; set; } = 0;

    [ExportGroup("Natural 3D Density")]
    // This deliberately starts disabled. Enable it before creating a world,
    // together with ChunkGridManager.UseSmoothNaturalTerrainPilot, to route
    // natural terrain through the shared signed-density pipeline.
    [Export] public bool EnableUnified3dDensityTerrain { get; set; } = false;
    [Export] public bool EnableProceduralCaves { get; set; } = true;
    [Export(PropertyHint.Range, "0.001,0.25,0.001")] public float CaveFrequency { get; set; } = 0.022f;
    [Export(PropertyHint.Range, "0.05,0.95,0.01")] public float CaveChannelThreshold { get; set; } = 0.34f;
    [Export(PropertyHint.Range, "4,128,1")] public float CaveCarveStrengthMeters { get; set; } = 42.0f;
    [Export(PropertyHint.Range, "4,512,1")] public int CaveMinimumDepthMeters { get; set; } = 24;
    [Export(PropertyHint.Range, "32,4096,1")] public int CaveMaximumDepthMeters { get; set; } = 640;
    [Export] public bool EnableNaturalOverhangs { get; set; } = true;
    [Export(PropertyHint.Range, "0.001,0.25,0.001")] public float OverhangFrequency { get; set; } = 0.035f;
    [Export(PropertyHint.Range, "1,48,1")] public float OverhangCarveStrengthMeters { get; set; } = 12.0f;

    [ExportGroup("Regional Fields")]
    [Export(PropertyHint.Range, "16000,512000,1000")] public int MacroRegionSizeMeters { get; set; } = 128000;
    [Export(PropertyHint.Range, "16,2048,1")] public int MacroRegionCacheCapacity { get; set; } = 256;
    [Export(PropertyHint.Range, "0.000001,0.001,0.000001")] public float ContinentalFrequency { get; set; } = 0.000015f;
    [Export(PropertyHint.Range, "0.000001,0.001,0.000001")] public float ErosionFrequency { get; set; } = 0.00005f;
    [Export(PropertyHint.Range, "0.000001,0.001,0.000001")] public float RidgeFrequency { get; set; } = 0.000035f;
    [Export(PropertyHint.Range, "0.000001,0.001,0.000001")] public float TemperatureFrequency { get; set; } = 0.00001f;
    [Export(PropertyHint.Range, "0.000001,0.001,0.000001")] public float MoistureFrequency { get; set; } = 0.00002f;
    [Export(PropertyHint.Range, "0.0,0.75,0.01")] public float ContinentalSecondaryStrength { get; set; } = 0.28f;
    [Export(PropertyHint.Range, "-0.50,0.50,0.01")] public float ContinentalOceanBias { get; set; } = 0.0f;
    [Export(PropertyHint.Range, "0.0,1.0,0.01")] public float ClimateLatitudeInfluence { get; set; } = 0.55f;
    [Export(PropertyHint.Range, "0.0,1.0,0.01")] public float OceanMoistureInfluence { get; set; } = 0.25f;
    [Export(PropertyHint.Range, "0.0,0.35,0.005")] public float AltitudeTemperatureDropPerKilometre { get; set; } = 0.075f;
    [Export(PropertyHint.Range, "0.0,0.60,0.01")] public float MountainRainShadowStrength { get; set; } = 0.18f;

    [ExportGroup("Continental Elevation")]
    [Export(PropertyHint.Range, "-1000,1000,1")] public int SeaLevelY { get; set; } = 0;
    [Export(PropertyHint.Range, "-2000,-1,1")] public int DeepOceanFloorY { get; set; } = -1800;
    [Export(PropertyHint.Range, "-2000,0,1")] public int OceanShelfFloorY { get; set; } = -220;
    [Export(PropertyHint.Range, "-2000,32,1")] public int CoastLowY { get; set; } = -20;
    [Export(PropertyHint.Range, "-16,64,1")] public int CoastHighY { get; set; } = 20;
    [Export(PropertyHint.Range, "0.0,1.0,0.01")] public float CoastalBeachStrength { get; set; } = 0.72f;
    [Export(PropertyHint.Range, "0,8000,1")] public int LowlandHighY { get; set; } = 350;
    [Export(PropertyHint.Range, "64,8000,1")] public int HighlandHighY { get; set; } = 1500;
    [Export(PropertyHint.Range, "128,8000,1")] public int MountainFoundationHighY { get; set; } = 2800;
    [Export(PropertyHint.Range, "1.0,2.0,0.01")] public float ContinentalContrast { get; set; } = 1.35f;
    [Export(PropertyHint.Range, "0.0,1.0,0.01")] public float RegionalDetailStrength { get; set; } = 0.65f;

    [ExportGroup("Engineered Macro Terrain")]
    [Export] public bool EnableEngineeredMacroTerrain { get; set; }
    [Export(PropertyHint.Range, "1000,200000000,1000")] public float InlandSeaHalfWidthMeters { get; set; } = 16000000.0f;
    [Export(PropertyHint.Range, "1000,100000000,1000")] public float InlandSeaShoreTransitionMeters { get; set; } = 8000000.0f;
    [Export(PropertyHint.Range, "1000,200000000,1000")] public float CoastalTerraceDistanceMeters { get; set; } = 40000000.0f;
    [Export(PropertyHint.Range, "1000,100000000,1000")] public float CoastalTerraceHalfWidthMeters { get; set; } = 15000000.0f;
    [Export(PropertyHint.Range, "0,4000,1")] public int CoastalTerraceHeightMeters { get; set; } = 650;
    [Export(PropertyHint.Range, "0,4000,1")] public int ArcEscarpmentHeightMeters { get; set; } = 350;
    [Export(PropertyHint.Range, "1000000,4000000000,1000000")] public float ArcEscarpmentWavelengthMeters { get; set; } = 600000000.0f;

    [ExportGroup("Mountain Belts")]
    [Export(PropertyHint.Range, "512,8000,1")] public int RarePeakY { get; set; } = 5200;
    [Export] public bool AutoRaiseRarePeakToBuildLimit { get; set; } = false;
    [Export(PropertyHint.Range, "0.0,1.0,0.01")] public float MountainMaskStart { get; set; } = 0.30f;
    [Export(PropertyHint.Range, "0.0,1.0,0.01")] public float MountainMaskFull { get; set; } = 0.72f;
    [Export(PropertyHint.Range, "0.000001,0.001,0.000001")] public float MountainWarpFrequency { get; set; } = 0.000018f;
    [Export(PropertyHint.Range, "0,20000,100")] public float MountainWarpDistanceMeters { get; set; } = 6000.0f;
    [Export(PropertyHint.Range, "1.0,24.0,0.1")] public float PeakRarityExponent { get; set; } = 16.0f;
    [Export(PropertyHint.Range, "0.0,1.0,0.01")] public float MountainBeltBroadness { get; set; } = 0.38f;
    [Export(PropertyHint.Range, "0.0,1.0,0.01")] public float ErodedMountainStrength { get; set; } = 0.35f;
    [Export(PropertyHint.Range, "0.000001,0.001,0.000001")] public float ValleyFrequency { get; set; } = 0.00007f;
    [Export(PropertyHint.Range, "0,600,1")] public float ValleyDepthVoxels { get; set; } = 220.0f;
    [Export(PropertyHint.Range, "1.0,12.0,0.1")] public float ValleyNarrowness { get; set; } = 6.0f;
    [Export(PropertyHint.Range, "0.000001,0.001,0.000001")] public float EscarpmentFrequency { get; set; } = 0.000025f;
    [Export(PropertyHint.Range, "0,1200,1")] public float EscarpmentHeightMeters { get; set; } = 260.0f;
    [Export(PropertyHint.Range, "0.0,1.0,0.01")] public float EscarpmentSharpness { get; set; } = 0.72f;

    [ExportGroup("Exceptional Summit")]
    [Export(PropertyHint.Range, "0,1,1")] public int ExceptionalSummitCount { get; set; } = 1;
    [Export(PropertyHint.Range, "512,8000,1")] public int ExceptionalSummitY { get; set; } = 7600;
    [Export(PropertyHint.Range, "1000,200000,1000")] public float ExceptionalSummitRadiusMeters { get; set; } = 40000.0f;
    [Export(PropertyHint.Range, "1.0,8.0,0.1")] public float ExceptionalSummitFalloffExponent { get; set; } = 3.0f;
    [Export(PropertyHint.Range, "8,256,1")] public int ExceptionalSummitCandidateCount { get; set; } = 64;
    [Export(PropertyHint.Range, "0,2000,1")] public int TerrainToBuildClearanceMeters { get; set; } = 400;

    [ExportGroup("Hydrology")]
    [Export] public bool EnableLakes { get; set; } = true;
    [Export(PropertyHint.Range, "8000,128000,1000")] public int LakeCellSizeMeters { get; set; } = 48000;
    [Export(PropertyHint.Range, "0.0,1.0,0.01")] public float LakeSpawnChance { get; set; } = 0.28f;
    [Export(PropertyHint.Range, "500,20000,100")] public float LakeMinimumRadiusMeters { get; set; } = 2500.0f;
    [Export(PropertyHint.Range, "1000,40000,100")] public float LakeMaximumRadiusMeters { get; set; } = 9000.0f;
    [Export(PropertyHint.Range, "1,100,1")] public int LakeMinimumDepthMeters { get; set; } = 8;
    [Export(PropertyHint.Range, "5,500,1")] public int LakeMaximumDepthMeters { get; set; } = 90;
    [Export(PropertyHint.Range, "0,4000,10")] public int LakeMaximumSurfaceY { get; set; } = 1800;
    [Export(PropertyHint.Range, "1,100,1")] public int LakeLevelQuantumMeters { get; set; } = 10;

    [Export] public bool EnableRivers { get; set; } = true;
    [Export(PropertyHint.Range, "20,1000,5")] public float WatershedRiverHalfWidthMeters { get; set; } = 90.0f;
    [Export(PropertyHint.Range, "0.0,1.0,0.01")] public float WatershedSourceChance { get; set; } = 0.58f;
    [Export(PropertyHint.Range, "0.000001,0.001,0.000001")] public float RiverFrequency { get; set; } = 0.000045f;
    [Export(PropertyHint.Range, "0.005,0.25,0.005")] public float RiverChannelThreshold { get; set; } = 0.055f;
    [Export(PropertyHint.Range, "0.0,1.0,0.01")] public float RiverValleyInfluence { get; set; } = 0.55f;
    [Export(PropertyHint.Range, "1.0,8.0,0.1")] public float RiverBankSharpness { get; set; } = 2.4f;
    [Export(PropertyHint.Range, "1,50,1")] public int RiverMinimumDepthMeters { get; set; } = 3;
    [Export(PropertyHint.Range, "2,100,1")] public int RiverMaximumDepthMeters { get; set; } = 18;
    [Export(PropertyHint.Range, "0.0,1.0,0.01")] public float RiverMinimumMoisture { get; set; } = 0.38f;
    [Export(PropertyHint.Range, "0,5000,10")] public int RiverMaximumSurfaceY { get; set; } = 2200;

}
