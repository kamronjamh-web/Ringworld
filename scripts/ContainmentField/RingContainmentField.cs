using System;
using Godot;
using RingworldTechDemo.Atmosphere;
using RingworldTechDemo.Rendering;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.ContainmentField;

/// <summary>
/// Containment field near patch renderer (docs/containment-field-design.md 3.3-3.7, WO3; decisions 5-7 of 2026-11-04).
/// <para>Each frame: the camera's canonical position, the exact nearest field point and signed distance (<see cref="ContainmentNearPatchPlanner"/>). Outside
/// 1.1 x the shimmer range nothing is drawn (no draw call, pixel identity). Inside it, a 48 x 48 patch built on the CPU in double around the nearest point
/// (<see cref="ContainmentFieldNearPatch.Build"/>, rebuilt when the nearest point moves N/4) carries the glass: refraction shimmer, Fresnel reflection and the etched honeycomb (user decision 2026-11-05)
/// (materials/ring_containment_field.gdshader; the per-pixel weight is exactly 0 at the range, so activation never pops).</para>
/// <para>Draw order (3.7): with the camera BELOW the field (inside the contained volume) the patch is a transparent MeshInstance3D, so Sunshine clouds, which are
/// in front of the field from there, composite over it. With the camera ABOVE the field every cloud is behind it, so the mesh is hidden and the same patch is
/// drawn after Sunshine by <c>scripts/render/RingContainmentFieldComposite.gd</c> (same include, same attributes).</para>
/// <para>Ripples (WO4) read <see cref="RingContainmentDisturbanceHost.SharedBus"/> in <see cref="UpdateRipples"/> and share the material's shader.</para>
/// <para>Debug (not menu): <c>--containment-field=off</c> (A/B), <c>--containment-debug=1|2|3</c> (coverage tint / distance heat / lattice mask),
/// <c>--containment-field-pass=post</c> (draw the patch through the post-Sunshine pass from below too, to compare the two passes).</para>
/// </summary>
public partial class RingContainmentField : Node3D
{
    public const float DefaultShimmerStrength = 1.0f;
    public const float DefaultShimmerRangeKm = 3.0f;
    public const float DefaultSheenStrength = 1.0f;
    public const float DefaultLatticeStrength = 1.0f;
    public const string ShaderPath = "res://materials/ring_containment_field.gdshader";
    public const string CompositeScriptPath = "res://scripts/render/RingContainmentFieldComposite.gd";
    private const float MaxOffsetPixels = 3.0f;

    /// <summary>Near refraction amplitude, 0..2 (menu "Shimmer strength"). With the glass reflection and the etch also 0 the patch is hidden.</summary>
    public float ShimmerStrength { get; set; } = DefaultShimmerStrength;
    /// <summary>Distance N (km) inside which the shimmer shows, 0.5..10 (menu "Shimmer range").</summary>
    public float ShimmerRangeKm { get; set; } = DefaultShimmerRangeKm;
    /// <summary>Glass reflection strength (Fresnel reflection of the sky, habitat and sun), 0..2 (menu "Glass reflection"; the property keeps its WO3 name).</summary>
    public float SheenStrength { get; set; } = DefaultSheenStrength;
    /// <summary>Etch strength of the frosted hexagonal lattice (100 m cells, lit by the sun and sky; user decisions 2026-11-05), 0..2 (menu "Etched lattice").</summary>
    public float LatticeStrength { get; set; } = DefaultLatticeStrength;

    [Export] public bool Enabled { get; set; } = true;
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath WeatherPath { get; set; } = new("../RingVolumetricClouds");
    [Export] public NodePath SunLightPath { get; set; } = new("../StarLight");
    /// <summary>The node that publishes the scene lighting (day_side_atmosphere.gd, <c>get_published_lighting</c>).</summary>
    [Export] public NodePath DaySideAtmospherePath { get; set; } = new("../DaySideAtmosphere");
    /// <summary>Debug: 0 off, 1 coverage tint, 2 distance heat, 3 lattice mask (<c>--containment-debug</c>).</summary>
    [Export] public int DebugMode { get; set; }
    /// <summary>Debug: draw through the post-Sunshine pass from below too (<c>--containment-field-pass=post</c>).</summary>
    [Export] public bool ForcePostPass { get; set; }

    private static readonly StringName ShimmerName = "cf_shimmer_strength";
    private static readonly StringName SheenName = "cf_glass_strength";
    private static readonly StringName ToSunName = "cf_to_sun";
    private static readonly StringName UpName = "cf_up";
    private static readonly StringName SunIrradianceName = "cf_sun_irradiance";
    private static readonly StringName SkyRadianceName = "cf_sky_radiance";
    private static readonly StringName GroundRadianceName = "cf_ground_radiance";
    private static readonly StringName AmbientRadianceName = "cf_ambient_radiance";
    private static readonly StringName GetPublishedLightingName = "get_published_lighting";
    private static readonly StringName LatticeName = "cf_lattice_strength";
    private static readonly StringName LatticeOriginName = "cf_lattice_origin";
    private static readonly StringName RangeName = "cf_range_m";
    private static readonly StringName MaxOffsetName = "cf_max_offset_px";
    private static readonly StringName NoiseName = "cf_noise_origins";
    private static readonly StringName CenterName = "cf_ring_center_world";
    private static readonly StringName DebugName = "cf_debug_mode";
    private static readonly StringName PublishMeshName = "publish_mesh";
    private static readonly StringName PublishFrameName = "publish_frame";

    private VoxelChunkGridManager? _grid;
    private RingVolumetricCloudLayer? _weather;
    private DirectionalLight3D? _sun;
    private MeshInstance3D? _patch;
    private ShaderMaterial? _material;
    private ArrayMesh? _mesh;
    private CompositorEffect? _composite;
    private WorldEnvironment? _compositeEnvironment;
    private int _slotCheckCountdown;
    private bool _compositeActive;
    private bool _hazeRegistered;
    private readonly ContainmentNearPatchPlanner _planner = new();
    private readonly float[] _frameParams = new float[40];
    private Node? _daySide;
    private ContainmentFieldProfile? _localProfile;
    private (double Width, double WallTop) _localProfileKey;

    /// <summary>The last planner result (diagnostics and tests).</summary>
    public ContainmentNearPatchView LastView { get; private set; }
    /// <summary>The spatial patch is drawn this frame (camera below the field, in range).</summary>
    public bool NearPatchVisible => _patch?.Visible == true;
    /// <summary>The post-Sunshine pass is drawing this frame (camera above the field, in range).</summary>
    public bool PostPassActive => _compositeActive;
    public int PatchBuildCount => _planner.BuildCount;
    public long PostPassDispatches => _composite?.Get("dispatches").AsInt64() ?? 0;
    public CompositorEffect? PostComposite => _composite;
    public ShaderMaterial? Material => _material;
    /// <summary>CPU cost of the last <see cref="_Process"/> in microseconds (perf acceptance 3.9: near <= 50 us; a rebuild frame is reported separately).</summary>
    public double LastProcessMicroseconds { get; private set; }
    public double LastRebuildMicroseconds { get; private set; }
    /// <summary>Exponential average (about 50 frames) of the per-frame CPU cost in microseconds.</summary>
    public double AverageProcessMicroseconds { get; private set; }
    /// <summary>Diagnostics: microseconds of the camera transform, the snapshot and the planner in the last visible-range frame.</summary>
    public string TimingBreakdown { get; private set; } = string.Empty;
    private static double Us(long a, long b) => (b - a) * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency;

    public override void _Ready()
    {
        if (LoadSavedDefaults) LoadSettingsFromDisk();
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg == "--containment-field=off") Enabled = false;
            else if (arg.StartsWith("--containment-debug=", StringComparison.Ordinal) && int.TryParse(arg.AsSpan("--containment-debug=".Length), out int mode))
                DebugMode = Math.Clamp(mode, 0, 3);
            else if (arg == "--containment-field-pass=post") ForcePostPass = true;
        }
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _weather = GetNodeOrNull<RingVolumetricCloudLayer>(WeatherPath);
        _sun = GetNodeOrNull<DirectionalLight3D>(SunLightPath);
        _daySide = GetNodeOrNull(DaySideAtmospherePath);
        _material = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderPath) };
        _material.SetShaderParameter(MaxOffsetName, MaxOffsetPixels);
        _mesh = new ArrayMesh();
        _patch = new MeshInstance3D
        {
            Name = "NearPatch",
            Mesh = _mesh,
            MaterialOverride = _material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            Visible = false,
            Layers = RingRenderLayers.NoPrecipitationOcclusion,
            TopLevel = true,
        };
        _patch.AddToGroup(RingRenderLayers.NoPrecipitationOcclusionGroup);
        AddChild(_patch);
    }

    public override void _ExitTree()
    {
        SetCompositeActive(false, default, null);
        RemoveCompositeFromCompositor();
    }

    public override void _Process(double delta)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        ProcessField();
        LastProcessMicroseconds = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1000.0;
        AverageProcessMicroseconds = AverageProcessMicroseconds <= 0.0 ? LastProcessMicroseconds : AverageProcessMicroseconds + (LastProcessMicroseconds - AverageProcessMicroseconds) * 0.02;
    }

    private void ProcessField()
    {
        if (_patch == null || _material == null) return;
        Camera3D? camera = GetViewport()?.GetCamera3D();
        ContainmentFieldProfile? profile = CurrentProfile();
        bool on = Enabled && _grid != null && camera != null && profile is { IsValid: true }
            && (ShimmerStrength > 0.0f || SheenStrength > 0.0f || LatticeStrength > 0.0f) && ShimmerRangeKm > 0.0f;
        if (!on) { Hide(); return; }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        RingCanonicalPosition cameraCanonical = _grid!.WorldToCanonicalRingPosition64(camera!.GlobalPosition);
        double range = ShimmerRangeKm * 1000.0;
        // Cheap idle reject: the field's lowest points are the wall-top roots, so a camera more than 1.1 N below the wall top is out of range
        // (the whole habitat floor; no snapshot, no nearest-point solve).
        if (profile!.Value.WallTopMeters - cameraCanonical.VerticalMeters >= range * ContainmentFieldNearPatch.VisibleRangeFactor)
        {
            LastView = new ContainmentNearPatchView(false, ContainmentFieldViewSide.None, double.NaN, default, false);
            Hide();
            return;
        }
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        RingLodGeometrySnapshot snapshot = _grid.CaptureLodGeometrySnapshot();
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        ContainmentNearPatchView view = _planner.Update(profile!.Value, snapshot, cameraCanonical, range);
        long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
        TimingBreakdown = $"canonical={Us(t0, t1):0.0} snapshot={Us(t1, t2):0.0} planner={Us(t2, t3):0.0}";
        LastView = view;
        ContainmentNearPatchMesh? patch = _planner.Mesh;
        if (!view.Visible || patch == null) { Hide(); return; }
        if (view.Rebuilt || _mesh!.GetSurfaceCount() == 0)
        {
            long rebuildStart = System.Diagnostics.Stopwatch.GetTimestamp();
            Upload(patch);
            LastRebuildMicroseconds = System.Diagnostics.Stopwatch.GetElapsedTime(rebuildStart).TotalMilliseconds * 1000.0;
        }

        ContainmentFieldPoint anchor = patch.Anchor;
        Vector3 anchorWorld = snapshot.CanonicalToWorld(anchor.AroundMeters, anchor.VerticalMeters, anchor.AcrossMeters);
        double now = _weather?.CloudSimulationTimeSeconds ?? 0.0;
        Vector4 noise = ContainmentFieldNearPatch.NoiseOrigins(patch, now);
        Vector2 lattice = ContainmentFieldNearPatch.LatticeOrigin(patch);
        bool above = ForcePostPass || view.Side == ContainmentFieldViewSide.Above;
        FieldLighting light = UpdateLighting(snapshot, anchor);
        LastLighting = light;
        if (above)
        {
            if (_patch.Visible) _patch.Visible = false;
            Array.Clear(_frameParams);
            _frameParams[0] = MaxOffsetPixels; _frameParams[1] = DebugMode;
            _frameParams[4] = ShimmerStrength; _frameParams[5] = SheenStrength; _frameParams[6] = (float)range;
            _frameParams[7] = 1.0f;   // haze visibility: above the field the camera-to-patch path (<= 3.3 km) is in vacuum or air at >= 11.6 km (T > 0.99)
            _frameParams[8] = noise.X; _frameParams[9] = noise.Y; _frameParams[10] = noise.Z; _frameParams[11] = noise.W;
            _frameParams[12] = LatticeStrength; _frameParams[13] = lattice.X; _frameParams[14] = lattice.Y;
            Pack(16, light.ToSun); Pack(20, light.Up); Pack(24, light.SunIrradiance); Pack(28, light.SkyRadiance); Pack(32, light.GroundRadiance);
            Pack(36, light.AmbientRadiance);
            SetCompositeActive(true, anchorWorld, _frameParams);
        }
        else
        {
            SetCompositeActive(false, default, null);
            _patch.GlobalTransform = new Transform3D(Basis.Identity, anchorWorld);
            _material.SetShaderParameter(ShimmerName, ShimmerStrength);
            _material.SetShaderParameter(SheenName, SheenStrength);
            _material.SetShaderParameter(LatticeName, LatticeStrength);
            _material.SetShaderParameter(LatticeOriginName, lattice);
            _material.SetShaderParameter(RangeName, (float)range);
            _material.SetShaderParameter(NoiseName, noise);
            _material.SetShaderParameter(CenterName, _grid.GetCanonicalRingCenterWorld());
            _material.SetShaderParameter(DebugName, DebugMode);
            _material.SetShaderParameter(ToSunName, light.ToSun);
            _material.SetShaderParameter(UpName, light.Up);
            _material.SetShaderParameter(SunIrradianceName, light.SunIrradiance);
            _material.SetShaderParameter(SkyRadianceName, light.SkyRadiance);
            _material.SetShaderParameter(GroundRadianceName, light.GroundRadiance);
            _material.SetShaderParameter(AmbientRadianceName, light.AmbientRadiance);
            if (!_hazeRegistered)
            {
                RingPoiHaze.RegisterMaterial(_material, snapshot.RingRadiusMeters);
                _hazeRegistered = true;
            }
            RingPoiHaze.Tick(this, _grid, _sun);
            if (!_patch.Visible) _patch.Visible = true;
        }
        UpdateRipples();
    }

    /// <summary>Scene lighting at the patch (world space, linear, the units of lit surfaces): what the glass reflects and the etch scatters.</summary>
    public readonly record struct FieldLighting(Vector3 ToSun, Vector3 Up, Vector3 SunIrradiance, Vector3 SkyRadiance, Vector3 GroundRadiance,
        Vector3 AmbientRadiance, float Day);

    public FieldLighting LastLighting { get; private set; }

    /// <summary>Albedo of the habitat seen in the glass from above (terrain, sea and cloud tops under the field, averaged).</summary>
    private const float HabitatAlbedo = 0.2f;

    private void Pack(int index, Vector3 v) { _frameParams[index] = v.X; _frameParams[index + 1] = v.Y; _frameParams[index + 2] = v.Z; _frameParams[index + 3] = 0.0f; }

    private static float SmoothStep(float a, float b, float x)
    {
        float t = Mathf.Clamp((x - a) / Mathf.Max(b - a, 1e-6f), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }

    /// <summary>
    /// The lighting the glass and the etch receive, from what the scene already publishes (day_side_atmosphere.gd <c>get_published_lighting</c>: the haze
    /// model's sun irradiance and ambient / sky radiance, eclipse visibility, hub day, megastorm darkness K and storm ambient at the camera) and the real
    /// sun direction. The local day at the patch anchor uses the grid's day dots like every surface; at night only the night sky light is left, and the
    /// sky seen in the glass fades to black above 8-20 km like the sky itself. One set of values for both passes, so crossing the sheet does not pop.
    /// </summary>
    private FieldLighting UpdateLighting(in RingLodGeometrySnapshot snapshot, in ContainmentFieldPoint anchor)
    {
        Vector3 toSun = _sun != null && GodotObject.IsInstanceValid(_sun) ? _sun.GlobalTransform.Basis.Z.Normalized() : Vector3.Up;
        ContainmentFieldPlacement.AxesAt(snapshot, anchor.AroundMeters, out _, out Vector3 up, out _);
        up = up.Normalized();
        Vector3 sunIrr = new(12.566f, 12.566f, 12.566f), ambDay = new(0.2631f, 0.3464f, 0.4922f), ambNight = new(0.004f, 0.005f, 0.008f);
        Vector3 skyDay = ambDay, skyNight = ambNight;
        float nightScale = 1.0f, solarVisibility = 1.0f, hubDay = 0.0f, k = 1.0f, stormAmbient = 1.0f;
        if (_daySide != null && GodotObject.IsInstanceValid(_daySide) && _daySide.HasMethod(GetPublishedLightingName))
        {
            float[] v = _daySide.Call(GetPublishedLightingName).AsFloat32Array();
            if (v.Length >= 20)
            {
                sunIrr = new Vector3(v[0], v[1], v[2]); ambDay = new Vector3(v[3], v[4], v[5]); ambNight = new Vector3(v[6], v[7], v[8]);
                skyDay = new Vector3(v[9], v[10], v[11]); skyNight = new Vector3(v[12], v[13], v[14]);
                nightScale = v[15]; solarVisibility = v[16]; hubDay = v[17]; k = v[18]; stormAmbient = Mathf.Clamp(v[19], 0.0f, 1.0f);
            }
        }
        float dayStart = _grid?.RingDayStartDot ?? -0.2f, dayFull = Mathf.Max(dayStart + 0.001f, _grid?.RingDayFullDot ?? 0.35f);
        float day = Mathf.Max(SmoothStep(dayStart, dayFull, up.Dot(toSun)) * Mathf.Clamp(solarVisibility, 0.0f, 1.0f), Mathf.Clamp(hubDay, 0.0f, 1.0f));
        Vector3 sun = sunIrr * (day * k);
        Vector3 ambient = (ambNight * (nightScale * (1.0f - day)) + ambDay * day) * stormAmbient;
        float skyFade = 1.0f - SmoothStep(8_000.0f, 20_000.0f, (float)anchor.VerticalMeters);
        Vector3 sky = (skyNight * (nightScale * (1.0f - day)) + skyDay * day) * (stormAmbient * skyFade);
        Vector3 ground = (sun * (Mathf.Max(up.Dot(toSun), 0.0f) / Mathf.Pi) + ambient) * HabitatAlbedo;
        return new FieldLighting(toSun, up, sun, sky, ground, ambient, day);
    }

    /// <summary>WO4 hook: the ripple pool reads <see cref="RingContainmentDisturbanceHost.SharedBus"/> (Slots, Amplitude, FrontRadius, FlashAt) here.</summary>
    partial void UpdateRipplesCore();

    private void UpdateRipples() => UpdateRipplesCore();

    private void Hide()
    {
        if (_patch != null && _patch.Visible) _patch.Visible = false;
        SetCompositeActive(false, default, null);
    }

    /// <summary>The live profile: the disturbance host's (the one the cloud ceiling and the bus use), else the same construction from the grid.</summary>
    private ContainmentFieldProfile? CurrentProfile()
    {
        if (RingContainmentDisturbanceHost.Instance?.Profile is { } hosted) return hosted;
        if (_grid == null) return null;
        double width = _grid.RingAcrossWidthMeters, wallTop = _grid.GetStructuralWallTopGlobalY();
        if (_localProfile == null || _localProfileKey != (width, wallTop))
        {
            RimWallDescription wall = RimWallDescription.Current;
            _localProfile = new ContainmentFieldProfile(width, wallTop, ContainmentFieldProfile.DefaultApexMeters, wall.RootAcross(0, width), wall.RootAcross(1, width),
                ContainmentFieldProfile.DefaultSkirtHeightMeters);
            _localProfileKey = (width, wallTop);
        }
        return _localProfile;
    }

    private void Upload(ContainmentNearPatchMesh patch)
    {
        int count = patch.Positions.Length;
        var tangents = new float[count * 4];
        var bytes = new float[count * 12];
        for (int k = 0; k < count; k++)
        {
            Vector3 p = patch.Positions[k], n = patch.Normals[k], t = patch.Tangents[k];
            Vector2 uv = patch.Uv[k], uv2 = patch.Uv2[k];
            tangents[k * 4] = t.X; tangents[k * 4 + 1] = t.Y; tangents[k * 4 + 2] = t.Z; tangents[k * 4 + 3] = 1.0f;
            int o = k * 12;
            bytes[o] = p.X; bytes[o + 1] = p.Y; bytes[o + 2] = p.Z; bytes[o + 3] = uv2.X;
            bytes[o + 4] = n.X; bytes[o + 5] = n.Y; bytes[o + 6] = n.Z; bytes[o + 7] = uv.X;
            bytes[o + 8] = t.X; bytes[o + 9] = t.Y; bytes[o + 10] = t.Z; bytes[o + 11] = uv.Y;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = patch.Positions;
        arrays[(int)Mesh.ArrayType.Normal] = patch.Normals;
        arrays[(int)Mesh.ArrayType.Tangent] = tangents;
        arrays[(int)Mesh.ArrayType.TexUV] = patch.Uv;
        arrays[(int)Mesh.ArrayType.TexUV2] = patch.Uv2;
        arrays[(int)Mesh.ArrayType.Index] = patch.Indices;
        _mesh!.ClearSurfaces();
        _mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _patch!.CustomAabb = patch.Bounds.Grow(1.0f);
        byte[] raw = new byte[bytes.Length * sizeof(float)];
        Buffer.BlockCopy(bytes, 0, raw, 0, raw.Length);
        EnsureComposite();
        _composite?.Call(PublishMeshName, raw, ContainmentNearPatchMesh.Quads);
    }

    private void EnsureComposite()
    {
        if (_composite != null) return;
        _composite = GD.Load<GDScript>(CompositeScriptPath)?.New().AsGodotObject() as CompositorEffect;
    }

    private void SetCompositeActive(bool active, Vector3 anchorWorld, float[]? parameters)
    {
        if (!active)
        {
            if (!_compositeActive || _composite == null) { _compositeActive = false; return; }
            _composite.Call(PublishFrameName, Vector3.Zero, new float[40], false);
            _compositeActive = false;
            return;
        }
        EnsureComposite();
        if (_composite == null || !EnsureCompositorSlot()) { _compositeActive = false; return; }
        _composite.Call(PublishFrameName, anchorWorld, parameters!, true);
        _compositeActive = true;
    }

    private static string ScriptPathOf(CompositorEffect? effect) => (effect?.GetScript().AsGodotObject() as Resource)?.ResourcePath ?? string.Empty;

    /// <summary>
    /// Slot: after Sunshine and the lightning channel overlay, before the rainbow, rain streaks and rain haze (3.7). Re-checked every 30 frames while active
    /// (other effects attach later, e.g. the rain haze forcing itself last); the other owners accept any position between Sunshine and their own tail.
    /// </summary>
    private bool EnsureCompositorSlot()
    {
        if (_composite == null) return false;
        if (_compositeEnvironment != null && GodotObject.IsInstanceValid(_compositeEnvironment) && _slotCheckCountdown-- > 0) return true;
        _slotCheckCountdown = 30;
        WorldEnvironment? environment = _compositeEnvironment != null && GodotObject.IsInstanceValid(_compositeEnvironment) ? _compositeEnvironment : null;
        if (environment == null)
        {
            Viewport? viewport = GetViewport();
            if (viewport == null) return false;
            foreach (Node candidate in viewport.FindChildren("*", "WorldEnvironment", true, false))
                if (candidate is WorldEnvironment owner && owner.GetViewport() == viewport) { environment = owner; break; }
            if (environment == null) return false;
            _compositeEnvironment = environment;
        }
        environment.Compositor ??= new Compositor();
        var effects = environment.Compositor.CompositorEffects;
        using Godot.Collections.Array lifetime = (Godot.Collections.Array)effects;
        int self = -1, after = -1, tail = effects.Count;
        for (int i = 0; i < effects.Count; i++)
        {
            CompositorEffect fx = effects[i];
            if (fx == _composite) { self = i; continue; }
            string path = ScriptPathOf(fx);
            if (path.Contains("SunshineClouds2/SunshineClouds", StringComparison.Ordinal) || path.Contains("RingLightningChannelComposite", StringComparison.Ordinal))
                after = Math.Max(after, i);
            else if (tail == effects.Count && (path.Contains("RingRainbowComposite", StringComparison.Ordinal) || path.Contains("RingRainStreaksComposite", StringComparison.Ordinal)
                || path.Contains("RingRainHazeComposite", StringComparison.Ordinal)))
                tail = i;
        }
        if (self >= 0 && self > after && self < tail) return true;
        if (self >= 0) { effects.RemoveAt(self); if (tail > self) tail--; if (after > self) after--; }
        effects.Insert(Math.Max(after + 1, Math.Min(tail, effects.Count)), _composite);
        environment.Compositor.CompositorEffects = effects;
        return true;
    }

    private void RemoveCompositeFromCompositor()
    {
        if (_composite == null || _compositeEnvironment == null || !GodotObject.IsInstanceValid(_compositeEnvironment) || _compositeEnvironment.Compositor == null) return;
        var effects = _compositeEnvironment.Compositor.CompositorEffects;
        using Godot.Collections.Array lifetime = (Godot.Collections.Array)effects;
        if (effects.Remove(_composite)) _compositeEnvironment.Compositor.CompositorEffects = effects;
        _compositeEnvironment = null;
    }
}
