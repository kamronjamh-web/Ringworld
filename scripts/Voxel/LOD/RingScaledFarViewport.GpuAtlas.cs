using System;
using System.Diagnostics;
using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.LOD;

// GPU offload GPU-4: the whole-ring far weather atlases and the fine layer atlas are built by compute (RingFarWeatherAtlasGpu, RingFarWeatherAtlas.glsl)
// instead of the CPU loops in UpdateFarWeatherAtlas / StepFarFineLayerAtlasBuild. The CPU build stays as the fallback (no RenderingDevice, manual / preview
// weather, a triggered megastorm, FarAtlasForceCpu) and as the parity reference (BuildFarWeatherAtlasCpuReference, tools/FarWeatherAtlasGpuParityTest.cs).
// The GPU copy is visual only: lightning, audio and wetness keep querying the CPU weather.
public partial class RingScaledFarViewport
{
    /// <summary>Forces the CPU atlas builds (parity reference, A/B captures); environment RING_FAR_ATLAS_CPU=1 sets it at startup. Not a menu setting.</summary>
    public static bool FarAtlasForceCpu { get; set; } = OS.GetEnvironment("RING_FAR_ATLAS_CPU") == "1";

    private RingFarWeatherAtlasGpu? _farAtlasGpu;
    private int _farWeatherGpuJob, _farWeatherGpuBuffer, _farWeatherGpuPublished = 1;
    private double _farWeatherGpuSnapshot;
    private int _farFineGpuJob, _farFineGpuBuffer, _farFineGpuPublished = 1;
    private readonly byte[] _farAtlasLayerBlock = new byte[RingCloudLayers.BlockBytes];

    /// <summary>True when the last published whole-ring atlas came from the GPU build.</summary>
    public bool FarWeatherAtlasUsesGpu { get; private set; }
    /// <summary>True when the last published fine layer atlas came from the GPU build.</summary>
    public bool FarFineAtlasUsesGpu { get; private set; }
    /// <summary>Status of the GPU atlas builder (diagnostics).</summary>
    public string FarAtlasGpuStatus => _farAtlasGpu == null ? (FarAtlasForceCpu ? "forced CPU" : "not created") : _farAtlasGpu.Status;

    private const int FarAtlasSlots = 32;

    private bool FarAtlasGpuEligible()
    {
        if (FarAtlasForceCpu || _weatherSource == null || _grid?.TerrainSampler == null) return false;
        if (_farAtlasGpu is { Failed: true }) return false;
        if (_weatherSource.MegastormSource != null || _weatherSource.ManualWeatherCount != 0) return false;
        if (!RingFarWeatherAtlasGpu.DeviceAvailable()) return false;
        GetAtlasCellGeometry(out _, out _, out int aroundCells, out int searchRadius);
        return aroundCells >= searchRadius * 2 + 1;
    }

    private void GetAtlasCellGeometry(out float macroCell, out float maximumRadius, out int aroundCells, out int searchRadius)
    {
        macroCell = Mathf.Clamp(_weatherSource!.MacroWeatherCellMeters, 16000.0f, 64000.0f);
        maximumRadius = Mathf.Clamp(_weatherSource.MaximumMacroSystemRadiusMeters, 40000.0f, 160000.0f);
        aroundCells = Math.Max(1, Mathf.CeilToInt((float)(Math.Max(1.0, _circumferenceMeters) / macroCell)));
        searchRadius = Mathf.CeilToInt((float)(maximumRadius / macroCell)) + 1;
    }

    /// <summary>The 36 floats of RingFarWeatherAtlas.glsl FarAtlasParams for a snapshot time (and, for the fine atlas, its window).</summary>
    private float[] PackFarAtlasParams(double time, double fineOrigin, float fineTexel)
    {
        RingVolumetricCloudLayer weather = _weatherSource!;
        time = double.IsFinite(time) ? Math.Max(0.0, time) : 0.0;
        double circumference = Math.Max(1.0, _circumferenceMeters);
        Vector2 wind = weather.WeatherWindMetersPerSecond;
        GetAtlasCellGeometry(out float macroCell, out float maximumRadius, out int aroundCells, out int searchRadius);
        double cycle = Math.Max(1.0, weather.WeatherBirthSeconds + weather.WeatherDevelopSeconds + weather.WeatherMatureSeconds
            + weather.WeatherDissipateSeconds + weather.WeatherQuietSeconds);
        double wholeCycles = Math.Floor(time / cycle);
        double remainder = time - wholeCycles * cycle;
        double shiftAround = -wind.X * time;
        shiftAround -= circumference * Math.Floor(shiftAround / circumference);
        int seed = _grid!.TerrainSampler!.Seed;
        float seedBits = BitConverter.Int32BitsToSingle(seed);
        float layerBase = _farWsHeaderRead ? _farWsHeader.X : 0.5f;
        int smallCells = Math.Max(2, weather.SmallWeatherClusterCells), largeCells = Math.Max(4, weather.LargeWeatherClusterCells);
        double acrossTexel = RingWidthMeters / (double)FarFineAtlasHeight;
        var p = new float[RingFarWeatherAtlasGpu.ParamFloats];
        void Set(int vec, float x, float y, float z, float w) { p[vec * 4] = x; p[vec * 4 + 1] = y; p[vec * 4 + 2] = z; p[vec * 4 + 3] = w; }
        Set(0, (float)circumference, _grid.RingAcrossWidthMeters, seedBits, weather.NaturalWeatherLifecyclesEnabled ? 1.0f : 0.0f);
        Set(1, macroCell, maximumRadius, weather.MacroSystemSpawnChance, (float)(wind.Y * time));
        Set(2, weather.WeatherBirthSeconds, weather.WeatherDevelopSeconds, weather.WeatherMatureSeconds, weather.WeatherDissipateSeconds);
        Set(3, weather.WeatherQuietSeconds, weather.WeatherLifecycleVariation, (float)cycle, (float)remainder);
        Set(4, (float)shiftAround, (float)(-wind.Y * time), MathF.Atan2(wind.Y, wind.X), 0.0f);
        Set(5, weather.Coverage, layerBase, 0.0f, 0.0f);
        Set(6, (float)fineOrigin, fineTexel, (float)acrossTexel, RingWidthMeters);
        Set(7, Mathf.Max(1000.0f, weather.FormationCellMeters), weather.WeatherClusteringStrength, smallCells + 256 * largeCells, seedBits);
        p[32] = BitConverter.Int32BitsToSingle(unchecked((int)Math.Min(wholeCycles, int.MaxValue)));
        p[33] = BitConverter.Int32BitsToSingle(aroundCells);
        p[34] = BitConverter.Int32BitsToSingle(searchRadius);
        p[35] = 0.0f;
        return p;
    }

    private void BuildFarAtlasLayerBlock()
    {
        Vector3 sunLocal = _farLayerBlockValid
            ? new Vector3(_farLayerBlockVectors[4].X, _farLayerBlockVectors[4].Y, _farLayerBlockVectors[4].Z)
            : Vector3.Zero;
        RingCloudLayers.BuildBlock(CloudLayerTable.Shared, sunLocal, _farAtlasLayerBlock);
    }

    private RingFarWeatherAtlasGpu? EnsureFarAtlasGpu()
    {
        _farAtlasGpu ??= new RingFarWeatherAtlasGpu();
        return _farAtlasGpu.Failed ? null : _farAtlasGpu;
    }

    private static ulong HashAtlasInputs(double time, float[] parameters)
    {
        ulong hash = 1469598103934665603UL;
        hash = (hash ^ (ulong)BitConverter.DoubleToInt64Bits(time)) * 1099511628211UL;
        foreach (float value in parameters) hash = (hash ^ (uint)BitConverter.SingleToInt32Bits(value)) * 1099511628211UL;
        return hash;
    }

    // ---- Whole-ring atlas ----

    private bool TryBeginGpuFarWeatherAtlas(double snapshotTime)
    {
        if (!FarAtlasGpuEligible()) return false;
        RingFarWeatherAtlasGpu? gpu = EnsureFarAtlasGpu();
        if (gpu == null) return false;
        BuildFarAtlasLayerBlock();
        int width = Mathf.Clamp(FarWeatherAtlasWidth, 512, 8192);
        int height = Mathf.Clamp(FarWeatherAtlasHeight, 32, 512);
        int seed = _grid!.TerrainSampler!.Seed;
        _farWeatherGpuBuffer = 1 - _farWeatherGpuPublished;
        _farWeatherGpuSnapshot = snapshotTime;
        _farWeatherGpuJob = gpu.NextJobId();
        gpu.QueueWeather(_farWeatherGpuJob, _farWeatherGpuBuffer, width, height, PackFarAtlasParams(snapshotTime, 0.0, 0.0f), _farAtlasLayerBlock,
            unchecked((int)((uint)seed ^ 0x9e3779b9u)));
        return true;
    }

    /// <summary>Publishes the GPU build when it has completed; with <paramref name="wait"/> it blocks (bounded) and falls back to the CPU build on a timeout or failure.</summary>
    private void StepGpuFarWeatherAtlas(bool wait)
    {
        RingFarWeatherAtlasGpu gpu = _farAtlasGpu!;
        Stopwatch stopwatch = Stopwatch.StartNew();
        if (!gpu.WeatherJobDone(_farWeatherGpuJob))
        {
            if (!wait) return;
            while (!gpu.WeatherJobDone(_farWeatherGpuJob) && stopwatch.ElapsedMilliseconds < 3000) OS.DelayMsec(1);
        }
        if (gpu.Failed || !gpu.WeatherJobDone(_farWeatherGpuJob))
        {
            // Hand this and every later build to the CPU path.
            if (!gpu.Failed) GD.PushWarning("Far weather atlas GPU build timed out; using the CPU build.");
            FarAtlasForceCpu = FarAtlasForceCpu || !gpu.Failed;
            _farWeatherGpuJob = 0;
            BeginFarWeatherAtlasRebuild(_farWeatherGpuSnapshot);
            return;
        }
        int buffer = _farWeatherGpuBuffer;
        _farWeatherGpuJob = 0;
        _farWeatherGpuPublished = buffer;
        _farWeatherAtlas = gpu.WeatherTexture(buffer);
        _farLayerAtlas = gpu.LayerTexture(buffer);
        _farWeatherAtlasSnapshotTime = _farWeatherGpuSnapshot;
        _farWeatherAtlasNextRefreshTime = _farWeatherGpuSnapshot + Mathf.Max(1.0f, FarWeatherAtlasRefreshSeconds);
        FarWeatherAtlasUsesGpu = true;
        FarWeatherAtlasLastWorkMilliseconds = 0.0;
        FarWeatherAtlasLastWorkItems = 0;
        FarWeatherAtlasLastUploadMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        FarWeatherAtlasPublicationCount++;
        // The GPU build has no CPU-side texel data to hash: the hash covers the build inputs (snapshot time, seed, wind, lifecycle settings).
        FarWeatherAtlasContentHash = HashAtlasInputs(_farWeatherGpuSnapshot, PackFarAtlasParams(_farWeatherGpuSnapshot, 0.0, 0.0f));
        _farLayerAtlasPublished = true;
        ApplyFarLayerAtlasToMaterials();
        PublishFarWeatherMaterials();
        RequestFarWeatherCloudBake();
    }

    // ---- Fine layer atlas ----

    /// <summary>Returns true when the GPU build owns this fine atlas build (queued, in flight or just published).</summary>
    private bool StepFarFineGpu(bool forceComplete)
    {
        if (_farFineGpuJob == 0)
        {
            // A CPU build already under way finishes on the CPU.
            if (_farFineIndex > 0 || DebugFarFineExact || DebugFarFineVerify || !FarAtlasGpuEligible()) return false;
            RingFarWeatherAtlasGpu? started = EnsureFarAtlasGpu();
            if (started == null) return false;
            BuildFarAtlasLayerBlock();
            _farFineGpuBuffer = 1 - _farFineGpuPublished;
            _farFineGpuJob = started.NextJobId();
            started.QueueFine(_farFineGpuJob, _farFineGpuBuffer,
                PackFarAtlasParams(_farFineBuildSnapshotTime, _farFineBuildOrigin, _farFineBuildTexel), _farAtlasLayerBlock);
            _farFineBuildFrames = 0;
        }
        RingFarWeatherAtlasGpu gpu = _farAtlasGpu!;
        _farFineBuildFrames++;
        Stopwatch stopwatch = Stopwatch.StartNew();
        if (!gpu.FineJobDone(_farFineGpuJob))
        {
            if (!forceComplete) return true;
            while (!gpu.FineJobDone(_farFineGpuJob) && stopwatch.ElapsedMilliseconds < 3000) OS.DelayMsec(1);
        }
        if (gpu.Failed || !gpu.FineJobDone(_farFineGpuJob))
        {
            if (!gpu.Failed) { GD.PushWarning("Far fine layer atlas GPU build timed out; using the CPU build."); FarAtlasForceCpu = true; }
            _farFineGpuJob = 0;
            _farFineIndex = 0;
            return false;
        }
        int buffer = _farFineGpuBuffer;
        _farFineGpuJob = 0;
        _farFineGpuPublished = buffer;
        _farFineAtlas = gpu.FineTexture(buffer);
        FarFineAtlasContentHash = (uint)HashAtlasInputs(_farFineBuildSnapshotTime, PackFarAtlasParams(_farFineBuildSnapshotTime, _farFineBuildOrigin, _farFineBuildTexel));
        FarFineAtlasLastBuildMilliseconds = 0.0;
        FarFineAtlasLastBuildFrames = _farFineBuildFrames;
        _farFineBuilding = false;
        _farFinePublished = true;
        FarFineAtlasUsesGpu = true;
        _farFineSnapshotTime = _farFineBuildSnapshotTime;
        _farFineOrigin = _farFineBuildOrigin;
        _farFineTexel = _farFineBuildTexel;
        _farFineNextRefreshTime = _farFineBuildSnapshotTime + FarFineAtlasRefreshSeconds;
        FarFineAtlasPublicationCount++;
        _farLayerSkyMaterial?.SetShaderParameter(FarFineAtlasName, _farFineAtlas);
        return true;
    }

    // ---- Parity (tools/FarWeatherAtlasGpuParityTest.cs) ----

    /// <summary>
    /// Builds the whole-ring atlases for <paramref name="time"/> with the CPU path and returns their level 0 bytes (width x height x 4 each): exactly what the CPU build uploads.
    /// </summary>
    public void BuildFarWeatherAtlasCpuReference(double time, out byte[] weatherPixels, out byte[] layerPixels, out int width, out int height)
    {
        width = Mathf.Clamp(FarWeatherAtlasWidth, 512, 8192);
        height = Mathf.Clamp(FarWeatherAtlasHeight, 32, 512);
        var build = new FarWeatherAtlasBuild(time, width, height);
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!AdvanceFarWeatherAtlasCpuBuild(build, int.MaxValue, stopwatch)) { }
        weatherPixels = build.Pixels;
        layerPixels = build.LayerPixels;
        build.Image.Dispose();
        build.LayerImage.Dispose();
    }

    /// <summary>The CPU fine layer atlas (128 x 64 x 4 bytes) for a window, with the per-column system collection of the live build.</summary>
    public byte[] BuildFarFineAtlasCpuReference(double time, double origin, float texel)
    {
        var pixels = new byte[FarFineAtlasWidth * FarFineAtlasHeight * 4];
        double circumference = Math.Max(1.0, _circumferenceMeters);
        double acrossTexel = RingWidthMeters / (double)FarFineAtlasHeight;
        for (int x = 0; x < FarFineAtlasWidth; x++)
        {
            double around = origin + (x + 0.5) * texel;
            around -= circumference * Math.Floor(around / circumference);
            _farFineColumnSystems.Clear();
            _weatherSource!.CollectRenderWeatherSystems(new RingCanonicalPosition(around, 0.0, 0.5 * RingWidthMeters), 0.5 * RingWidthMeters + texel,
                time, _farFineColumnSystems, 0.0f, 500.0f, includeMegastorm: false);
            for (int y = 0; y < FarFineAtlasHeight; y++)
            {
                double across = (y + 0.5) * acrossTexel;
                float coverage = _weatherSource.QueryLocalWeatherCoverageAtTime(new RingCanonicalPosition(around, 0.0, across), time, includeMegastorm: false).CloudCoverage;
                Vector4 layers = ComputeFarLayerSample(around, across, time, coverage, _farFineColumnSystems);
                int pixel = (y * FarFineAtlasWidth + x) * 4;
                pixels[pixel] = Rgba8Channel(layers.X);
                pixels[pixel + 1] = Rgba8Channel(layers.Y);
                pixels[pixel + 2] = Rgba8Channel(layers.Z);
                pixels[pixel + 3] = Rgba8Channel(layers.W);
            }
        }
        return pixels;
    }

    /// <summary>Test hook: the GPU builder (null until the first GPU build) and the buffer currently published.</summary>
    public RingFarWeatherAtlasGpu? FarAtlasGpuForTest => _farAtlasGpu;
    public int FarWeatherGpuPublishedBufferForTest => _farWeatherGpuPublished;
    public int FarFineGpuPublishedBufferForTest => _farFineGpuPublished;
    public double FarFineAtlasOriginForTest => _farFinePublished ? _farFineOrigin : _circumferenceMeters * 0.3;
    public float FarFineAtlasTexelForTest => _farFinePublished ? _farFineTexel : 6000.0f;

    /// <summary>Test hook: queues a whole-ring GPU build for <paramref name="time"/> into the back buffer and returns its job id (0 when the GPU path is ineligible). Publishes nothing.</summary>
    public int QueueFarWeatherAtlasGpuForTest(double time, out int buffer)
    {
        buffer = 0;
        if (!FarAtlasGpuEligible()) return 0;
        RingFarWeatherAtlasGpu? gpu = EnsureFarAtlasGpu();
        if (gpu == null) return 0;
        BuildFarAtlasLayerBlock();
        int seed = _grid!.TerrainSampler!.Seed;
        buffer = 1 - _farWeatherGpuPublished;
        int job = gpu.NextJobId();
        gpu.QueueWeather(job, buffer, Mathf.Clamp(FarWeatherAtlasWidth, 512, 8192), Mathf.Clamp(FarWeatherAtlasHeight, 32, 512),
            PackFarAtlasParams(time, 0.0, 0.0f), _farAtlasLayerBlock, unchecked((int)((uint)seed ^ 0x9e3779b9u)));
        return job;
    }

    /// <summary>Test hook: queues a fine layer GPU build for a window into the back buffer and returns its job id (0 when ineligible).</summary>
    public int QueueFarFineAtlasGpuForTest(double time, double origin, float texel, out int buffer)
    {
        buffer = 0;
        if (!FarAtlasGpuEligible()) return 0;
        RingFarWeatherAtlasGpu? gpu = EnsureFarAtlasGpu();
        if (gpu == null) return 0;
        BuildFarAtlasLayerBlock();
        buffer = 1 - _farFineGpuPublished;
        int job = gpu.NextJobId();
        gpu.QueueFine(job, buffer, PackFarAtlasParams(time, origin, texel), _farAtlasLayerBlock);
        return job;
    }

    /// <summary>The CPU whole-ring atlas build (the original UpdateFarWeatherAtlas sample and texel loops): advances <paramref name="build"/> within the work budget and returns true when every texel is done.</summary>
    private bool AdvanceFarWeatherAtlasCpuBuild(FarWeatherAtlasBuild build, int? forcedBudget, Stopwatch stopwatch)
    {
        double workBudgetMilliseconds = FarWeatherAtlasWorkBudgetMilliseconds;
        int completedItems = 0;
        int sampleCap = forcedBudget ?? Mathf.Clamp(FarWeatherAtlasWorkBudgetPerFrame, 16, 128);
        const int sampleWidth = 128, sampleHeight = 32;
        int terrainSeed = _grid!.TerrainSampler!.Seed;
        while (completedItems < sampleCap && build.SampleIndex < build.Samples.Length)
        {
            if (forcedBudget == null && stopwatch.Elapsed.TotalMilliseconds >= workBudgetMilliseconds) break;
            int index = build.SampleIndex++;
            int sx = index % sampleWidth;
            int sy = index / sampleWidth;
            double around = sx / (double)sampleWidth * _circumferenceMeters;
            double across = sy / (double)(sampleHeight - 1) * RingWidthMeters;
            global::RingworldTechDemo.RingVolumetricCloudLayer.LocalWeatherState weather = _weatherSource?.QueryLocalWeatherCoverageAtTime(
            	new RingCanonicalPosition(around, 0.0, across), build.SnapshotTime, includeMegastorm: false) ?? default;
            float storm = Mathf.Clamp(weather.SystemInfluence * 0.65f + weather.PrecipitationPotential * 0.55f, 0.0f, 1.0f);
            build.Samples[index] = new Vector2(weather.CloudCoverage, storm);
            build.LayerSamples[index] = ComputeFarLayerSample(around, across, build.SnapshotTime, weather.CloudCoverage);
            completedItems++;
        }
        uint seed = unchecked((uint)terrainSeed) ^ 0x9e3779b9u;
        while (completedItems < 8192 && build.SampleIndex >= build.Samples.Length && build.TexelIndex < build.Width * build.Height)
        {
            if (forcedBudget == null && stopwatch.Elapsed.TotalMilliseconds >= workBudgetMilliseconds) break;
            int index = build.TexelIndex++;
            int x = index % build.Width;
            int y = index / build.Width;
            float u = x / (float)build.Width;
            float v = y / (float)Math.Max(1, build.Height - 1);
            Vector2 weather = SampleWeatherGrid(build.Samples, sampleWidth, sampleHeight, u, v);
            Vector4 layers = SampleWeatherGrid(build.LayerSamples, sampleWidth, sampleHeight, u, v);
            float coverage = Mathf.Clamp(weather.X, 0.0f, 1.0f);
            float storm = Mathf.Clamp(weather.Y, 0.0f, 1.0f);
            int pixel = index * 4;
            build.Pixels[pixel] = Rgba8Channel(coverage);
            build.Pixels[pixel + 1] = Rgba8Channel(storm);
            build.Pixels[pixel + 2] = Rgba8Channel(SeedUnit(seed ^ 0x27d4eb2du, x / 7, y / 5));
            build.Pixels[pixel + 3] = Rgba8Channel(SeedUnit(seed ^ 0x165667b1u, x / 3, y / 3));
            build.LayerPixels[pixel] = Rgba8Channel(layers.X);
            build.LayerPixels[pixel + 1] = Rgba8Channel(layers.Y);
            build.LayerPixels[pixel + 2] = Rgba8Channel(layers.Z);
            build.LayerPixels[pixel + 3] = Rgba8Channel(layers.W);
            build.ContentHash = (build.ContentHash ^ (uint)BitConverter.SingleToInt32Bits(coverage)) * 1099511628211UL;
            build.ContentHash = (build.ContentHash ^ (uint)BitConverter.SingleToInt32Bits(storm)) * 1099511628211UL;
            completedItems++;
        }
        FarWeatherAtlasLastWorkMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        FarWeatherAtlasLastWorkItems = completedItems;
        return build.TexelIndex >= build.Width * build.Height;
    }
}
