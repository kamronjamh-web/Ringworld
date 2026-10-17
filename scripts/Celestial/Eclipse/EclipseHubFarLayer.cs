using System;
using Godot;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Celestial.Eclipse;

/// <summary>
/// Eclipse spire to hub beams, far layer (docs/eclipse-false-sun-spec.md E.2, phase E4b): the same 595 ribbons as
/// <see cref="EclipseHubBeamLayer"/> placed once in the absolute scaled far world (ring axis = Y, ring centre and hub at the origin), from
/// <c>ScaledPoint(crown)</c> to the origin. The scaled space never rebases, so the transforms are built once; the layer lives in the far
/// viewport and is drawn under the far cloud layers. Its material is driven by the near layer, which owns the settings and the event time.
/// </summary>
public partial class EclipseHubFarLayer : Node3D
{
    public const string BeamShaderPath = "res://materials/ring_eclipse_hub_beam_far.gdshader";
    /// <summary>Below the far cloud layers (10 shell, 11 canopy, 12-14 clipmap in RingScaledFarViewport), so far clouds blend over far beams.</summary>
    public const int FarRenderPriority = EmitterGridFarLayer.FarRenderPriority;

    private EmitterGridTopology? _topology;
    private MultiMeshInstance3D? _instances;
    private ShaderMaterial? _beamMaterial;
    private Vector3[] _crown = Array.Empty<Vector3>();
    private Transform3D[] _mirror = Array.Empty<Transform3D>();
    private float _coordinateScale = 0.001f;

    public ShaderMaterial? BeamMaterial => _beamMaterial;
    public MultiMeshInstance3D? Instances => _instances;
    public float CoordinateScale => _coordinateScale;
    public int BeamCount => _topology?.HubBeamCount ?? 0;
    public bool IsBuilt => _topology != null && _instances != null;
    /// <summary>MultiMesh writes since construction: the build, plus one custom data pass per timeline. A steady frame adds none.</summary>
    public long TransformWriteCount { get; private set; }
    public long CustomDataWriteCount { get; private set; }

    /// <summary>Builds the beam MultiMesh in scaled space. False when the shader is missing.</summary>
    public bool Build(EmitterGridTopology topology, Func<double, double, double, Vector3> scaledPoint, float coordinateScale, double ringRadiusMeters)
    {
        if (IsBuilt) return true;
        Shader? shader = GD.Load<Shader>(BeamShaderPath);
        if (shader == null) return false;
        // The far viewport sits outside the gameplay root that disables physics interpolation.
        PhysicsInterpolationMode = PhysicsInterpolationModeEnum.Off;
        _topology = topology;
        _coordinateScale = coordinateScale;
        int count = topology.HubBeamCount;
        _crown = new Vector3[count];
        _mirror = new Transform3D[count];
        _beamMaterial = new ShaderMaterial { Shader = shader, RenderPriority = FarRenderPriority };
        RingAtmosphereModel.ConfigureMaterial(_beamMaterial, ringRadiusMeters);
        _beamMaterial.SetShaderParameter("world_scale", coordinateScale);
        _beamMaterial.SetShaderParameter("ring_center", Vector3.Zero);

        var multiMesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true };
        multiMesh.Mesh = EclipseHubBeamLayer.BuildRibbonMesh(out _);
        multiMesh.InstanceCount = count;
        multiMesh.VisibleInstanceCount = count;
        for (int i = 0; i < count; i++)
        {
            var (around, vertical, across) = topology.HubBeamCrown(i);
            Vector3 crown = scaledPoint(around, vertical, across);
            _crown[i] = crown;
            // The hub is the scaled-world origin (spec E.1), so the beam vector is simply -crown.
            var transform = new Transform3D(new Basis(-crown, Vector3.Up, Vector3.Back), crown);
            _mirror[i] = transform;
            multiMesh.SetInstanceTransform(i, transform);
            multiMesh.SetInstanceCustomData(i, EclipseHubBeamMath.EncodeCustom(0d, 0, EclipseHubBeamMath.NeverFireTau, i == 0));
        }
        TransformWriteCount += count;
        CustomDataWriteCount += count;
        _instances = new MultiMeshInstance3D
        {
            Name = "FarHubBeams",
            Multimesh = multiMesh,
            MaterialOverride = _beamMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            IgnoreOcclusionCulling = true,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled
        };
        AddChild(_instances);
        RenderingServer.InstanceSetIgnoreCulling(_instances.GetInstance(), true);
        return true;
    }

    /// <summary>Writes the per-beam custom data (once per timeline; the near layer owns the array).</summary>
    public void WriteCustomData(Color[] custom)
    {
        if (_instances?.Multimesh is not { } beams) return;
        int count = Math.Min(custom.Length, beams.InstanceCount);
        for (int i = 0; i < count; i++) beams.SetInstanceCustomData(i, custom[i]);
        CustomDataWriteCount += count;
    }

    public Vector3 GetScaledCrown(int beam) => _crown[beam];
    /// <summary>Scaled-space transform written for the beam (CPU mirror): origin = crown, basis X = origin - crown.</summary>
    public Transform3D GetBeamTransform(int beam) => beam >= 0 && beam < _mirror.Length ? _mirror[beam] : Transform3D.Identity;
}
