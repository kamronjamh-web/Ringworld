using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo;

/// <summary>Small, canonical-cell anchored rain field for storms seen beyond local particles.</summary>
public partial class RingDistantRainCurtains : Node3D
{
    [Export] public bool Enabled { get; set; } = true;
    [Export] public int MaximumCurtains { get; set; } = 25;
    [Export] public float CellMeters { get; set; } = 4000.0f;
    [Export] public float MinimumRangeMeters { get; set; } = 3000.0f;
    // Center fades finish inside the guaranteed 5x5 stencil, before recycling.
    [Export] public float MaximumRangeMeters { get; set; } = 8000.0f;
    [Export] public float PrecipitationStart { get; set; } = 0.18f;
    [Export] public float SnowTemperatureThreshold { get; set; } = -0.08f;
    [Export] public int SamplesPerFrame { get; set; } = 3;

    public int SampleCount { get; private set; }
    public int SamplesLastFrame { get; private set; }
    public int TerrainQueriesLastFrame { get; private set; }
    public int VisibleCurtainCount { get; private set; }
    public int MeshCount => _curtains.Count;
    public bool ProviderEligible { get; private set; }
    private RingVoxelExplorerController? _explorer;
    private VoxelChunkGridManager? _grid;
    private RingVolumetricCloudLayer? _weather;
    private DirectionalLight3D? _sun;
    private readonly List<Curtain> _curtains = new();
    private long _anchorAround = long.MinValue;
    private int _anchorAcross = int.MinValue, _nextSample;
    private double _lastClock = double.NegativeInfinity;
    private double _refreshAccumulator;
    private RingSunshineCloudBridge? _sunshine;
    private RingCloudProviderController? _provider;
    private RingLocalWeatherEffects? _localEffects;
    private Camera3D? _camera;
    private object? _cachedSampler;
    private double _cachedCircumference, _cachedWidth;
    private float _cachedCellMeters;
    private double _aroundSpacing;

    private sealed class Curtain
    {
        public readonly MeshInstance3D Node = new();
        public long Around; public int Across; public bool Valid, Sampled, TerrainCached;
        public float Intensity, Target, Precipitation, Temperature, CloudBase, Surface;
        public Vector2 Wind;
        public Vector2 StormOffset, StormRadii;
        public float StormOrientation;
        public bool ManualStorm;
    }

    public override void _Ready()
    {
        _explorer = GetParent().GetNodeOrNull<RingVoxelExplorerController>("Explorer");
        _grid = GetParent().GetNodeOrNull<VoxelChunkGridManager>("ChunkGridManager");
        _weather = GetParent().GetNodeOrNull<RingVolumetricCloudLayer>("RingVolumetricClouds");
        _sunshine = GetParent().GetNodeOrNull<RingSunshineCloudBridge>("SunshineCloudBridge");
        _provider = GetParent().GetNodeOrNull<RingCloudProviderController>("CloudProviderController");
        _localEffects = GetParent().GetNodeOrNull<RingLocalWeatherEffects>("LocalWeatherEffects");
        _camera = GetParent().GetNodeOrNull<Camera3D>("Explorer/Head/Camera3D");
        _sun = GetParent().GetNodeOrNull<DirectionalLight3D>("StarLight");
        for (int i = 0; i < Math.Clamp(MaximumCurtains, 1, 25); i++) CreateCurtain();
    }

    public override void _Process(double delta)
    {
        SamplesLastFrame = 0;
        TerrainQueriesLastFrame = 0;
        ProviderEligible = Enabled && _localEffects?.Enabled == true && _sunshine?.Enabled == true
            && _weather?.Enabled == true && _weather.SunshineCloudsPrimaryRenderer
            && _weather.Quality != RingVolumetricCloudLayer.CloudQuality.Off
            && _localEffects.CanProcess() && _sunshine.CanProcess() && _weather.CanProcess()
            && (_provider == null || _provider.SelectedProvider == RingCloudProviderController.CloudProvider.Sunshine);
        if (!ProviderEligible || _explorer == null || _grid?.TerrainSampler == null || _weather == null)
        { Clear(); return; }
        double clock = _weather.CloudSimulationTimeSeconds;
        if (!double.IsFinite(clock)) { Clear(); _lastClock = double.NegativeInfinity; return; }
        bool discontinuity = double.IsFinite(_lastClock) && (clock < _lastClock || clock - _lastClock > 5.0);
        _lastClock = clock;
        if (discontinuity) { Clear(); return; }
        float dt = double.IsFinite(delta) ? (float)Math.Clamp(delta, 0.0, 0.25) : 0.0f;
        RingCanonicalPosition player = _explorer.CanonicalPosition;
        ReconcileCells(player);
        _refreshAccumulator += dt;
        if (_refreshAccumulator >= 0.1)
        {
            _refreshAccumulator = 0.0;
            int budget = Mathf.Clamp(SamplesPerFrame, 1, 3);
            int visited = 0;
            while (visited++ < _curtains.Count && SamplesLastFrame < budget)
            {
                Curtain c = _curtains[_nextSample];
                _nextSample = (_nextSample + 1) % _curtains.Count;
                if (!c.Valid) continue;
                SampleWeather(c); SamplesLastFrame++;
            }
        }
        // Fade retained cells between sampling passes, avoiding abrupt weather-clock transitions.
        VisibleCurtainCount = 0;
        RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
        foreach (Curtain curtain in _curtains) ApplyVisual(curtain, player, geometry, dt);
    }

    private void CreateCurtain()
    {
        var mesh = new QuadMesh { Size = Vector2.One };
        var material = new ShaderMaterial { Shader = GD.Load<Shader>("res://materials/ring_distant_rain_curtain.gdshader") };
        material.SetShaderParameter("rain_tint", new Color(0.43f, 0.57f, 0.70f, 1));
        var curtain = new Curtain();
        curtain.Node.Mesh = mesh;
        curtain.Node.MaterialOverride = material;
        curtain.Node.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        curtain.Node.Visible = false;
        curtain.Node.Name = "RainCurtain" + _curtains.Count;
        AddChild(curtain.Node); _curtains.Add(curtain);
    }

    private void ReconcileCells(RingCanonicalPosition player)
    {
        double circumference = _grid!.GetCanonicalRingCircumferenceMeters64();
        float desired = float.IsFinite(CellMeters) ? Mathf.Clamp(CellMeters, 1000.0f, 8000.0f) : 4000.0f;
        int aroundCount = Math.Max(1, (int)Math.Round(circumference / desired));
        double spacing = circumference / aroundCount;
        double width = _grid.RingAcrossWidthMeters;
        int acrossCount = Math.Max(1, (int)Math.Ceiling(width / desired));
        int anchorA = Mod((int)Math.Floor(player.AroundMeters / spacing), aroundCount);
        int anchorX = Mathf.Clamp(Mathf.FloorToInt((float)(player.AcrossMeters / desired)), 0, acrossCount - 1);
        bool definitionChanged = !ReferenceEquals(_cachedSampler, _grid.TerrainSampler)
            || _cachedCircumference != circumference || _cachedWidth != width || _cachedCellMeters != desired;
        if (!definitionChanged && anchorA == _anchorAround && anchorX == _anchorAcross) return;
        if (definitionChanged) Clear();
        _cachedSampler = _grid.TerrainSampler;
        _cachedCircumference = circumference; _cachedWidth = width; _cachedCellMeters = desired;
        _aroundSpacing = spacing; _anchorAround = anchorA; _anchorAcross = anchorX;
        var wanted = new HashSet<(long Around, int Across)>();
        for (int z = -2; z <= 2; z++) for (int x = -2; x <= 2; x++)
        {
            int xi = anchorX + z; if (xi < 0 || xi >= acrossCount) continue;
            int ai = Mod(anchorA + x, aroundCount);
            long around = (long)Math.Round((ai + .5) * spacing) % (long)Math.Round(circumference);
            int across = (int)Math.Round((xi * desired + Math.Min((xi + 1.0) * desired, width)) * .5);
            wanted.Add((around, across));
        }
        foreach (Curtain c in _curtains) if (c.Valid && !wanted.Contains((c.Around, c.Across))) Invalidate(c);
        foreach (var key in wanted)
        {
            bool exists = false; foreach (Curtain c in _curtains) if (c.Valid && (c.Around, c.Across) == key) { exists = true; break; }
            if (exists) continue;
            Curtain? free = _curtains.Find(c => !c.Valid); if (free == null) continue;
            Assign(free, key.Around, key.Across);
        }
    }

    private void Assign(Curtain c, long aroundVoxel, int across)
    {
        c.Around = aroundVoxel; c.Across = across; c.Valid = true;
        c.Sampled = c.TerrainCached = false; c.Intensity = c.Target = c.Precipitation = 0;
    }
    private void SampleWeather(Curtain c)
    {
        // Assignment/teleport never performs a 25-column catch-up burst.
        if (!c.TerrainCached)
        {
            var terrain = _grid!.TerrainSampler!.GetTerrainSample(c.Around, c.Across);
            c.Temperature = terrain.Temperature;
            c.Surface = terrain.ResolveDisplayedHeight(terrain.SurfaceHeight);
            c.TerrainCached = true;
            TerrainQueriesLastFrame++;
        }
        RingVolumetricCloudLayer.LocalWeatherState weather = _weather!.QueryLocalWeather(new RingCanonicalPosition(c.Around, c.Surface, c.Across));
        c.Sampled = true;
        c.Precipitation = weather.PrecipitationPotential;
        c.Wind = weather.WindMetersPerSecond;
        bool naturalFront = weather.SystemKind is RingVolumetricCloudLayer.WeatherSystemKind.MajorFront
            or RingVolumetricCloudLayer.WeatherSystemKind.ExceptionalFront;
        c.CloudBase = weather.SystemId < 0
            ? weather.CloudBaseAltitudeMeters + (weather.SystemId == -1003 ? _sunshine!.FixedCloudAltitudeDatumMeters : 0.0f)
            : _sunshine!.FixedCloudAltitudeDatumMeters + Mathf.Max(100.0f,
                naturalFront ? _sunshine.StormFrontCloudBaseAltitudeMeters : _sunshine.NaturalCloudBaseAltitudeMeters);
        float start = Mathf.Clamp(PrecipitationStart, 0.0f, .99f);
        float value = Mathf.Clamp((weather.PrecipitationPotential - start) / (1.0f - start), 0, 1);
        c.Target = c.Temperature <= SnowTemperatureThreshold || c.Surface >= _localEffects!.SnowGuaranteedAboveAltitudeMeters
            || c.CloudBase <= c.Surface + 40.0f ? 0.0f : value * value * (3.0f - 2.0f * value);
        // Retain the last wet footprint while a removed storm fades away.
        if (c.Target > 0)
        {
            c.ManualStorm = weather.SystemId < 0;
            c.StormOffset = new Vector2((float)ShortestDelta(c.Around, weather.SystemCenterMeters.X, _cachedCircumference),
                (c.Across - weather.SystemCenterMeters.Y) * (c.ManualStorm ? 1 : -1));
            c.StormRadii = weather.SystemRadiiMeters;
            c.StormOrientation = weather.SystemOrientationRadians;
        }
        SampleCount++;
    }

    private void ApplyVisual(Curtain c, RingCanonicalPosition player, in RingLodGeometrySnapshot geometry, float delta)
    {
        c.Intensity = Mathf.MoveToward(c.Intensity, c.Target, delta * (c.Target > c.Intensity ? 0.75f : 0.35f));
        if (!c.Valid || !c.Sampled || c.Intensity < 0.01f) { c.Node.Visible = false; return; }
        float height = c.CloudBase - c.Surface;
        if (height <= 40.0f) { c.Node.Visible = false; return; }
        Vector3 center = geometry.CanonicalToWorld(c.Around, c.Surface + height * 0.5f, c.Across);
        Vector3 a = (geometry.CanonicalToWorld(c.Around + 10, c.Surface, c.Across) - geometry.CanonicalToWorld(c.Around, c.Surface, c.Across)).Normalized();
        Vector3 u = (geometry.CanonicalToWorld(c.Around, c.Surface + 10, c.Across) - geometry.CanonicalToWorld(c.Around, c.Surface, c.Across)).Normalized();
        Vector3 toCamera = (_camera?.GlobalPosition ?? _explorer!.GlobalPosition) - center;
        Vector3 projected = toCamera - u * toCamera.Dot(u);
        if (projected.LengthSquared() < 0.001f) projected = a;
        projected = projected.Normalized();
        Vector3 right = u.Cross(projected).Normalized();
        Vector3 normal = right.Cross(u).Normalized();
        float sheetWidth = (float)Math.Min(_aroundSpacing, _cachedCellMeters) * 1.15f;
        Vector3 acrossAxis = (geometry.CanonicalToWorld(c.Around, c.Surface, c.Across + 100) - geometry.CanonicalToWorld(c.Around, c.Surface, c.Across)).Normalized();
        Vector2 sheetDelta = new(right.Dot(a) * sheetWidth, right.Dot(acrossAxis) * sheetWidth);
        var mesh = (QuadMesh)c.Node.Mesh!;
        var size = new Vector2(sheetWidth, height);
        if (mesh.Size != size) mesh.Size = size;
        var transform = new Transform3D(new Basis(right, u, normal), center);
        if (c.Node.GlobalTransform != transform) c.Node.GlobalTransform = transform;
        double da = ShortestDelta(c.Around, player.AroundMeters, _cachedCircumference);
        float horizontalRange = Mathf.Sqrt((float)(da * da + (c.Across - player.AcrossMeters) * (c.Across - player.AcrossMeters)));
        float range = toCamera.Length();
        float maximum = Math.Min(float.IsFinite(MaximumRangeMeters) ? Math.Clamp(MaximumRangeMeters, 1000, 16000) : 8000,
            (float)Math.Min(_aroundSpacing, _cachedCellMeters) * 2.0f - 2.0f);
        if (_camera != null) maximum = Mathf.Min(maximum, _camera.Far * .85f);
        float minimum = Mathf.Clamp(float.IsFinite(MinimumRangeMeters) ? MinimumRangeMeters : 3000, 0, maximum * .5f);
        float fade = Mathf.SmoothStep(minimum, minimum + 1500, range)
            * (1.0f - Mathf.SmoothStep(maximum * .75f, maximum, range))
            * (1.0f - Mathf.SmoothStep(maximum * .75f, maximum, horizontalRange));
        if (fade <= .001f) { c.Node.Visible = false; return; }
        var material = (ShaderMaterial)c.Node.MaterialOverride;
        material.SetShaderParameter("opacity", c.Intensity * fade);
        // Bounded weather-clock phase avoids loss of sub-frame motion in long saves.
        material.SetShaderParameter("wind_phase", (float)(_weather!.CloudSimulationTimeSeconds % 4096.0));
        material.SetShaderParameter("cell_phase", (float)((c.Around * .001 + c.Across * .0017) % 256.0));
        material.SetShaderParameter("wind_meters_per_second", c.Wind);
        material.SetShaderParameter("across_extent", new Vector2(c.Across, sheetDelta.Y));
        material.SetShaderParameter("ring_width", (float)_cachedWidth);
        material.SetShaderParameter("storm_offset", c.StormOffset);
        material.SetShaderParameter("storm_right_delta", new Vector2(sheetDelta.X, sheetDelta.Y * (c.ManualStorm ? 1 : -1)));
        material.SetShaderParameter("storm_radii", c.StormRadii.Max(Vector2.One));
        material.SetShaderParameter("storm_rotation", new Vector2(Mathf.Cos(c.StormOrientation), Mathf.Sin(c.StormOrientation)));
        material.SetShaderParameter("storm_edges", c.ManualStorm ? new Vector2(.55f, 1.3f) : new Vector2(.32f, 1.0f));
        material.SetShaderParameter("sun_energy", _sun?.LightEnergy ?? 0.0f);
        float geometricDay = Mathf.SmoothStep(_grid!.RingDayStartDot, Mathf.Max(_grid.RingDayStartDot + .001f, _grid.RingDayFullDot), u.Dot(_sun?.GlobalBasis.Z.Normalized() ?? Vector3.Zero));
        material.SetShaderParameter("geometric_day", geometricDay);
        c.Node.Visible = fade > 0.001f; VisibleCurtainCount += c.Node.Visible ? 1 : 0;
    }

    private void Invalidate(Curtain c)
    {
        c.Valid = c.Sampled = c.TerrainCached = false;
        c.Target = c.Intensity = 0;
        c.Node.Visible = false;
    }

    private void Clear()
    {
        foreach (Curtain c in _curtains) Invalidate(c);
        VisibleCurtainCount = 0;
        _refreshAccumulator = 0;
        _nextSample = 0;
        _anchorAround = long.MinValue;
        _anchorAcross = int.MinValue;
    }
    private static int Mod(int value, int modulo) => ((value % modulo) + modulo) % modulo;
    private static double ShortestDelta(double a, double b, double circumference) { double d = a - b; if (d > circumference * .5) d -= circumference; if (d < -circumference * .5) d += circumference; return d; }
    public Godot.Collections.Array<Godot.Collections.Dictionary> GetCurtainDiagnostics()
    {
        var result = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        foreach (Curtain c in _curtains)
            result.Add(new()
            {
                ["valid"] = c.Valid && c.Sampled,
                ["around_meters"] = c.Around, ["across_meters"] = c.Across,
                ["world_position"] = c.Node.GlobalPosition,
                ["intensity"] = c.Intensity, ["target_intensity"] = c.Target,
                ["precipitation"] = c.Precipitation, ["temperature"] = c.Temperature,
                ["cloud_base_meters"] = c.CloudBase, ["surface_meters"] = c.Surface,
                ["visible"] = c.Node.Visible
            });
        return result;
    }

    public Godot.Collections.Dictionary GetDiagnostics() => new()
    {
        ["enabled"] = Enabled, ["provider_eligible"] = ProviderEligible,
        ["sample_count"] = SampleCount, ["samples_last_frame"] = SamplesLastFrame,
        ["terrain_queries_last_frame"] = TerrainQueriesLastFrame,
        ["sample_cap"] = MeshCount, ["samples_per_refresh_cap"] = Math.Clamp(SamplesPerFrame, 1, 3),
        ["refresh_interval_seconds"] = 0.1,
        ["mesh_count"] = MeshCount, ["visible_count"] = VisibleCurtainCount
    };
}
