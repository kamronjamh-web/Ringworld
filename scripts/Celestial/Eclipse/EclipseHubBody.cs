using System;
using System.Globalization;
using Godot;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Celestial.Eclipse;

/// <summary>
/// Hub sun body (docs/eclipse-false-sun-spec.md E.1, phase E4c; user decision 2026-10-17 item 1): a main-viewport proxy. A camera-facing quad is
/// placed along the direction to the hub point at half the camera far plane and scaled to the hub's true angular size, drawn additive and
/// unshaded with depth test on and no depth write, so Sunshine near clouds and any overhead geometry still hide it. It is not drawn in the far
/// viewport (that viewport is tonemapped twice and tops out near 0.85, T3). The hub direction comes from the canonical hub point in double
/// precision every frame. The look comes from <see cref="EclipseHubBodySettings"/> and the director's hub level (including the debug override);
/// outside the event the quad is hidden and costs one comparison per frame.
/// </summary>
public partial class EclipseHubBody : Node3D
{
    public const string ShaderPath = "res://materials/ring_eclipse_hub_body.gdshader";
    /// <summary>Above the beam layers (0, 1) and anything else additive in the main viewport.</summary>
    public const int BodyRenderPriority = 20;
    /// <summary>The proxy sits at this fraction of the camera far plane (T3 prototype: 0.5).</summary>
    public const float ProxyFarFraction = 0.5f;

    [Export] public bool Enabled { get => _enabled; set { _enabled = value; UpdateVisibility(); } }
    [Export] public bool AutoBind { get; set; } = true;
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath GridLayerPath { get; set; } = new("../EmitterGrid");
    [Export] public NodePath DirectorPath { get; set; } = new("../EclipseEvent");

    private bool _enabled = true;
    private bool _undergroundHidden;
    private bool _wanted;
    private VoxelChunkGridManager? _grid;
    private EmitterGridBeamLayer? _gridLayer;
    private EclipseEventDirector? _director;
    private MeshInstance3D? _quad;
    private ShaderMaterial? _material;
    private Vector3D _hubWorld;
    private bool _hubKnown;
    private double _hubAcross = double.NaN;
    private Transform3D _lastGridGlobal;
    private bool _subscribed;
    private float _appliedMinPx = -1f, _appliedQuadHalf = -1f, _appliedCoreRadius = -1f, _appliedHaloRadius = -1f, _appliedCoreEmission = -1f, _appliedHaloEmission = -1f;

    /// <summary>Number of hub point evaluations (bind plus every floating-origin anchor change).</summary>
    public int HubRebuildCount { get; private set; }
    public bool UndergroundHold { get; set; }
    public bool UndergroundHidden => _undergroundHidden;
    public MeshInstance3D? Quad => _quad;
    public ShaderMaterial? Material => _material;
    public Vector3D HubWorld => _hubWorld;
    public bool HubKnown => _hubKnown;
    /// <summary>Hub level used for the last frame this body was drawn (0 when hidden).</summary>
    public float DrawnLevel { get; private set; }
    /// <summary>Distance from the camera to the hub point in metres, and the proxy plane distance, for the last drawn frame.</summary>
    public double HubDistanceMeters { get; private set; }
    public double ProxyDistanceMeters { get; private set; }
    /// <summary>Real core radius in km (after the level factor) of the last drawn frame, and its angular radius in degrees.</summary>
    public double CoreRadiusKm { get; private set; }
    public double CoreAngularRadiusDegrees { get; private set; }
    /// <summary>Unit direction from the camera to the hub of the last drawn frame (world space).</summary>
    public Vector3 HubDirection { get; private set; } = Vector3.Up;

    public override void _Ready()
    {
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        if (!AutoBind) SetProcess(false);
        TopLevel = true;
    }

    public override void _ExitTree()
    {
        Unsubscribe();
        _hubKnown = false;
    }

    public override void _Process(double delta)
    {
        _ = delta;
        ResolveNodes();
        if (_director == null || !GodotObject.IsInstanceValid(_director)) { SetWanted(false); return; }
        EclipseHubBodySettings settings = _director.Engine.Settings.HubBody;
        float level = _director.HubLightLevel;
        bool wanted = settings.BodyEnabled && _enabled && level > 0f && _director.HubLightWeight > 0f && !_undergroundHidden;
        if (!wanted) { SetWanted(false); return; }
        Camera3D? camera = GetViewport()?.GetCamera3D();
        if (camera == null || _grid == null || !EnsureHub()) { SetWanted(false); return; }
        if (UndergroundHold) return;
        if (!EnsureQuad()) { SetWanted(false); return; }
        SetWanted(true);
        Apply(camera, settings, level);
    }

    private void ResolveNodes()
    {
        if (_grid == null || !GodotObject.IsInstanceValid(_grid)) { Unsubscribe(); _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath); _hubKnown = false; }
        if (_gridLayer == null || !GodotObject.IsInstanceValid(_gridLayer)) _gridLayer = GetNodeOrNull<EmitterGridBeamLayer>(GridLayerPath);
        if (_director == null || !GodotObject.IsInstanceValid(_director)) _director = GetNodeOrNull<EclipseEventDirector>(DirectorPath);
    }

    // ---------------------------------------------------------------- hub point (floating origin)

    /// <summary>Evaluates the world-space hub point (radial 0, across W/2) when it is unknown or the grid moved; false when there is no emitter topology to take the ring width from.</summary>
    private bool EnsureHub()
    {
        if (_grid == null) return false;
        EmitterGridTopology? topology = _gridLayer != null && GodotObject.IsInstanceValid(_gridLayer) && _gridLayer.IsBound ? _gridLayer.Topology : null;
        if (topology == null) return _hubKnown;
        if (!_subscribed) { _grid.CanonicalFloatingOriginAnchorChanged += HandleAnchorChanged; _subscribed = true; }
        if (!_hubKnown || topology.HubAcrossMeters != _hubAcross || !_grid.GlobalTransform.IsEqualApprox(_lastGridGlobal))
        {
            _hubAcross = topology.HubAcrossMeters;
            RebuildHub(_grid.CaptureLodGeometrySnapshot());
        }
        return _hubKnown;
    }

    private void HandleAnchorChanged(RingLodGeometrySnapshot previous, RingLodGeometrySnapshot current, RingCanonicalPosition anchor)
    {
        _ = previous; _ = anchor;
        if (double.IsFinite(_hubAcross)) RebuildHub(current);
    }

    /// <summary>Absolute evaluation of the hub point against <paramref name="snapshot"/> (never a delta from a previous rebase).</summary>
    public void RebuildHub(in RingLodGeometrySnapshot snapshot)
    {
        _hubWorld = new EmitterSiteFrameContext(snapshot).HubWorldPosition(_hubAcross);
        _hubKnown = true;
        if (_grid != null && _grid.IsInsideTree()) _lastGridGlobal = _grid.GlobalTransform;
        HubRebuildCount++;
    }

    private void Unsubscribe()
    {
        if (_subscribed && _grid != null && GodotObject.IsInstanceValid(_grid)) _grid.CanonicalFloatingOriginAnchorChanged -= HandleAnchorChanged;
        _subscribed = false;
    }

    // ---------------------------------------------------------------- build

    private bool EnsureQuad()
    {
        if (_quad != null && GodotObject.IsInstanceValid(_quad)) return true;
        Shader? shader = GD.Load<Shader>(ShaderPath);
        if (shader == null) return false;
        _material = new ShaderMaterial { Shader = shader, RenderPriority = BodyRenderPriority };
        var mesh = new QuadMesh { Size = Vector2.One };
        mesh.CustomAabb = new Aabb(new Vector3(-1e7f, -1e7f, -1e7f), new Vector3(2e7f, 2e7f, 2e7f));
        _quad = new MeshInstance3D
        {
            Name = "HubBodyQuad",
            Mesh = mesh,
            MaterialOverride = _material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            IgnoreOcclusionCulling = true,
            Visible = false
        };
        AddChild(_quad);
        RenderingServer.InstanceSetIgnoreCulling(_quad.GetInstance(), true);
        _appliedMinPx = _appliedQuadHalf = _appliedCoreRadius = _appliedHaloRadius = _appliedCoreEmission = _appliedHaloEmission = -1f;
        return true;
    }

    // ---------------------------------------------------------------- per frame

    private void Apply(Camera3D camera, EclipseHubBodySettings s, float level)
    {
        Vector3 cameraPosition = camera.GlobalPosition;
        Vector3D toHub = new(_hubWorld.X - cameraPosition.X, _hubWorld.Y - cameraPosition.Y, _hubWorld.Z - cameraPosition.Z);
        double distance = Math.Sqrt(toHub.X * toHub.X + toHub.Y * toHub.Y + toHub.Z * toHub.Z);
        if (!(distance > 1000d)) { SetWanted(false); return; }
        var direction = new Vector3((float)(toHub.X / distance), (float)(toHub.Y / distance), (float)(toHub.Z / distance));
        double proxy = Math.Max(1000d, camera.Far * ProxyFarFraction);
        double scale = proxy / distance;          // proxy metres per real metre
        GlobalPosition = cameraPosition + direction * (float)proxy;

        double coreKm = EclipseHubBodyMath.CoreRadiusKm(s, level);
        float coreRadius = (float)(coreKm * 1000d * scale);
        float haloRadius = (float)(s.HaloRadiusKm * 1000d * scale);
        float quadHalf = Math.Max(5f * haloRadius, 6f * coreRadius);
        float coreEmission = (float)EclipseHubBodyMath.CoreEmission(s, level);
        float haloEmission = (float)EclipseHubBodyMath.HaloEmission(s, level);
        float minPx = (float)s.MinRadiusPixels;
        if (_material != null)
        {
            if (coreRadius != _appliedCoreRadius) { _material.SetShaderParameter("core_radius_m", coreRadius); _appliedCoreRadius = coreRadius; }
            if (haloRadius != _appliedHaloRadius) { _material.SetShaderParameter("halo_radius_m", haloRadius); _appliedHaloRadius = haloRadius; }
            if (quadHalf != _appliedQuadHalf) { _material.SetShaderParameter("quad_half_m", quadHalf); _appliedQuadHalf = quadHalf; }
            if (coreEmission != _appliedCoreEmission) { _material.SetShaderParameter("core_emission", coreEmission); _appliedCoreEmission = coreEmission; }
            if (haloEmission != _appliedHaloEmission) { _material.SetShaderParameter("halo_emission", haloEmission); _appliedHaloEmission = haloEmission; }
            if (minPx != _appliedMinPx) { _material.SetShaderParameter("min_radius_px", minPx); _appliedMinPx = minPx; }
        }
        DrawnLevel = level;
        HubDistanceMeters = distance;
        ProxyDistanceMeters = proxy;
        CoreRadiusKm = coreKm;
        CoreAngularRadiusDegrees = Math.Atan2(coreKm * 1000d, distance) * 180d / Math.PI;
        HubDirection = direction;
    }

    // ---------------------------------------------------------------- visibility

    public void SetUndergroundHidden(bool hidden)
    {
        if (_undergroundHidden == hidden) return;
        _undergroundHidden = hidden;
        UpdateVisibility();
    }

    private void SetWanted(bool wanted)
    {
        if (_wanted == wanted) return;
        _wanted = wanted;
        if (!wanted) DrawnLevel = 0f;
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        if (_quad != null && GodotObject.IsInstanceValid(_quad)) _quad.Visible = _enabled && _wanted && !_undergroundHidden;
    }

    public string DescribeStatus()
    {
        if (!_wanted) return "Hub body hidden (it is drawn while the hub level is above 0).";
        return string.Format(CultureInfo.InvariantCulture,
            "Hub body level {0:0.00}, core {1:0.0} km ({2:0.000} deg), hub {3:0} km away, proxy at {4:0} km, hub points evaluated {5}",
            DrawnLevel, CoreRadiusKm, CoreAngularRadiusDegrees, HubDistanceMeters / 1000d, ProxyDistanceMeters / 1000d, HubRebuildCount);
    }
}
