using System;
using Godot;
using RingworldTechDemo.Poi;

namespace RingworldTechDemo.ContainmentField;

/// <summary>
/// Containment field far strike layer (docs/containment-field-design.md 3.10.3, WO4): the light of ripples that reach past the main camera's far fade band and of far-list strikes
/// (> 350 km), drawn in the scaled far world (materials/ring_containment_strike_far.gdshader). One MultiMesh quad per instance: transform = (T_around P, T_arch P, N) at
/// <c>ScaledPoint(contact)</c>, custom data = (strike level, Rc, crawl front, seed x 256 + biasByte), colour = wave (A, rho, Rmax, flash). The scaled space never rebases, so instances need
/// no anchor handling. Driven by <see cref="RingContainmentField"/> (RingContainmentField.FarStrikes.cs). Modelled on <see cref="EmitterFieldShotFarLayer"/>.
/// </summary>
public partial class RingContainmentFarStrikeLayer : Node3D
{
    public const string ShaderPath = "res://materials/ring_containment_strike_far.gdshader";
    /// <summary>Below the far cloud layers (10 shell, 11 canopy, 12-14 clipmap in RingScaledFarViewport), so far clouds blend over the far light.</summary>
    public const int FarRenderPriority = EmitterGridFarLayer.FarRenderPriority;
    public const int InitialCapacity = 16;

    private MultiMeshInstance3D? _instances;
    private ShaderMaterial? _material;
    private int _capacity;
    private float _coordinateScale = 0.001f;

    public bool IsBuilt => _instances != null;
    public ShaderMaterial? Material => _material;
    public MultiMeshInstance3D? Instances => _instances;
    public float CoordinateScale => _coordinateScale;
    public int DrawnCount { get; private set; }
    public int Capacity => _capacity;

    /// <summary>Builds the MultiMesh in scaled space. False when the shader is missing.</summary>
    public bool Build(float coordinateScale)
    {
        if (IsBuilt) return true;
        Shader? shader = GD.Load<Shader>(ShaderPath);
        if (shader == null) return false;
        // The far viewport sits outside the gameplay root that disables physics interpolation.
        PhysicsInterpolationMode = PhysicsInterpolationModeEnum.Off;
        _coordinateScale = coordinateScale;
        _material = new ShaderMaterial { Shader = shader, RenderPriority = FarRenderPriority };
        _material.SetShaderParameter("world_scale", coordinateScale);
        _capacity = InitialCapacity;
        var multiMesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, UseCustomData = true };
        multiMesh.Mesh = new QuadMesh { Size = new Vector2(2.0f, 2.0f) };
        multiMesh.InstanceCount = _capacity;
        multiMesh.VisibleInstanceCount = 0;
        _instances = new MultiMeshInstance3D
        {
            Name = "FarContainmentStrikes",
            Multimesh = multiMesh,
            MaterialOverride = _material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            IgnoreOcclusionCulling = true,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
        };
        AddChild(_instances);
        RenderingServer.InstanceSetIgnoreCulling(_instances.GetInstance(), true);
        return true;
    }

    /// <summary>The main camera's far fade band in metres (ContainmentRipplePatches.FarFade).</summary>
    public void SetFarFade(Vector2 band) => _material?.SetShaderParameter("cf_far_fade", band);

    /// <summary>Starts a frame's writes: makes room for <paramref name="count"/> instances (the capacity doubles) and resets the drawn count.</summary>
    public void Begin(int count)
    {
        if (_instances?.Multimesh is not { } mm) return;
        if (count > _capacity)
        {
            while (_capacity < count) _capacity *= 2;
            mm.InstanceCount = _capacity;
        }
        DrawnCount = 0;
    }

    /// <summary>Writes the next instance.</summary>
    public void Add(in Transform3D transform, Color custom, Color wave)
    {
        if (_instances?.Multimesh is not { } mm || DrawnCount >= _capacity) return;
        int i = DrawnCount++;
        mm.SetInstanceTransform(i, transform);
        mm.SetInstanceCustomData(i, custom);
        mm.SetInstanceColor(i, wave);
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
}
