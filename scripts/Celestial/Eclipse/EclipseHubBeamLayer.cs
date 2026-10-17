using System;
using System.Diagnostics;
using System.Globalization;
using Godot;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Celestial.Eclipse;

/// <summary>
/// Eclipse spire to hub beams, main-camera layer (docs/eclipse-false-sun-spec.md E.2, phase E4b): one MultiMesh of 595 ribbons (origin = the beam origin of
/// <see cref="EclipseBeamSettings.BeamOrigin"/>: the bore iris by default, 200 m under the pad, or the crown or the reactor exhaust top,
/// basis X = hub - crown, custom data = (p_site, column, fire tau, kind)) plus the depth-writing core as the halo material's next_pass.
/// All beam state lives on the GPU: the custom data is written once per timeline (an eclipse or a timeline-setting change) and every frame
/// only a handful of uniforms change (event time and the camera), so a steady frame writes nothing per beam. The endpoint transforms are
/// rebuilt absolutely on every floating-origin anchor change, never applied as deltas. The layer exists only from shortly before the main
/// fire until the collapse has cut the beams; outside an eclipse it is not built, not drawn and costs one comparison per frame.
/// The far counterpart is <see cref="EclipseHubFarLayer"/>.
/// </summary>
public partial class EclipseHubBeamLayer : Node3D
{
    public const string BeamShaderPath = "res://materials/ring_eclipse_hub_beam.gdshader";
    public const string CoreShaderPath = "res://materials/ring_eclipse_hub_beam_core.gdshader";
    public const int RibbonSubSegments = 16;
    /// <summary>Tweaks T3: the ribbon mesh ends with one orb billboard quad (4 vertices, 6 indices) marked by UV2 = (-1, -1).</summary>
    public const int OrbQuadVertices = 4, OrbQuadIndices = 6;
    /// <summary>
    /// Sub-segment boundaries are f_k = (k / N)^exponent. A hub beam is 5,000 km long, so uniform sub-segments of 312 km would be far too coarse
    /// inside the 700 km near range; cubing packs them toward the crown (the first is 1.2 km, the 8th ends at 625 km).
    /// </summary>
    public const float RibbonSegmentExponent = 3f;
    /// <summary>
    /// The halo draws after the near water (see <see cref="EmitterGridBeamLayer.NearWaterRenderPriority"/>), and the depth-writing core after
    /// the halo, or the halo would depth-fight the depth the core writes. Same values as the relay grid.
    /// </summary>
    public const int HaloRenderPriority = EmitterGridBeamLayer.HaloRenderPriority, CoreRenderPriority = EmitterGridBeamLayer.CoreRenderPriority;
    /// <summary>The layer is built this long before the main fire so the first lit frame does not pay for the build.</summary>
    public const double PrebuildSeconds = 10d;
    private const float NoHandoff = 1e12f;
    /// <summary>Phase E4e (spec E.6): at most this many weather queries per eclipse (one per near spire, nearest first).</summary>
    public const int MaxWeatherQueries = 30;
    /// <summary>Phase E4e (spec E.7): crown glow lane values: full while the beam fires, then the ember it settles to at the hand-off (the main spire keeps a stronger one).</summary>
    public const float CrownGlowFire = 1f, CrownGlowEmberRegular = 0.3f, CrownGlowEmberMain = 0.7f;
    private const double CloudScanSeconds = 0.25d;

    [Export] public bool Enabled { get => _enabled; set { _enabled = value; UpdateVisibility(); } }
    [Export] public bool AutoBind { get; set; } = true;
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath GridLayerPath { get; set; } = new("../EmitterGrid");
    [Export] public NodePath DirectorPath { get; set; } = new("../EclipseEvent");
    [Export] public NodePath GlobalTerrainPath { get; set; } = new("../GlobalTerrainLod3");
    [Export] public NodePath FarViewportPath { get; set; } = new("../ScaledFarViewport");
    [Export] public NodePath WeatherPath { get; set; } = new("../RingVolumetricClouds");
    [Export] public NodePath StandInsPath { get; set; } = new("../EmitterStandIns");
    [Export] public NodePath BoresPath { get; set; } = new("../EmitterBores");

    private bool _enabled = true;
    private bool _undergroundHidden;
    private bool _lit;
    private VoxelChunkGridManager? _grid;
    private EmitterGridBeamLayer? _gridLayer;
    private EclipseEventDirector? _director;
    private RingTerrainGlobalLod? _globalTerrain;
    private RingScaledFarViewport? _farViewport;
    private EmitterGridTopology? _topology;
    private EclipseEventTimeline? _boundTimeline;
    private EclipseBeamSettings _beams = new();
    private MultiMeshInstance3D? _instances;
    private ShaderMaterial? _beamMaterial, _coreMaterial;
    private EclipseHubFarLayer? _farLayer;
    private double _farBindRetry;
    private RingVolumetricCloudLayer? _weather;
    private EmitterStandInLayer? _standIns;
    private EmitterBoreLayer? _bores;
    private Color[] _cloud = Array.Empty<Color>();
    private double[] _cloudDeckBase = Array.Empty<double>(), _cloudDeckTop = Array.Empty<double>(); // absolute altitudes behind _cloud (re-encoded when the beam origin moves)
    private EmitterBeamOrigin _cloudOrigin = EmitterBeamOrigin.Base, _originApplied = EmitterBeamOrigin.Base;
    private Func<double, double, double, Vector3>? _farScaledPoint;
    private bool[] _queried = Array.Empty<bool>();
    private int _cloudEclipse = int.MinValue;
    private double _cloudScanDelay;
    private int[] _beamOfSite = Array.Empty<int>();
    private GlowEvent[] _glowEvents = Array.Empty<GlowEvent>();
    private float[] _glowWritten = Array.Empty<float>();
    private int _glowCursor, _glowPending;
    private double _glowLastTau = double.NaN;
    private int _standInsBindRevision = -1;
    private int[] _glowTouched = Array.Empty<int>();
    private bool[] _glowSeen = Array.Empty<bool>();
    private float[] _glowTarget = Array.Empty<float>();
    private bool _standInSettingsApplied;
    private readonly record struct GlowEvent(double Tau, int Beam, float Value);
    private Vector3D[] _origins = Array.Empty<Vector3D>();
    private Transform3D[] _mirror = Array.Empty<Transform3D>();
    private Color[] _custom = Array.Empty<Color>();
    private Transform3D _worldToLayer = Transform3D.Identity;
    private Transform3D _lastGridGlobal, _lastLayerGlobal;
    private Vector3D _hubWorld;
    private EclipseHubBeamUniforms _uniforms;
    private double _tau = double.NaN;
    private readonly Vector4[] _flashFronts = new Vector4[EmitterGridBeamLayer.MaxShaderFronts];
    private int _flashFrontCount;
    private bool _flashCleared;
    private EmitterGridPulseClock? _flashClock;
    private float _effectiveMax = 700_000f, _appliedMax = -1f, _appliedFade = -1f, _appliedNear = -1f, _farAppliedNear = -1f;
    private Vector3 _appliedRingCenter = new(float.NaN, 0f, 0f);
    private Vector2 _handoffBand = new(NoHandoff, 2f * NoHandoff), _appliedBand = new(float.NaN, 0f);

    public bool IsBound => _topology != null && _instances != null;
    public bool UndergroundHold { get; set; }
    public bool UndergroundHidden => _undergroundHidden;
    /// <summary>Number of full endpoint rebuilds (bind plus every floating-origin anchor change).</summary>
    public int RebuildCount { get; private set; }
    public double LastRebuildMs { get; private set; }
    public int GeometryRevision { get; private set; }
    /// <summary>Number of times the per-beam custom data was written (one pass per timeline, never per frame).</summary>
    public int TimelineBindCount { get; private set; }
    /// <summary>MultiMesh writes since construction (near layer): transforms and custom data. A steady frame adds none (spec E.2).</summary>
    public long TransformWriteCount { get; private set; }
    public long CustomDataWriteCount { get; private set; }
    /// <summary>Phase E4e: MultiMesh COLOR writes (the in-cloud data): 595 at the bind, then one per queried near spire, never per frame.</summary>
    public long ColorWriteCount { get; private set; }
    /// <summary>Phase E4e: weather queries made for the current eclipse (at most <see cref="MaxWeatherQueries"/>).</summary>
    public int WeatherQueryCount { get; private set; }
    /// <summary>Test/tool hook: replaces the weather layer's <c>QueryLocalWeatherAtTime</c>. Null (default) uses the layer found at <see cref="WeatherPath"/>.</summary>
    public Func<RingCanonicalPosition, double, RingVolumetricCloudLayer.LocalWeatherState>? WeatherQueryOverride { get; set; }
    /// <summary>Test/tool hook: replaces the near-layer range (700 km by default) that selects the spires whose weather is queried.</summary>
    public double? CloudNearRangeOverrideMeters { get; set; }
    /// <summary>Resolves the collaborator nodes (director, grid, weather, stand-ins, bores) now; the layer does it every frame when it processes itself.</summary>
    public void ResolveCollaborators() => ResolveNodes();
    /// <summary>Phase E4e: stand-in crown glow writes made by this layer (3 per beam per eclipse: fire, hand-off, cut).</summary>
    public long CrownGlowWriteCount { get; private set; }
    public int BeamCount => _topology?.HubBeamCount ?? 0;
    public int RibbonVertexCount { get; private set; }
    public EmitterGridTopology? Topology => _topology;
    public EclipseEventTimeline? BoundTimeline => _boundTimeline;
    public MultiMeshInstance3D? Instances => _instances;
    public ShaderMaterial? BeamMaterial => _beamMaterial;
    public ShaderMaterial? CoreMaterial => _coreMaterial;
    public EclipseHubFarLayer? FarLayer => FarLayerValid ? _farLayer : null;
    public bool FarLayerActive => FarLayerValid && _beams.FarLayerEnabled && _farLayer!.Visible;
    public EclipseBeamSettings Beams => _beams;
    public EclipseHubBeamUniforms Uniforms => _uniforms;
    public double EventTau => _tau;
    public Vector2 HandoffBandMeters => _handoffBand;
    public Vector3D HubWorld => _hubWorld;
    private bool FarLayerValid => _farLayer != null && GodotObject.IsInstanceValid(_farLayer);

    public override void _Ready()
    {
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        UpdateVisibility();
        if (!AutoBind) SetProcess(false);
    }

    public override void _ExitTree() => Unbind(true);

    public override void _Process(double delta)
    {
        ResolveNodes();
        if (_director == null || !GodotObject.IsInstanceValid(_director)) return;
        EclipseEventTimeline? timeline = _director.Engine.Timeline;
        EclipseEventState state = _director.State;
        EclipseBeamSettings beams = _director.Engine.Settings.Beams;
        if (!ShouldBuild(timeline, state, beams))
        {
            if (IsBound) Unbind();
            return;
        }
        // The settings first: the build places the beams from the beam origin setting.
        if (!ReferenceEquals(beams, _beams) && beams != _beams) ApplyBeamSettings(beams);
        if (!IsBound)
        {
            if (_grid == null || _gridLayer == null || !_gridLayer.IsBound || _gridLayer.Topology == null) return;
            if (!Bind(_grid, _gridLayer.Topology)) return;
        }
        if (!ReferenceEquals(timeline, _boundTimeline)) BindTimeline(timeline);
        if (_grid != null && (!_grid.GlobalTransform.IsEqualApprox(_lastGridGlobal) || !GlobalTransform.IsEqualApprox(_lastLayerGlobal)))
            Rebuild(_grid.CaptureLodGeometrySnapshot());
        ApplyEventTime(state.Tau, state.MainBeamLit, delta, _director.Engine.GridTimeSeconds);
    }

    private void ResolveNodes()
    {
        if (_grid == null || !GodotObject.IsInstanceValid(_grid)) _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        if (_gridLayer == null || !GodotObject.IsInstanceValid(_gridLayer)) _gridLayer = GetNodeOrNull<EmitterGridBeamLayer>(GridLayerPath);
        if (_director == null || !GodotObject.IsInstanceValid(_director)) _director = GetNodeOrNull<EclipseEventDirector>(DirectorPath);
        if (_globalTerrain == null || !GodotObject.IsInstanceValid(_globalTerrain)) _globalTerrain = GetNodeOrNull<RingTerrainGlobalLod>(GlobalTerrainPath);
        if (_farViewport == null || !GodotObject.IsInstanceValid(_farViewport)) _farViewport = GetNodeOrNull<RingScaledFarViewport>(FarViewportPath);
        if (_weather == null || !GodotObject.IsInstanceValid(_weather)) _weather = GetNodeOrNull<RingVolumetricCloudLayer>(WeatherPath);
        if (_standIns == null || !GodotObject.IsInstanceValid(_standIns)) { _standIns = GetNodeOrNull<EmitterStandInLayer>(StandInsPath); _standInSettingsApplied = false; _standInsBindRevision = -1; }
        if (_bores == null || !GodotObject.IsInstanceValid(_bores)) _bores = GetNodeOrNull<EmitterBoreLayer>(BoresPath);
    }

    /// <summary>True from <see cref="PrebuildSeconds"/> before the main fire until the collapse has cut the beams, while the beams are enabled.</summary>
    public static bool ShouldBuild(EclipseEventTimeline? timeline, in EclipseEventState state, EclipseBeamSettings beams)
        => beams.BeamsEnabled && timeline != null && timeline.Active && state.Active
            && state.Tau >= timeline.TauMainFire - PrebuildSeconds && state.Tau < timeline.TauImplosion + timeline.BeamRetract + 1d;

    // ---------------------------------------------------------------- build

    /// <summary>Creates the beam MultiMesh and its materials for the topology and rebuilds the endpoint transforms. False when a shader is missing.</summary>
    public bool Bind(VoxelChunkGridManager grid, EmitterGridTopology topology)
    {
        if (IsBound) return true;
        Shader? beamShader = GD.Load<Shader>(BeamShaderPath), coreShader = GD.Load<Shader>(CoreShaderPath);
        if (beamShader == null || coreShader == null) return false;
        _grid = grid;
        _topology = topology;
        int count = topology.HubBeamCount;
        _origins = new Vector3D[count];
        _mirror = new Transform3D[count];
        _custom = new Color[count];
        for (int i = 0; i < count; i++) _custom[i] = EclipseHubBeamMath.EncodeCustom(0d, 0, EclipseHubBeamMath.NeverFireTau, i == 0);
        double ringRadius = grid.GetCanonicalRingCircumferenceMeters64() / Math.Tau;
        _beamMaterial = CreateMaterial(beamShader, ringRadius);
        _coreMaterial = CreateMaterial(coreShader, ringRadius);
        _beamMaterial.RenderPriority = HaloRenderPriority;
        _coreMaterial.RenderPriority = CoreRenderPriority;

        ArrayMesh mesh = BuildRibbonMesh(out int vertexCount);
        RibbonVertexCount = vertexCount;
        var multiMesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, UseColors = true };
        multiMesh.Mesh = mesh;
        multiMesh.InstanceCount = count;
        multiMesh.VisibleInstanceCount = count;
        for (int i = 0; i < count; i++) { multiMesh.SetInstanceCustomData(i, _custom[i]); CustomDataWriteCount++; }
        // The in-cloud data lives in COLOR (k, deck base, deck top, queried): explicit zeros first, then whatever this eclipse has already queried.
        if (_cloud.Length != count)
        {
            _cloud = new Color[count]; _queried = new bool[count]; _cloudDeckBase = new double[count]; _cloudDeckTop = new double[count];
            WeatherQueryCount = 0; _cloudEclipse = int.MinValue; _cloudOrigin = _beams.BeamOrigin;
        }
        ReencodeClouds(write: false); // the origin setting may have changed while the layer was not built
        for (int i = 0; i < count; i++) { multiMesh.SetInstanceColor(i, _cloud[i]); ColorWriteCount++; }
        _beamOfSite = new int[topology.Registry.Count];
        Array.Fill(_beamOfSite, -1);
        for (int i = 0; i < count; i++) _beamOfSite[topology.HubBeamSite(i)] = i;
        if (_glowWritten.Length != count) { _glowWritten = new float[count]; _glowTarget = new float[count]; _glowTouched = new int[count]; _glowSeen = new bool[count]; }
        _instances = new MultiMeshInstance3D
        {
            Name = "HubBeams",
            Multimesh = multiMesh,
            MaterialOverride = _beamMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            IgnoreOcclusionCulling = true,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled
        };
        AddChild(_instances);
        RenderingServer.InstanceSetIgnoreCulling(_instances.GetInstance(), true);

        ApplyBeamSettings(_beams);
        _lit = false;
        UpdateVisibility();
        grid.CanonicalFloatingOriginAnchorChanged += HandleAnchorChanged;
        Rebuild(grid.CaptureLodGeometrySnapshot());
        return true;
    }

    private static ShaderMaterial CreateMaterial(Shader shader, double ringRadius)
    {
        var material = new ShaderMaterial { Shader = shader };
        RingAtmosphereModel.ConfigureMaterial(material, ringRadius);
        material.SetShaderParameter("world_scale", 1f);
        return material;
    }

    /// <summary>Frees the beams (instances, materials and the far layer). The next eclipse builds them again.</summary>
    public void Unbind() => Unbind(false);

    private void Unbind(bool exitingTree)
    {
        ClearCrownGlow();
        ResetBoreBoost();
        if (_grid != null && GodotObject.IsInstanceValid(_grid)) _grid.CanonicalFloatingOriginAnchorChanged -= HandleAnchorChanged;
        if (FarLayerValid)
        {
            if (!exitingTree) _farLayer!.GetParent()?.RemoveChild(_farLayer);
            _farLayer!.QueueFree();
        }
        _farLayer = null;
        if (_instances != null && GodotObject.IsInstanceValid(_instances))
        {
            // The parent is already removing its children while the tree exits; QueueFree detaches the instance itself.
            if (!exitingTree) RemoveChild(_instances);
            _instances.QueueFree();
        }
        _instances = null;
        _beamMaterial = null;
        _coreMaterial = null;
        _topology = null;
        _boundTimeline = null;
        _lit = false;
        _tau = double.NaN;
        _flashFrontCount = 0; _flashCleared = false; _flashClock = null;
        _glowCursor = 0; _glowLastTau = double.NaN; _cloudScanDelay = 0d;
        _appliedMax = _appliedFade = _appliedNear = _farAppliedNear = -1f;
        _appliedBand = new Vector2(float.NaN, 0f);
        _appliedRingCenter = new Vector3(float.NaN, 0f, 0f);
        _farBindRetry = 0d;
        if (!exitingTree) UpdateVisibility();
    }

    /// <summary>
    /// Sub-segments of 4 vertices each: UV = (f, side), UV2 = (f_a, f_b) of the enclosing sub-segment, so the vertex stage can collapse or
    /// near-clip a whole sub-segment without producing partial triangles. Boundaries are (k / N)^<see cref="RibbonSegmentExponent"/>.
    /// </summary>
    public static ArrayMesh BuildRibbonMesh(out int vertexCount)
    {
        const int segments = RibbonSubSegments;
        vertexCount = segments * 4 + OrbQuadVertices;
        var vertices = new Vector3[vertexCount];
        var uv = new Vector2[vertexCount];
        var uv2 = new Vector2[vertexCount];
        var indices = new int[segments * 6 + OrbQuadIndices];
        for (int s = 0; s < segments; s++)
        {
            float fa = SegmentBoundary(s), fb = SegmentBoundary(s + 1);
            for (int corner = 0; corner < 4; corner++)
            {
                int v = s * 4 + corner;
                float f = corner < 2 ? fa : fb, side = (corner & 1) == 0 ? -1f : 1f;
                vertices[v] = new Vector3(f, side * 0.01f, 0f);
                uv[v] = new Vector2(f, side);
                uv2[v] = new Vector2(fa, fb);
            }
            int b = s * 4, i = s * 6;
            indices[i] = b; indices[i + 1] = b + 1; indices[i + 2] = b + 2;
            indices[i + 3] = b + 1; indices[i + 4] = b + 3; indices[i + 5] = b + 2;
        }
        // The orb quad: UV = the corner (-1 or 1 on both axes), UV2 = (-1, -1) (a ribbon sub-segment never has a negative chord fraction).
        int ob = segments * 4;
        for (int corner = 0; corner < 4; corner++)
        {
            vertices[ob + corner] = Vector3.Zero;
            uv[ob + corner] = new Vector2((corner & 1) == 0 ? -1f : 1f, corner < 2 ? -1f : 1f);
            uv2[ob + corner] = new Vector2(-1f, -1f);
        }
        int oi = segments * 6;
        indices[oi] = ob; indices[oi + 1] = ob + 1; indices[oi + 2] = ob + 2;
        indices[oi + 3] = ob + 1; indices[oi + 4] = ob + 3; indices[oi + 5] = ob + 2;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.TexUV] = uv;
        arrays[(int)Mesh.ArrayType.TexUV2] = uv2;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.CustomAabb = new Aabb(new Vector3(-1e7f, -1e7f, -1e7f), new Vector3(2e7f, 2e7f, 2e7f));
        return mesh;
    }

    /// <summary>Chord fraction of the k-th sub-segment boundary (0 at k = 0, exactly 1 at k = <see cref="RibbonSubSegments"/>).</summary>
    public static float SegmentBoundary(int k) => k >= RibbonSubSegments ? 1f : MathF.Pow(k / (float)RibbonSubSegments, RibbonSegmentExponent);

    // ---------------------------------------------------------------- geometry (floating origin)

    private void HandleAnchorChanged(RingLodGeometrySnapshot previous, RingLodGeometrySnapshot current, RingCanonicalPosition anchor)
    {
        _ = previous; _ = anchor;
        Rebuild(current);
    }

    /// <summary>
    /// Recomputes every beam transform from <paramref name="snapshot"/> (layer-local, absolute, never a delta) for the current
    /// <see cref="EclipseBeamSettings.BeamOrigin"/>. Allocation free; custom data is untouched.
    /// </summary>
    public void Rebuild(in RingLodGeometrySnapshot snapshot)
    {
        if (_topology == null || _instances?.Multimesh is not { } beams) return;
        long started = Stopwatch.GetTimestamp();
        _worldToLayer = IsInsideTree() ? GlobalTransform.AffineInverse() : Transform3D.Identity;
        _topology.FillHubBeamEndpoints(snapshot, _beams.BeamOrigin, _origins, out _hubWorld);
        _originApplied = _beams.BeamOrigin;
        Basis basis = _worldToLayer.Basis;
        for (int i = 0; i < _mirror.Length; i++)
        {
            Vector3D a = _origins[i];
            Vector3D span = new(_hubWorld.X - a.X, _hubWorld.Y - a.Y, _hubWorld.Z - a.Z);
            var local = new Transform3D(new Basis(basis * span.ToVector3(), Vector3.Up, Vector3.Back), _worldToLayer * a.ToVector3());
            _mirror[i] = local;
            beams.SetInstanceTransform(i, local);
        }
        TransformWriteCount += _mirror.Length;
        if (_grid != null && _grid.IsInsideTree()) _lastGridGlobal = _grid.GlobalTransform;
        if (IsInsideTree()) _lastLayerGlobal = GlobalTransform;
        RebuildCount++;
        GeometryRevision++;
        LastRebuildMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    /// <summary>Layer-local transform last written for the beam (CPU mirror): origin = the beam origin, basis X = hub - origin.</summary>
    public Transform3D GetBeamTransform(int beam) => beam >= 0 && beam < _mirror.Length ? _mirror[beam] : Transform3D.Identity;
    /// <summary>World position of the point the beam rises from (<see cref="EclipseBeamSettings.BeamOrigin"/>).</summary>
    public Vector3D GetWorldOrigin(int beam) => _origins[beam];
    /// <summary>Metres from the beam's origin up to the spire's crown focus (0 for the Crown origin): the capture tools mask the rows above the crown.</summary>
    public double CrownHeightAboveOrigin(int beam)
        => _topology == null ? 0d : _topology.HubBeamCrown(beam).Vertical - _topology.HubBeamOrigin(beam, _originApplied).Vertical;
    /// <summary>The beam origin the transforms and the in-cloud data currently reflect.</summary>
    public EmitterBeamOrigin AppliedBeamOrigin => _originApplied;

    /// <summary>
    /// A changed beam origin setting rebuilds only the 595 transforms (and the far layer's) and re-encodes the in-cloud data relative to the new origin:
    /// no timeline bind, no custom data write.
    /// </summary>
    private void ApplyBeamOrigin()
    {
        if (!IsBound || _grid == null || _beams.BeamOrigin == _originApplied) return;
        Rebuild(_grid.CaptureLodGeometrySnapshot());
        ReencodeClouds(write: true);
        if (FarLayerValid && _farScaledPoint != null) _farLayer!.SetOrigin(_beams.BeamOrigin, _farScaledPoint);
    }

    /// <summary>
    /// Re-encodes the queried beams' in-cloud data (deck altitudes relative to the beam origin) when the origin setting differs from the one the mirror was
    /// encoded for. Only beams with a deck are touched; <paramref name="write"/> pushes them to the MultiMesh COLOR (one write each).
    /// </summary>
    private void ReencodeClouds(bool write)
    {
        if (_topology == null || _cloudOrigin == _beams.BeamOrigin) return;
        _cloudOrigin = _beams.BeamOrigin;
        MultiMesh? beams = write ? _instances?.Multimesh : null;
        for (int i = 0; i < _cloud.Length; i++)
        {
            if (!_queried[i] || _cloud[i].R <= 0f) continue;
            _cloud[i] = EclipseHubBeamMath.EncodeCloud(_cloud[i].R, _cloudDeckBase[i], _cloudDeckTop[i], _topology.HubBeamOrigin(i, _beams.BeamOrigin).Vertical);
            if (beams == null) continue;
            beams.SetInstanceColor(i, _cloud[i]);
            ColorWriteCount++;
        }
    }
    public Color GetCustomMirror(int beam) => _custom[beam];

    // ---------------------------------------------------------------- per-eclipse state (written once per timeline)

    /// <summary>
    /// Writes the per-beam custom data (p_site, column, fire tau, kind) for <paramref name="timeline"/> and the timeline-constant uniforms.
    /// Called when the director's timeline object changes (a new eclipse or a timeline setting change), never per frame. A null timeline
    /// resets every beam to "never fires".
    /// </summary>
    public void BindTimeline(EclipseEventTimeline? timeline)
    {
        if (_topology == null || _instances?.Multimesh is not { } beams) return;
        int count = _custom.Length;
        EclipseSiteTable? table = timeline?.Sites;
        bool consistent = table != null && table.Count + 1 == count;
        _custom[0] = EclipseHubBeamMath.EncodeCustom(0d, 0, timeline != null && timeline.Active ? timeline.TauMainFire : EclipseHubBeamMath.NeverFireTau, true);
        for (int i = 1; i < count; i++)
        {
            int entry = i - 1;
            // The topology and the site table list the regular spires in the same order (chain link order); a mismatch must never light the wrong spire.
            bool match = consistent && table!.SiteIndex[entry] == _topology.HubBeamSite(i);
            _custom[i] = match
                ? EclipseHubBeamMath.EncodeCustom(table!.P[entry], table.Column[entry],
                    timeline!.Active && timeline.SiteAlive(entry) ? timeline.SiteFireTau(entry) : EclipseHubBeamMath.NeverFireTau, false)
                : EclipseHubBeamMath.EncodeCustom(0d, 0, EclipseHubBeamMath.NeverFireTau, false);
        }
        for (int i = 0; i < count; i++) beams.SetInstanceCustomData(i, _custom[i]);
        CustomDataWriteCount += count;
        if (FarLayerValid) _farLayer!.WriteCustomData(_custom);
        _boundTimeline = timeline;
        _uniforms = timeline != null ? EclipseHubBeamMath.FromTimeline(timeline, _beams, double.IsFinite(_tau) ? _tau : 0d) : default;
        TimelineBindCount++;
        if (timeline != null)
        {
            SetBeamUniform("ev_marks", _uniforms.Marks);
            SetBeamUniform("ev_climb", _uniforms.Climb);
            SetBeamUniform("ev_level", _uniforms.Level);
            SetBeamUniform("ev_flash", _uniforms.Flash);
            SetBeamUniform("ev_refuel", _uniforms.Refuel);
            SetBeamUniform("ev_orb", _uniforms.Orb);
        }
        _flashClock = null; // the speed in ev_flash may have changed with the grid clock
        BindCloudAndGlow(timeline);
    }

    // ---------------------------------------------------------------- refuel flashes (phase E4d)

    /// <summary>Seconds after the last possible launch (Instability start) during which an orb can still be alive: travel + hold + fade + 1 (tweaks T3).</summary>
    private double FlashTailSeconds() => _boundTimeline == null ? 0d
        : _boundTimeline.Settings.OrbTravelSeconds + _boundTimeline.Settings.FlashHoldSeconds + _boundTimeline.Settings.FlashFadeSeconds + 1d;

    /// <summary>
    /// Publishes the grid's normal pulse fronts at <paramref name="gridTime"/> to the beam shaders (the flash maths reads <c>eg_fronts</c>) while a refuel
    /// flash can be visible: from the start of Sustain to the end of the last flash before Instability. They come straight from the pulse clock, so they
    /// do not depend on the grid layer's update order or on the suppression of its normal pulse. Outside that window the front count is 0 (written once).
    /// </summary>
    private void UpdateRefuelFronts(double tau, double? gridTime)
    {
        EmitterGridPulseClock? clock = RefuelFrontClock ?? (_gridLayer != null && GodotObject.IsInstanceValid(_gridLayer) ? _gridLayer.Clock : null);
        EclipseEventTimeline? timeline = _boundTimeline;
        bool window = clock != null && gridTime.HasValue && timeline != null && timeline.Active
            && tau >= timeline.TauSustain && tau < timeline.TauInstability + FlashTailSeconds();
        if (!window)
        {
            if (_flashFrontCount != 0 || !_flashCleared) { _flashFrontCount = 0; _flashCleared = true; SetBeamUniform("eg_front_count", 0); }
            return;
        }
        _flashCleared = false;
        if (!ReferenceEquals(clock, _flashClock))
        {
            _flashClock = clock;
            SetBeamUniform("eg_direction", clock!.Settings.Direction == EmitterPulseDirection.Prograde ? 1f : -1f);
            SetBeamUniform("eg_circumference", (float)clock.CircumferenceMeters);
        }
        Span<EmitterPulseFront> buffer = stackalloc EmitterPulseFront[32];
        int total = 0;
        for (int column = 0; column < (_topology?.Columns ?? EmitterGridPulseClock.ColumnCount) && total < _flashFronts.Length; column++)
        {
            // The orbs live up to OrbTravelSeconds + hold + fade after their front passes a spire: no legacy age cutoff (a long travel keeps the last rows' orbs).
            int n = clock!.GetFrontsWithin(gridTime!.Value, column, clock.SpeedMetersPerSecond * (FlashTailSeconds() + 1d), buffer);
            for (int i = 0; i < n && total < _flashFronts.Length; i++) _flashFronts[total++] = new Vector4((float)buffer[i].HeadQ, column, 0f, 0f);
        }
        for (int i = total; i < _flashFronts.Length; i++) _flashFronts[i] = Vector4.Zero;
        _flashFrontCount = total;
        SetBeamUniform("eg_front_count", total);
        SetBeamUniform("eg_fronts", _flashFronts);
    }

    /// <summary>Test/tool hook: the pulse clock the refuel flash fronts come from. Null (default) uses the grid layer's clock.</summary>
    public EmitterGridPulseClock? RefuelFrontClock { get; set; }

    /// <summary>The grid fronts last published for the refuel flashes (shader lanes), for tests and captures.</summary>
    public ReadOnlySpan<Vector4> RefuelFronts => _flashFronts.AsSpan(0, _flashFrontCount);

    // ---------------------------------------------------------------- in-cloud diffusion, crown glow, bore light (phase E4e)

    private bool CloudQueryAvailable => WeatherQueryOverride != null || (_weather != null && GodotObject.IsInstanceValid(_weather));

    private RingVolumetricCloudLayer.LocalWeatherState QueryWeather(in RingCanonicalPosition position)
    {
        if (WeatherQueryOverride != null) return WeatherQueryOverride(position, double.IsFinite(_tau) ? _tau : 0d);
        return _weather!.QueryLocalWeatherAtTime(position, _weather.CloudSimulationTimeSeconds);
    }

    /// <summary>Resets the per-eclipse query state when the eclipse changes and rebuilds the crown glow event list for the timeline.</summary>
    private void BindCloudAndGlow(EclipseEventTimeline? timeline)
    {
        if (timeline != null && timeline.Window.Index != _cloudEclipse)
        {
            _cloudEclipse = timeline.Window.Index;
            WeatherQueryCount = 0;
            if (_instances?.Multimesh is { } beams)
                for (int i = 0; i < _cloud.Length; i++)
                {
                    if (!_queried[i]) continue;
                    _queried[i] = false;
                    _cloud[i] = default;
                    beams.SetInstanceColor(i, default);
                    ColorWriteCount++;
                }
        }
        // The crown glow events: fire (full), hand-off complete (ember), cut (off). Dead sites never fire and get none.
        int count = _custom.Length;
        var events = new System.Collections.Generic.List<GlowEvent>(count * 3);
        if (timeline != null && timeline.Active)
        {
            for (int i = 0; i < count; i++)
            {
                float fire = _custom[i].B;
                if (fire >= 1e8f) continue;
                bool main = _custom[i].A > 0.5f;
                events.Add(new GlowEvent(fire, i, CrownGlowFire));
                events.Add(new GlowEvent(timeline.TauHandoff + timeline.HandoffFade, i, main ? CrownGlowEmberMain : CrownGlowEmberRegular));
                events.Add(new GlowEvent(timeline.TauImplosion, i, 0f));
            }
        }
        events.Sort(static (a, b) => { int c = a.Tau.CompareTo(b.Tau); return c != 0 ? c : a.Beam.CompareTo(b.Beam); });
        _glowEvents = events.ToArray();
        _glowCursor = 0;
        _glowLastTau = double.NaN; // the next frame resyncs from the event time
    }

    /// <summary>
    /// Phase E4e (spec E.6): once per scan, queries the weather at the nearest unqueried fired spires within the near range (at most
    /// <see cref="MaxWeatherQueries"/> per eclipse) and writes the in-cloud data to that beam's MultiMesh COLOR (one write per query; none otherwise).
    /// </summary>
    private void ScanClouds(double tau, double delta, Camera3D camera, double nearMeters)
    {
        if (!_beams.CloudDiffusionEnabled || WeatherQueryCount >= MaxWeatherQueries || _topology == null || _instances?.Multimesh is not { } beams || !CloudQueryAvailable) return;
        if ((_cloudScanDelay -= delta) > 0d) return;
        _cloudScanDelay = CloudScanSeconds;
        Vector3 cameraPosition = camera.GlobalPosition;
        while (WeatherQueryCount < MaxWeatherQueries)
        {
            int best = -1;
            double bestDistance = nearMeters * nearMeters;
            for (int i = 0; i < _origins.Length; i++)
            {
                if (_queried[i] || _custom[i].B >= 1e8f || tau <= _custom[i].B) continue;
                double dx = _origins[i].X - cameraPosition.X, dy = _origins[i].Y - cameraPosition.Y, dz = _origins[i].Z - cameraPosition.Z;
                double d2 = dx * dx + dy * dy + dz * dz;
                if (d2 < bestDistance) { bestDistance = d2; best = i; }
            }
            if (best < 0) break;
            (double around, double vertical, double across) = _topology.HubBeamOrigin(best, _beams.BeamOrigin);
            RingVolumetricCloudLayer.LocalWeatherState weather = QueryWeather(new RingCanonicalPosition(around, 0d, across));
            WeatherQueryCount++;
            _queried[best] = true;
            bool deck = float.IsFinite(weather.CloudBaseAltitudeMeters) && float.IsFinite(weather.CloudTopAltitudeMeters) && weather.CloudTopAltitudeMeters > weather.CloudBaseAltitudeMeters;
            double coverage = deck && float.IsFinite(weather.CloudCoverage) ? weather.CloudCoverage : 0d;
            // Below 2 % coverage the deck does nothing visible: keep the beam unchanged (flag 1 so it is not queried again).
            _cloud[best] = coverage < 0.02d ? new Color(0f, 0f, 0f, 1f) : EclipseHubBeamMath.EncodeCloud(coverage, weather.CloudBaseAltitudeMeters, weather.CloudTopAltitudeMeters, vertical);
            _cloudDeckBase[best] = weather.CloudBaseAltitudeMeters; _cloudDeckTop[best] = weather.CloudTopAltitudeMeters;
            beams.SetInstanceColor(best, _cloud[best]);
            ColorWriteCount++;
        }
    }

    /// <summary>
    /// Debug/capture hook: answers every weather query with a cloud deck of <paramref name="coverage"/> between the altitudes <paramref name="baseMeters"/> and
    /// <paramref name="topMeters"/>, whatever the real weather is (call <see cref="ResetCloudQueries"/> afterwards). A negative coverage restores the real weather.
    /// </summary>
    public void SetForcedDeck(float coverage, float baseMeters, float topMeters)
        => WeatherQueryOverride = coverage < 0f ? null : (_, _) => default(RingVolumetricCloudLayer.LocalWeatherState) with
            { CloudCoverage = coverage, CloudBaseAltitudeMeters = baseMeters, CloudTopAltitudeMeters = topMeters };

    /// <summary>Beam index of a registry site (-1 when the site has no beam or the layer is unbound).</summary>
    public int BeamOfSite(int site) => site >= 0 && site < _beamOfSite.Length ? _beamOfSite[site] : -1;

    /// <summary>
    /// Debug/capture hook: forgets this eclipse's weather queries (zero colours written for the queried beams, the count back to 0) so the next scan
    /// queries again, e.g. after a storm was spawned. Not used by the game itself: the weather is queried once per spire per eclipse.
    /// </summary>
    public void ResetCloudQueries()
    {
        WeatherQueryCount = 0;
        if (_instances?.Multimesh is not { } beams) return;
        for (int i = 0; i < _cloud.Length; i++)
        {
            if (!_queried[i]) continue;
            _queried[i] = false;
            _cloud[i] = default;
            beams.SetInstanceColor(i, default);
            ColorWriteCount++;
        }
        _cloudScanDelay = 0d;
    }

    /// <summary>The in-cloud data last written for the beam (CPU mirror): (k, deck base above the beam origin, deck top above the beam origin, 1 = queried).</summary>
    public Color GetCloudMirror(int beam) => beam >= 0 && beam < _cloud.Length ? _cloud[beam] : default;

    /// <summary>
    /// Phase E4e (spec E.7): drives the far stand-ins' crown glow lane (INSTANCE_CUSTOM.b) from the timeline's event list: full on fire, an ember at the
    /// end of the hand-off, off at the cut. Each spire is written at most once per event and nothing per frame; a backward jump (scrub) resyncs.
    /// </summary>
    private void UpdateCrownGlow(double tau)
    {
        if (_standIns == null || !GodotObject.IsInstanceValid(_standIns) || !_standIns.IsBound) return;
        if (_standIns.BindRevision != _standInsBindRevision)
        {
            // The stand-in layer (re)allocated its crown glow lane: whatever this layer believes it wrote is gone, so resync from the event time.
            _standInsBindRevision = _standIns.BindRevision;
            Array.Clear(_glowWritten);
            _glowLastTau = double.NaN;
            _standInSettingsApplied = false;
        }
        if (!_standInSettingsApplied) { _standIns.SetCrownGlowEmission((float)_beams.CrownGlowEmission); _standInSettingsApplied = true; }
        if (!_beams.CrownGlowEnabled)
        {
            ClearCrownGlow();
            return;
        }
        int target;
        if (double.IsNaN(_glowLastTau) || tau < _glowLastTau - 1e-6)
        {
            // First frame or a backward jump: recompute the whole state at tau.
            Array.Clear(_glowTarget);
            target = 0;
            while (target < _glowEvents.Length && _glowEvents[target].Tau <= tau) { _glowTarget[_glowEvents[target].Beam] = _glowEvents[target].Value; target++; }
            for (int i = 0; i < _glowWritten.Length; i++) WriteGlow(i, _glowTarget[i]);
            Array.Clear(_glowTarget);
            _glowCursor = target;
        }
        else
        {
            target = _glowCursor;
            while (target < _glowEvents.Length && _glowEvents[target].Tau <= tau) target++;
            if (target > _glowCursor)
            {
                // Several events of one beam in the same frame collapse to its last value (one write per spire).
                int touched = 0;
                for (int e = _glowCursor; e < target; e++)
                {
                    int beam = _glowEvents[e].Beam;
                    if (!_glowSeen[beam]) { _glowSeen[beam] = true; _glowTouched[touched++] = beam; }
                    _glowTarget[beam] = _glowEvents[e].Value;
                }
                for (int t = 0; t < touched; t++)
                {
                    int beam = _glowTouched[t];
                    _glowSeen[beam] = false;
                    WriteGlow(beam, _glowTarget[beam]);
                }
                _glowCursor = target;
            }
        }
        _glowLastTau = tau;
    }

    private void WriteGlow(int beam, float value)
    {
        if (_glowWritten[beam] == value || _standIns == null) return;
        _glowWritten[beam] = value;
        _standIns.SetSiteCrownGlow(_topology!.HubBeamSite(beam), value);
        CrownGlowWriteCount++;
    }

    /// <summary>Switches every glowing crown off (the layer is freed, the glow is disabled or the event ended).</summary>
    private void ClearCrownGlow()
    {
        for (int i = 0; i < _glowWritten.Length; i++)
        {
            if (_glowWritten[i] == 0f || _topology == null) continue;
            _glowWritten[i] = 0f;
            if (_standIns != null && GodotObject.IsInstanceValid(_standIns)) _standIns.SetSiteCrownGlow(_topology.HubBeamSite(i), 0f);
            CrownGlowWriteCount++;
        }
        _glowLastTau = double.NaN;
        _glowCursor = 0;
    }

    /// <summary>Test hook: the crown glow lane value this layer last wrote for the beam.</summary>
    public float GetCrownGlowWritten(int beam) => beam >= 0 && beam < _glowWritten.Length ? _glowWritten[beam] : 0f;

    /// <summary>
    /// The beam's light level at its crown (handoff x lit x the refuel flash envelope, 0..1): the CPU twin of what the shader shows at f = 0 before the day
    /// factor and the atmosphere. Drives the bore light boost.
    /// </summary>
    public float CrownLevel(int beam)
    {
        if (beam < 0 || beam >= _custom.Length) return 0f;
        float fire = _custom[beam].B, kind = _custom[beam].A;
        EclipseHubBeamUniforms u = _uniforms;
        float own = EclipseHubBeamMath.Tip(u, fire, kind) > 0f ? EclipseHubBeamMath.Handoff(u, kind) : 0f;
        float flash = 0f;
        if (_flashFrontCount > 0 && _flashClock != null)
            flash = EclipseHubBeamMath.RefuelFlash(u, RefuelFronts, _flashClock.Settings.Direction == EmitterPulseDirection.Prograde ? 1f : -1f,
                (float)_flashClock.CircumferenceMeters, fire, kind, _custom[beam].R, _custom[beam].G).Envelope;
        return Math.Clamp(Math.Max(own, flash) * u.Level, 0f, 1f);
    }

    /// <summary>Factor on the bore plug light energy for the site: 1 + (BoreLightBoost - 1) x the crown level of its beam (1 when the beams are not lit).</summary>
    public float BoreLightScale(int site)
    {
        if (!_lit || site < 0 || site >= _beamOfSite.Length || _beamOfSite[site] < 0) return 1f;
        return 1f + ((float)_beams.BoreLightBoost - 1f) * CrownLevel(_beamOfSite[site]);
    }

    /// <summary>Phase E4e (spec E.7): boosts the (at most two) near POI bore plug lights while their spire's beam fires.</summary>
    private void UpdateBoreBoost()
    {
        if (_bores == null || !GodotObject.IsInstanceValid(_bores)) return;
        for (int slot = 0; slot < EmitterBoreLayer.SlotCount; slot++)
            _bores.SetSlotEventScale(slot, BoreLightScale(_bores.GetActiveSite(slot)));
    }

    private void ResetBoreBoost()
    {
        if (_bores == null || !GodotObject.IsInstanceValid(_bores)) return;
        for (int slot = 0; slot < EmitterBoreLayer.SlotCount; slot++) _bores.SetSlotEventScale(slot, 1f);
    }

    // ---------------------------------------------------------------- settings

    /// <summary>Applies the beam look settings (widths, emission, depth core, far layer). Safe to call every frame; only changed values reach the GPU.</summary>
    public void ApplyBeamSettings(EclipseBeamSettings beams)
    {
        _beams = (beams ?? throw new ArgumentNullException(nameof(beams))).Sanitized();
        EclipseBeamSettings b = _beams;
        if (_beamMaterial != null)
        {
            SetBeamUniform("beam_widths", new Vector4((float)b.CoreWidthMeters, (float)b.HaloWidthMeters, (float)b.MinPixelWidth, (float)b.FarCompensation));
            SetBeamUniform("main_widths", new Vector2((float)b.MainCoreWidthMeters, (float)b.MainHaloWidthMeters));
            SetBeamUniform("main_emission_scale", (float)b.MainEmissionScale);
            SetBeamUniform("halo_amplitude", (float)b.HaloAmplitude);
            SetBeamUniform("base_fade_meters", (float)b.BaseFadeMeters);
            SetBeamUniform("base_emission", (float)b.BaseEmission);
            SetBeamUniform("close_soften_m", (float)b.CloseSoftenMeters);
            SetBeamUniform("orb_look", new Vector2((float)b.OrbHaloMeters, (float)b.OrbEmission));
            if (_boundTimeline != null)
            {
                _uniforms = _uniforms with { Orb = EclipseHubBeamMath.OrbUniform(_boundTimeline, b) };
                SetBeamUniform("ev_orb", _uniforms.Orb);
            }
            SetBeamUniform("core_depth", new Vector2((float)b.CoreDepthThreshold, (float)b.CoreDepthMinPixels));
            SetBeamUniform("ev_shape", new Vector2((float)b.SoftTipFraction, (float)b.DayVisibility));
            SetBeamUniform("cloud_params", new Vector4((float)b.CloudWidening, (float)b.CloudCoreCut, (float)b.CloudHaloBoost, b.CloudDiffusionEnabled ? 1f : 0f));
            _beamMaterial.NextPass = b.DepthWriteCore ? _coreMaterial : null;
        }
        _standIns?.SetCrownGlowEmission((float)b.CrownGlowEmission);
        ApplyBeamOrigin();
        _uniforms = _uniforms with { SoftTip = (float)b.SoftTipFraction, DayVisibility = (float)b.DayVisibility };
        // Force the next frame to rewrite the layer uniforms (a far layer that was off may have missed some).
        _appliedMax = _appliedFade = -1f;
        _appliedBand = new Vector2(float.NaN, 0f);
        UpdateVisibility();
    }

    /// <summary>Writes a uniform to the near halo, the near core pass and (when present) the far beam material.</summary>
    private void SetBeamUniform(StringName name, Variant value)
    {
        _beamMaterial?.SetShaderParameter(name, value);
        _coreMaterial?.SetShaderParameter(name, value);
        if (FarLayerValid) _farLayer!.BeamMaterial?.SetShaderParameter(name, value);
    }

    // ---------------------------------------------------------------- per frame

    /// <summary>
    /// Publishes the event time <paramref name="tau"/> (seconds from the window start) and whether the main beam is lit. Hidden layers write
    /// nothing. The only per-frame GPU writes are a few uniforms; no per-beam data is touched.
    /// </summary>
    public void ApplyEventTime(double tau, bool lit, double delta, double? gridTime = null)
    {
        _tau = tau;
        _uniforms = _uniforms with { Time = (float)tau };
        if (_lit != lit) { _lit = lit; UpdateVisibility(); }
        if (_topology != null)
        {
            UpdateCrownGlow(tau);
            UpdateBoreBoost();
        }
        if (!Visible || UndergroundHold || _beamMaterial == null) return;
        SetBeamUniform("ev_time", (float)tau);
        UpdateRefuelFronts(tau, gridTime);
        Camera3D? camera = GetViewport()?.GetCamera3D();
        if (camera == null || _grid == null) return;
        EmitterGridBeamLayer? gridLayer = _gridLayer != null && GodotObject.IsInstanceValid(_gridLayer) ? _gridLayer : null;
        GridView view = ReadGridView(gridLayer);

        double? fadeEnd = _globalTerrain != null && GodotObject.IsInstanceValid(_globalTerrain) && _globalTerrain.EnableFarDistanceFade ? _globalTerrain.FarFadeEndMeters : null;
        _effectiveMax = (float)EmitterStandInLayer.ComputeEffectiveMaxDistance(view.NearLayerMaxKm * 1000d, fadeEnd, camera.Far);
        float fade = Mathf.Min(EmitterGridBeamLayer.DefaultFadeMeters, 0.5f * _effectiveMax);
        Vector3 ringCenter = _grid.GetCanonicalRingCenterWorld();
        // The near material and core need the camera every frame; the rest only when it changes.
        Vector3 cameraPosition = camera.GlobalPosition;
        _beamMaterial.SetShaderParameter("camera_world_position", cameraPosition);
        _coreMaterial?.SetShaderParameter("camera_world_position", cameraPosition);
        if (_effectiveMax != _appliedMax) { _beamMaterial.SetShaderParameter("max_distance", _effectiveMax); _coreMaterial?.SetShaderParameter("max_distance", _effectiveMax); }
        if (fade != _appliedFade) { _beamMaterial.SetShaderParameter("fade_meters", fade); _coreMaterial?.SetShaderParameter("fade_meters", fade); }
        if (camera.Near != _appliedNear) { _beamMaterial.SetShaderParameter("near_clip", camera.Near); _coreMaterial?.SetShaderParameter("near_clip", camera.Near); _appliedNear = camera.Near; }
        if (ringCenter != _appliedRingCenter) { _beamMaterial.SetShaderParameter("ring_center", ringCenter); _coreMaterial?.SetShaderParameter("ring_center", ringCenter); _appliedRingCenter = ringCenter; }

        if (_farLayer == null && _beams.FarLayerEnabled && _farViewport != null && GodotObject.IsInstanceValid(_farViewport) && _farViewport.HasScaledWorld
            && (_farBindRetry -= delta) <= 0d)
        {
            _farBindRetry = 0.5d;
            BindFarLayer(_farViewport.ScaledPoint, _farViewport.CoordinateScale, _farViewport.AddScaledWorldChild);
        }
        ScanClouds(tau, delta, camera, CloudNearRangeOverrideMeters ?? view.NearLayerMaxKm * 1000d);
        Vector2 band = ResolveHandoffBand(view);
        _handoffBand = band;
        if (band != _appliedBand) { _beamMaterial.SetShaderParameter("eg_handoff", band); _coreMaterial?.SetShaderParameter("eg_handoff", band); }
        if (FarLayerActive) WriteFarUniforms(fade, band);
        _appliedMax = _effectiveMax;
        _appliedFade = fade;
        _appliedBand = band;
    }

    private readonly record struct GridView(float NearLayerMaxKm, bool FollowsClouds, float HandoffStartKm, float HandoffEndKm);

    private static GridView ReadGridView(EmitterGridBeamLayer? gridLayer)
    {
        var s = gridLayer?.Settings;
        return s == null ? new(700f, true, 120f, 172f) : new(s.NearLayerMaxKm, s.FarHandoffFollowsClouds, s.FarHandoffStartKm, s.FarHandoffEndKm);
    }

    private Vector2 ResolveHandoffBand(in GridView view)
    {
        bool known = _farViewport != null && GodotObject.IsInstanceValid(_farViewport);
        return EmitterGridBeamLayer.ResolveHandoffBand(FarLayerActive, view.FollowsClouds, known, known && _farViewport!.NearCloudOwnershipEnabled,
            known ? _farViewport!.FarCloudClipmapHandoffStartMeters : 0d, known ? _farViewport!.LocalCloudRadiusMeters : 0d,
            view.HandoffStartKm, view.HandoffEndKm);
    }

    private void WriteFarUniforms(float fade, Vector2 band)
    {
        EclipseHubFarLayer far = _farLayer!;
        ShaderMaterial? material = far.BeamMaterial;
        if (material == null) return;
        Camera3D? camera = far.GetViewport()?.GetCamera3D();
        float nearClip = camera?.Near ?? 0.1f;
        if (_effectiveMax != _appliedMax) material.SetShaderParameter("max_distance", _effectiveMax);
        if (fade != _appliedFade) material.SetShaderParameter("fade_meters", fade);
        if (nearClip != _farAppliedNear) { material.SetShaderParameter("near_clip", nearClip); _farAppliedNear = nearClip; }
        if (band != _appliedBand) material.SetShaderParameter("eg_handoff", band);
    }

    // ---------------------------------------------------------------- far layer

    /// <summary>
    /// Creates the far layer for the bound topology and attaches it through <paramref name="attach"/> (normally
    /// <see cref="RingScaledFarViewport.AddScaledWorldChild"/>). False when unbound, the assets are missing or the attach fails.
    /// </summary>
    public bool BindFarLayer(Func<double, double, double, Vector3> scaledPoint, float coordinateScale, Func<Node3D, bool> attach)
    {
        if (FarLayerValid) return true;
        if (_topology == null || _grid == null) return false;
        var layer = new EclipseHubFarLayer { Name = "EclipseHubFar" };
        if (!layer.Build(_topology, scaledPoint, coordinateScale, _topology.CircumferenceMeters / Math.Tau, _beams.BeamOrigin) || !attach(layer))
        {
            layer.Free();
            return false;
        }
        _farLayer = layer;
        _farScaledPoint = scaledPoint;
        layer.WriteCustomData(_custom);
        // Mirror the whole uniform state onto the new far material.
        ApplyBeamSettings(_beams);
        if (_boundTimeline != null)
        {
            layer.BeamMaterial?.SetShaderParameter("ev_marks", _uniforms.Marks);
            layer.BeamMaterial?.SetShaderParameter("ev_climb", _uniforms.Climb);
            layer.BeamMaterial?.SetShaderParameter("ev_level", _uniforms.Level);
            layer.BeamMaterial?.SetShaderParameter("ev_flash", _uniforms.Flash);
            layer.BeamMaterial?.SetShaderParameter("ev_refuel", _uniforms.Refuel);
            layer.BeamMaterial?.SetShaderParameter("ev_orb", _uniforms.Orb);
        }
        layer.BeamMaterial?.SetShaderParameter("ev_time", _uniforms.Time);
        _appliedMax = _appliedFade = -1f;
        _appliedBand = new Vector2(float.NaN, 0f);
        _farAppliedNear = -1f;
        _flashClock = null; // the new far material needs the direction and circumference too
        UpdateVisibility();
        return true;
    }

    // ---------------------------------------------------------------- visibility

    public void SetUndergroundHidden(bool hidden)
    {
        if (_undergroundHidden == hidden) return;
        _undergroundHidden = hidden;
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        Visible = _enabled && IsBound && _lit && !_undergroundHidden && _beams.BeamsEnabled;
        if (FarLayerValid) _farLayer!.Visible = Visible && _beams.FarLayerEnabled;
    }

    /// <summary>Beams whose tip has left the crown at the current event time (CPU count over the custom data; the status line only). A refuel orb does not relight its beam.</summary>
    public int LitBeamCount()
    {
        int lit = 0;
        for (int i = 0; i < _custom.Length; i++)
            if (EclipseHubBeamMath.IsLit(_uniforms, _custom[i].B, _custom[i].A, 0f)) lit++;
        return lit;
    }

    /// <summary>Sustain refuel orbs alive at the current event time (CPU twin over the published fronts; the status line and the tests).</summary>
    public int OrbsInFlight()
    {
        if (_flashFrontCount == 0 || _flashClock == null) return 0;
        float direction = _flashClock.Settings.Direction == EmitterPulseDirection.Prograde ? 1f : -1f, circumference = (float)_flashClock.CircumferenceMeters;
        int orbs = 0;
        for (int i = 0; i < _custom.Length; i++)
            if (EclipseHubBeamMath.Orb(_uniforms, RefuelFronts, direction, circumference, _custom[i].B, _custom[i].A, _custom[i].R, _custom[i].G).Envelope > 0f) orbs++;
        return orbs;
    }

    /// <summary>The orb (envelope, fraction along the beam) of a beam at the current event time, from the published fronts (CPU twin).</summary>
    public (float Envelope, float Fraction) GetOrb(int beam)
    {
        if (beam < 0 || beam >= _custom.Length || _flashFrontCount == 0 || _flashClock == null) return (0f, 0f);
        return EclipseHubBeamMath.Orb(_uniforms, RefuelFronts, _flashClock.Settings.Direction == EmitterPulseDirection.Prograde ? 1f : -1f,
            (float)_flashClock.CircumferenceMeters, _custom[beam].B, _custom[beam].A, _custom[beam].R, _custom[beam].G);
    }

    public string DescribeStatus()
    {
        if (!IsBound) return "Hub beams not built (only from just before the main fire until the collapse).";
        string farState = FarLayerActive ? "active" : FarLayerValid || !_beams.FarLayerEnabled ? "off" : "waiting";
        string handoff = _handoffBand.X >= NoHandoff ? "near only"
            : string.Format(CultureInfo.InvariantCulture, "{0:0}-{1:0} km", _handoffBand.X / 1000f, _handoffBand.Y / 1000f);
        return string.Format(CultureInfo.InvariantCulture, "Hub beams {0}, lit {1}, tau {2:0.0}, visible {3}, core depth {4}, far layer {5}, handoff {6}, rebuilds {7} ({8:0.00} ms), timeline binds {9}, cloud queries {10}/{11}, crown glow writes {12}, orbs in flight {13}",
            BeamCount, LitBeamCount(), _tau, Visible, _beams.DepthWriteCore ? "on" : "off", farState, handoff, RebuildCount, LastRebuildMs, TimelineBindCount,
            WeatherQueryCount, MaxWeatherQueries, CrownGlowWriteCount, OrbsInFlight());
    }
}
