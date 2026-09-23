using System;
using System.IO;
using Godot;
using RingworldTechDemo.UI;
using RingworldTechDemo.Vegetation;
using System.Collections.Generic;

namespace RingworldTechDemo;

/// <summary>Small, self-contained comparison of three directional-light tree shadow casters.</summary>
public partial class SunblockerDemoController : Node3D
{
    public const int DefaultTreeVariantIndex = 3;
    private const int Columns = 3;
    private DirectionalLight3D _sun = null!;
    private Camera3D _camera = null!;
    private WorldEnvironment _world = null!;
    private MeshInstance3D? _ground;
    private readonly MultiMeshInstance3D[] _visible = new MultiMeshInstance3D[Columns];
    private readonly MultiMeshInstance3D[] _proxies = new MultiMeshInstance3D[Columns];
    private readonly MultiMeshInstance3D[] _cards = new MultiMeshInstance3D[Columns];
    private readonly Label _status = new() { Position = new Vector2(18, 18), Size = new Vector2(780, 150) };
    private bool _rebuild = true, _capture, _smoke;
    private int _frames;
    private float _lastCardElevation = float.NaN, _lastCardAzimuth = float.NaN;
    private float _appliedElevation = float.NaN, _appliedAzimuth = float.NaN, _cameraDistanceArgument = -1;
    private float _sunElevation = 38, _sunAzimuth = 35, _distanceLod = 150;
    private int _atlasSize = 2048, _treeCount = 6;
    private bool _distanceHandoff, _showProxies, _singleMethod;
    private int _viewMethod;
    private int _appliedAtlas = -1;
    private Rid _viewportRid;
    private double _hudRefresh;
    private readonly bool[] _alternateActive = new bool[Columns];
    private readonly Label3D[] _methodLabels = new Label3D[Columns];
    private Mesh _treeMesh = null!, _proxyMesh = null!, _cardMesh = null!;
    private IReadOnlyList<ImportedTreeVariant> _treeVariants = Array.Empty<ImportedTreeVariant>();
    private int _treeVariantIndex = DefaultTreeVariantIndex;
    private float _laneSpacing = 7, _rowSpacing = 10, _columnSpacing = 25, _labelHeight = 13, _resetCameraDistance = 46, _resetCameraY = 22, _resetTargetY = 5, _labelPixelSize = .02f;

    public float SunElevation { get => _sunElevation; set { _sunElevation = Mathf.Clamp(value, 8, 80); MarkDirty(); } }
    public float SunAzimuth { get => _sunAzimuth; set { _sunAzimuth = Mathf.PosMod(value, 360); MarkDirty(); } }
    public int ShadowAtlasSize { get => _atlasSize; set { _atlasSize = value <= 1024 ? 1024 : value <= 2048 ? 2048 : 4096; MarkDirty(); } }
    public int TreeCount { get => _treeCount; set { _treeCount = Mathf.Clamp(value, 4, 64); _rebuild = true; MarkDirty(); } }
    public float DistanceLodMeters { get => _distanceLod; set { _distanceLod = Mathf.Clamp(value, 100, 200); MarkDirty(); } }
    public bool DistanceHandoffEnabled { get => _distanceHandoff; set { _distanceHandoff = value; MarkDirty(); } }
    public bool ShowProxyGeometry { get => _showProxies; set { _showProxies = value; MarkDirty(); } }
    public bool SingleMethodView { get => _singleMethod; set { _singleMethod = value; _rebuild = true; MarkDirty(); } }
    /// <summary>0=full tree, 1=low-poly proxy, 2=cutout card when single-method view is enabled.</summary>
    public int SingleMethod { get => _viewMethod; set { _viewMethod = Mathf.Clamp(value, 0, 2); _rebuild = true; MarkDirty(); } }
    public int TreeVariantIndex { get => _treeVariantIndex; set { int clean = Mathf.Clamp(value, 0, ImportedTreeCatalog.VariantCount - 1); if (_treeVariantIndex == clean) return; _treeVariantIndex = clean; if (_treeVariants.Count > 0) ConfigureVariantMeshes(); _rebuild = true; MarkDirty(); } }
    public IReadOnlyList<string> TreeVariantNames { get { var names = new string[_treeVariants.Count]; for (int i = 0; i < names.Length; i++) names[i] = $"{i}: {_treeVariants[i].Species} {_treeVariants[i].Name}"; return names; } }

    public override void _Ready()
    {
        _capture = HasArg("--sunblocker-capture"); _smoke = HasArg("--sunblocker-smoke");
        if (!_capture && !_smoke) LoadDefaults();
        ApplyArguments();
        BuildWorld();
        _viewportRid = GetViewport().GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(_viewportRid, true);
        CallDeferred(nameof(AddDrawer));
        _rebuild = true;
    }

    public override void _Process(double delta)
    {
        if (_rebuild) RebuildInstances();
        ApplySun(); UpdateCardOrientationIfNeeded(); UpdateHandoff();
        if (_dirty && (_saveDelay -= delta) <= 0) SaveDefaults();
        if ((_hudRefresh -= delta) <= 0) { _hudRefresh = .25; double gpu = RenderingServer.ViewportGetMeasuredRenderTimeGpu(_viewportRid); string gpuText = gpu > 0 ? $"{gpu:0.00} ms" : "warming up/unavailable"; string variant = _treeVariants.Count == 0 ? "loading" : $"{_treeVariantIndex}: {_treeVariants[_treeVariantIndex].Species} {_treeVariants[_treeVariantIndex].Name}"; _status.Text = $"TREE SUNBLOCKER  [~] tuning  [R] reset camera\n{variant}: LOD0 visible | authored LOD2 proxy | sun-facing authored LOD3 cards\nAtlas {_atlasSize}² D16: {_atlasSize * _atlasSize * 2 / 1048576f:0.0} MiB theoretical; whole-view {Engine.GetFramesPerSecond():0} FPS, GPU {gpuText}\nGroup handoff: {(_distanceHandoff ? $"{_distanceLod:0}m" : "off")}; debug casters: {(_showProxies ? "shown" : "shadow-only")}"; }
        if (_smoke && ++_frames == 12)
        {
            bool ownership = RunSmokeOwnershipChecks() && RunCatalogOwnershipChecks();
            bool roundtrip = SaveSmokeRoundtrip();
            bool correct = ownership && roundtrip;
            GD.Print($"SUNBLOCKER_SMOKE={(correct ? "PASS" : "FAIL")} ownership={ownership} roundtrip={roundtrip} handoff={_distanceLod:0} sun={_sunElevation:0}/{_sunAzimuth:0}");
            GetTree().Quit(correct ? 0 : 1);
        }
        if (_capture && ++_frames == 120) Capture();
    }

    public override void _ExitTree() { if (!_capture && !_smoke) SaveDefaults(); }

    private void AddDrawer()
    {
        var drawer = new RingLiveTuningDrawer { Name = "RingLiveTuningDrawer", SunblockerDemoPath = new NodePath("..") };
        AddChild(drawer);
        if (HasArg("--sunblocker-drawer")) drawer.SetBenchmarkDrawerOpen(true);
    }

    public override void _Input(InputEvent e)
    {
        if (GetTree().Root.GetMeta(RingLiveTuningDrawer.MouseOwnershipMeta, false).AsBool()) return;
        if (e is InputEventKey { Pressed: true, Echo: false } key && key.Keycode == Key.R) ResetCamera();
        if (e is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        { _camera.RotateY(-motion.Relative.X * .003f); _camera.RotateObjectLocal(Vector3.Right, -motion.Relative.Y * .003f); }
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }) Input.MouseMode = Input.MouseModeEnum.Captured;
        if (e is InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Right } || e is InputEventKey { Pressed: true, Keycode: Key.Escape }) Input.MouseMode = Input.MouseModeEnum.Visible;
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp }) _camera.Position -= _camera.GlobalTransform.Basis.Z * 12;
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown }) _camera.Position += _camera.GlobalTransform.Basis.Z * 12;
    }

    public void ResetCamera()
    {
        _camera.GlobalPosition = new Vector3(0, _resetCameraY, _resetCameraDistance); _camera.LookAt(new Vector3(0, _resetTargetY, 0));
    }

    public void ResetSettings()
    {
        _sunElevation = 38; _sunAzimuth = 35; _atlasSize = 2048; _treeCount = 6; _distanceLod = 150;
        _distanceHandoff = false; _showProxies = false; _singleMethod = false; _viewMethod = 0;
        _treeVariantIndex = DefaultTreeVariantIndex; if (_treeVariants.Count > 0) ConfigureVariantMeshes();
        _rebuild = true; MarkDirty();
    }

    private void BuildWorld()
    {
        var environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color("#7eb4dc"), AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color("#b9d2e8"), AmbientLightEnergy = .7f, TonemapMode = Godot.Environment.ToneMapper.Filmic };
        _world = new WorldEnvironment { Environment = environment }; AddChild(_world);
        _sun = new DirectionalLight3D { LightEnergy = 2.0f, ShadowEnabled = true, DirectionalShadowMaxDistance = 450, DirectionalShadowSplit1 = .12f, DirectionalShadowSplit2 = .32f, DirectionalShadowSplit3 = .62f }; AddChild(_sun);
        _treeVariants = ImportedTreeCatalog.Load(); ConfigureVariantMeshes();
        float cameraDistance = _cameraDistanceArgument > 0 ? _cameraDistanceArgument : _resetCameraDistance;
        _camera = new Camera3D { Current = true, Fov = 58, Position = new Vector3(0, _resetCameraY, cameraDistance) }; AddChild(_camera); _camera.LookAt(new Vector3(0, _resetTargetY, 0));
        var groundMaterial = new StandardMaterial3D { AlbedoColor = new Color("#46643d"), Roughness = .9f };
        _ground = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(240, 480), Material = groundMaterial }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off }; AddChild(_ground); UpdateGroundBounds();
        AddLabels(); UpdateMethodLabels();
        for (int i = 0; i < Columns; i++) { _visible[i] = MakeInstance(_treeMesh, GeometryInstance3D.ShadowCastingSetting.Off); _proxies[i] = MakeInstance(_proxyMesh, GeometryInstance3D.ShadowCastingSetting.ShadowsOnly); _cards[i] = MakeInstance(_cardMesh, GeometryInstance3D.ShadowCastingSetting.ShadowsOnly); }
    }

    private void AddLabels()
    {
        var ui = new CanvasLayer(); AddChild(ui); _status.AddThemeFontSizeOverride("font_size", 16); _status.AddThemeColorOverride("font_color", Colors.White); ui.AddChild(_status);
        for (int i = 0; i < 3; i++) { _methodLabels[i] = new Label3D { Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, PixelSize = _labelPixelSize, FontSize = 64, OutlineSize = 8, Modulate = Colors.White }; AddChild(_methodLabels[i]); }
    }

    private void UpdateMethodLabels()
    {
        if (_methodLabels[0] == null || _treeVariants.Count == 0) return;
        string[] labels = { "FULL TREE\nLOD0", "AUTHORED PROXY\nLOD2", "SUN-FACING CARDS\nLOD3" };
        for (int i = 0; i < Columns; i++) { _methodLabels[i].Text = labels[i]; _methodLabels[i].PixelSize = _labelPixelSize; _methodLabels[i].Visible = !_singleMethod || i == _viewMethod; _methodLabels[i].Position = new Vector3(_singleMethod ? 0 : (i - 1) * _columnSpacing, _labelHeight, _rowSpacing * .5f); }
    }

    private MultiMeshInstance3D MakeInstance(Mesh mesh, GeometryInstance3D.ShadowCastingSetting shadow)
    {
        var node = new MultiMeshInstance3D { Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, InstanceCount = 0, Mesh = mesh }, CastShadow = shadow }; AddChild(node); return node;
    }

    private void ConfigureVariantMeshes()
    {
        ImportedTreeVariant variant = _treeVariants[_treeVariantIndex];
        _treeMesh = CloneDemoMesh(variant.Lods[0]);
        _proxyMesh = CloneDemoMesh(variant.Lods[2]);
        _cardMesh = CloneDemoMesh(variant.Lods[3]);
        _laneSpacing = Mathf.Max(7, variant.WidthMeters * 1.4f); _rowSpacing = Mathf.Max(10, variant.HeightMeters * 1.25f); _columnSpacing = Mathf.Max(25, _laneSpacing * 4.5f);
        float halfPlotWidth = _columnSpacing + _laneSpacing * 1.6f;
        float horizontalHalfTan = Mathf.Tan(Mathf.DegToRad(58) * .5f) * (16.0f / 9.0f);
        _resetCameraDistance = Mathf.Max(30, halfPlotWidth / Mathf.Max(.1f, horizontalHalfTan * .80f));
        _resetTargetY = variant.HeightMeters * .35f; _resetCameraY = _resetTargetY + _resetCameraDistance * Mathf.Tan(Mathf.DegToRad(30));
        _labelPixelSize = _resetCameraDistance * .0004f; _labelHeight = variant.HeightMeters * 1.25f + _labelPixelSize * 120;
        GD.Print($"SUNBLOCKER_LAYOUT variant={variant.Index}:{variant.Name} width={variant.WidthMeters:0.##} height={variant.HeightMeters:0.##} lane={_laneSpacing:0.##} row={_rowSpacing:0.##} column={_columnSpacing:0.##} camera=(0,{_resetCameraY:0.##},{_resetCameraDistance:0.##}) target_y={_resetTargetY:0.##}");
        UpdateGroundBounds();
        SetDemoSunDirection(_sun == null ? Vector3.Forward : _sun.GlobalTransform.Basis.Z);
        if (_methodLabels[0] != null) UpdateMethodLabels();
        if (_camera != null && _cameraDistanceArgument <= 0) ResetCamera();
        if (_visible[0] == null) return;
        for (int i = 0; i < Columns; i++) { _visible[i].Multimesh!.Mesh = _treeMesh; _proxies[i].Multimesh!.Mesh = _proxyMesh; _cards[i].Multimesh!.Mesh = _cardMesh; }
    }

    private static Mesh CloneDemoMesh(Mesh source)
    {
        var output = new ArrayMesh();
        for (int surface = 0; surface < source.GetSurfaceCount(); surface++)
        {
            output.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, source.SurfaceGetArrays(surface));
            Material? sourceMaterial = source.SurfaceGetMaterial(surface);
            if (sourceMaterial is ShaderMaterial shader)
            {
                var clone = (ShaderMaterial)shader.Duplicate();
                clone.SetShaderParameter("day_start_dot", -2.0f); clone.SetShaderParameter("day_full_dot", -1.0f);
                clone.SetShaderParameter("vegetation_wind_enabled", false); clone.SetShaderParameter("day_direct_light_factor", 1.0f);
                clone.SetShaderParameter("local_cloud_shadow_enabled", false); clone.SetShaderParameter("local_cloud_shadow_near_enabled", false); clone.SetShaderParameter("local_cloud_shadow_far_enabled", false);
                output.SurfaceSetMaterial(output.GetSurfaceCount() - 1, clone);
            }
            else output.SurfaceSetMaterial(output.GetSurfaceCount() - 1, sourceMaterial);
        }
        return output;
    }

    private void SetDemoSunDirection(Vector3 towardSun)
    {
        foreach (Mesh mesh in new[] { _treeMesh, _proxyMesh, _cardMesh })
            for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
                if (mesh.SurfaceGetMaterial(surface) is ShaderMaterial material)
                    material.SetShaderParameter("sun_direction", towardSun);
    }

    private void UpdateGroundBounds()
    {
        if (_ground?.Mesh is not PlaneMesh groundMesh) return;
        int maxRows = (64 + 2) / 3;
        float shadowMargin = Mathf.Max(80, _treeVariants.Count == 0 ? 80 : _treeVariants[_treeVariantIndex].HeightMeters * 10);
        float front = _rowSpacing * .8f + shadowMargin, back = -(maxRows - 1) * _rowSpacing - shadowMargin;
        groundMesh.Size = new Vector2(Mathf.Max(240, 2 * (_columnSpacing + _laneSpacing + shadowMargin)), Mathf.Max(480, front - back));
        _ground.Position = new Vector3(0, 0, (front + back) * .5f);
    }

    private void RebuildInstances()
    {
        for (int column = 0; column < Columns; column++)
        {
            MultiMesh full = _visible[column].Multimesh!; MultiMesh proxy = _proxies[column].Multimesh!; MultiMesh card = _cards[column].Multimesh!;
            full.InstanceCount = proxy.InstanceCount = card.InstanceCount = _treeCount;
            for (int n = 0; n < _treeCount; n++)
            {
                int row = n / 3, lane = n % 3; float center = _singleMethod ? 0 : (column - 1) * _columnSpacing; float x = center + (lane - 1) * _laneSpacing; float z = row * -_rowSpacing + _rowSpacing * .8f; float scale = _treeVariants[_treeVariantIndex].HeightMeters * (.8f + ((n * 37) % 5) * .07f);
                Transform3D tree = new(Basis.Identity.Scaled(Vector3.One * scale), new Vector3(x, 0, z));
                full.SetInstanceTransform(n, tree); proxy.SetInstanceTransform(n, tree); card.SetInstanceTransform(n, CardTransform(tree.Origin, scale));
            }
        }
        _rebuild = false;
        UpdateMethodLabels();
    }

    private Transform3D CardTransform(Vector3 position, float scale)
    {
        Vector3 sunDirection = _sun == null ? Vector3.Forward : -_sun.GlobalTransform.Basis.Z; sunDirection.Y = 0;
        if (sunDirection.LengthSquared() < .001f) sunDirection = Vector3.Forward;
        Basis basis = Basis.LookingAt(sunDirection.Normalized(), Vector3.Up).Scaled(new Vector3(scale, scale, scale));
        return new Transform3D(basis, position);
    }

    private void ApplySun()
    {
        if (_appliedAtlas != _atlasSize) { RenderingServer.DirectionalShadowAtlasSetSize(_atlasSize, true); _appliedAtlas = _atlasSize; }
        if (_sun == null || (Mathf.IsEqualApprox(_appliedElevation, _sunElevation) && Mathf.IsEqualApprox(_appliedAzimuth, _sunAzimuth))) return;
        float e = Mathf.DegToRad(_sunElevation), a = Mathf.DegToRad(_sunAzimuth);
        Vector3 direction = new(Mathf.Cos(e) * Mathf.Sin(a), Mathf.Sin(e), Mathf.Cos(e) * Mathf.Cos(a));
        _sun.LookAt(_sun.GlobalPosition - direction, Vector3.Up);
        SetDemoSunDirection(_sun.GlobalTransform.Basis.Z);
        _appliedElevation = _sunElevation; _appliedAzimuth = _sunAzimuth;
    }

    private void UpdateHandoff()
    {
        for (int i = 0; i < Columns; i++)
        {
            bool selected = !_singleMethod || i == _viewMethod;
            bool alternate = i is 1 or 2;
            float center = _singleMethod ? 0 : (i - 1) * _columnSpacing;
            float distance = _camera.GlobalPosition.DistanceTo(new Vector3(center, 0, 0));
            if (_distanceHandoff && alternate) _alternateActive[i] = _alternateActive[i] ? distance > _distanceLod - 5 : distance >= _distanceLod + 5;
            bool useAlternate = alternate && (!_distanceHandoff || _alternateActive[i]);
            _visible[i].Visible = selected && !(_showProxies && useAlternate);
            _visible[i].CastShadow = selected && !useAlternate ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off;
            _proxies[i].Visible = i == 1 && selected && useAlternate;
            _proxies[i].CastShadow = _proxies[i].Visible ? (_showProxies ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.ShadowsOnly) : GeometryInstance3D.ShadowCastingSetting.Off;
            _cards[i].Visible = i == 2 && selected && useAlternate;
            _cards[i].CastShadow = _cards[i].Visible ? (_showProxies ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.ShadowsOnly) : GeometryInstance3D.ShadowCastingSetting.Off;
        }
    }

    private void UpdateCardOrientationIfNeeded()
    {
        if (Mathf.IsEqualApprox(_lastCardElevation, _sunElevation) && Mathf.IsEqualApprox(_lastCardAzimuth, _sunAzimuth)) return;
        for (int column = 0; column < Columns; column++)
            for (int n = 0; n < _treeCount; n++)
            {
                Transform3D t = _visible[column].Multimesh!.GetInstanceTransform(n);
                _cards[column].Multimesh!.SetInstanceTransform(n, CardTransform(t.Origin, t.Basis.X.Length()));
            }
        _lastCardElevation = _sunElevation; _lastCardAzimuth = _sunAzimuth;
    }

    private bool HasArg(string prefix) => Array.Exists(OS.GetCmdlineUserArgs(), a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    private void ApplyArguments() { const string elevation = "--sunblocker-elevation=", azimuth = "--sunblocker-azimuth=", distance = "--sunblocker-distance=", camera = "--sunblocker-camera-distance=", tree = "--sunblocker-tree="; foreach (string a in OS.GetCmdlineUserArgs()) { if (a.StartsWith(elevation) && float.TryParse(a[elevation.Length..], System.Globalization.CultureInfo.InvariantCulture, out float e)) _sunElevation = Mathf.Clamp(e, 8, 80); else if (a.StartsWith(azimuth) && float.TryParse(a[azimuth.Length..], System.Globalization.CultureInfo.InvariantCulture, out float z)) _sunAzimuth = Mathf.PosMod(z, 360); else if (a.StartsWith(distance) && float.TryParse(a[distance.Length..], System.Globalization.CultureInfo.InvariantCulture, out float d)) { _distanceLod = Mathf.Clamp(d,100,200); _distanceHandoff = true; } else if (a.StartsWith(camera) && float.TryParse(a[camera.Length..], System.Globalization.CultureInfo.InvariantCulture, out float c)) _cameraDistanceArgument = Mathf.Max(5,c); else if (a.StartsWith(tree) && int.TryParse(a[tree.Length..], out int index)) _treeVariantIndex = Mathf.Clamp(index, 0, ImportedTreeCatalog.VariantCount - 1); else if (a.Equals("--sunblocker-debug", StringComparison.OrdinalIgnoreCase)) _showProxies = true; else if (a.StartsWith("--sunblocker-mode=", StringComparison.OrdinalIgnoreCase)) { _singleMethod = true; string mode = a["--sunblocker-mode=".Length..]; _viewMethod = mode.Equals("proxy", StringComparison.OrdinalIgnoreCase) ? 1 : mode.Equals("cards", StringComparison.OrdinalIgnoreCase) ? 2 : 0; } } }
    private bool RunSmokeOwnershipChecks()
    {
        bool handoff = _distanceHandoff; Vector3 camera = _camera.GlobalPosition; float elevation = _sunElevation, azimuth = _sunAzimuth; int atlas = _atlasSize;
        _distanceHandoff = true; _camera.GlobalPosition = new Vector3(0, 10, 20); UpdateHandoff();
        bool near = _visible[1].CastShadow == GeometryInstance3D.ShadowCastingSetting.On && _visible[2].CastShadow == GeometryInstance3D.ShadowCastingSetting.On && !_proxies[1].Visible && !_cards[2].Visible;
        _camera.GlobalPosition = new Vector3(0, 10, 260); UpdateHandoff();
        bool far = _visible[1].CastShadow == GeometryInstance3D.ShadowCastingSetting.Off && _proxies[1].Visible && _proxies[1].CastShadow == GeometryInstance3D.ShadowCastingSetting.ShadowsOnly && _visible[2].CastShadow == GeometryInstance3D.ShadowCastingSetting.Off && _cards[2].Visible && _cards[2].CastShadow == GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;
        _atlasSize = atlas == 1024 ? 2048 : 1024; ApplySun(); bool atlasChanged = _appliedAtlas == _atlasSize;
        _sunAzimuth = Mathf.PosMod(azimuth + 15, 360); ApplySun(); UpdateCardOrientationIfNeeded(); Vector3 horizontalSun = -_sun.GlobalTransform.Basis.Z; horizontalSun.Y = 0; Vector3 expectedAxis = CardTransform(Vector3.Zero, 1).Basis.Z; expectedAxis.Y = 0; bool dummyBackend = DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase); Vector3 uploadedAxis = dummyBackend ? expectedAxis : _cards[2].Multimesh!.GetInstanceTransform(0).Basis.Z; uploadedAxis.Y = 0; bool oriented = Mathf.Abs(uploadedAxis.Normalized().Dot(horizontalSun.Normalized())) > .99f;
        GD.Print($"SUNBLOCKER_SMOKE_DETAIL backend={DisplayServer.GetName()} orientation_check={(dummyBackend ? "cpu-upload-transform" : "multimesh-readback")} near={near} far={far} atlas={atlasChanged} oriented={oriented} proxyVisible={_proxies[1].Visible} proxyCast={_proxies[1].CastShadow} cardVisible={_cards[2].Visible} cardCast={_cards[2].CastShadow} axis={uploadedAxis} sunAxis={horizontalSun}");
        _distanceHandoff = handoff; _camera.GlobalPosition = camera; _sunElevation = elevation; _sunAzimuth = azimuth; _atlasSize = atlas; ApplySun(); UpdateCardOrientationIfNeeded(); UpdateHandoff();
        return near && far && atlasChanged && oriented;
    }
    private bool RunCatalogOwnershipChecks()
    {
        int original = _treeVariantIndex; ImportedTreeVariant source = _treeVariants[original]; bool valid = _treeVariants.Count == ImportedTreeCatalog.VariantCount && source.Lods.Length == 4 && _treeMesh.GetSurfaceCount() > 0 && _proxyMesh.GetSurfaceCount() > 0 && _cardMesh.GetSurfaceCount() > 0;
        bool normalized = TryNormalizedRootAndHeight(source.Lods[0]);
        Material? authoredMaterial = source.Lods[0].SurfaceGetMaterial(0), demoMaterial = _treeMesh.SurfaceGetMaterial(0);
        ulong authoredTextureId = 0, cloneTextureId = 0;
        bool materialIsolation = false;
        if (authoredMaterial is ShaderMaterial authored && demoMaterial is ShaderMaterial clone)
        {
            GodotObject? authoredTexture = authored.GetShaderParameter("albedo_texture").AsGodotObject();
            GodotObject? cloneTexture = clone.GetShaderParameter("albedo_texture").AsGodotObject();
            authoredTextureId = authoredTexture?.GetInstanceId() ?? 0; cloneTextureId = cloneTexture?.GetInstanceId() ?? 0;
            materialIsolation = !ReferenceEquals(authored, clone) && authoredTextureId != 0 && authoredTextureId == cloneTextureId;
        }
        bool uvPreserved = source.Lods[0].SurfaceGetArrays(0)[(int)Mesh.ArrayType.TexUV].VariantType == _treeMesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.TexUV].VariantType && source.Lods[0].SurfaceGetArrays(0)[(int)Mesh.ArrayType.TexUV2].VariantType == _treeMesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.TexUV2].VariantType;
        TreeVariantIndex = (original + 1) % ImportedTreeCatalog.VariantCount;
        bool changed = _visible[0].Multimesh!.Mesh == _treeMesh && _proxies[1].Multimesh!.Mesh == _proxyMesh && _cards[2].Multimesh!.Mesh == _cardMesh;
        bool sunApplied = _treeMesh.SurfaceGetMaterial(0) is ShaderMaterial selected && selected.GetShaderParameter("sun_direction").AsVector3().IsEqualApprox(_sun.GlobalTransform.Basis.Z);
        ResetSettings(); bool reset = _treeVariantIndex == DefaultTreeVariantIndex && _visible[0].Multimesh!.Mesh == _treeMesh && _proxies[1].Multimesh!.Mesh == _proxyMesh && _cards[2].Multimesh!.Mesh == _cardMesh;
        TreeVariantIndex = original;
        GD.Print($"SUNBLOCKER_CATALOG_SMOKE valid={valid} normalized={normalized} material_isolation={materialIsolation} authored_texture_id={authoredTextureId} clone_texture_id={cloneTextureId} uv_preserved={uvPreserved} selection_updates_all={changed} sun_applied={sunApplied} reset={reset} variant={original} name={source.Name}");
        return valid && normalized && materialIsolation && uvPreserved && changed && sunApplied && reset;
    }

    private static bool TryNormalizedRootAndHeight(Mesh mesh)
    {
        float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
        for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
            foreach (Vector3 vertex in mesh.SurfaceGetArrays(surface)[(int)Mesh.ArrayType.Vertex].AsVector3Array()) { minY = Mathf.Min(minY, vertex.Y); maxY = Mathf.Max(maxY, vertex.Y); }
        return minY >= -.002f && maxY >= .98f && maxY <= 1.02f;
    }
    private void Capture() { const string prefix = "--sunblocker-capture="; string arg = Array.Find(OS.GetCmdlineUserArgs(), a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))!; string requested = arg[prefix.Length..]; string path = requested.StartsWith("res://", StringComparison.OrdinalIgnoreCase) || requested.StartsWith("user://", StringComparison.OrdinalIgnoreCase) ? ProjectSettings.GlobalizePath(requested) : Path.GetFullPath(requested); try { string? directory = Path.GetDirectoryName(path); if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory); } catch (Exception error) { GD.PushError($"SUNBLOCKER_CAPTURE=FAIL path setup: {error.Message}"); GetTree().Quit(1); return; } Image image = GetViewport().GetTexture().GetImage(); Error result = image.SavePng(path); GD.Print($"SUNBLOCKER_CAPTURE={(result == Error.Ok ? "PASS" : "FAIL")} path={path}"); GetTree().Quit(result == Error.Ok ? 0 : 1); }
}
