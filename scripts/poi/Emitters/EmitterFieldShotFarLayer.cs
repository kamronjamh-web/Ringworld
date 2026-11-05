using System;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Poi;

/// <summary>
/// Emitter field shots, far layer: the same orbs as <see cref="EmitterFieldShotLayer"/> placed in the scaled far world (<c>ScaledPoint</c> of the evaluated
/// canonical point), drawn ring-wide under the far cloud layers. One MultiMesh instance per drawn shot (origin = the orb centre in scaled units, custom data
/// = orb envelope, flare, spread, energy). The scaled space never rebases, so instances need no anchor handling. Its material is driven by the near layer,
/// which owns the settings and the layer uniforms (modelled on <c>EclipseHubFarLayer</c>).
/// </summary>
public partial class EmitterFieldShotFarLayer : Node3D
{
    public const string ShaderPath = "res://materials/ring_emitter_field_shot_far.gdshader";
    /// <summary>Below the far cloud layers (10 shell, 11 canopy, 12-14 clipmap in RingScaledFarViewport), so far clouds blend over far orbs.</summary>
    public const int FarRenderPriority = EmitterGridFarLayer.FarRenderPriority;
    public const int InitialCapacity = 64;

    private MultiMeshInstance3D? _instances;
    private ShaderMaterial? _material;
    private int _capacity;
    private Vector3[] _origins = Array.Empty<Vector3>();
    private Color[] _custom = Array.Empty<Color>();
    private float _coordinateScale = 0.001f;

    public bool IsBuilt => _instances != null;
    public ShaderMaterial? Material => _material;
    public MultiMeshInstance3D? Instances => _instances;
    public float CoordinateScale => _coordinateScale;
    public int DrawnCount { get; private set; }
    public int Capacity => _capacity;

    /// <summary>Builds the MultiMesh in scaled space. False when the shader is missing.</summary>
    public bool Build(float coordinateScale, double ringRadiusMeters)
    {
        if (IsBuilt) return true;
        Shader? shader = GD.Load<Shader>(ShaderPath);
        if (shader == null) return false;
        // The far viewport sits outside the gameplay root that disables physics interpolation.
        PhysicsInterpolationMode = PhysicsInterpolationModeEnum.Off;
        _coordinateScale = coordinateScale;
        _material = new ShaderMaterial { Shader = shader, RenderPriority = FarRenderPriority };
        RingAtmosphereModel.ConfigureMaterial(_material, ringRadiusMeters);
        _material.SetShaderParameter("world_scale", coordinateScale);
        _material.SetShaderParameter("ring_center", Vector3.Zero);
        _capacity = InitialCapacity;
        _origins = new Vector3[_capacity];
        _custom = new Color[_capacity];
        var multiMesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true };
        multiMesh.Mesh = EmitterFieldShotLayer.BuildOrbMesh();
        multiMesh.InstanceCount = _capacity;
        multiMesh.VisibleInstanceCount = 0;
        _instances = new MultiMeshInstance3D
        {
            Name = "FarFieldShotOrbs",
            Multimesh = multiMesh,
            MaterialOverride = _material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            IgnoreOcclusionCulling = true,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled
        };
        AddChild(_instances);
        RenderingServer.InstanceSetIgnoreCulling(_instances.GetInstance(), true);
        return true;
    }

    /// <summary>Starts a frame's writes: makes room for <paramref name="count"/> instances (the capacity doubles) and resets the drawn count.</summary>
    public void Begin(int count)
    {
        if (_instances?.Multimesh is not { } mm) return;
        if (count > _capacity)
        {
            while (_capacity < count) _capacity *= 2;
            Array.Resize(ref _origins, _capacity);
            Array.Resize(ref _custom, _capacity);
            mm.InstanceCount = _capacity;
        }
        DrawnCount = 0;
    }

    /// <summary>Writes the next instance (scaled-space origin and (envelope, flare, spread, energy)).</summary>
    public void Add(Vector3 scaledOrigin, Color custom)
    {
        if (_instances?.Multimesh is not { } mm || DrawnCount >= _capacity) return;
        int i = DrawnCount++;
        _origins[i] = scaledOrigin;
        _custom[i] = custom;
        mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, scaledOrigin));
        mm.SetInstanceCustomData(i, custom);
    }

    /// <summary>Ends a frame's writes: shows exactly the instances added.</summary>
    public void End()
    {
        if (_instances?.Multimesh is { } mm && mm.VisibleInstanceCount != DrawnCount) mm.VisibleInstanceCount = DrawnCount;
    }

    /// <summary>Hides everything (nothing drawn).</summary>
    public void Clear()
    {
        DrawnCount = 0;
        if (_instances?.Multimesh is { } mm && mm.VisibleInstanceCount != 0) mm.VisibleInstanceCount = 0;
    }

    /// <summary>CPU mirror of an instance written this frame: scaled-space origin and custom data.</summary>
    public (Vector3 Origin, Color Custom) GetInstance(int index)
        => index >= 0 && index < DrawnCount ? (_origins[index], _custom[index]) : (Vector3.Zero, default);
}
