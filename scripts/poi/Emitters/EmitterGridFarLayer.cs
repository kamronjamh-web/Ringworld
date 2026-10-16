using System;
using Godot;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Poi;

/// <summary>
/// Emitter relay grid, far layer (docs/emitter-grid-climate-machines-spec.md G4): the same 600 links and head sprites as
/// <see cref="EmitterGridBeamLayer"/> placed once in the absolute scaled far world (ring axis = Y, ring centre at the origin).
/// The scaled space never rebases, so the transforms are built once; the layer lives in the far viewport and is drawn under the
/// far cloud layers. Its materials are driven by the near layer, which owns the settings.
/// </summary>
public partial class EmitterGridFarLayer : Node3D
{
    public const string BeamShaderPath = "res://materials/ring_emitter_grid_beam_far.gdshader";
    public const string HeadShaderPath = "res://materials/ring_emitter_grid_head_far.gdshader";
    /// <summary>Below the far cloud layers (10 shell, 11 canopy, 12-14 clipmap in RingScaledFarViewport), so far clouds blend over far beams.</summary>
    public const int FarRenderPriority = 5;

    private EmitterGridTopology? _topology;
    private MultiMeshInstance3D? _linkInstances, _headInstances;
    private ShaderMaterial? _beamMaterial, _headMaterial;
    private Vector3[] _endA = Array.Empty<Vector3>(), _endB = Array.Empty<Vector3>();
    private Transform3D[] _mirror = Array.Empty<Transform3D>();
    private readonly Vector3[] _headPositions = new Vector3[EmitterGridBeamLayer.MaxHeadSprites];
    private int _headCount;
    private float _coordinateScale = 0.001f;

    public ShaderMaterial? BeamMaterial => _beamMaterial;
    public ShaderMaterial? HeadMaterial => _headMaterial;
    public MultiMeshInstance3D? LinkInstances => _linkInstances;
    public MultiMeshInstance3D? HeadInstances => _headInstances;
    public float CoordinateScale => _coordinateScale;
    public int LinkCount => _topology?.LinkCount ?? 0;
    public int HeadCount => _headCount;
    public bool IsBuilt => _topology != null && _linkInstances != null;

    /// <summary>Builds the link and head MultiMeshes in scaled space. False when a shader is missing.</summary>
    public bool Build(EmitterGridTopology topology, Func<double, double, double, Vector3> scaledPoint, float coordinateScale, double ringRadiusMeters)
    {
        if (IsBuilt) return true;
        Shader? beamShader = GD.Load<Shader>(BeamShaderPath), headShader = GD.Load<Shader>(HeadShaderPath);
        if (beamShader == null || headShader == null) return false;
        // Heads are rewritten from _Process; the far viewport sits outside the gameplay root that disables interpolation.
        PhysicsInterpolationMode = PhysicsInterpolationModeEnum.Off;
        _topology = topology;
        _coordinateScale = coordinateScale;
        int count = topology.LinkCount;
        _endA = new Vector3[count]; _endB = new Vector3[count];
        _mirror = new Transform3D[count];
        _beamMaterial = CreateMaterial(beamShader, ringRadiusMeters, coordinateScale);
        _headMaterial = CreateMaterial(headShader, ringRadiusMeters, coordinateScale);

        ArrayMesh mesh = EmitterGridBeamLayer.BuildRibbonMesh(out _);
        var links = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true };
        links.Mesh = mesh;
        links.InstanceCount = count;
        links.VisibleInstanceCount = count;
        for (int i = 0; i < count; i++)
        {
            EmitterLink link = topology[i];
            var (fa, fv, fc) = topology.RelayNode(link.FromSite);
            var (ta, tv, tc) = topology.RelayNode(link.ToSite);
            Vector3 a = scaledPoint(fa, fv, fc), b = scaledPoint(ta, tv, tc);
            _endA[i] = a; _endB[i] = b;
            var transform = new Transform3D(new Basis(b - a, Vector3.Up, Vector3.Back), a);
            _mirror[i] = transform;
            links.SetInstanceTransform(i, transform);
            links.SetInstanceCustomData(i, new Color((float)link.P0, (float)link.P1, link.Column, (float)(byte)link.Kind));
        }
        _linkInstances = AddInstance("FarGridLinks", links, _beamMaterial);

        var heads = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true };
        heads.Mesh = new QuadMesh { Size = Vector2.One, CustomAabb = BigAabb() };
        heads.InstanceCount = EmitterGridBeamLayer.MaxHeadSprites;
        heads.VisibleInstanceCount = 0;
        for (int i = 0; i < EmitterGridBeamLayer.MaxHeadSprites; i++) heads.SetInstanceCustomData(i, new Color(1f, 0f, 0f, 0f));
        _headInstances = AddInstance("FarGridHeads", heads, _headMaterial);
        return true;
    }

    private static ShaderMaterial CreateMaterial(Shader shader, double ringRadiusMeters, float coordinateScale)
    {
        var material = new ShaderMaterial { Shader = shader, RenderPriority = FarRenderPriority };
        RingAtmosphereModel.ConfigureMaterial(material, ringRadiusMeters);
        material.SetShaderParameter("world_scale", coordinateScale);
        material.SetShaderParameter("ring_center", Vector3.Zero);
        return material;
    }

    private MultiMeshInstance3D AddInstance(string name, MultiMesh multiMesh, Material material)
    {
        var instance = new MultiMeshInstance3D
        {
            Name = name,
            Multimesh = multiMesh,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            IgnoreOcclusionCulling = true,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled
        };
        AddChild(instance);
        RenderingServer.InstanceSetIgnoreCulling(instance.GetInstance(), true);
        return instance;
    }

    private static Aabb BigAabb() => new(new Vector3(-1e7f, -1e7f, -1e7f), new Vector3(2e7f, 2e7f, 2e7f));

    public Vector3 GetScaledEndpointA(int link) => _endA[link];
    public Vector3 GetScaledEndpointB(int link) => _endB[link];
    /// <summary>Scaled-space transform written for the link (CPU mirror).</summary>
    public Transform3D GetLinkTransform(int link) => link >= 0 && link < _mirror.Length ? _mirror[link] : Transform3D.Identity;
    public Vector3 GetHeadPosition(int index) => _headPositions[index];

    /// <summary>Places head sprite i at A_s + (B_s - A_s) * fractions[i] on links[i] (scaled space).</summary>
    public void WriteHeads(ReadOnlySpan<int> links, ReadOnlySpan<double> fractions, int count)
    {
        if (_headInstances?.Multimesh is not { } heads) return;
        count = Math.Clamp(count, 0, Math.Min(EmitterGridBeamLayer.MaxHeadSprites, Math.Min(links.Length, fractions.Length)));
        for (int i = 0; i < count; i++)
        {
            int link = links[i];
            Vector3 position = _endA[link] + (_endB[link] - _endA[link]) * (float)fractions[i];
            _headPositions[i] = position;
            heads.SetInstanceTransform(i, new Transform3D(Basis.Identity, position));
        }
        _headCount = count;
        if (heads.VisibleInstanceCount != count) heads.VisibleInstanceCount = count;
    }
}
