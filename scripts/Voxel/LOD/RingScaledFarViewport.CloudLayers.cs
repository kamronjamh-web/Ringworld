using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.LOD;

// S6 far cloud layers (docs/storm-revamp-s6-cloud-layers-design.md section 3.5): feeds the S6 layer table, the layer gradient, the
// Sunshine macro map, the storm sculpt list and a whole-ring layer atlas to the far "layer sky" pass
// (materials/ring_far_cloud_layer_sky.gdshader) and the far cloud clipmap.
public partial class RingScaledFarViewport
{
    private const string FarLayerSkyShaderPath = "res://materials/ring_far_cloud_layer_sky.gdshader";
    private const float FarLayerBlockRefreshSeconds = 0.25f;
    private const float FarLayerReceiverHeightMeters = 3000.0f;
    private const int FarLayerBlockVectors = RingCloudLayers.BlockBytes / 16;
    private const int FarLayerMaximumSculpts = 8;
    private const int FarLayerWeatherSystemLimit = 64;
    // Camera-centred fine layer atlas: 128 x 64 texels, the ring's whole width across (about 5 km) and a window of the same texel size around.
    private const int FarFineAtlasWidth = 128;
    private const int FarFineAtlasHeight = 64;
    private const float FarFineAtlasMinTexelMeters = 4000.0f;
    private const float FarFineAtlasMaxTexelMeters = 8000.0f;
    private const float FarFineAtlasFadeTexels = 10.0f;
    // Timed refresh (sim seconds from the build snapshot). A build is about 870 frames at the 0.5 ms budget, so a shorter period
    // keeps it building every frame; drift, manual-weather and discontinuity triggers still rebuild at once.
    private const double FarFineAtlasRefreshSeconds = 30.0;
    /// <summary>Camera drift from the window centre that starts a rebuild (debug env RING_FAR_FINE_RECENTRE_M shortens it for recentre pop tests).</summary>
    private static readonly double FarFineAtlasRecentreMeters = double.TryParse(OS.GetEnvironment("RING_FAR_FINE_RECENTRE_M"), out double recentre) && recentre > 100.0 ? recentre : 60000.0;
    private const double FarFineAtlasWorkBudgetMilliseconds = 0.5;

    private static readonly StringName FarCloudLayersName = "far_cloud_layers";
    private static readonly StringName FarCloudLayerGradientName = "far_cloud_layer_gradient";
    private static readonly StringName FarMacroMapName = "far_macro_map";
    private static readonly StringName FarMacroMapAName = "far_macro_map_a";
    private static readonly StringName FarMacroMapBName = "far_macro_map_b";
    private static readonly StringName FarLayerAtlasName = "far_layer_atlas";
    private static readonly StringName FarLayerAtlasEnabledName = "far_layer_atlas_enabled";
    private static readonly StringName FarFineAtlasName = "far_fine_atlas";
    private static readonly StringName FarFineAName = "far_fine_a";
    private static readonly StringName FarFineBName = "far_fine_b";
    private static readonly StringName FarFineWindName = "far_fine_wind";
    private static readonly StringName FarLayersEnabledName = "far_layers_enabled";
    private static readonly StringName FarWsHeaderName = "far_ws_header";
    private static readonly StringName FarSculptCountName = "far_sculpt_count";
    private static readonly StringName FarSculptAName = "far_sculpt_a";
    private static readonly StringName FarSculptBName = "far_sculpt_b";
    private static readonly StringName FarLayersMarchName = "far_layers_march";
    private static readonly StringName NearHandoffName = "near_handoff";
    private static readonly StringName DiskFadeName = "disk_fade";
    private static readonly StringName RingCircumferenceMetersName = "ring_circumference_meters";
    private static readonly StringName RingWidthMetersName = "ring_width_meters";

    private MeshInstance3D? _farLayerSky;
    private ShaderMaterial? _farLayerSkyMaterial;
    private ImageTexture? _farLayerAtlas;
    private double _farLayerSkyNextCreateAttempt;
    private double _farLayerBlockElapsed = double.PositiveInfinity;

    private readonly byte[] _farLayerBlockBytes = new byte[RingCloudLayers.BlockBytes];
    private readonly Vector4[] _farLayerBlockVectors = new Vector4[FarLayerBlockVectors];
    private bool _farLayerBlockValid;
    private bool _farLayerBlockSet;
    // Fine atlas state (main thread only). The build is staged and published whole, so a frame never sees a half-written window.
    private readonly byte[] _farFinePixels = new byte[FarFineAtlasWidth * FarFineAtlasHeight * 4];
    private ImageTexture? _farFineAtlas;
    private bool _farFineBuilding;
    private int _farFineIndex;
    private double _farFineBuildSnapshotTime;
    private double _farFineBuildOrigin;
    private float _farFineBuildTexel;
    private double _farFineNextRefreshTime;
    private double _farFineLastRawTime = double.NaN;
    private readonly List<RingVolumetricCloudLayer.RenderWeatherSystem> _farFineColumnSystems = new();
    /// <summary>Debug (environment RING_FAR_FINE_EXACT=1): collect the systems per texel instead of per column (the reference the column superset must equal).</summary>
    private static readonly bool DebugFarFineExact = OS.GetEnvironment("RING_FAR_FINE_EXACT") == "1";
    /// <summary>Diagnostics: FNV hash of the last published fine atlas bytes.</summary>
    public uint FarFineAtlasContentHash { get; private set; }
    private static readonly bool DebugFarFineVerify = OS.GetEnvironment("RING_FAR_FINE_VERIFY") == "1";
    public int FarFineAtlasVerifyMismatches { get; private set; } = -1;
    public int FarFineAtlasVerifyMaxDelta { get; private set; }
    private ulong _farFineManualRevision = ulong.MaxValue;
    private bool _farFinePublished;
    private double _farFineSnapshotTime;
    private double _farFineOrigin;
    private float _farFineTexel;
    private readonly LastUniformValue<Vector4> _farFineAApplied = new(), _farFineBApplied = new();
    private readonly LastUniformValue<Vector2> _farFineWindApplied = new();
    /// <summary>Diagnostics: CPU milliseconds the fine layer atlas spent in the last frame (build samples + publish), and the worst since reset.</summary>
    public double FarFineAtlasLastFrameMilliseconds { get; private set; }
    public double FarFineAtlasWorstFrameMilliseconds { get; set; }
    public int FarFineAtlasPublicationCount { get; private set; }
    /// <summary>Diagnostics: CPU milliseconds and frames the last completed fine atlas build took in total.</summary>
    public double FarFineAtlasLastBuildMilliseconds { get; private set; }
    public int FarFineAtlasLastBuildFrames { get; private set; }
    private double _farFineBuildAccumMs;
    private int _farFineBuildFrames;
    public bool FarFineAtlasBuildInProgress => _farFineBuilding;
    private readonly Vector4[] _farSculptA = new Vector4[FarLayerMaximumSculpts];
    private readonly Vector4[] _farSculptB = new Vector4[FarLayerMaximumSculpts];
    private int _farSculptCount = -1;
    private Vector4 _farWsHeader = new(0.5f, 0.82f, 0.0f, 0.0f);
    private bool _farWsHeaderRead;
    private readonly float[] _farSculptCandidateDistance = new float[FarLayerWeatherSystemLimit];
    private readonly Vector4[] _farSculptCandidateA = new Vector4[FarLayerWeatherSystemLimit];
    private readonly Vector4[] _farSculptCandidateB = new Vector4[FarLayerWeatherSystemLimit];

    private GodotObject? _farLayerGradientApplied;
    private Rid _farMacroMapRid;
    private Texture2DArrayRD? _farMacroMapTexture;
    private bool _farMacroValid;
    private bool _farLayerAtlasPublished;
    private bool _farLayersEnabled;
    /// <summary>Debug A/B (environment RING_FAR_LAYERS_OFF=1): the pre-WO6 far clouds (no layer sky; the clipmap draws its own body).</summary>
    private static readonly bool DebugFarLayersOff = OS.GetEnvironment("RING_FAR_LAYERS_OFF") == "1";
    /// <summary>Debug A/B (environment RING_FAR_FINE_OFF=1): no fine layer atlas (the whole-ring atlas beyond the macro map).</summary>
    private static readonly bool DebugFarFineOff = OS.GetEnvironment("RING_FAR_FINE_OFF") == "1";
    private bool _farLayerSkyVisible;

    private readonly LastUniformValue<Vector4> _farMacroMapAApplied = new(), _farMacroMapBApplied = new(), _farLayersMarchApplied = new(),
        _nearHandoffApplied = new(), _farWsHeaderApplied = new();
    private readonly LastUniformValue<Vector2> _diskFadeApplied = new();
    private readonly LastUniformValue<bool> _farLayersEnabledApplied = new();
    private readonly LastUniformValue<int> _farSculptCountApplied = new();
    private readonly LastUniformValue<float> _farLayerReceiverHeightApplied = new();

    // Layer-atlas scratch (main thread only).
    private readonly List<RingVolumetricCloudLayer.RenderWeatherSystem> _farLayerScratchSystems = new();
    private readonly List<CloudLayerSystem> _farLayerScratchLayerSystems = new();


    /// <summary>Test diagnostics: visibility, block / macro-map / atlas validity and the uploaded sculpt count of the far layer sky.</summary>
    public string FarLayerSkyStatus => _farLayerSky == null
        ? "FarLayerSky: not created"
        : $"FarLayerSky: visible={_farLayerSkyVisible} layers_valid={_farLayerBlockValid} macro_valid={_farMacroValid} atlas_published={_farLayerAtlasPublished} fine={_farFinePublished}/{FarFineAtlasPublicationCount} verify_mismatch={FarFineAtlasVerifyMismatches} max_delta={FarFineAtlasVerifyMaxDelta} hash={FarFineAtlasContentHash:X8} build_ms={FarFineAtlasLastBuildMilliseconds:F0} build_frames={FarFineAtlasLastBuildFrames} building={_farFineBuilding}@{_farFineIndex} fine_off={DebugFarFineOff} sculpts={Math.Max(0, _farSculptCount)}";

    private void UpdateFarCloudLayers(double delta)
    {
        if (_farViewport == null || _farCamera == null) return;
        EnsureFarLayerSky();
        if (_farLayerSky == null || _farLayerSkyMaterial == null) return;
        ShaderMaterial material = _farLayerSkyMaterial;
        _farLayerSky.GlobalPosition = _farCamera.GlobalPosition;

        Resource? clouds = _sunshineFarCloudResource;
        _farLayerBlockElapsed += delta;
        bool refresh = _farLayerBlockElapsed >= FarLayerBlockRefreshSeconds;
        if (refresh) _farLayerBlockElapsed = 0.0;

        if (clouds == null)
        {
            _farLayerBlockValid = false;
            _farMacroValid = false;
        }
        else
        {
            if (refresh)
            {
                RefreshFarLayerBlock(clouds, material);
                RefreshFarWeatherSystems(clouds, material);
            }
            UpdateFarMacroMap(clouds, material);
            UpdateFarLayerGradient(clouds, material);
            UpdateFarLayerMarch(clouds, material);
        }

        UpdateFarFineLayerAtlas(material, forceComplete: false);

        _farLayersEnabled = !DebugFarLayersOff && _farLayerBlockValid && (_farMacroValid || _farLayerAtlasPublished);
        _farLayersEnabledApplied.Set(material, FarLayersEnabledName, _farLayersEnabled);
        foreach (ShaderMaterial? clipmap in _farCloudClipmapMaterials)
            _farLayersEnabledApplied.Set(clipmap, FarLayersEnabledName, _farLayersEnabled);

        bool visible = FarCloudClipmapEnabled && _farLayersEnabled && (_farCloudClipmapLayers[0]?.Visible ?? false);
        _farLayerSkyVisible = visible;
        if (_farLayerSky.Visible != visible) _farLayerSky.Visible = visible;
    }

    /// <summary>Finishes the fine layer atlas build synchronously (tests and captures that need it ready on the next frame).</summary>
    public void CompleteFarFineLayerAtlas()
    {
        if (_farLayerSkyMaterial == null || !_farLayerBlockValid) return;
        for (int pass = 0; pass < 2; pass++) UpdateFarFineLayerAtlas(_farLayerSkyMaterial, forceComplete: true);
    }

    private void UpdateFarFineLayerAtlas(ShaderMaterial material, bool forceComplete)
    {
        Stopwatch frame = Stopwatch.StartNew();
        if (_weatherSource != null && _explorer != null && _farLayerBlockValid && _grid?.TerrainSampler != null && !DebugFarLayersOff && !DebugFarFineOff)
        {
            double time = ResolveFarWeatherRawTimeSeconds();
            bool discontinuity = !double.IsNaN(_farFineLastRawTime) && Math.Abs(time - _farFineLastRawTime) > 16.0;
            if (_weatherSource.ManualWeatherRevision != _farFineManualRevision)
            {
                _farFineManualRevision = _weatherSource.ManualWeatherRevision;
                discontinuity = true;
            }
            _farFineLastRawTime = time;
            if (_farFineBuilding && (discontinuity || time < _farFineBuildSnapshotTime)) _farFineBuilding = false;
            if (!_farFineBuilding && (discontinuity || !_farFinePublished || time >= _farFineNextRefreshTime || time < _farFineSnapshotTime || FarFineWindowDrift() > FarFineAtlasRecentreMeters))
                BeginFarFineLayerAtlasBuild(time);
            if (_farFineBuilding) StepFarFineLayerAtlasBuild(frame, forceComplete);
        }
        if (_farFinePublished && !DebugFarFineOff)
        {
            Vector2 wind = _weatherSource?.WeatherWindMetersPerSecond ?? Vector2.Zero;
            float age = (float)Math.Min(Math.Max(0.0, ResolveFarWeatherRawTimeSeconds() - _farFineSnapshotTime), 1_000_000.0);
            float acrossTexel = RingWidthMeters / FarFineAtlasHeight;
            _farFineAApplied.Set(material, FarFineAName, new Vector4((float)_farFineOrigin, _farFineTexel, acrossTexel, DebugFarLayersOff || DebugFarFineOff ? 0.0f : 1.0f));
            _farFineBApplied.Set(material, FarFineBName, new Vector4(FarFineAtlasWidth, FarFineAtlasHeight, age, FarFineAtlasFadeTexels));
            _farFineWindApplied.Set(material, FarFineWindName, wind);
        }
        double elapsed = frame.Elapsed.TotalMilliseconds;
        FarFineAtlasLastFrameMilliseconds = elapsed;
        if (elapsed > FarFineAtlasWorstFrameMilliseconds) FarFineAtlasWorstFrameMilliseconds = elapsed;
    }

    /// <summary>Distance (m, around the ring) from the camera to the published window's centre.</summary>
    private double FarFineWindowDrift()
    {
        double circumference = Math.Max(1.0, _circumferenceMeters);
        double d = _explorer!.CanonicalPosition.AroundMeters - (_farFineOrigin + 0.5 * FarFineAtlasWidth * _farFineTexel);
        d -= circumference * Math.Round(d / circumference);
        return Math.Abs(d);
    }

    private void BeginFarFineLayerAtlasBuild(double snapshotTime)
    {
        // The window covers the sky pass's reach (the shell crossfade end) plus a margin for the camera moving while it builds.
        float reach = GetEffectiveFarCloudClipmapRadiusMeters();
        float texel = Mathf.Clamp(Mathf.Ceil(reach * 1.3f * 2.0f / FarFineAtlasWidth / 250.0f) * 250.0f, FarFineAtlasMinTexelMeters, FarFineAtlasMaxTexelMeters);
        double circumference = Math.Max(1.0, _circumferenceMeters);
        double cameraAround = _explorer!.CanonicalPosition.AroundMeters;
        // Snap the grid to whole texels so successive windows sample the same positions where they overlap (no recentre shimmer).
        double origin = Math.Floor((cameraAround - 0.5 * FarFineAtlasWidth * texel) / texel) * texel;
        origin -= circumference * Math.Floor(origin / circumference);
        _farFineBuildOrigin = origin;
        _farFineBuildTexel = texel;
        _farFineBuildSnapshotTime = snapshotTime;
        _farFineIndex = 0;
        _farFineBuildAccumMs = 0.0;
        _farFineBuildFrames = 0;
        _farFineBuilding = true;
    }

    private void StepFarFineLayerAtlasBuild(Stopwatch frame, bool forceComplete)
    {
        double circumference = Math.Max(1.0, _circumferenceMeters);
        double acrossTexel = RingWidthMeters / (double)FarFineAtlasHeight;
        int total = FarFineAtlasWidth * FarFineAtlasHeight;
        _farFineBuildFrames++;
        double budget = _farFinePublished ? FarFineAtlasWorkBudgetMilliseconds : FarFineAtlasWorkBudgetMilliseconds * 3.0;
        while (_farFineIndex < total)
        {
            if (!forceComplete && frame.Elapsed.TotalMilliseconds >= budget) { _farFineBuildAccumMs += frame.Elapsed.TotalMilliseconds; return; }
            int index = _farFineIndex++;
            // Column-major: every texel of a column shares one system collection (a superset of each texel's own, see below).
            int x = index / FarFineAtlasHeight;
            int y = index % FarFineAtlasHeight;
            double around = _farFineBuildOrigin + (x + 0.5) * _farFineBuildTexel;
            around -= circumference * Math.Floor(around / circumference);
            double across = (y + 0.5) * acrossTexel;
            if (y == 0)
            {
                // Any system whose support reaches a texel of this column is within W/2 + one texel of the column's centre line.
                _farFineColumnSystems.Clear();
                _weatherSource!.CollectRenderWeatherSystems(new RingCanonicalPosition(around, 0.0, 0.5 * RingWidthMeters), 0.5 * RingWidthMeters + _farFineBuildTexel,
                    _farFineBuildSnapshotTime, _farFineColumnSystems, 0.0f, 500.0f, includeMegastorm: false);
            }
            float coverage = _weatherSource?.QueryLocalWeatherCoverageAtTime(
                new RingCanonicalPosition(around, 0.0, across), _farFineBuildSnapshotTime, includeMegastorm: false).CloudCoverage ?? 0.0f;
            Vector4 layers = ComputeFarLayerSample(around, across, _farFineBuildSnapshotTime, coverage, DebugFarFineExact ? null : _farFineColumnSystems);
            int pixel = (y * FarFineAtlasWidth + x) * 4;
            _farFinePixels[pixel] = Rgba8Channel(layers.X);
            _farFinePixels[pixel + 1] = Rgba8Channel(layers.Y);
            _farFinePixels[pixel + 2] = Rgba8Channel(layers.Z);
            _farFinePixels[pixel + 3] = Rgba8Channel(layers.W);
        }
        _farFineBuildAccumMs += frame.Elapsed.TotalMilliseconds;
        FarFineAtlasLastBuildMilliseconds = _farFineBuildAccumMs;
        FarFineAtlasLastBuildFrames = _farFineBuildFrames;
        if (DebugFarFineVerify)
        {
            // Debug: recompute every texel with its own system collection and count the bytes that differ from the column superset path.
            FarFineAtlasVerifyMismatches = 0;
            FarFineAtlasVerifyMaxDelta = 0;
            for (int i = 0; i < total; i++)
            {
                int vx = i / FarFineAtlasHeight, vy = i % FarFineAtlasHeight;
                double a = _farFineBuildOrigin + (vx + 0.5) * _farFineBuildTexel;
                a -= circumference * Math.Floor(a / circumference);
                double c = (vy + 0.5) * acrossTexel;
                float cov = _weatherSource!.QueryLocalWeatherCoverageAtTime(new RingCanonicalPosition(a, 0.0, c), _farFineBuildSnapshotTime, includeMegastorm: false).CloudCoverage;
                Vector4 l = ComputeFarLayerSample(a, c, _farFineBuildSnapshotTime, cov, null);
                int px = (vy * FarFineAtlasWidth + vx) * 4;
                int d = Math.Max(Math.Max(Math.Abs(_farFinePixels[px] - Rgba8Channel(l.X)), Math.Abs(_farFinePixels[px + 1] - Rgba8Channel(l.Y))),
                    Math.Max(Math.Abs(_farFinePixels[px + 2] - Rgba8Channel(l.Z)), Math.Abs(_farFinePixels[px + 3] - Rgba8Channel(l.W))));
                if (d > 0) FarFineAtlasVerifyMismatches++;
                FarFineAtlasVerifyMaxDelta = Math.Max(FarFineAtlasVerifyMaxDelta, d);
            }
        }
        uint hash = 2166136261u;
        foreach (byte b in _farFinePixels) hash = (hash ^ b) * 16777619u;
        FarFineAtlasContentHash = hash;
        var image = Image.CreateFromData(FarFineAtlasWidth, FarFineAtlasHeight, false, Image.Format.Rgba8, _farFinePixels);
        if (_farFineAtlas != null) _farFineAtlas.Update(image);
        else _farFineAtlas = ImageTexture.CreateFromImage(image);
        image.Dispose();
        _farFineBuilding = false;
        _farFinePublished = true;
        _farFineSnapshotTime = _farFineBuildSnapshotTime;
        _farFineOrigin = _farFineBuildOrigin;
        _farFineTexel = _farFineBuildTexel;
        _farFineNextRefreshTime = _farFineBuildSnapshotTime + FarFineAtlasRefreshSeconds;
        FarFineAtlasPublicationCount++;
        _farLayerSkyMaterial?.SetShaderParameter(FarFineAtlasName, _farFineAtlas);
    }

    private void EnsureFarLayerSky()
    {
        if (_farLayerSky != null || _farViewport == null || _farCamera == null) return;
        double now = Time.GetTicksMsec() * 0.001;
        if (now < _farLayerSkyNextCreateAttempt) return;
        _farLayerSkyNextCreateAttempt = now + 1.0;
        Shader? shader = GD.Load<Shader>(FarLayerSkyShaderPath);
        if (shader == null) return;
        var material = new ShaderMaterial { Shader = shader, RenderPriority = 16 };
        RingAtmosphereModel.ConfigureMaterial(material, _radiusMeters, CoordinateScale);
        var instance = new MeshInstance3D
        {
            Name = "FarCloudLayerSky",
            Mesh = new SphereMesh { Radius = 4.0f, Height = 8.0f, RadialSegments = 32, Rings = 16 },
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false
        };
        _farViewport.AddChild(instance);
        DisableWholeRingFrustumCulling(instance);
        instance.GlobalPosition = _farCamera.GlobalPosition;
        _farLayerSky = instance;
        _farLayerSkyMaterial = material;

        material.SetShaderParameter(RingCircumferenceMetersName, (float)_circumferenceMeters);
        material.SetShaderParameter(RingWidthMetersName, (float)RingWidthMeters);
        if (_farWeatherAtlas != null) ConfigureFarWeatherMaterial(material);
        if (_farFineAtlas != null) material.SetShaderParameter(FarFineAtlasName, _farFineAtlas);
        if (_farLayerAtlas != null)
        {
            material.SetShaderParameter(FarLayerAtlasName, _farLayerAtlas);
            material.SetShaderParameter(FarLayerAtlasEnabledName, true);
        }
        // Force the Sunshine density / texture / position bindings onto the new material, keeping the cached arguments (and coverage bias).
        float coverageBias = _lastFarCloudDetailStatic.CoverageBias;
        Resource? cached = _sunshineFarCloudResource;
        _hasFarCloudDetailStatic = false;
        _hasFarCloudDetailPositions = false;
        if (cached != null)
        {
            _sunshineFarCloudResource = null;
            ConfigureSunshineFarCloudDetail(cached, _sunshineFarCloudDetailEnabled, _sunshineFarCloudDetailStrength,
                _sunshineFarCloudOpacityMultiplier, _sunshineFarCloudRadiusMeters, coverageBias);
        }
        _farLayerBlockElapsed = double.PositiveInfinity;
    }

    private void RefreshFarLayerBlock(Resource clouds, ShaderMaterial material)
    {
        byte[] bytes;
        using (Variant value = clouds.Get("cloud_layers_bytes")) bytes = value.AsByteArray();
        if (bytes.Length != RingCloudLayers.BlockBytes)
        {
            _farLayerBlockValid = false;
            return;
        }
        _farLayerBlockValid = true;
        if (_farLayerBlockSet && bytes.AsSpan().SequenceEqual(_farLayerBlockBytes)) return;
        bytes.CopyTo(_farLayerBlockBytes, 0);
        for (int i = 0; i < FarLayerBlockVectors; i++)
        {
            int o = i * 16;
            _farLayerBlockVectors[i] = new Vector4(
                BitConverter.ToSingle(bytes, o), BitConverter.ToSingle(bytes, o + 4),
                BitConverter.ToSingle(bytes, o + 8), BitConverter.ToSingle(bytes, o + 12));
        }
        material.SetShaderParameter(FarCloudLayersName, Variant.From(_farLayerBlockVectors));
        _farLayerBlockSet = true;
    }

    private void UpdateFarLayerGradient(Resource clouds, ShaderMaterial material)
    {
        Texture2D? gradient = GetResourceObject<Texture2D>(clouds, "layer_gradient_texture");
        if (gradient == null || ReferenceEquals(gradient, _farLayerGradientApplied)) return;
        material.SetShaderParameter(FarCloudLayerGradientName, gradient);
        _farLayerGradientApplied = gradient;
    }

    private void UpdateFarMacroMap(Resource clouds, ShaderMaterial material)
    {
        bool valid;
        using (Variant value = clouds.Get("macro_map_valid")) valid = value.AsBool();
        Rid rid;
        using (Variant value = clouds.Get("macro_map_texture")) rid = value.AsRid();
        if (valid && rid.IsValid && rid != _farMacroMapRid)
        {
            _farMacroMapTexture = new Texture2DArrayRD { TextureRdRid = rid };
            _farMacroMapRid = rid;
            material.SetShaderParameter(FarMacroMapName, _farMacroMapTexture);
        }
        _farMacroValid = valid && rid.IsValid && _farMacroMapTexture != null;
        Vector2 origin; float texel, circumference; long layer; Vector2I size;
        using (Variant value = clouds.Get("macro_map_front_origin")) origin = value.AsVector2();
        using (Variant value = clouds.Get("macro_map_front_texel")) texel = (float)value.AsDouble();
        using (Variant value = clouds.Get("macro_map_front_layer")) layer = value.AsInt64();
        using (Variant value = clouds.Get("macro_map_front_size")) size = value.AsVector2I();
        using (Variant value = clouds.Get("macro_map_front_circumference")) circumference = (float)value.AsDouble();
        _farMacroMapAApplied.Set(material, FarMacroMapAName, new Vector4(origin.X, origin.Y, texel, layer));
        _farMacroMapBApplied.Set(material, FarMacroMapBName, new Vector4(size.X, size.Y, circumference, _farMacroValid ? 1.0f : 0.0f));
    }

    private void UpdateFarLayerMarch(Resource clouds, ShaderMaterial material)
    {
        float density, sharpness, maxStepDistance, maxStepCount;
        Vector4 handoff;
        using (Variant value = clouds.Get("clouds_density")) density = (float)value.AsDouble();
        using (Variant value = clouds.Get("clouds_sharpness")) sharpness = (float)value.AsDouble();
        using (Variant value = clouds.Get("max_step_distance")) maxStepDistance = (float)value.AsDouble();
        using (Variant value = clouds.Get("max_step_count")) maxStepCount = (float)value.AsDouble();
        using (Variant value = clouds.Get("cloud_handoff")) handoff = value.AsVector4();
        float referenceStep = Mathf.Max(maxStepDistance, handoff.Y / Mathf.Max(1.0f, maxStepCount));
        _farLayersMarchApplied.Set(material, FarLayersMarchName, new Vector4(density, sharpness, referenceStep, 0.0f));
        _nearHandoffApplied.Set(material, NearHandoffName, new Vector4(handoff.X, handoff.Y, _nearCloudOwnershipEnabled ? 1.0f : 0.0f,
            Mathf.Clamp(global::RingworldTechDemo.RingFarWeatherShadowGlobals.Frame.Z, 0.0f, 1.0f)));
        float outer = GetEffectiveFarCloudClipmapRadiusMeters();
        _diskFadeApplied.Set(material, DiskFadeName, new Vector2(outer * 0.78f, outer));
        _farLayerReceiverHeightApplied.Set(material, TerrainCloudReceiverHeightName, FarLayerReceiverHeightMeters);
    }

    private void RefreshFarWeatherSystems(Resource clouds, ShaderMaterial material)
    {
        byte[] bytes;
        using (Variant value = clouds.Get("weather_systems_bytes")) bytes = value.AsByteArray();
        if (bytes.Length < 64)
        {
            if (_farSculptCount != 0) { _farSculptCount = 0; }
            _farSculptCountApplied.Set(material, FarSculptCountName, 0);
            return;
        }
        float F(int index) => BitConverter.ToSingle(bytes, index * 4);
        int available = (bytes.Length / 4 - 16) / 16;
        int count = Math.Clamp((int)(F(0) + 0.5f), 0, Math.Min(FarLayerWeatherSystemLimit, Math.Max(0, available)));
        _farWsHeader = new Vector4(F(2), F(4), F(3), F(7));
        _farWsHeaderRead = true;
        _farWsHeaderApplied.Set(material, FarWsHeaderName, _farWsHeader);

        Vector4 frame;
        using (Variant value = clouds.Get("cloud_weather_frame")) frame = value.AsVector4();
        Vector2 shift = new(frame.Y, frame.Z);
        float circumference = (float)Math.Max(1.0, _circumferenceMeters);
        float cameraAround = 0.0f, cameraAcross = 0.0f;
        if (_explorer != null)
        {
            RingCanonicalPosition canonical = _explorer.CanonicalPosition;
            cameraAround = (float)canonical.AroundMeters;
            cameraAcross = (float)canonical.AcrossMeters;
        }

        int candidates = 0;
        for (int i = 0; i < count; i++)
        {
            int o = 16 + i * 16;
            uint flags = (uint)(F(o + 11) + 0.5f);
            if ((flags & RingVolumetricCloudLayer.RenderWeatherFlagSculpt) == 0u) continue;
            bool isStatic = (flags & RingVolumetricCloudLayer.RenderWeatherFlagStatic) != 0u;
            float dAround = F(o) - (isStatic ? cameraAround : cameraAround + shift.X);
            float dAcross = F(o + 1) - (isStatic ? cameraAcross : cameraAcross + shift.Y);
            dAround -= circumference * MathF.Round(dAround / circumference);
            _farSculptCandidateDistance[candidates] = dAround * dAround + dAcross * dAcross;
            _farSculptCandidateA[candidates] = new Vector4(F(o), F(o + 1), F(o + 2), F(o + 3));
            _farSculptCandidateB[candidates] = new Vector4(F(o + 4), F(o + 5), isStatic ? 1.0f : 0.0f, 0.0f);
            candidates++;
        }
        int chosen = Math.Min(candidates, FarLayerMaximumSculpts);
        for (int slot = 0; slot < FarLayerMaximumSculpts; slot++)
        {
            if (slot < chosen)
            {
                int best = slot;
                for (int j = slot + 1; j < candidates; j++)
                    if (_farSculptCandidateDistance[j] < _farSculptCandidateDistance[best]) best = j;
                (_farSculptCandidateDistance[slot], _farSculptCandidateDistance[best]) = (_farSculptCandidateDistance[best], _farSculptCandidateDistance[slot]);
                (_farSculptCandidateA[slot], _farSculptCandidateA[best]) = (_farSculptCandidateA[best], _farSculptCandidateA[slot]);
                (_farSculptCandidateB[slot], _farSculptCandidateB[best]) = (_farSculptCandidateB[best], _farSculptCandidateB[slot]);
                _farSculptA[slot] = _farSculptCandidateA[slot];
                _farSculptB[slot] = _farSculptCandidateB[slot];
            }
            else
            {
                _farSculptA[slot] = Vector4.Zero;
                _farSculptB[slot] = Vector4.Zero;
            }
        }
        _farSculptCount = chosen;
        _farSculptCountApplied.Set(material, FarSculptCountName, chosen);
        // The two arrays are small; they change whenever the camera, wind shift or storm lifecycle does, so write them on the 0.25 s timer.
        material.SetShaderParameter(FarSculptAName, Variant.From(_farSculptA));
        material.SetShaderParameter(FarSculptBName, Variant.From(_farSculptB));
    }

    /// <summary>
    /// Layer value for one whole-ring atlas sample: RingCloudLayers.EvaluateMap at the sample, with the high layer's coverage scaled by (1 - CPU storm
    /// ownership) (twin of ring_ws_storm_owned_at). Returns (low, mid, high * (1 - owned), weatherness).
    /// </summary>
    private Vector4 ComputeFarLayerSample(double around, double across, double time, float coverage, List<RingVolumetricCloudLayer.RenderWeatherSystem>? sharedSystems = null)
    {
        if (_weatherSource == null) return Vector4.Zero;
        Vector2 wind = _weatherSource.WeatherWindMetersPerSecond;
        float circumference = (float)Math.Max(1.0, _circumferenceMeters);
        var systems = _farLayerScratchSystems;
        if (sharedSystems != null) systems = sharedSystems;
        else
        {
            systems.Clear();
            _weatherSource.CollectRenderWeatherSystems(new RingCanonicalPosition(around, 0.0, across), 0.0, time, systems, 0.0f, 500.0f, includeMegastorm: false);
        }
        var layerSystems = _farLayerScratchLayerSystems;
        layerSystems.Clear();
        foreach (RingVolumetricCloudLayer.RenderWeatherSystem system in systems) layerSystems.Add(new CloudLayerSystem(system, 1.0f));

        var table = CloudLayerTable.Shared;
        Vector2 shift = new((float)(-wind.X * time), (float)(-wind.Y * time));
        shift.X -= circumference * MathF.Floor(shift.X / circumference);
        Vector3 sunLocal = _farLayerBlockValid
            ? new Vector3(_farLayerBlockVectors[4].X, _farLayerBlockVectors[4].Y, _farLayerBlockVectors[4].Z)
            : Vector3.Zero;
        // SampleContinuousClusterTerm advects by the wind itself (source = canonical - wind * time) and wraps around, so the canonical
        // position with the snapshot time equals the probe's source-space position with time 0.
        var point = new CloudLayerPoint((float)around, (float)across, shift, circumference, coverage,
            _farWsHeaderRead ? _farWsHeader.X : 0.5f,
            _weatherSource.SampleContinuousClusterTerm(around, across, time, table.MidClusterSalts.X, table.MidClusterSalts.Y),
            _weatherSource.SampleContinuousClusterTerm(around, across, time, table.HighClusterSalts.X, table.HighClusterSalts.Y),
            MathF.Atan2(wind.Y, wind.X), sunLocal);
        Vector4 map = RingCloudLayers.EvaluateMap(point, layerSystems, table);

        float owned = 0.0f;
        foreach (RingVolumetricCloudLayer.RenderWeatherSystem system in systems)
        {
            if (!system.Sculpts) continue;
            float offX = system.IsStatic ? 0.0f : shift.X;
            float offY = system.IsStatic ? 0.0f : shift.Y;
            float dA = (float)(around + offX - system.CenterAroundMeters);
            dA -= circumference * MathF.Round(dA / circumference);
            float dC = (float)(across + offY - system.CenterAcrossMeters);
            float qx = (system.OrientationCosine * dA + system.OrientationSine * dC) / Math.Max(system.MajorRadiusMeters, 1.0f);
            float qz = (-system.OrientationSine * dA + system.OrientationCosine * dC) / Math.Max(system.MinorRadiusMeters, 1.0f);
            float d2 = qx * qx + qz * qz;
            float t = Math.Clamp((d2 - 1.69f) / (1.45f * 1.45f - 1.69f), 0.0f, 1.0f);
            float ownership = 1.0f - t * t * (3.0f - 2.0f * t);
            if (ownership > owned) owned = ownership;
        }
        return new Vector4(map.X, map.Y, map.Z * (1.0f - owned), map.W);
    }

    private static Vector4 SampleWeatherGrid(Vector4[] values, int width, int height, float u, float v)
    {
        float x = u * width;
        int x0 = Mathf.PosMod(Mathf.FloorToInt(x), width);
        int x1 = (x0 + 1) % width;
        float y = Mathf.Clamp(v, 0.0f, 1.0f) * (height - 1);
        int y0 = Mathf.FloorToInt(y);
        int y1 = Mathf.Min(y0 + 1, height - 1);
        float fx = x - Mathf.Floor(x);
        return values[y0 * width + x0].Lerp(values[y0 * width + x1], fx).Lerp(
            values[y1 * width + x0].Lerp(values[y1 * width + x1], fx), y - y0);
    }

    private void PublishFarLayerAtlas(FarWeatherAtlasBuild build)
    {
        build.LayerImage.SetData(build.Width, build.Height, false, Image.Format.Rgba8, build.LayerPixels);
        if (_farLayerAtlas != null && _farLayerAtlas.GetSize() == new Vector2(build.Width, build.Height)) _farLayerAtlas.Update(build.LayerImage);
        else _farLayerAtlas = ImageTexture.CreateFromImage(build.LayerImage);
        build.LayerImage.Dispose();
        _farLayerAtlasPublished = true;
        foreach (ShaderMaterial? material in new[] { _farLayerSkyMaterial, _cloudMaterial, _mainShellMaterial, _farWeatherCloudBakeMaterial })
        {
            if (material == null) continue;
            material.SetShaderParameter(FarLayerAtlasName, _farLayerAtlas);
            material.SetShaderParameter(FarLayerAtlasEnabledName, true);
        }
    }
}
