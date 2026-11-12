using System;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Terrain.Hydrology;

/// <summary>Which height source a bake may use. <see cref="Auto"/> takes the GPU when the inputs say a device exists and falls back to the CPU on any failure.</summary>
public enum HydrologySourcePreference { Auto, Gpu, Cpu }

/// <summary>
/// Everything a river bake reads, captured on the main thread (S12): the recipe, the climate inputs (GPU settings and a CPU reader factory built from a settings duplicate), the folder of the graph file, the worker
/// count and whether a GPU device was available. Immutable; the worker never touches a Node or the live settings.
/// </summary>
public sealed class HydrologyBakeInputs
{
    private ulong _identity;

    public TerrainGenerationParameters Parameters { get; }
    public DrainageClimateInputs Climate { get; }
    /// <summary>Absolute folder of the graph file; empty means no file is read or written.</summary>
    public string Directory { get; }
    /// <summary>True when the file lives in a world's own folder (one file name per format); false adds the identity to the name (the shared cache folder).</summary>
    public bool WorldFolder { get; }
    public int Workers { get; }
    public bool GpuAvailable { get; }
    public HydrologySourcePreference Source { get; }

    public HydrologyBakeInputs(TerrainGenerationParameters parameters, DrainageClimateInputs climate, string directory, bool worldFolder, int workers, bool gpuAvailable,
        HydrologySourcePreference source = HydrologySourcePreference.Auto)
    {
        Parameters = parameters; Climate = climate; Directory = directory; WorldFolder = worldFolder; Workers = Math.Max(1, workers); GpuAvailable = gpuAvailable; Source = source;
    }

    /// <summary>The pre-graph identity (S12); builds the site registries on first use, so call it off the main thread.</summary>
    public ulong Identity
    {
        get
        {
            if (_identity == 0) _identity = HydrologyBakeIdentity.Compute(Parameters);
            return _identity;
        }
    }

    public string FilePath(ulong identity)
        => System.IO.Path.Combine(Directory, WorldFolder ? $"river_graph_v{HydrologyVersions.GraphFormat}.bin" : $"river_graph_v{HydrologyVersions.GraphFormat}_{identity:X16}.bin");

    /// <summary>The default worker count of the game: all but two logical processors, at least one.</summary>
    public static int DefaultWorkers => Math.Max(1, System.Environment.ProcessorCount - 2);

    /// <summary>The inputs of the world whose terrain settings are given, or null when it has no <c>water.rivers</c> recipe (such a world bakes nothing, writes nothing and waits for nothing).</summary>
    public static HydrologyBakeInputs? CaptureForWorld(VoxelTerrainSettings? settings)
    {
        if (settings == null || string.IsNullOrWhiteSpace(settings.SurfaceGenerationRecipeJson)) return null;
        TerrainGenerationParameters recipe = TerrainGenerationParameters.FromJson(settings.SurfaceGenerationRecipeJson);
        return recipe.HasRivers ? Capture(settings, recipe, DefaultWorkers) : null;
    }

    /// <summary>
    /// Captures the inputs for a recipe on the main thread. <paramref name="live"/> is only read (and duplicated); its recipe climate is applied to the duplicate the way the runtime sampler does.
    /// The folder is <c>&lt;world&gt;/hydrology</c> for <c>ringworld/runtime/active_world_directory</c>, else <c>user://ring_cache/hydrology</c>.
    /// </summary>
    public static HydrologyBakeInputs Capture(VoxelTerrainSettings live, TerrainGenerationParameters recipe, int workers, HydrologySourcePreference source = HydrologySourcePreference.Auto, string? directoryOverride = null)
    {
        VoxelTerrainSettings effective = live.Duplicate(true) as VoxelTerrainSettings ?? throw new InvalidOperationException("Terrain settings could not be duplicated.");
        if (recipe.IsWorldGenerationRecipe) RingTerrainSampler.ApplyRecipeClimate(effective, recipe);
        DrainageClimateInputs climate = DrainageClimateInputs.FromSettings(effective, (long)TerrainGenerationParameters.CertifiedCircumferenceMeters, (int)TerrainGenerationParameters.CertifiedWidthMeters);
        string directory, world = ProjectSettings.GetSetting("ringworld/runtime/active_world_directory", "").AsString();
        bool worldFolder = directoryOverride == null && !string.IsNullOrWhiteSpace(world);
        if (directoryOverride != null) directory = directoryOverride;
        else directory = System.IO.Path.Combine(ProjectSettings.GlobalizePath(worldFolder ? world : "user://ring_cache"), "hydrology");
        bool gpu = DisplayServer.GetName() != "headless" && RenderingServer.GetRenderingDevice() != null;
        return new HydrologyBakeInputs(recipe, climate, directory, worldFolder, workers, gpu, source);
    }
}
