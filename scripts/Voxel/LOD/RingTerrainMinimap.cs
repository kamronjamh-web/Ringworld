using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;
using RingworldTechDemo.Terrain.Editable;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Lightweight local elevation map. It samples canonical terrain directly and
/// renders a small independent heightfield instead of drawing the voxel world a
/// second time.
/// </summary>
public partial class RingTerrainMinimap : CanvasLayer
{
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath SourceCameraPath { get; set; } = new("../Explorer/Head/Camera3D");
    [Export(PropertyHint.Range, "192,512,16")] public int PixelSize { get; set; } = 288;
    [Export(PropertyHint.Range, "16,128,1")] public int TerrainCells { get; set; } = 64;
    [Export(PropertyHint.Range, "500,128000,100")] public float MapSpanMeters { get; set; } = 32000.0f;
    [Export(PropertyHint.Range, "500,8000,100")] public float MinimumSpanMeters { get; set; } = 500.0f;
    [Export(PropertyHint.Range, "4000,256000,1000")] public float MaximumSpanMeters { get; set; } = 128000.0f;
    [Export(PropertyHint.Range, "0.05,1.0,0.01")] public float VerticalScale { get; set; } = 1.0f;
    [Export(PropertyHint.Range, "1.0,2.0,0.05")] public float TerrainOverscan { get; set; } = 1.35f;
    [Export(PropertyHint.Range, "50,2000,50")] public float ContourIntervalMeters { get; set; } = 250.0f;
    [Export(PropertyHint.Range, "32,2048,32")] public float RebuildDistanceMeters { get; set; } = 256.0f;
    [Export(PropertyHint.Range, "8,128,1")] public float CloseRebuildDistanceMeters { get; set; } = 32.0f;
    [ExportGroup("Close 3D Transition")]
    [Export(PropertyHint.Range, "1000,10000,100")] public float Close3DTransitionStartMeters { get; set; } = 3000.0f;
    [Export(PropertyHint.Range, "500,2000,100")] public float Close3DTransitionEndMeters { get; set; } = 500.0f;
    [Export(PropertyHint.Range, "45,85,1")] public float WideCameraElevationDegrees { get; set; } = 62.0f;
    [Export(PropertyHint.Range, "20,50,1")] public float CloseCameraElevationDegrees { get; set; } = 34.0f;
    [Export(PropertyHint.Range, "0.0,0.25,0.01")] public float CloseLookAheadFraction { get; set; } = 0.10f;
    [Export(PropertyHint.Range, "5,50,5")] public float CloseContourIntervalMeters { get; set; } = 10.0f;
    [Export(PropertyHint.Range, "1.5,3.0,0.05")] public float CloseTerrainOverscan { get; set; } = 2.5f;
    [Export(PropertyHint.Range, "64,128,1")] public int CloseTerrainCells { get; set; } = 96;
    [Export(PropertyHint.Range, "0.1,2.0,0.05")] public float RebuildIntervalSeconds { get; set; } = 0.25f;
    [Export(PropertyHint.Range, "5,60,1")] public float RenderFramesPerSecond { get; set; } = 30.0f;
    [Export] public bool HeadingUp { get; set; }
    [Export] public bool StartVisible { get; set; } = false;

    private VoxelChunkGridManager? _grid;
    private RingVoxelExplorerController? _explorer;
    private Camera3D? _sourceCamera;
    private SubViewport? _mapViewport;
    private Camera3D? _mapCamera;
    private Node3D? _contentRoot;
    private Node3D? _terrainRoot;
    private Node3D? _trackableRoot;
    private MeshInstance3D? _terrainMesh;
    private MeshInstance3D? _playerMarker;
    private MeshInstance3D? _landingTether;
    private MeshInstance3D? _landingMarker;
    private CylinderMesh? _landingTetherMesh;
    private ShaderMaterial? _terrainMaterial;
    private PanelContainer? _panel;
    private Label? _modeLabel;
    private Label? _infoLabel;
    private Label? _coordinateLabel;
    private Task<MinimapMeshData>? _pendingBuild;
    private RingCanonicalPosition _lastRequestedCenter;
    private bool _hasRequestedCenter;
    private double _rebuildAccumulator;
    private double _renderAccumulator;
    private double _infoAccumulator;
    private float _markerHeight;
    private ulong _lastEditVersion;
    private RingCanonicalPosition _meshCenter;
    private float _meshCenterElevation;
    private bool _hasMeshCenter;
    private float _landingRelativeY;
    private double _trackableScanAccumulator;
    private bool _captureMinimap;
    private bool _captureHighAltitudeLod;
    private bool _captureGroundHorizon;
    private bool _captureBandSite;
    private bool _captureBandSmoothOnly;
    private bool _captureBandProxyOnly;
    private bool _captureCloseMinimap;
    private bool _captureHydrology;
    private bool _previewHydrology;
    private bool _captureMeshReady;
    private int _captureFrames;
    private float _close3DFactor;
    private readonly Dictionary<RingMinimapTrackable, MeshInstance3D> _trackableVisuals = new();

    private sealed record MinimapMeshData(
        Vector3[] Vertices,
        Vector3[] Normals,
        Color[] Colors,
        Vector2[] Uvs,
        int[] Indices,
        float CenterElevation,
        float MaximumDisplayHeight,
        RingCanonicalPosition Center,
        ulong EditVersion);

    public override void _Ready()
    {
        Layer = 40;
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _sourceCamera = GetNodeOrNull<Camera3D>(SourceCameraPath);
        if (_grid?.TerrainSampler == null || _explorer == null || _sourceCamera == null)
        {
            GD.PushError("RingTerrainMinimap could not resolve its grid, terrain sampler, explorer, or camera.");
            SetProcess(false);
            return;
        }

        string[] arguments = OS.GetCmdlineUserArgs();
        _captureMinimap = Array.Exists(arguments, argument => argument == "--capture-minimap");
        _captureHighAltitudeLod = Array.Exists(
            arguments,
            argument => argument == "--capture-high-altitude-lod");
        _captureGroundHorizon = Array.Exists(
            arguments,
            argument => argument == "--capture-ground-horizon");
        _captureBandSite = Array.Exists(arguments,argument => argument == "--capture-band-site");
        _captureBandSmoothOnly = Array.Exists(arguments,argument => argument == "--capture-band-site-smooth-only");
        _captureBandProxyOnly = Array.Exists(arguments,argument => argument == "--capture-band-site-proxy-only");
        _captureCloseMinimap = Array.Exists(arguments, argument => argument == "--capture-minimap-close");
        _captureHydrology = Array.Exists(arguments, argument => argument == "--capture-hydrology");
        _previewHydrology = Array.Exists(arguments, argument => argument == "--hydrology-preview");
        if (_captureBandSite||_captureBandSmoothOnly||_captureBandProxyOnly)
        {
            _captureMinimap=true;
            MoveExplorerToBandSite();
            ApplyBandCaptureIsolation();
        }
        else if (_captureHighAltitudeLod)
        {
            _captureMinimap = true;
            MapSpanMeters = 32000.0f;
            MoveExplorerToCaptureAltitude(26000.0, lookStraightDown: true);
        }
        else if (_captureGroundHorizon)
        {
            _captureMinimap = true;
            MoveExplorerToGroundCapture();
        }
        else if (_captureHydrology)
        {
            _captureMinimap = true;
            MapSpanMeters = 32000.0f;
            MoveExplorerToHydrologyWaypoint(3000.0);
        }
        else if (_previewHydrology)
        {
            MapSpanMeters = 32000.0f;
            MoveExplorerToHydrologyWaypoint(300.0);
        }
        else if (_captureCloseMinimap)
        {
            _captureMinimap = true;
            MapSpanMeters = MinimumSpanMeters;
        }
        else if (_captureMinimap)
        {
            MoveExplorerToCaptureAltitude(15000.0, lookStraightDown: false);
        }

        CreateMapViewport();
        CreateHudPanel();
        QueueTerrainBuild(force: true);
        UpdateMapOrientation();
        UpdateInformation();
    }

    public override void _ExitTree()
    {
        // The worker owns an independent sampler and disposes it before exit.
        _pendingBuild = null;
    }

    public override void _Process(double delta)
    {
        if (_grid?.TerrainSampler == null || _explorer == null) return;

        if (_pendingBuild is { IsCompletedSuccessfully: true } completed)
        {
            ApplyTerrainMesh(completed.Result);
            _pendingBuild = null;
        }
        else if (_pendingBuild is { IsFaulted: true } faulted)
        {
            GD.PushWarning($"Minimap terrain build failed: {faulted.Exception?.GetBaseException().Message}");
            _pendingBuild = null;
        }

        _rebuildAccumulator += delta;
        if (_rebuildAccumulator >= Math.Max(0.1, RebuildIntervalSeconds))
        {
            _rebuildAccumulator = 0.0;
            QueueTerrainBuild(force: false);
        }

        _renderAccumulator += delta;
        if (_mapViewport != null && _renderAccumulator >= 1.0 / Math.Max(5.0, RenderFramesPerSecond))
        {
            _renderAccumulator = 0.0;
            _mapViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        }

        _infoAccumulator += delta;
        if (_infoAccumulator >= 0.2)
        {
            _infoAccumulator = 0.0;
            UpdateInformation();
        }

        UpdateMapOrientation();
        UpdatePlayerCenteredContent();
        UpdateTrackableVisuals(delta);

        bool bandCapture=_captureBandSite||_captureBandSmoothOnly||_captureBandProxyOnly;
        bool terrainReady=!bandCapture||GetNodeOrNull<SmoothTerrainGrid>("../SmoothTerrainGrid")?.IsQuiescent==true;
        if (_captureMinimap && _captureMeshReady && terrainReady && ++_captureFrames == 30)
            CaptureMinimapAndQuit();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey key || !key.Pressed || key.Echo) return;
        if (key.Keycode == Key.M)
        {
            if (_panel != null) _panel.Visible = !_panel.Visible;
            GetViewport().SetInputAsHandled();
        }
        else if (key.Keycode == Key.N)
        {
            HeadingUp = !HeadingUp;
            UpdateMapOrientation();
            GetViewport().SetInputAsHandled();
        }
        else if (key.Keycode is Key.Equal or Key.KpAdd)
        {
            SetMapSpan(MapSpanMeters * 0.75f);
            GetViewport().SetInputAsHandled();
        }
        else if (key.Keycode is Key.Minus or Key.KpSubtract)
        {
            SetMapSpan(MapSpanMeters / 0.75f);
            GetViewport().SetInputAsHandled();
        }
    }

    private void CreateMapViewport()
    {
        _mapViewport = new SubViewport
        {
            Name = "TerrainMapViewport",
            Size = new Vector2I(PixelSize, PixelSize),
            OwnWorld3D = true,
            TransparentBg = false,
            HandleInputLocally = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Once,
            Msaa3D = Viewport.Msaa.Msaa2X,
            ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled,
            UseTaa = false,
            UseDebanding = true
        };
        AddChild(_mapViewport);

        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.018f, 0.027f, 0.040f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.72f, 0.78f, 0.86f),
            AmbientLightEnergy = 0.8f
        };
        _mapViewport.AddChild(new WorldEnvironment { Environment = environment });

        _contentRoot = new Node3D { Name = "PlayerRelativeContent" };
        _mapViewport.AddChild(_contentRoot);
        _terrainRoot = new Node3D { Name = "TerrainRoot" };
        _contentRoot.AddChild(_terrainRoot);
        _trackableRoot = new Node3D { Name = "Trackables" };
        _contentRoot.AddChild(_trackableRoot);
        _terrainMesh = new MeshInstance3D
        {
            Name = "ElevationTerrain",
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            IgnoreOcclusionCulling = true
        };
        _terrainRoot.AddChild(_terrainMesh);

        Shader? shader = GD.Load<Shader>("res://materials/ring_terrain_minimap.gdshader");
        if (shader != null)
        {
            _terrainMaterial = new ShaderMaterial { Shader = shader };
            _terrainMaterial.SetShaderParameter("vertical_scale", VerticalScale);
            _terrainMaterial.SetShaderParameter("contour_interval_meters", ContourIntervalMeters);
            _terrainMesh.MaterialOverride = _terrainMaterial;
        }

        _playerMarker = new MeshInstance3D
        {
            Name = "PlayerHeading",
            Mesh = BuildPlayerMarkerMesh(),
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = new Color(1.0f, 0.82f, 0.12f),
                NoDepthTest = true
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        _mapViewport.AddChild(_playerMarker);

        var tetherMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(0.25f, 0.88f, 1.0f, 0.82f),
            NoDepthTest = true
        };
        _landingTetherMesh = new CylinderMesh
        {
            TopRadius = Mathf.Max(1.5f, MapSpanMeters * 0.0022f),
            BottomRadius = Mathf.Max(1.5f, MapSpanMeters * 0.0022f),
            Height = 1.0f,
            RadialSegments = 8
        };
        _landingTether = new MeshInstance3D
        {
            Name = "LandingTether",
            Mesh = _landingTetherMesh,
            MaterialOverride = tetherMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        _contentRoot.AddChild(_landingTether);
        _landingMarker = new MeshInstance3D
        {
            Name = "LandingPoint",
            Mesh = new CylinderMesh
            {
                TopRadius = Mathf.Max(4.0f, MapSpanMeters * 0.009f),
                BottomRadius = Mathf.Max(4.0f, MapSpanMeters * 0.009f),
                Height = Mathf.Max(1.0f, MapSpanMeters * 0.001f),
                RadialSegments = 24
            },
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = new Color(0.12f, 0.65f, 1.0f),
                NoDepthTest = true
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        _contentRoot.AddChild(_landingMarker);

        _mapCamera = new Camera3D
        {
            Name = "TerrainMapCamera",
            Projection = Camera3D.ProjectionType.Orthogonal,
            Current = true,
            Near = 1.0f
        };
        _mapViewport.AddChild(_mapCamera);
        UpdateCameraForSpan();
    }

    private void CreateHudPanel()
    {
        int panelWidth = PixelSize + 18;
        int panelHeight = PixelSize + 104;
        _panel = new PanelContainer
        {
            Name = "TerrainMinimapPanel",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            OffsetLeft = -panelWidth - 16,
            OffsetTop = -panelHeight - 16,
            OffsetRight = -16,
            OffsetBottom = -16
        };
        // The status text is live data (and can become very long while
        // travelling at noclip speed).  Containers otherwise use a child's
        // combined minimum size and can grow this panel beyond the viewport.
        // Treat the HUD as a fixed, clipped instrument instead.
        _panel.CustomMinimumSize = new Vector2(panelWidth, panelHeight);
        _panel.Size = new Vector2(panelWidth, panelHeight);
        _panel.ClipContents = true;
        _panel.SetAnchorsPreset(Control.LayoutPreset.BottomRight, keepOffsets: true);
        var panelStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.012f, 0.020f, 0.032f, 0.92f),
            BorderColor = new Color(0.25f, 0.48f, 0.62f, 0.9f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8
        };
        _panel.AddThemeStyleboxOverride("panel", panelStyle);
        AddChild(_panel);
        _panel.Visible = StartVisible || _captureMinimap || _previewHydrology;

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 8);
        margin.AddThemeConstantOverride("margin_right", 8);
        margin.AddThemeConstantOverride("margin_top", 7);
        margin.AddThemeConstantOverride("margin_bottom", 7);
        _panel.AddChild(margin);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 3);
        margin.AddChild(column);

        var header = new HBoxContainer();
        column.AddChild(header);
        var title = new Label { Text = "TERRAIN MAP", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        title.AddThemeColorOverride("font_color", new Color(0.72f, 0.92f, 1.0f));
        title.AddThemeFontSizeOverride("font_size", 14);
        header.AddChild(title);
        _modeLabel = new Label { HorizontalAlignment = HorizontalAlignment.Right };
        _modeLabel.AddThemeColorOverride("font_color", new Color(0.95f, 0.80f, 0.30f));
        header.AddChild(_modeLabel);

        var texture = new TextureRect
        {
            Texture = _mapViewport!.GetTexture(),
            CustomMinimumSize = new Vector2(PixelSize, PixelSize),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        column.AddChild(texture);

        _coordinateLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            CustomMinimumSize = new Vector2(PixelSize, 14)
        };
        _coordinateLabel.AddThemeColorOverride("font_color", new Color(0.62f, 0.76f, 0.83f));
        _coordinateLabel.AddThemeFontSizeOverride("font_size", 11);
        column.AddChild(_coordinateLabel);
        _infoLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            CustomMinimumSize = new Vector2(PixelSize, 30)
        };
        _infoLabel.AddThemeColorOverride("font_color", new Color(0.88f, 0.94f, 0.98f));
        _infoLabel.AddThemeFontSizeOverride("font_size", 12);
        column.AddChild(_infoLabel);
    }

    private void QueueTerrainBuild(bool force)
    {
        if (_pendingBuild != null || _grid?.TerrainSampler == null || _explorer == null) return;
        RingCanonicalPosition center = _explorer.CanonicalPosition;
        RingLodSurfaceEditSnapshot edits = _grid.CaptureLodSurfaceEditSnapshot();
        float rebuildDistance = GetEffectiveRebuildDistance();
        if (!force && _hasRequestedCenter
            && ShortestAroundDistance(center.AroundMeters, _lastRequestedCenter.AroundMeters) < rebuildDistance
            && Math.Abs(center.AcrossMeters - _lastRequestedCenter.AcrossMeters) < rebuildDistance
            && edits.Version == _lastEditVersion)
        {
            return;
        }

        _lastRequestedCenter = center;
        _hasRequestedCenter = true;
        _lastEditVersion = edits.Version;
        float closeFactor = GetClose3DFactor(MapSpanMeters);
        int cells = Mathf.Clamp(
            Mathf.RoundToInt(Mathf.Lerp(TerrainCells, CloseTerrainCells, closeFactor)),
            16,
            128);
        float overscan = Mathf.Lerp(
            Mathf.Clamp(TerrainOverscan, 1.0f, 2.0f),
            Mathf.Clamp(CloseTerrainOverscan, 1.5f, 3.0f),
            closeFactor);
        float span = Mathf.Clamp(MapSpanMeters, MinimumSpanMeters, MaximumSpanMeters) * overscan;
        float verticalScale = VerticalScale;
        float ringWidth = _grid.RingAcrossWidthMeters;
        float structuralDepth = _grid.StructuralDepthMeters;
        RingTerrainSampler sampler = _grid.TerrainSampler.CreateIndependentCopy();
        _pendingBuild = Task.Run(() => BuildTerrainData(
            center, cells, span, verticalScale, ringWidth, structuralDepth, sampler, edits));
    }

    private MinimapMeshData BuildTerrainData(
        RingCanonicalPosition center,
        int cells,
        float span,
        float verticalScale,
        float ringWidth,
        float structuralDepth,
        RingTerrainSampler sampler,
        RingLodSurfaceEditSnapshot edits)
    {
        try
        {
            int row = cells + 1;
            int count = row * row;
            float step = span / cells;
			var heights = new float[count];
            var vertices = new Vector3[count];
            var normals = new Vector3[count];
            var colors = new Color[count];
            var uvs = new Vector2[count];
            int centerAcross = Mathf.Clamp(Mathf.RoundToInt((float)center.AcrossMeters), 0, Mathf.RoundToInt(ringWidth));
            int centerAround = (int)Math.Round(center.AroundMeters);
			TerrainSample centerSample = edits.GetPresentationTerrainSample(sampler, centerAround, centerAcross);
			bool floatSurface = sampler.UsesRuntimeSurfaceGeneration || edits.UsesFloatSurface;
			float centerHeight = floatSurface
				? Mathf.Max(edits.GetPresentationSurfaceHeight(sampler, centerAround, centerAcross), centerSample.HasSurfaceWater ? centerSample.WaterSurfaceHeight : float.NegativeInfinity)
				: centerSample.ResolveDisplayedHeight(edits.TryGetEditedSurfaceHeight(centerAround, centerAcross, out int centerEditedHeight) ? centerEditedHeight : centerSample.SurfaceHeight);
            float maximumHeight = float.NegativeInfinity;

            for (int z = 0; z <= cells; z++)
            for (int x = 0; x <= cells; x++)
            {
                float localX = x * step - span * 0.5f;
                float localZ = z * step - span * 0.5f;
                int around = (int)Math.Round(center.AroundMeters + localX);
                int across = (int)Math.Round(center.AcrossMeters + localZ);
                bool outside = across < 0 || across > ringWidth;
                int sampleAcross = Mathf.Clamp(across, 0, Mathf.RoundToInt(ringWidth));
				TerrainSample sample = edits.GetPresentationTerrainSample(sampler, around, sampleAcross);
				float height = outside
					? -structuralDepth
					: floatSurface
						? Mathf.Max(edits.GetPresentationSurfaceHeight(sampler, around, sampleAcross), sample.HasSurfaceWater ? sample.WaterSurfaceHeight : float.NegativeInfinity)
						: sample.ResolveDisplayedHeight(edits.TryGetEditedSurfaceHeight(around, sampleAcross, out int editedHeight) ? editedHeight : sample.SurfaceHeight);
                int index = z * row + x;
                heights[index] = height;
                vertices[index] = new Vector3(localX, (height - centerHeight) * verticalScale, localZ);
                colors[index] = outside
                    ? new Color(0.07f, 0.09f, 0.12f, 1.0f)
                    : RingTerrainVisuals.ResolveSeededColor(
						 sample, Mathf.FloorToInt(height), sampler,
                        new Color(0.10f, 0.19f, 0.11f),
                        new Color(0.48f, 0.44f, 0.36f));
                uvs[index] = new Vector2(x / (float)cells, z / (float)cells);
                maximumHeight = Mathf.Max(maximumHeight, vertices[index].Y);
            }

            for (int z = 0; z <= cells; z++)
            for (int x = 0; x <= cells; x++)
            {
                int left = z * row + Mathf.Max(0, x - 1);
                int right = z * row + Mathf.Min(cells, x + 1);
                int down = Mathf.Max(0, z - 1) * row + x;
                int up = Mathf.Min(cells, z + 1) * row + x;
                float dx = (heights[right] - heights[left]) * verticalScale / Math.Max(step, 1.0f);
                float dz = (heights[up] - heights[down]) * verticalScale / Math.Max(step, 1.0f);
                normals[z * row + x] = new Vector3(-dx, 2.0f, -dz).Normalized();
            }

            var indices = new int[cells * cells * 6];
            int cursor = 0;
            for (int z = 0; z < cells; z++)
            for (int x = 0; x < cells; x++)
            {
                int i00 = z * row + x, i10 = i00 + 1, i01 = i00 + row, i11 = i01 + 1;
                indices[cursor++] = i00; indices[cursor++] = i01; indices[cursor++] = i10;
                indices[cursor++] = i10; indices[cursor++] = i01; indices[cursor++] = i11;
            }
            return new MinimapMeshData(vertices, normals, colors, uvs, indices,
                centerHeight, maximumHeight, center, edits.Version);
        }
        finally
        {
            sampler.Dispose();
        }
    }

    private void ApplyTerrainMesh(MinimapMeshData data)
    {
        if (_terrainMesh == null) return;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = data.Vertices;
        arrays[(int)Mesh.ArrayType.Normal] = data.Normals;
        arrays[(int)Mesh.ArrayType.Color] = data.Colors;
        arrays[(int)Mesh.ArrayType.TexUV] = data.Uvs;
        arrays[(int)Mesh.ArrayType.Index] = data.Indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _terrainMesh.Mesh = mesh;
        _terrainMaterial?.SetShaderParameter("center_elevation_meters", data.CenterElevation);
        _terrainMaterial?.SetShaderParameter("vertical_scale", VerticalScale);
        _terrainMaterial?.SetShaderParameter("contour_interval_meters", GetEffectiveContourInterval());
        _markerHeight = data.MaximumDisplayHeight;
        _meshCenter = data.Center;
        _meshCenterElevation = data.CenterElevation;
        _hasMeshCenter = true;
        if (_playerMarker != null) _playerMarker.Position = Vector3.Zero;
        _captureMeshReady = true;
        _mapViewport!.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
    }

    private void UpdateMapOrientation()
    {
        if (_grid == null || _explorer == null || _sourceCamera == null || _contentRoot == null || _playerMarker == null) return;
        double angle = CanonicalAngle(_explorer.CanonicalPosition);
        Vector3 tangentLocal = new(-(float)Math.Sin(angle), 0.0f, (float)Math.Cos(angle));
        Vector3 cameraForwardWorld = -_sourceCamera.GlobalBasis.Z;
        Vector3 cameraForwardLocal = _grid.GlobalBasis.Orthonormalized().Inverse() * cameraForwardWorld;
        float around = cameraForwardLocal.Dot(tangentLocal);
        float across = cameraForwardLocal.Y;
        float markerYaw = Mathf.Atan2(-around, -across);
        _contentRoot.Rotation = new Vector3(0.0f, HeadingUp ? -markerYaw : 0.0f, 0.0f);
        _playerMarker.Rotation = new Vector3(0.0f, HeadingUp ? 0.0f : markerYaw, 0.0f);
        UpdateModeLabel();
    }

    private void UpdateInformation()
    {
        if (_grid?.TerrainSampler == null || _explorer == null) return;
        RingCanonicalPosition position = _explorer.CanonicalPosition;
        int across = Mathf.Clamp(Mathf.RoundToInt((float)position.AcrossMeters), 0, Mathf.RoundToInt(_grid.RingAcrossWidthMeters));
        RingLodSurfaceEditSnapshot edits = _grid.CaptureLodSurfaceEditSnapshot();
		TerrainSample sample = edits.GetPresentationTerrainSample(_grid.TerrainSampler, (int)Math.Round(position.AroundMeters), across);
        int around = (int)Math.Round(position.AroundMeters);
		bool floatSurface = _grid.TerrainSampler.UsesRuntimeSurfaceGeneration || edits.UsesFloatSurface;
		float elevation = floatSurface
			? edits.GetPresentationSurfaceHeight(_grid.TerrainSampler, around, across)
			: edits.TryGetEditedSurfaceHeight(around, across, out int editedElevation) ? editedElevation : sample.SurfaceHeight;
        double altitude = position.VerticalMeters - elevation;
        _landingRelativeY = (float)(-altitude * VerticalScale);
        if (_coordinateLabel != null)
        {
            string spanText = MapSpanMeters < 1000.0f
                ? $"{MapSpanMeters:0} m"
                : $"{MapSpanMeters / 1000.0f:0.#} km";
            _coordinateLabel.Text = $"AROUND +  →   ACROSS +  ↑   |   span {spanText}";
        }
        if (_infoLabel != null)
        {
            string hydrology = sample.HasSurfaceWater
                ? $"  |  {sample.WaterKind.ToString().ToLowerInvariant()} {sample.WaterSurfaceHeight:N0} m / {sample.WaterDepth:N0} m deep"
                : string.Empty;
            string noclip = string.Empty;
            if (_explorer.NoclipEnabled)
            {
                float currentSpeed = _explorer.NoclipCurrentSpeedMetersPerSecond;
                float targetSpeed = _explorer.NoclipTargetSpeedMetersPerSecond;
                string transition = Mathf.Abs(currentSpeed - targetSpeed) > targetSpeed * 0.02f
                    ? $"{FormatFlightSpeed(currentSpeed)} → {FormatFlightSpeed(targetSpeed)}"
                    : FormatFlightSpeed(targetSpeed);
                noclip = $"  |  noclip {transition}";
            }
            _infoLabel.Text = $"{sample.Region}  |  ground {elevation:N0} m{hydrology}  |  alt {altitude:N0} m  |  gravity {_explorer.CurrentGravityFactor * 100.0f:0}%{noclip}\nPLAYER-TRACKED  •  M hide  •  +/- zoom  •  N rotate";
        }
    }

    private static string FormatFlightSpeed(float speed)
    {
        if (speed >= 1000.0f) return $"{speed / 1000.0f:0.##} km/s";
        if (speed >= 10.0f) return $"{speed:0} m/s";
        return $"{speed:0.0} m/s";
    }

    private void SetMapSpan(float span)
    {
        MapSpanMeters = Mathf.Clamp(span, MinimumSpanMeters, MaximumSpanMeters);
        if (_playerMarker != null) _playerMarker.Mesh = BuildPlayerMarkerMesh();
        RebuildLandingIndicatorMeshes();
        UpdateCameraForSpan();
        _hasRequestedCenter = false;
        QueueTerrainBuild(force: true);
        UpdateInformation();
    }

    private float GetClose3DFactor(float span)
    {
        float transitionStart = Mathf.Max(
            Close3DTransitionEndMeters + 1.0f,
            Close3DTransitionStartMeters);
        float transitionEnd = Mathf.Clamp(
            Close3DTransitionEndMeters,
            MinimumSpanMeters,
            transitionStart - 1.0f);
        double logStart = Math.Log(transitionStart);
        double logEnd = Math.Log(transitionEnd);
        float t = (float)((Math.Log(Mathf.Clamp(span, transitionEnd, transitionStart)) - logStart)
            / (logEnd - logStart));
        t = Mathf.Clamp(t, 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }

    private float GetEffectiveRebuildDistance()
    {
        float spanDistance = MapSpanMeters * 0.06f;
        return Mathf.Min(
            Mathf.Max(CloseRebuildDistanceMeters, spanDistance),
            Mathf.Max(CloseRebuildDistanceMeters, RebuildDistanceMeters));
    }

    private float GetEffectiveContourInterval()
    {
        float desired = Mathf.Clamp(
            MapSpanMeters / 128.0f,
            CloseContourIntervalMeters,
            ContourIntervalMeters);
        float magnitude = Mathf.Pow(10.0f, Mathf.Floor(Mathf.Log(desired) / Mathf.Log(10.0f)));
        float normalized = desired / magnitude;
        float nice = normalized <= 1.0f ? 1.0f
            : normalized <= 2.5f ? 2.5f
            : normalized <= 5.0f ? 5.0f
            : 10.0f;
        return Mathf.Clamp(nice * magnitude, CloseContourIntervalMeters, ContourIntervalMeters);
    }

    private void UpdateModeLabel()
    {
        if (_modeLabel == null) return;
        string viewMode = _close3DFactor >= 0.65f
            ? "LOCAL 3D"
            : _close3DFactor > 0.05f
                ? "3D TRANSITION"
                : "MAP";
        string orientation = HeadingUp ? "HDG-UP" : "N-UP";
        _modeLabel.Text = $"{viewMode}  •  {orientation}  [N]";
    }

    private void UpdateCameraForSpan()
    {
        if (_mapCamera == null) return;
        float span = Mathf.Clamp(MapSpanMeters, MinimumSpanMeters, MaximumSpanMeters);
        _close3DFactor = GetClose3DFactor(span);
        float elevationDegrees = Mathf.Lerp(
            WideCameraElevationDegrees,
            CloseCameraElevationDegrees,
            _close3DFactor);
        float elevationRadians = Mathf.DegToRad(elevationDegrees);
        float lookAhead = span * Mathf.Clamp(CloseLookAheadFraction, 0.0f, 0.25f) * _close3DFactor;
        Vector3 target = new(0.0f, 0.0f, -lookAhead);
        float distance = span * 1.25f + 1000.0f;
        _mapCamera.Size = span * 1.12f;
        _mapCamera.Far = distance + Mathf.Max(25000.0f, span * 4.0f);
        _mapCamera.Position = target + new Vector3(
            0.0f,
            Mathf.Sin(elevationRadians) * distance,
            Mathf.Cos(elevationRadians) * distance);
        _mapCamera.LookAt(target, Vector3.Up);
        _terrainMaterial?.SetShaderParameter("contour_interval_meters", GetEffectiveContourInterval());
        _terrainMaterial?.SetShaderParameter("close_3d_factor", _close3DFactor);
        UpdateModeLabel();
    }

    private ArrayMesh BuildPlayerMarkerMesh()
    {
        float size = Mathf.Clamp(MapSpanMeters * 0.026f, 6.0f, 2400.0f);
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new[]
        {
            new Vector3(0, 0, -size),
            new Vector3(size * 0.55f, 0, size * 0.65f),
            new Vector3(0, 0, size * 0.30f),
            new Vector3(-size * 0.55f, 0, size * 0.65f)
        };
        arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2, 0, 2, 3 };
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private double ShortestAroundDistance(double a, double b)
    {
        if (_grid == null) return Math.Abs(a - b);
        double circumference = _grid.GetCanonicalRingCircumferenceMeters64();
        double delta = a - b;
        delta -= Math.Round(delta / circumference) * circumference;
        return Math.Abs(delta);
    }

    private double SignedAroundOffset(double target, double origin)
    {
        if (_grid == null) return target - origin;
        double circumference = _grid.GetCanonicalRingCircumferenceMeters64();
        double delta = target - origin;
        delta -= Math.Round(delta / circumference) * circumference;
        return delta;
    }

    private void UpdatePlayerCenteredContent()
    {
        if (_explorer == null || _terrainRoot == null || !_hasMeshCenter) return;
        RingCanonicalPosition player = _explorer.CanonicalPosition;
        _terrainRoot.Position = new Vector3(
            (float)SignedAroundOffset(_meshCenter.AroundMeters, player.AroundMeters),
            (float)((_meshCenterElevation - player.VerticalMeters) * VerticalScale),
            (float)(_meshCenter.AcrossMeters - player.AcrossMeters));

        float visibleHalfExtent = MapSpanMeters * 0.78f;
        bool terrainInVolume = Mathf.Abs(_landingRelativeY) <= visibleHalfExtent;
        _terrainRoot.Visible = terrainInVolume;
        if (_landingTether != null && _landingTetherMesh != null)
        {
            float length = Mathf.Abs(_landingRelativeY);
            _landingTether.Visible = terrainInVolume && length > 1.0f;
            _landingTetherMesh.Height = Mathf.Max(1.0f, length);
            _landingTether.Position = new Vector3(0.0f, _landingRelativeY * 0.5f, 0.0f);
        }
        if (_landingMarker != null)
        {
            _landingMarker.Visible = terrainInVolume && Mathf.Abs(_landingRelativeY) > 1.0f;
            _landingMarker.Position = new Vector3(0.0f, _landingRelativeY, 0.0f);
        }
    }

    private void RebuildLandingIndicatorMeshes()
    {
        if (_landingTetherMesh != null)
        {
            float radius = Mathf.Max(1.5f, MapSpanMeters * 0.0022f);
            _landingTetherMesh.TopRadius = radius;
            _landingTetherMesh.BottomRadius = radius;
        }
        if (_landingMarker != null)
        {
            _landingMarker.Mesh = new CylinderMesh
            {
                TopRadius = Mathf.Max(4.0f, MapSpanMeters * 0.009f),
                BottomRadius = Mathf.Max(4.0f, MapSpanMeters * 0.009f),
                Height = Mathf.Max(1.0f, MapSpanMeters * 0.001f),
                RadialSegments = 24
            };
        }
    }

    private void UpdateTrackableVisuals(double delta)
    {
        if (_grid == null || _explorer == null || _trackableRoot == null) return;
        _trackableScanAccumulator += delta;
        if (_trackableScanAccumulator >= 1.0)
        {
            _trackableScanAccumulator = 0.0;
            RefreshTrackableRegistry();
        }

        RingCanonicalPosition player = _explorer.CanonicalPosition;
        float volumeRadius = MapSpanMeters * 0.75f;
        foreach ((RingMinimapTrackable trackable, MeshInstance3D visual) in _trackableVisuals)
        {
            if (!GodotObject.IsInstanceValid(trackable)) continue;
            RingCanonicalPosition target = trackable.ResolveCanonicalPosition(_grid);
            var relative = new Vector3(
                (float)SignedAroundOffset(target.AroundMeters, player.AroundMeters),
                (float)((target.VerticalMeters - player.VerticalMeters) * VerticalScale),
                (float)(target.AcrossMeters - player.AcrossMeters));
            float limit = Mathf.Min(volumeRadius, trackable.DetectionRangeMeters);
            visual.Visible = relative.Length() <= limit;
            visual.Position = relative;
            float iconSize = Mathf.Max(trackable.DisplaySizeMeters, MapSpanMeters * 0.006f);
            visual.Scale = Vector3.One * iconSize;
        }
    }

    private void RefreshTrackableRegistry()
    {
        if (_trackableRoot == null) return;
        var live = new HashSet<RingMinimapTrackable>();
        foreach (Node node in GetTree().GetNodesInGroup(RingMinimapTrackable.GroupName))
        {
            if (node is not RingMinimapTrackable trackable) continue;
            live.Add(trackable);
            if (_trackableVisuals.ContainsKey(trackable)) continue;
            var material = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = trackable.MapColor,
                NoDepthTest = true
            };
            var visual = new MeshInstance3D
            {
                Name = $"Trackable_{trackable.DisplayName}",
                Mesh = CreateTrackableMesh(trackable.Category),
                MaterialOverride = material,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            _trackableRoot.AddChild(visual);
            _trackableVisuals[trackable] = visual;
        }

        var removed = new List<RingMinimapTrackable>();
        foreach ((RingMinimapTrackable trackable, MeshInstance3D visual) in _trackableVisuals)
        {
            if (live.Contains(trackable) && GodotObject.IsInstanceValid(trackable)) continue;
            visual.QueueFree();
            removed.Add(trackable);
        }
        foreach (RingMinimapTrackable trackable in removed) _trackableVisuals.Remove(trackable);
    }

    private static Mesh CreateTrackableMesh(RingMinimapTrackable.TrackableCategory category)
    {
        return category switch
        {
            RingMinimapTrackable.TrackableCategory.Asteroid => new SphereMesh
            {
                Radius = 0.5f, Height = 1.0f, RadialSegments = 8, Rings = 4
            },
            RingMinimapTrackable.TrackableCategory.Station => new BoxMesh { Size = Vector3.One },
            RingMinimapTrackable.TrackableCategory.Ring => new CylinderMesh
            {
                TopRadius = 0.7f, BottomRadius = 0.7f, Height = 0.12f, RadialSegments = 24
            },
            _ => new SphereMesh { Radius = 0.5f, Height = 1.0f, RadialSegments = 12, Rings = 6 }
        };
    }

    private void MoveExplorerToCaptureAltitude(double altitudeMeters, bool lookStraightDown)
    {
        if (_grid?.TerrainSampler == null || _explorer == null) return;
        RingCanonicalPosition current = _explorer.CanonicalPosition;
        int across = Mathf.Clamp(Mathf.RoundToInt((float)current.AcrossMeters), 0, Mathf.RoundToInt(_grid.RingAcrossWidthMeters));
		RingLodSurfaceEditSnapshot edits = _grid.CaptureLodSurfaceEditSnapshot();
		bool floatSurface = _grid.TerrainSampler.UsesRuntimeSurfaceGeneration || edits.UsesFloatSurface;
		float surface = floatSurface
			? edits.GetPresentationSurfaceHeight(_grid.TerrainSampler, (int)Math.Round(current.AroundMeters), across)
			: _grid.TerrainSampler.GetSurfaceHeight((int)Math.Round(current.AroundMeters), across);
        RingCanonicalPosition elevated = current with
        {
            VerticalMeters = surface + Math.Max(100.0, altitudeMeters)
        };
        _explorer.SetBenchmarkCanonicalPosition(elevated);
        if (lookStraightDown && _sourceCamera != null)
        {
            Vector3 target = _grid.CanonicalRingPositionToWorld(
                elevated with { VerticalMeters = surface });
            Vector3 screenUp = (_grid.GlobalBasis.Orthonormalized() * Vector3.Up).Normalized();
            _sourceCamera.LookAt(target, screenUp);
        }
    }

    private void MoveExplorerToGroundCapture()
    {
        if (_grid?.TerrainSampler == null || _explorer == null) return;
        RingCanonicalPosition current = _explorer.CanonicalPosition;
        int across = Mathf.Clamp(
            Mathf.RoundToInt((float)current.AcrossMeters),
            0,
            Mathf.RoundToInt(_grid.RingAcrossWidthMeters));
		RingLodSurfaceEditSnapshot edits = _grid.CaptureLodSurfaceEditSnapshot();
		bool floatSurface = _grid.TerrainSampler.UsesRuntimeSurfaceGeneration || edits.UsesFloatSurface;
		float surface = floatSurface
			? edits.GetPresentationSurfaceHeight(_grid.TerrainSampler, (int)Math.Round(current.AroundMeters), across)
			: edits.GetSurfaceHeight(_grid.TerrainSampler, (int)Math.Round(current.AroundMeters), across);
        _explorer.SetBenchmarkCanonicalPosition(current with { VerticalMeters = surface + 2.0 });
    }

    private void MoveExplorerToBandSite()
    {
        if(_grid?.TerrainSampler==null||_explorer==null)return;
        const long around=28_779_700;
        const int across=163_800;
        float surface=_grid.TerrainSampler.GetSmoothSurfaceHeight(around,across);
        _explorer.SetBenchmarkCanonicalPosition(new RingCanonicalPosition(around,surface+12.0,across));
        _explorer.SetPresentationView(97.33f,-20.8f);
        GD.Print($"Band-site capture position: around={around}, across={across}, surface={surface:0.00}");
    }

    private void ApplyBandCaptureIsolation()
    {
        Node? world=GetParent();
        if(world==null)return;
        if(_captureBandProxyOnly&&world.GetNodeOrNull<Node3D>("SmoothTerrainGrid") is Node3D smooth)
            smooth.Visible=false;
        if(!_captureBandSmoothOnly)return;
        foreach(Node child in world.GetChildren())
            if(child is RingTerrainProxySector proxy)proxy.Visible=false;
        if(world.GetNodeOrNull<GeometryInstance3D>("GlobalTerrainLod3") is GeometryInstance3D global)
            global.Visible=false;
    }

    private void MoveExplorerToHydrologyWaypoint(double altitudeMeters)
    {
        if (_grid?.TerrainSampler == null || _explorer == null) return;
        const int aroundSamples = 1024;
        const int acrossSamples = 128;
        double circumference = _grid.GetCanonicalRingCircumferenceMeters64();
        float width = _grid.RingAcrossWidthMeters;
        for (int z = 0; z < acrossSamples; z++)
        for (int x = 0; x < aroundSamples; x++)
        {
            int around = (int)Math.Round((x + 0.5) * circumference / aroundSamples);
            int across = Mathf.RoundToInt((z + 0.5f) * width / acrossSamples);
            if (across < 20000 || across > width - 20000.0f) continue;
            TerrainSample sample = _grid.TerrainSampler.GetTerrainSample(around, across);
            if (sample.WaterKind != SurfaceWaterKind.Lake) continue;
            _explorer.SetBenchmarkCanonicalPosition(new RingCanonicalPosition(
                around,
                sample.WaterSurfaceHeight + altitudeMeters,
                across));
            GD.Print($"Hydrology waypoint: kind={sample.WaterKind}, canonical=({around},{across}), water={sample.WaterSurfaceHeight}m, depth={sample.WaterDepth}m, altitude={altitudeMeters:0}m");
            return;
        }

        GD.PushWarning("Hydrology capture could not locate a sampled lake; retaining the launch position.");
    }

    private void CaptureMinimapAndQuit()
    {
        string fileName = _captureBandSmoothOnly
            ? "band_site_smooth_only.png"
            : _captureBandProxyOnly
                ? "band_site_proxy_only.png"
                : _captureBandSite
                    ? "band_site_combined.png"
        : _captureHydrology
            ? "terrain_minimap_hydrology.png"
            : _captureHighAltitudeLod
                ? "high_altitude_lod_clouds.png"
            : _captureGroundHorizon
                ? "ground_horizon_scaled_fallback.png"
            : _captureCloseMinimap
                ? "terrain_minimap_close_3d.png"
                : "terrain_minimap_player_centered.png";
        string path = RingGeneratedOutputPaths.File("captures/minimap", fileName);
        Error error = GetViewport().GetTexture().GetImage().SavePng(path);
        string mode = _captureBandSmoothOnly
            ? "bandSiteSmoothOnly"
            : _captureBandProxyOnly
                ? "bandSiteProxyOnly"
                : _captureBandSite
                    ? "bandSiteCombined"
        : _captureHydrology
            ? "hydrology"
            : _captureHighAltitudeLod
                ? "highAltitudeLod"
                : _captureGroundHorizon
                    ? "groundHorizon"
                : _captureCloseMinimap ? "close3D" : "altitude";
        GD.Print($"Minimap capture: mode={mode}, span={MapSpanMeters:0}m, closeFactor={_close3DFactor:0.00}, contour={GetEffectiveContourInterval():0.#}m, recenter={GetEffectiveRebuildDistance():0.#}m, result={error}:{path}");
        GetTree().Quit(error == Error.Ok ? 0 : 1);
    }

    private double CanonicalAngle(in RingCanonicalPosition position)
    {
        if (_grid == null) return 0.0;
        return position.AroundMeters / (_grid.GetCanonicalRingCircumferenceMeters64() / Math.Tau);
    }
}
