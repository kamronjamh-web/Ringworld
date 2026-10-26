using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Lightning;

/// <summary>
/// Red sprites, blue jets and gigantic jets above storm tops (storm revamp S4 WO-E). Discharges are a pure function of the
/// shared lightning events (<see cref="UpperAtmosphereLightning"/>), drawn additively in the scaled far viewport below the far
/// cloud layers. The near world has no pass: they are only visible as far backdrop (across the arch), where their size is
/// bounded below by a minimum angular size.
/// </summary>
public partial class RingUpperAtmosphereLightningLayer : Node3D
{
    public const string ShaderPath = "res://materials/ring_upper_atmosphere_lightning.gdshader";
    public const int MaxInstances = 16;
    /// <summary>Above the far cloud layers (10 shell, 11 canopy, 12-14 clipmap in RingScaledFarViewport): discharges sit above the cloud tops, so they add over the clouds rather than being blended under them.</summary>
    public const int FarRenderPriority = 20;

    [Export] public bool Enabled { get; set; } = true;
    [Export] public NodePath WeatherPath { get; set; } = new("../RingVolumetricClouds");
    [Export] public NodePath FarViewportPath { get; set; } = new("../ScaledFarViewport");
    [Export] public NodePath SunLightPath { get; set; } = new("../StarLight");
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    /// <summary>Test hook: a value of zero or more replaces the user chance scale (the capture harness forces discharges); negative uses the setting.</summary>
    [Export] public float ChanceScaleOverride { get; set; } = -1.0f;
    /// <summary>Minimum angular radius in degrees (design: 0.5 degree minimum angular size).</summary>
    [Export] public float MinimumAngularSizeDegrees { get; set; } = 0.5f;
    [Export] public float BaseEmission { get; set; } = 700.0f;

    private RingVolumetricCloudLayer? _weather;
    private RingScaledFarViewport? _farViewport;
    private DirectionalLight3D? _sun;
    private Node3D? _grid;
    private Camera3D? _observer;
    private MultiMeshInstance3D? _instances;
    private ShaderMaterial? _material;
    private double _bindRetry;
    private readonly List<UpperAtmosphereSprite> _live = new();
    private readonly float[] _gain = new float[MaxInstances];
    private readonly float[] _transmittance = new float[MaxInstances];

    public bool IsBound => _instances != null;
    public int LiveCount => _live.Count;
    public IReadOnlyList<UpperAtmosphereSprite> LiveSprites => _live;
    public ShaderMaterial? Material => _material;
    /// <summary>Air transmittance of the live discharge at <paramref name="index"/> (1 when no observer is available).</summary>
    public float TransmittanceOf(int index) => index >= 0 && index < _live.Count ? _transmittance[index] : 1.0f;

    public override void _Ready()
    {
        // The far viewport sits outside the gameplay root that disables interpolation; instances are rewritten every frame.
        PhysicsInterpolationMode = PhysicsInterpolationModeEnum.Off;
        _weather = GetNodeOrNull<RingVolumetricCloudLayer>(WeatherPath);
        _farViewport = GetNodeOrNull<RingScaledFarViewport>(FarViewportPath);
        _sun = GetNodeOrNull<DirectionalLight3D>(SunLightPath);
        _grid = GetNodeOrNull<Node3D>(GridManagerPath);
    }

    public override void _Process(double delta)
    {
        if (_instances == null)
        {
            if (!Enabled || (_bindRetry -= delta) > 0.0) return;
            _bindRetry = 0.5;
            Bind();
            if (_instances == null) return;
        }
        Update();
    }

    private bool Bind()
    {
        if (_farViewport == null || !GodotObject.IsInstanceValid(_farViewport) || !_farViewport.HasScaledWorld) return false;
        Shader? shader = GD.Load<Shader>(ShaderPath);
        if (shader == null) return false;
        _material = new ShaderMaterial { Shader = shader, RenderPriority = FarRenderPriority };
        var multiMesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true };
        multiMesh.Mesh = new QuadMesh { Size = Vector2.One, CustomAabb = new Aabb(new Vector3(-1e7f, -1e7f, -1e7f), new Vector3(2e7f, 2e7f, 2e7f)) };
        multiMesh.InstanceCount = MaxInstances;
        multiMesh.VisibleInstanceCount = 0;
        _instances = new MultiMeshInstance3D
        {
            Name = "UpperAtmosphereDischarges", Multimesh = multiMesh, MaterialOverride = _material,
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

    private void Update()
    {
        if (_weather == null || _farViewport == null || _instances?.Multimesh is not { } multiMesh || _material == null) return;
        _live.Clear();
        double time = _weather.CloudSimulationTimeSeconds;
        float? scale = ChanceScaleOverride >= 0.0f ? ChanceScaleOverride : null;
        if (Enabled && _weather.EnableSharedLightning)
        {
            var events = _weather.ActiveLightningEvents;
            for (int i = 0; i < events.Count && _live.Count < MaxInstances; i++)
            {
                RingLightningEvent e = events[i];
                if (UpperAtmosphereLightning.TryDerive(e, _weather.CloudTopMeters, out UpperAtmosphereSprite sprite, scale)
                    && UpperAtmosphereLightning.Level(sprite, time) > 0.0005f) _live.Add(sprite);
            }
        }
        float worldScale = _farViewport.CoordinateScale;
        if (_observer == null || !GodotObject.IsInstanceValid(_observer)) _observer = _farViewport.GetNodeOrNull<Camera3D>(_farViewport.SourceCameraPath);
        var grid = _grid as VoxelChunkGridManager;
        bool haveObserver = _observer != null && grid != null;
        RingCanonicalPosition observerPosition = haveObserver ? grid!.WorldToCanonicalRingPosition64(_observer!.GlobalPosition) : default;
        double circumference = haveObserver ? grid!.GetCanonicalRingCircumferenceMeters64() : 0.0;
        for (int i = 0; i < _live.Count; i++)
        {
            UpperAtmosphereSprite s = _live[i];
            // Air between the observer and the discharge dims it like it dims far cloud flashes.
            _transmittance[i] = haveObserver
                ? UpperAtmosphereLightning.PathTransmittance(observerPosition, s.BasePosition, circumference / Math.Tau, circumference) : 1.0f;
            _gain[i] = UpperAtmosphereLightning.Level(s, time) * s.Energy * _transmittance[i];
        }
        // Reduced flashes: overlapping discharges share one brightness ceiling.
        LightningVisualPolicy.LimitSummed(_gain.AsSpan(0, _live.Count));
        for (int i = 0; i < _live.Count; i++)
        {
            UpperAtmosphereSprite s = _live[i];
            Vector3 position = _farViewport.ScaledPoint(s.BasePosition.AroundMeters, s.BasePosition.VerticalMeters, s.BasePosition.AcrossMeters);
            multiMesh.SetInstanceTransform(i, new Transform3D(Basis.Identity, position));
            multiMesh.SetInstanceCustomData(i, new Color((float)(int)s.Kind, _gain[i], s.HeightMeters * worldScale, s.WidthMeters * worldScale));
        }
        if (multiMesh.VisibleInstanceCount != _live.Count) multiMesh.VisibleInstanceCount = _live.Count;
        // Sprite energy already carries the reduced-flash peak scale; the shader multiplies by emission.
        _material.SetShaderParameter("base_emission", BaseEmission);
        _material.SetShaderParameter("min_angular_radians", Mathf.DegToRad(Mathf.Max(0.01f, MinimumAngularSizeDegrees)));
        Vector3 sunWorld = _sun?.GlobalTransform.Basis.Z.Normalized() ?? Vector3.Right;
        Vector3 sun = _grid != null ? (_grid.GlobalBasis.Orthonormalized().Inverse() * sunWorld).Normalized() : sunWorld;
        _material.SetShaderParameter("sun_direction", sun);
    }
}
