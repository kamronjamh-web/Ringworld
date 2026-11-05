using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Lightning;

/// <summary>
/// Draws every field strike (a <see cref="RingLightningEvent"/> with the <c>FieldStrike</c> flag: GroundPosition = the contact point under the containment field,
/// CloudPosition = the channel base) whatever system made it (docs/megastorm-design.md 5.5.5, WO13), plus the megastorm's near-rim contact arcs.
/// Two channel sets share one handoff: the MAIN set is the post-cloud channel overlay (RingLocalWeatherEffects.LightningChannels.cs pulls events and weights from
/// <see cref="SelectMain"/>; the overlay tests terrain depth and fades by the cloud in front, which a depth-less mesh could not), used within about 120-150 km; the FAR
/// set is a camera-facing quad per strike in the scaled far viewport (materials/ring_field_strike.gdshader), faded in over the same band out to the antipode, with a
/// minimum angular size. The canopy flare beyond 400 km belongs to the field's own far strike layer; this layer draws only the channel.
/// </summary>
public partial class RingFieldStrikeLayer : Node3D
{
    public const string ShaderPath = "res://materials/ring_field_strike.gdshader";
    public const int MaxFarInstances = 6, MaxMainEvents = 4;
    public const int FarRenderPriority = 21;
    /// <summary>Main (overlay) to far handoff band, horizontal distance of the contact point from the observer.</summary>
    public const double HandoffStartMeters = 120000.0, HandoffEndMeters = 150000.0;
    /// <summary>Without a far viewport the overlay carries strikes out to here (the main camera far plane is 220 km).</summary>
    public const double MainOnlyStartMeters = 180000.0, MainOnlyEndMeters = 210000.0;

    [Export] public bool Enabled { get; set; } = true;
    [Export] public NodePath WeatherPath { get; set; } = new("../RingVolumetricClouds");
    [Export] public NodePath FarViewportPath { get; set; } = new("../ScaledFarViewport");
    [Export] public NodePath SunLightPath { get; set; } = new("../StarLight");
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    /// <summary>Minimum angular width of the far channel in degrees (docs: 0.3).</summary>
    [Export] public float MinimumAngularSizeDegrees { get; set; } = 0.3f;
    [Export] public float BaseEmission { get; set; } = 30.0f;

    public static RingFieldStrikeLayer? Instance { get; private set; }
    /// <summary>True once the far set exists (the scaled far world accepted the layer); the overlay then hands over at 120-150 km.</summary>
    public static bool FarSetBound => Instance?._instances != null;

    private RingVolumetricCloudLayer? _weather;
    private RingScaledFarViewport? _farViewport;
    private DirectionalLight3D? _sun;
    private Node3D? _grid;
    private Camera3D? _observer;
    private MultiMeshInstance3D? _instances;
    private ShaderMaterial? _material;
    private double _bindRetry;
    private readonly List<RingLightningEvent> _farLive = new();
    private readonly float[] _gain = new float[MaxFarInstances];
    private readonly float[] _progress = new float[MaxFarInstances];

    public int FarLiveCount => _farLive.Count;
    public IReadOnlyList<RingLightningEvent> FarLive => _farLive;
    public ShaderMaterial? Material => _material;

    public override void _EnterTree() => Instance = this;
    public override void _ExitTree() { if (Instance == this) Instance = null; }

    public override void _Ready()
    {
        PhysicsInterpolationMode = PhysicsInterpolationModeEnum.Off;
        _weather = GetNodeOrNull<RingVolumetricCloudLayer>(WeatherPath);
        _farViewport = GetNodeOrNull<RingScaledFarViewport>(FarViewportPath);
        _sun = GetNodeOrNull<DirectionalLight3D>(SunLightPath);
        _grid = GetNodeOrNull<Node3D>(GridManagerPath);
    }

    // ---- Pure selection and weights (also used by the overlay driver and the tests) ----------------------------------------

    private static float Smooth(double lo, double hi, double x) { double t = Math.Clamp((x - lo) / Math.Max(hi - lo, 1e-6), 0.0, 1.0); return (float)(t * t * (3.0 - 2.0 * t)); }

    /// <summary>Overlay (main set) weight of a strike whose contact point is <paramref name="distanceMeters"/> away horizontally.</summary>
    public static float MainWeight(double distanceMeters, bool farSetBound)
        => farSetBound ? 1.0f - Smooth(HandoffStartMeters, HandoffEndMeters, distanceMeters) : 1.0f - Smooth(MainOnlyStartMeters, MainOnlyEndMeters, distanceMeters);

    /// <summary>Far set weight (0 when there is no far set).</summary>
    public static float FarWeight(double distanceMeters, bool farSetBound) => farSetBound ? Smooth(HandoffStartMeters, HandoffEndMeters, distanceMeters) : 0.0f;

    /// <summary>
    /// The main-set strikes: live flagged events (and contact arcs) whose life covers <paramref name="now"/>, with a positive main weight, nearest contact point first
    /// (ties by EventId), at most <paramref name="max"/>. <paramref name="weights"/> is parallel to <paramref name="events"/>. Lists are cleared first; no allocation.
    /// </summary>
    public static int SelectMain(IReadOnlyList<RingLightningEvent> active, IReadOnlyList<RingLightningEvent>? arcs, double now,
        Func<RingCanonicalPosition, double> distanceToObserver, bool farSetBound, int max, List<RingLightningEvent> events, List<float> weights)
    {
        events.Clear(); weights.Clear();
        if (max <= 0) return 0;
        void Consider(in RingLightningEvent e)
        {
            double age = now - e.StartTimeSeconds;
            if (!e.FieldStrike || age < 0.0 || age > e.LifeSeconds) return;
            float w = MainWeight(distanceToObserver(e.GroundPosition), farSetBound);
            if (!(w > 0.0f)) return;
            double d = distanceToObserver(e.GroundPosition);
            int at = events.Count;
            while (at > 0)
            {
                double dp = distanceToObserver(events[at - 1].GroundPosition);
                if (dp < d || (dp == d && events[at - 1].EventId < e.EventId)) break;
                at--;
            }
            events.Insert(at, e); weights.Insert(at, w);
        }
        for (int i = 0; i < active.Count; i++) Consider(active[i]);
        if (arcs != null) for (int i = 0; i < arcs.Count; i++) Consider(arcs[i]);
        if (events.Count > max) { events.RemoveRange(max, events.Count - max); weights.RemoveRange(max, weights.Count - max); }
        return events.Count;
    }

    // ---- Far set -----------------------------------------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        if (_instances == null)
        {
            if (!Enabled || (_bindRetry -= delta) > 0.0) return;
            _bindRetry = 0.5;
            Bind();
            if (_instances == null) return;
        }
        UpdateFar();
    }

    private bool Bind()
    {
        if (_farViewport == null || !GodotObject.IsInstanceValid(_farViewport) || !_farViewport.HasScaledWorld) return false;
        Shader? shader = GD.Load<Shader>(ShaderPath);
        if (shader == null) return false;
        _material = new ShaderMaterial { Shader = shader, RenderPriority = FarRenderPriority };
        var multiMesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true };
        multiMesh.Mesh = new QuadMesh { Size = Vector2.One, CustomAabb = new Aabb(new Vector3(-1e7f, -1e7f, -1e7f), new Vector3(2e7f, 2e7f, 2e7f)) };
        multiMesh.InstanceCount = MaxFarInstances;
        multiMesh.VisibleInstanceCount = 0;
        _instances = new MultiMeshInstance3D
        {
            Name = "FieldStrikeChannels", Multimesh = multiMesh, MaterialOverride = _material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, IgnoreOcclusionCulling = true, GIMode = GeometryInstance3D.GIModeEnum.Disabled
        };
        AddChild(_instances);
        RenderingServer.InstanceSetIgnoreCulling(_instances.GetInstance(), true);
        if (!_farViewport.AddScaledWorldChild(this))
        {
            _instances.QueueFree(); _instances = null; _material = null;
            return false;
        }
        return true;
    }

    private void UpdateFar()
    {
        if (_weather == null || _farViewport == null || _instances?.Multimesh is not { } multiMesh || _material == null) return;
        _farLive.Clear();
        double time = _weather.CloudSimulationTimeSeconds;
        if (_observer == null || !GodotObject.IsInstanceValid(_observer)) _observer = _farViewport.GetNodeOrNull<Camera3D>(_farViewport.SourceCameraPath);
        var grid = _grid as VoxelChunkGridManager;
        bool haveObserver = _observer != null && grid != null;
        RingCanonicalPosition observerPosition = haveObserver ? grid!.WorldToCanonicalRingPosition64(_observer!.GlobalPosition) : default;
        double circumference = haveObserver ? grid!.GetCanonicalRingCircumferenceMeters64() : 0.0;
        double radius = haveObserver ? circumference / Math.Tau : 0.0;
        if (Enabled && _weather.EnableSharedLightning && haveObserver)
        {
            var events = _weather.ActiveLightningEvents;
            for (int i = 0; i < events.Count && _farLive.Count < MaxFarInstances; i++)
            {
                RingLightningEvent e = events[i];
                double age = time - e.StartTimeSeconds;
                if (!e.FieldStrike || age < 0.0 || age > e.LifeSeconds) continue;
                double da = MegastormModelWrap(e.GroundPosition.AroundMeters - observerPosition.AroundMeters, circumference), dz = e.GroundPosition.AcrossMeters - observerPosition.AcrossMeters;
                if (FarWeight(Math.Sqrt(da * da + dz * dz), true) <= 0.0f) continue;
                _farLive.Add(e);
            }
        }
        float worldScale = _farViewport.CoordinateScale;
        for (int i = 0; i < _farLive.Count; i++)
        {
            RingLightningEvent e = _farLive[i];
            double da = MegastormModelWrap(e.GroundPosition.AroundMeters - observerPosition.AroundMeters, circumference), dz = e.GroundPosition.AcrossMeters - observerPosition.AcrossMeters;
            float weight = FarWeight(Math.Sqrt(da * da + dz * dz), true);
            var mid = new RingCanonicalPosition(e.GroundPosition.AroundMeters, 0.5 * (e.GroundPosition.VerticalMeters + e.CloudPosition.VerticalMeters), e.GroundPosition.AcrossMeters);
            float transmittance = UpperAtmosphereLightning.PathTransmittance(observerPosition, mid, radius, circumference);
            _gain[i] = e.PulseAt(time) * Math.Max(0.0f, e.Energy) * weight * transmittance;
            _progress[i] = e.FieldLeaderProgress(time);
        }
        LightningVisualPolicy.LimitSummed(_gain.AsSpan(0, _farLive.Count));   // reduced flashes: one brightness ceiling over the set
        for (int i = 0; i < _farLive.Count; i++)
        {
            RingLightningEvent e = _farLive[i];
            Vector3 baseWorld = _farViewport.ScaledPoint(e.CloudPosition.AroundMeters, e.CloudPosition.VerticalMeters, e.CloudPosition.AcrossMeters);
            Vector3 contactWorld = _farViewport.ScaledPoint(e.GroundPosition.AroundMeters, e.GroundPosition.VerticalMeters, e.GroundPosition.AcrossMeters);
            Vector3 axis = contactWorld - baseWorld;
            Vector3 an = axis.Length() > 1e-6f ? axis.Normalized() : Vector3.Up;
            Vector3 x = an.Cross(Mathf.Abs(an.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
            multiMesh.SetInstanceTransform(i, new Transform3D(new Basis(x, axis, an.Cross(x)), baseWorld));
            float fanHalf = (float)Math.Min(4000.0, 0.18 * (e.GroundPosition.VerticalMeters - e.CloudPosition.VerticalMeters)) * worldScale;
            multiMesh.SetInstanceCustomData(i, new Color(_gain[i], _progress[i], (e.ShapeSeed & 0xffff) / 65536.0f + e.FieldStrokeIndex(time), fanHalf));
        }
        if (multiMesh.VisibleInstanceCount != _farLive.Count) multiMesh.VisibleInstanceCount = _farLive.Count;
        _material.SetShaderParameter("base_emission", BaseEmission);
        _material.SetShaderParameter("min_angular_radians", Mathf.DegToRad(Mathf.Max(0.01f, MinimumAngularSizeDegrees)));
        Vector3 sunWorld = _sun?.GlobalTransform.Basis.Z.Normalized() ?? Vector3.Right;
        Vector3 sun = _grid != null ? (_grid.GlobalBasis.Orthonormalized().Inverse() * sunWorld).Normalized() : sunWorld;
        _material.SetShaderParameter("sun_direction", sun);
    }

    private static double MegastormModelWrap(double value, double circumference)
    {
        if (circumference <= 0.0) return value;
        double r = value % circumference;
        if (r > 0.5 * circumference) r -= circumference; else if (r < -0.5 * circumference) r += circumference;
        return r;
    }
}
