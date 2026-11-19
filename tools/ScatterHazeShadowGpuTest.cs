using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Tools;
using RingworldTechDemo.Vegetation.Scatter;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tests;

/// <summary>
/// T5-7a (docs/work-orders/terrain-vegetation/T5.md, design B4-B6): the scatter rock shader (materials/ring_scatter_rock.gdshader) on a CPU MultiMesh fixture, compared with a reference ground shader
/// (tools/scatter_reference_ground.gdshader) built from the same includes and the same calls the Terrain3D surface makes, through the shaders' debug views
/// (1 haze transmittance, 2 in-scatter, 3 direct visibility, 4 albedo after wetness, 5 strata band index, 6 coverage). All checks read a windowed SubViewport.
///   b. haze: boulders at 50 m, 500 m and 2 km (slant distance) match the ground beside them in T and in-scatter (2 % absolute: vegetation_haze_gpu_test compares a GPU atlas with the per-vertex
///      reference at 1e-3 / 3e-3 relative, a different quantity, so it has no pixel tolerance to inherit).
///   c. shadow: a roof box shades half the field (shadow map) and a cloud mask shades the same half (RingCloudShadowProjector's uniforms driven by hand); a boulder reads like the ground beside it
///      in each half (0.05); a ShadowsOnly boulder (the shader in the shadow pass) darkens the ground it shades.
///   d. wetness: view 4 darkens by the designed rock factor (porosity 0.5 x strength: 1 - 0.55 x 0.5) within 10 %.
///   e. strata: the outcrop's band index at 10 rows equals the reference wall's at matched absolute heights (orthographic camera, so a row is a height); a half-band offset must fail.
///   f. dither: coverage 0.5 covers 40-60 % of a boulder, its complement the rest (union ~100 %, overlap under 5 %).
///   g. GPU ms of the fixture frames (info only).
///   T5-7b (the layer, design B2/B3/B5/B6):
///   h. feature off: the creation helper gives no ScatterLayer for a generation 1 sampler and one for the review sampler (a real world is not booted here; gpu_tree_integration_test and the tree tests run unchanged beside this).
///   i. layer smoke on steep_eye records (BuildScatterReviewTile) plus synthetic rocks at known distances, fed through a fixed host: 10 batches (8 camera, 2 caster); every batch's full-list row count equals the count of
///      records inside its B5 band (budget slots applied in order); the caster batches hold only boulders of at least 2 m and outcrops inside the sun's near shadow distance; a boulder at 39 m is mostly LOD0 and at 41 m mostly
///      LOD1, with complementary dither coverage in the cross-fade and full coverage outside it; the camera list drops what is behind the camera; moving the camera 200 m rebuilds nothing, 700 m exactly one anchor rebuild.
///   j. tree identity is covered by gpu_tree_integration_test and tree_frustum_identity_gpu_test, run unchanged (the layer has its own compute instance).
/// Run windowed: godot --path . --script res://tools/scatter_haze_shadow_gpu_test.gd (or tools/run_tests.ps1 -Tests scatter_haze_shadow_gpu_test).
/// World frame: the ring axis is +Y, the surface point is the origin, up is -X (ring centre at x = -R), around is +Z, across is +Y; the ground is the plane x = 0.
/// </summary>
public partial class ScatterHazeShadowGpuTest : Node
{
    private const float Radius = 5_000_000f;
    private const float HazeDensityScale = 30f; // the fixture's air is 30x the default so T is far from 1 at 2 km (a dropped T must be visible); both receivers get the same air
    private const float HazeTolerance = 0.02f;
    private const float WetDarken = 0.55f * 0.5f; // ring_wet_apply: 0.55 x porosity (rock 0.5) x strength (1)
    private const float StrataStep = 8f;
    private const float StrataHeight = 5f;
    private const uint TintWord = (20u << 12) | (18u << 6) | 14u;
    private static readonly Vector3 Up = new(-1, 0, 0), AroundDir = new(0, 0, 1), AcrossDir = new(0, 1, 0);
    private static Vector3 P(float around, float height, float across) => new(-height, across, around);

    public string Result { get; private set; } = "RUNNING";
    private readonly List<string> _log = new();

    private SubViewport _vp = null!;
    private Camera3D _cam = null!;
    private DirectionalLight3D _sun = null!;
    private MeshInstance3D _ground = null!, _wall = null!, _roof = null!;
    private MultiMeshInstance3D _boulders = null!, _outcrop = null!, _caster = null!;
    private ShaderMaterial _boulderMat = null!, _outcropMat = null!, _refMat = null!;
    private ShaderMaterial[] _all = null!;
    private RockCatalog _catalog = null!;
    private Vector3 _sunToward = new(-.77f, 0f, -.64f);

    public async void Start()
    {
        try
        {
            Setup();
            await Run();
            Result = "PASS " + string.Join("; ", _log);
        }
        catch (Exception error) { Result = "FAIL " + error.Message + " | " + string.Join("; ", _log); }
        finally { await Frames(2); }
    }

    // ------------------------------------------------------------------ scene

    private void Setup()
    {
        _catalog = RockCatalog.Load();
        var rocks = new RingScatterRockMaterial(_catalog);
        _boulderMat = rocks.Get(RingScatterRenderGroup.Boulders, 0);
        _outcropMat = rocks.Get(RingScatterRenderGroup.Outcrops, 0);
        _refMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://tools/scatter_reference_ground.gdshader") };
        _all = new[] { _boulderMat, _outcropMat, _refMat };

        // Globals the shaders read: the fixture's lighting colours, ground look (bed height 8 m), albedo scale 1 (so debug 4 keeps its precision), world features untouched.
        RenderingServer.GlobalShaderParameterSet("ringworld_haze_sun_irradiance", new Vector3(3.2f, 3.0f, 2.7f));
        RenderingServer.GlobalShaderParameterSet("ringworld_haze_sun_irradiance_remote", new Vector3(3.2f, 3.0f, 2.7f));
        RenderingServer.GlobalShaderParameterSet("ringworld_haze_ambient_day", new Vector3(.25f, .32f, .45f));
        RenderingServer.GlobalShaderParameterSet("ringworld_haze_ambient_night", new Vector3(.004f, .005f, .008f));
        RenderingServer.GlobalShaderParameterSet("ringworld_haze_storm", new Vector4(0, 0, 0, 0));
        RenderingServer.GlobalShaderParameterSet("ringworld_hub_light_weight", 0f);
        RenderingServer.GlobalShaderParameterSet("ringworld_hub_day", 0f);
        RenderingServer.GlobalShaderParameterSet("ringworld_solar_visibility", 1f);
        RenderingServer.GlobalShaderParameterSet("ringworld_terrain_albedo_scale", 1f);
        RenderingServer.GlobalShaderParameterSet("ringworld_ground_world", new Vector4(StrataStep, 1234f, 0f, 0f));
        RenderingServer.GlobalShaderParameterSet("ringworld_ground_look", new Vector4(38f, .12f, 1f, 0f));
        SetWet(false);

        _vp = new SubViewport { Size = new Vector2I(768, 384), OwnWorld3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        AddChild(_vp);
        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = Colors.Black,
            AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = Colors.Black, AmbientLightEnergy = 0f,
            TonemapMode = Godot.Environment.ToneMapper.Linear,
        };
        _vp.AddChild(new WorldEnvironment { Environment = environment });
        _cam = new Camera3D { Near = .3f, Far = 6000f, Current = true };
        _vp.AddChild(_cam);
        _sun = new DirectionalLight3D { ShadowEnabled = true, DirectionalShadowMode = DirectionalLight3D.ShadowMode.Orthogonal, DirectionalShadowMaxDistance = 400f, LightEnergy = 1f };
        _vp.AddChild(_sun);

        _ground = Quad(new Vector3(0, -4000, -300), new Vector3(0, 4000, 6500));
        _wall = Quad(new Vector3(12, 4, 60), new Vector3(-30, 17, 60));
        _roof = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1f, 80f, 280f) }, Position = P(160f, 20f, 40f) };
        _boulders = new MultiMeshInstance3D { MaterialOverride = _boulderMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _outcrop = new MultiMeshInstance3D { MaterialOverride = _outcropMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _caster = new MultiMeshInstance3D { MaterialOverride = _boulderMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly };
        foreach (Node node in new Node[] { _ground, _wall, _roof, _boulders, _outcrop, _caster }) _vp.AddChild(node);
        _ground.MaterialOverride = _refMat; _wall.MaterialOverride = _refMat;
        _roof.Visible = false; _wall.Visible = false;
        foreach (ShaderMaterial material in _all) ConfigureAir(material);
        SetSun(_sunToward);
        RenderingServer.ViewportSetMeasureRenderTime(_vp.GetViewportRid(), true);
    }

    private static MeshInstance3D Quad(Vector3 a, Vector3 b)
    {
        // Axis-aligned rectangle spanning a..b in the two axes where they differ (cull_disabled receiver, normal = the plane's).
        Vector3 d = b - a;
        Vector3 u, v, n;
        if (MathF.Abs(d.X) < 1e-3f) { u = new Vector3(0, d.Y, 0); v = new Vector3(0, 0, d.Z); n = new Vector3(-1, 0, 0); }
        else { u = new Vector3(d.X, 0, 0); v = new Vector3(0, d.Y, 0); n = new Vector3(0, 0, -1); }
        var mesh = new ArrayMesh(); var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new[] { a, a + u, a + u + v, a + v };
        arrays[(int)Mesh.ArrayType.Normal] = new[] { n, n, n, n };
        arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.CustomAabb = new Aabb(a.Min(b) - Vector3.One * 10f, (b - a).Abs() + Vector3.One * 20f);
        return new MeshInstance3D { Mesh = mesh };
    }

    /// <summary>The air and ring frame both receivers share (identical values), except the per-shot camera.</summary>
    private static void ConfigureAir(ShaderMaterial m)
    {
        m.SetShaderParameter("ring_atmosphere_enabled", true);
        m.SetShaderParameter("ring_atmosphere_ray_frame_valid", true);
        m.SetShaderParameter("ring_atmosphere_outward_world", new Vector3(1, 0, 0));
        m.SetShaderParameter("ring_atmosphere_axis_world", new Vector3(0, 1, 0));
        m.SetShaderParameter("ring_atmosphere_sidewall_negative_meters", 1_000_000f);
        m.SetShaderParameter("ring_atmosphere_sidewall_positive_meters", 1_000_000f);
        m.SetShaderParameter("ring_atmosphere_coordinate_scale", 1f);
        m.SetShaderParameter("ring_radius_meters", Radius);
        m.SetShaderParameter("ring_atmosphere_center_world", new Vector3(-Radius, 0, 0));
        m.SetShaderParameter("atmosphere_haze_model", 1);
        m.SetShaderParameter("ring_haze_sigma_R", new Vector3(5.802e-6f, 13.558e-6f, 33.1e-6f) * HazeDensityScale);
        m.SetShaderParameter("ring_haze_sigma_A", 4.175e-6f * HazeDensityScale);
        m.SetShaderParameter("night_tint", Vector3.One);
        m.SetShaderParameter("ring_width_meters", 20000f);
        // Cloud shadow: per-material uniforms (the projector's globals stay off); see SetCloud.
        m.SetShaderParameter("local_cloud_shadow_use_globals", false);
        m.SetShaderParameter("local_cloud_shadow_enabled", false);
    }

    private void SetSun(Vector3 toSun)
    {
        _sunToward = toSun.Normalized();
        _sun.Transform = new Transform3D(Basis.LookingAt(-_sunToward, Vector3.Up), Vector3.Zero);
        foreach (ShaderMaterial m in _all)
        {
            m.SetShaderParameter("sun_direction", _sunToward);
            m.SetShaderParameter("ring_atmosphere_to_sun", _sunToward);
        }
    }

    private void SetView(int view) { foreach (ShaderMaterial m in _all) m.SetShaderParameter("rock_debug_view", view); }

    private void SetCloud(bool on)
    {
        // Half the field (across > 0) under a cloud: a 512 x 512 RG8 map, R = 1 for v >= 0.5; uv = planar / (2 radius) + 0.5, planar = (around, across) from the cascade centre at the origin.
        var image = Image.CreateEmpty(512, 512, false, Image.Format.Rg8);
        for (int y = 0; y < 512; y++) for (int x = 0; x < 512; x++) image.SetPixel(x, y, new Color(y >= 256 ? 1f : 0f, 0f, 0f, 0f));
        var texture = ImageTexture.CreateFromImage(image);
        foreach (ShaderMaterial m in _all)
        {
            m.SetShaderParameter("local_cloud_shadow_enabled", on);
            m.SetShaderParameter("local_cloud_shadow_near_enabled", on);
            m.SetShaderParameter("local_cloud_shadow_far_enabled", false);
            m.SetShaderParameter("local_cloud_shadow_texture", texture);
            m.SetShaderParameter("local_cloud_shadow_center_world", Vector3.Zero);
            m.SetShaderParameter("local_cloud_shadow_axis_around", AroundDir);
            m.SetShaderParameter("local_cloud_shadow_axis_across", AcrossDir);
            m.SetShaderParameter("local_cloud_shadow_axis_up", Up);
            m.SetShaderParameter("local_cloud_shadow_radius_meters", 2000f);
            m.SetShaderParameter("local_cloud_shadow_ring_radius_meters", 0f);
            m.SetShaderParameter("local_cloud_shadow_near_start_meters", -2f);
            m.SetShaderParameter("local_cloud_shadow_near_end_meters", -1f);
        }
    }

    private static Texture2D? _wetMap;
    private void SetWet(bool on)
    {
        if (_wetMap == null)
        {
            var image = Image.CreateEmpty(4, 4, false, Image.Format.Rgbah);
            image.Fill(new Color(1f, 1f, 0f, 0f)); // film 1, soak 1, no standing water, no rain
            _wetMap = ImageTexture.CreateFromImage(image);
        }
        RenderingServer.GlobalShaderParameterSet("ring_wet_map", _wetMap);
        RenderingServer.GlobalShaderParameterSet("ring_wet_map_frame", new Vector4(2000f, 2000f, 8f, 500f));
        RenderingServer.GlobalShaderParameterSet("ring_wet_clock", new Vector4(0f, 1f, 100000f, 100f));
        RenderingServer.GlobalShaderParameterSet("ring_wet_params", new Vector4(1f, .3f, 0f, on ? 1f : 0f));
    }

    /// <summary>Camera pose (perspective by horizontal fov, or orthographic by vertical size) and every per-camera uniform of both receivers.</summary>
    private void Pose(Vector3 position, Vector3 target, float fovDegrees, float orthoSize = 0f)
    {
        if (orthoSize > 0f) { _cam.Projection = Camera3D.ProjectionType.Orthogonal; _cam.Size = orthoSize; _cam.KeepAspect = Camera3D.KeepAspectEnum.Height; }
        else { _cam.Projection = Camera3D.ProjectionType.Perspective; _cam.KeepAspect = Camera3D.KeepAspectEnum.Width; _cam.Fov = MathF.Max(fovDegrees, 1f); }
        _cam.LookAtFromPosition(position, target, Up);
        float height = -position.X;
        foreach (ShaderMaterial m in _all)
        {
            m.SetShaderParameter("ring_atmosphere_camera_world", position);
            m.SetShaderParameter("ring_atmosphere_camera_world_position", position);
            m.SetShaderParameter("ring_atmosphere_camera_height_meters", height);
            m.SetShaderParameter("ring_viewer_world_position", position);
        }
        var frame = new Projection(new Vector4(AroundDir.X, AroundDir.Y, AroundDir.Z, 0f), new Vector4(AcrossDir.X, AcrossDir.Y, AcrossDir.Z, 0f),
            new Vector4(Up.X, Up.Y, Up.Z, Radius), new Vector4(0f, position.Y, position.Z, height));
        RenderingServer.GlobalShaderParameterSet("ring_wet_frame", frame);
    }

    // ------------------------------------------------------------------ instances

    private static Basis RockBasis(float size, float yaw) => new Basis(Up, yaw) * new Basis(AcrossDir, Up, AroundDir).Scaled(Vector3.One * size);

    private static Color Custom(int variant, float coverage, float height, bool dithered) => new(variant + 32f * TintWord, coverage, height, dithered ? 2f : 0f);

    private static void Fill(MultiMeshInstance3D node, RockLodMesh mesh, List<(Transform3D, Color)> instances)
    {
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = mesh.Carrier, InstanceCount = instances.Count,
            CustomAabb = new Aabb(new Vector3(-9000, -9000, -9000), new Vector3(18000, 18000, 18000)),
        };
        for (int i = 0; i < instances.Count; i++) { mm.SetInstanceTransform(i, instances[i].Item1); mm.SetInstanceCustomData(i, instances[i].Item2); }
        node.Multimesh = mm;
    }

    private void Boulders(params (float around, float across, float size, float yaw, float coverage, bool dithered)[] list)
    {
        var instances = new List<(Transform3D, Color)>();
        foreach (var b in list) instances.Add((new Transform3D(RockBasis(b.size, b.yaw), P(b.around, 0f, b.across)), Custom(3, b.coverage, 0f, b.dithered)));
        Fill(_boulders, _catalog.Boulders.Lods[0], instances);
    }

    private void Hide(params MeshInstance3D[] nodes) { foreach (MeshInstance3D n in nodes) n.Visible = false; }

    // ------------------------------------------------------------------ images

    private async Task<Image> Capture()
    {
        for (int i = 0; i < 4; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image image = _vp.GetTexture().GetImage();
        if (image == null || image.IsEmpty()) throw new InvalidOperationException("empty capture");
        return image;
    }

    private static Vector3 Lin(Color c) { c = c.SrgbToLinear(); return new Vector3(c.R, c.G, c.B); }

    private static bool[,] Mask(Image image, out int count, out Rect2I box)
    {
        int w = image.GetWidth(), h = image.GetHeight(), x0 = w, y0 = h, x1 = -1, y1 = -1;
        var mask = new bool[w, h]; count = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (image.GetPixel(x, y).R > .5f) { mask[x, y] = true; count++; x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
        box = count > 0 ? new Rect2I(x0, y0, x1 - x0 + 1, y1 - y0 + 1) : new Rect2I();
        return mask;
    }

    private static Vector3 MeanMasked(Image image, bool[,] mask, Rect2I within)
    {
        Vector3 sum = Vector3.Zero; int n = 0;
        for (int y = within.Position.Y; y < within.End.Y; y++)
            for (int x = within.Position.X; x < within.End.X; x++) if (mask[x, y]) { sum += Lin(image.GetPixel(x, y)); n++; }
        return n > 0 ? sum / n : Vector3.Zero;
    }

    private static Vector3 MeanRect(Image image, Rect2I rect)
    {
        Vector3 sum = Vector3.Zero; int n = 0;
        int x0 = Math.Max(rect.Position.X, 0), x1 = Math.Min(rect.End.X, image.GetWidth()), y0 = Math.Max(rect.Position.Y, 0), y1 = Math.Min(rect.End.Y, image.GetHeight());
        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { sum += Lin(image.GetPixel(x, y)); n++; }
        Check(n > 20, "reference strip outside the image");
        return sum / n;
    }

    /// <summary>The strip of ground beside a rock: the rock's rows, one rock-width away, on the outer side (towards the image edge it is nearer to).</summary>
    private Rect2I Strip(Rect2I box)
    {
        bool left = box.GetCenter().X < _vp.Size.X / 2;
        int w = box.Size.X;
        return left ? new Rect2I(box.Position.X - 2 * w, box.Position.Y, w, box.Size.Y) : new Rect2I(box.End.X + w, box.Position.Y, w, box.Size.Y);
    }

    // ------------------------------------------------------------------ run

    private async Task Run()
    {
        await HazeChecks();
        await ShadowChecks();
        await WetCheck();
        await StrataCheck();
        await DitherCheck();
        await StillAndTiming();
        FeatureOffChecks();
        await LayerChecks();
    }

    // b. haze
    private async Task HazeChecks()
    {
        float[] distances = { 50f, 500f, 2000f };
        var around = new float[3];
        for (int i = 0; i < 3; i++) around[i] = distances[i] * .866025f;
        Boulders((around[0], 0f, 2f, .3f, 1f, false), (around[1], 0f, 2f, 1.1f, 1f, false), (around[2], 0f, 2f, 2.0f, 1f, false));
        SetSun(new Vector3(-.77f, 0f, -.64f));
        float previousT = 1.1f;
        for (int i = 0; i < 3; i++)
        {
            float d = distances[i], hc = d * .5f;
            Pose(P(0f, hc, 0f), P(around[i], 1f, 0f), 2f * MathF.Atan(6f / d) * 180f / MathF.PI);
            _ground.Visible = false; SetView(6);
            bool[,] mask = Mask(await Capture(), out int count, out Rect2I box);
            Check(count > 200, $"haze {d} m: rock mask only {count} px");
            _ground.Visible = true;
            SetView(1);
            Image t = await Capture();
            SetView(2);
            Image s = await Capture();
            Vector3 rockT = MeanMasked(t, mask, box), groundT = MeanRect(t, Strip(box));
            Vector3 rockS = MeanMasked(s, mask, box), groundS = MeanRect(s, Strip(box));
            float dT = MaxAbs(rockT - groundT), dS = MaxAbs(rockS - groundS);
            _log.Add($"haze {d:0} m: T rock {Fmt(rockT)} ground {Fmt(groundT)} (max diff {dT:0.0000}); S rock {Fmt(rockS)} ground {Fmt(groundS)} (max diff {dS:0.0000})");
            Check(dT <= HazeTolerance, $"haze {d} m: T differs by {dT:0.0000} (rock {Fmt(rockT)}, ground {Fmt(groundT)})");
            Check(dS <= HazeTolerance, $"haze {d} m: in-scatter differs by {dS:0.0000} (rock {Fmt(rockS)}, ground {Fmt(groundS)})");
            Check(groundT.Z < previousT, $"haze {d} m: ground T did not fall with distance ({groundT.Z} vs {previousT})");
            previousT = groundT.Z;
            if (i == 2) Check(groundT.Z < .5f, $"fixture air too thin at 2 km: ground T_b {groundT.Z:0.000} (a dropped T would not show)");
        }
    }

    // c. shadow: shadow map (roof), cloud, and a caster in the shadow pass
    private async Task ShadowChecks()
    {
        SetView(3);
        SetSun(new Vector3(-MathF.Sin(MathF.PI * 50f / 180f), 0f, -MathF.Cos(MathF.PI * 50f / 180f)));
        Boulders((100f, 20f, 2f, .4f, 1f, false), (100f, -20f, 2f, 1.7f, 1f, false));
        Pose(P(0f, 5f, 0f), P(100f, 1f, 0f), 40f);
        await ShadowPair("roof", true, 1f, 0f);
        SetCloud(true);
        await ShadowPair("cloud", false, 1f, 1f - .45f);
        SetCloud(false);

        // The shader in the shadow pass: a ShadowsOnly boulder (3 m) darkens the ground to its shadow side and not the other.
        _roof.Visible = false;
        SetSun(new Vector3(-MathF.Sin(MathF.PI / 4f), MathF.Cos(MathF.PI / 4f), 0f));
        Boulders();
        Pose(P(0f, 60f, 0f), P(100f, 0f, 0f), 30f); // looking down so the shadow is not foreshortened
        var caster = new List<(Transform3D, Color)> { (new Transform3D(RockBasis(6f, .6f), P(100f, 0f, 0f)), Custom(3, 1f, 0f, false)) };
        Fill(_caster, _catalog.Boulders.Lods[0], caster);
        Image image = await Capture();
        DirAccess.MakeDirRecursiveAbsolute("c:/Projects/Ringworld/.godot/captures/t5-7");
        image.SavePng("c:/Projects/Ringworld/.godot/captures/t5-7/caster.png");
        // Scan the shadow side in 1 m patches: the shadow's extent depends on the rock's unit size, the darkest patch must be in shadow; the lit side far from the footprint must be open.
        float shaded = 1f;
        for (float across = -7f; across < -.5f; across += 1f) shaded = MathF.Min(shaded, MeanRect(image, WorldRect(P(99.5f, 0f, across), P(100.5f, 0f, across + 1f))).X);
        float open = MeanRect(image, WorldRect(P(99.5f, 0f, 9f), P(100.5f, 0f, 10f))).X;
        _log.Add($"shadow pass: ground visibility in the boulder's shadow {shaded:0.000}, beside it {open:0.000}");
        Check(open > .9f, $"shadow pass: open ground reads {open}");
        Check(shaded < .4f, $"shadow pass: ground in a ShadowsOnly boulder's shadow reads {shaded}");
        Fill(_caster, _catalog.Boulders.Lods[0], new List<(Transform3D, Color)>());
        _caster.Multimesh = null;
    }

    private Rect2I WorldRect(Vector3 a, Vector3 b)
    {
        Vector2 pa = _cam.UnprojectPosition(a), pb = _cam.UnprojectPosition(b);
        return new Rect2I((int)MathF.Min(pa.X, pb.X), (int)MathF.Min(pa.Y, pb.Y), Math.Max((int)MathF.Abs(pa.X - pb.X), 8), Math.Max((int)MathF.Abs(pa.Y - pb.Y), 8));
    }

    private async Task ShadowPair(string label, bool roof, float expectLit, float expectShadedFloor)
    {
        _roof.Visible = roof;
        SetView(6);
        _ground.Visible = false; _roof.Visible = false;
        bool[,] mask = Mask(await Capture(), out int count, out _);
        Check(count > 300, $"{label}: rock mask only {count} px");
        _ground.Visible = true; _roof.Visible = roof;
        SetView(3);
        Image image = await Capture();
        // Split the mask at the image middle: screen-left holds the +across rock (right = -Y), screen-right the -across rock.
        int mid = _vp.Size.X / 2;
        var leftBox = Box(mask, 0, mid); var rightBox = Box(mask, mid, _vp.Size.X);
        Check(leftBox.Size.X > 10 && rightBox.Size.X > 10, $"{label}: a rock is missing from the mask");
        float shadedRock = MeanMasked(image, mask, leftBox).X, shadedGround = MeanRect(image, Strip(leftBox)).X;
        float litRock = MeanMasked(image, mask, rightBox).X, litGround = MeanRect(image, Strip(rightBox)).X;
        _log.Add($"{label} shadow: shaded boulder {shadedRock:0.000} vs ground {shadedGround:0.000}; sunlit boulder {litRock:0.000} vs ground {litGround:0.000}");
        Check(MathF.Abs(shadedRock - shadedGround) <= .05f, $"{label}: shaded boulder {shadedRock} vs ground {shadedGround}");
        Check(MathF.Abs(litRock - litGround) <= .05f, $"{label}: sunlit boulder {litRock} vs ground {litGround}");
        Check(litGround >= expectLit - .05f, $"{label}: sunlit ground reads {litGround}");
        Check(shadedGround <= (roof ? .1f : expectShadedFloor + .05f) && shadedGround >= expectShadedFloor - .05f, $"{label}: shaded ground reads {shadedGround}");
        Check(litRock - shadedRock > .3f, $"{label}: the two halves do not differ");
    }

    private static Rect2I Box(bool[,] mask, int x0, int x1)
    {
        int h = mask.GetLength(1), ax = x1, ay = h, bx = -1, by = -1;
        for (int y = 0; y < h; y++) for (int x = x0; x < x1; x++) if (mask[x, y]) { ax = Math.Min(ax, x); bx = Math.Max(bx, x); ay = Math.Min(ay, y); by = Math.Max(by, y); }
        return bx < 0 ? new Rect2I() : new Rect2I(ax, ay, bx - ax + 1, by - ay + 1);
    }

    // d. wetness
    private async Task WetCheck()
    {
        SetView(6); SetSun(new Vector3(-.77f, 0f, -.64f));
        Boulders((43.3f, 0f, 2f, .3f, 1f, false));
        Pose(P(0f, 25f, 0f), P(43.3f, 1f, 0f), 2f * MathF.Atan(6f / 50f) * 180f / MathF.PI);
        _ground.Visible = false;
        bool[,] mask = Mask(await Capture(), out int count, out Rect2I box);
        Check(count > 200, $"wet: rock mask only {count} px");
        SetView(4);
        SetWet(false);
        Vector3 dry = MeanMasked(await Capture(), mask, box);
        SetWet(true);
        Vector3 wet = MeanMasked(await Capture(), mask, box);
        SetWet(false);
        float darken = 1f - (wet.X + wet.Y + wet.Z) / (dry.X + dry.Y + dry.Z);
        _log.Add($"wet: albedo dry {Fmt(dry)} wet {Fmt(wet)}; darkening {darken:0.0000} vs designed {WetDarken:0.0000}");
        Check(dry.X > .01f, "wet: dry albedo is black");
        Check(MathF.Abs(darken - WetDarken) <= .1f * WetDarken, $"wet: darkening {darken} vs designed {WetDarken}");
        _ground.Visible = true;
    }

    // e. strata
    private async Task StrataCheck()
    {
        RockLodMesh lod = _catalog.Outcrops.Lods[0];
        float size = 14f / (lod.UnitAabb.Size.Y - (float)(-0.75 - PrepareRockAssets.OutcropBaseY)); // the pre-T5-10b catalog's height (foot at -0.75): the deeper foot (-1.25) is buried, so the strata fixture keeps the same visible span
        _wall.Visible = true; _ground.Visible = false;
        SetSun(new Vector3(-.77f, 0f, -.64f));
        Boulders();
        _wall.Visible = false;
        SetOutcrop(size, StrataHeight);
        Pose(P(0f, 7.5f, 0f), P(100f, 7.5f, 0f), 0f, 18f);
        SetView(6);
        bool[,] mask = Mask(await Capture(), out int count, out Rect2I box);
        Check(count > 3000, $"strata: outcrop mask only {count} px");
        _wall.Visible = true;
        _refMat.SetShaderParameter("ref_up", Up);
        _refMat.SetShaderParameter("ref_height_offset", StrataHeight);
        SetView(5);
        Image wall = await Capture(); // reference wall and outcrop in one frame: distinct columns, the same rows
        int wallX = (int)_cam.UnprojectPosition(P(60f, 7f, 10f)).X, rockX = (int)_cam.UnprojectPosition(P(60f, 7f, -5f)).X;
        var rows = new List<int>();
        for (int y = box.Position.Y + 4; y < box.End.Y - 4; y++)
        {
            if (!mask[rockX, y]) continue;
            float v = wall.GetPixel(wallX, y).R;
            bool stable = true;
            for (int k = -3; k <= 3; k++) stable &= MathF.Abs(wall.GetPixel(wallX, y + k).R - v) < 1e-3f;
            if (stable) rows.Add(y);
        }
        Check(rows.Count >= 10, $"strata: only {rows.Count} stable rows");
        var picked = new List<int>();
        for (int i = 0; i < 10; i++) picked.Add(rows[i * (rows.Count - 1) / 9]);
        var distinct = new HashSet<int>();
        int mismatches = 0;
        foreach (int y in picked)
        {
            float expected = wall.GetPixel(wallX, y).R, actual = wall.GetPixel(rockX, y).R;
            distinct.Add((int)MathF.Round(expected * 255f));
            if (MathF.Abs(expected - actual) > 1.5f / 255f) mismatches++;
        }
        Check(distinct.Count >= 2, "strata: the 10 rows lie in one band");
        _log.Add($"strata: band index at 10 rows equals the reference at matched heights ({distinct.Count} bands, {mismatches} mismatches)");
        Check(mismatches == 0, $"strata: {mismatches} of 10 rows differ from the reference");

        // Can it fail: a half-band offset in INSTANCE_CUSTOM.z must move the bands.
        SetOutcrop(size, StrataHeight + StrataStep * .5f);
        Image shifted = await Capture();
        int moved = 0;
        foreach (int y in picked) if (MathF.Abs(wall.GetPixel(wallX, y).R - shifted.GetPixel(rockX, y).R) > 1.5f / 255f) moved++;
        _log.Add($"strata can-it-fail: half-band offset moves {moved} of 10 rows");
        Check(moved >= 3, $"strata: a half-band offset moved only {moved} of 10 rows (the check cannot fail)");
        _wall.Visible = false; _ground.Visible = true;
        Fill(_outcrop, lod, new List<(Transform3D, Color)>());
        _outcrop.Multimesh = null;
    }

    private void SetOutcrop(float size, float height)
    {
        var list = new List<(Transform3D, Color)> { (new Transform3D(RockBasis(size, 0f), P(60f, 0f, -5f)), Custom(2, 1f, height, false)) };
        Fill(_outcrop, _catalog.Outcrops.Lods[0], list);
    }

    // f. dither
    private async Task DitherCheck()
    {
        SetView(6); _ground.Visible = false; _wall.Visible = false;
        Pose(P(0f, .8f, 0f), P(100f, .8f, 0f), 0f, 3f);
        Boulders((20f, 0f, 2f, .5f, 1f, false));
        Mask(await Capture(), out int full, out _);
        Boulders((20f, 0f, 2f, .5f, .5f, true));
        bool[,] a = Mask(await Capture(), out int countA, out _);
        Boulders((20f, 0f, 2f, .5f, -.5f, true));
        bool[,] b = Mask(await Capture(), out int countB, out _);
        int union = 0, overlap = 0;
        for (int y = 0; y < a.GetLength(1); y++) for (int x = 0; x < a.GetLength(0); x++) { if (a[x, y] || b[x, y]) union++; if (a[x, y] && b[x, y]) overlap++; }
        float fa = countA / (float)full, fu = union / (float)full, fo = overlap / (float)full;
        _log.Add($"dither: {full} px full; 0.5 covers {fa:0.000}; union {fu:0.000}; overlap {fo:0.000}");
        Check(full > 5000, $"dither: boulder only {full} px");
        Check(fa is >= .4f and <= .6f, $"dither: coverage 0.5 covers {fa}");
        Check(MathF.Abs(fu - 1f) <= .05f, $"dither: union {fu}");
        Check(fo < .05f, $"dither: overlap {fo}");
        _ground.Visible = true;
    }

    // g. timing and the fixture still
    private async Task StillAndTiming()
    {
        SetView(0);
        SetSun(new Vector3(-.77f, 0f, -.64f));
        Boulders((43.3f, 0f, 2f, .3f, 1f, false), (30f, 6f, 1.2f, 1.1f, 1f, false), (55f, 8f, 2.4f, 2.5f, 1f, false), (80f, -8f, 3f, 0f, 1f, false), (433f, 0f, 2f, 1.1f, 1f, false));
        SetOutcrop(14f / _catalog.Outcrops.Lods[0].UnitAabb.Size.Y, StrataHeight);
        Pose(P(0f, 2.5f, 0f), P(45f, 2f, 0f), 60f);
        Image still = await Capture();
        var times = new List<float>();
        for (int i = 0; i < 20; i++)
        {
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            times.Add((float)RenderingServer.ViewportGetMeasuredRenderTimeGpu(_vp.GetViewportRid()));
        }
        times.Sort();
        _log.Add($"gpu ms (fixture frame, info): median {times[times.Count / 2]:0.00}, max {times[^1]:0.00}");
        const string path = "c:/Projects/Ringworld/.godot/audit_baseline/common/t5/t5-7a_fixture.png";
        DirAccess.MakeDirRecursiveAbsolute("c:/Projects/Ringworld/.godot/audit_baseline/common/t5");
        still.SavePng(path);
    }


    // ------------------------------------------------------------------ T5-7b: the layer

    private RingTerrainSampler _review = null!;

    // h. feature off
    private void FeatureOffChecks()
    {
        using RingTerrainSampler gen1 = LandscapeWorld.For(1).CreateSampler();
        bool made = false;
        RingScatterLayer? none = RingBiomeDecorationLayer.CreateScatterLayerFor(gen1, () => { made = true; return new RingScatterLayer(); });
        Check(none == null && !made, "feature off: a generation 1 world created a scatter layer");
        _review = LandscapeWorld.For(1, new[] { "ground.surface", "scatter.rocks" }).CreateSampler();
        RingScatterLayer? some = RingBiomeDecorationLayer.CreateScatterLayerFor(_review, () => { made = true; return new RingScatterLayer(); });
        Check(some != null && made, "feature off: the review world did not create a scatter layer");
        Check(some!.BatchCount == 0 && some.Compute == null, "feature off: a fresh layer already owns batches");
        some.Free();
        _log.Add("feature off: generation 1 creates no ScatterLayer, the review world creates one");
    }

    private const float AnchorMeters = RingScatterLayer.AnchorMeters;
    private static readonly uint MarkerTint = ScatterRecord.PackBaseTint(1f, 0f, 1f);
    private const long MarkerTint666 = (63L << 12) | 63L;

    private static float Smooth(float a, float b, float x) { float t = Math.Clamp((x - a) / (b - a), 0f, 1f); return t * t * (3f - 2f * t); }
    /// <summary>Share of a dithered row's pixels its coverage words keep (the compute's tree_kept_share).</summary>
    private static float KeptShare(float coverage, float marker) => coverage < 0f ? -coverage : MathF.Max(coverage - (marker > 2.001f ? (marker - 2f) * 5f : 0f), 0f);

    private async Task<(uint[] Commands, float[] Rows)> ReadFull(RingScatterLayer layer, int id)
    {
        var done = new TaskCompletionSource<(uint[], float[])>();
        layer.Compute!.RequestReadbackForTests(id, (c, r) => done.TrySetResult((c, r)));
        return await done.Task;
    }

    private async Task<uint> ReadCameraCount(RingScatterLayer layer, int id)
    {
        var done = new TaskCompletionSource<uint[]>();
        layer.Compute!.RequestCameraReadbackForTests(id, (c, _) => done.TrySetResult(c));
        uint[] commands = await done.Task;
        return commands.Length >= 2 ? commands[1] : 0u;
    }

    private async Task WaitFrames(Func<bool> done, int maximum, string what)
    {
        for (int i = 0; i < maximum && !done(); i++) await Frames(1);
        Check(done(), "timeout waiting for " + what);
    }

    // i. the layer on a fixture
    private async Task LayerChecks()
    {
        const double Circ = LandscapeWorld.Circumference;
        double ringRadius = Circ / Math.Tau, aroundCell = Circ / Math.Ceiling(Circ / 30.0);
        LandscapePose pose = LandscapePoseFinder.ReadFixture(ProjectSettings.GlobalizePath("res://tools/fixtures/landscape_poses_dressing_s1337.json")).Poses.First(p => p.Name == "steep_eye");
        long ta = (long)Math.Floor(Math.Floor(pose.Around / aroundCell) / 8.0), tz = (long)Math.Floor(pose.Across / 240.0);
        var records = RingBiomeDecorationLayer.BuildScatterReviewTile(_review, ta, tz, LandscapeWorld.Seed, Circ, LandscapeWorld.Width, RingScatterClassTable.Production);
        int tileRecords = records.Count;
        ScatterRecord Syn(ulong id, ushort cls, double d, float size, int variant)
            => new(id, (long)Math.Round((pose.Around + d) * 100.0), (int)Math.Round(pose.Across * 100.0), (float)pose.Ground, cls, (ushort)variant, size, .3f, ScatterRecord.EncodeUpOct(0, 1, 0), MarkerTint);
        // Boulders (class 2) at 30, 39, 41, 100 and 300 m; outcrops (1) at 100, 300 and 1000 m; cobbles (3) at 5 and 30 m. The variant says which is which.
        foreach ((int cls, double d, float size, int variant) in new[] { (2, 30.0, 2.5f, 11), (2, 39.0, 2.5f, 13), (2, 41.0, 2.5f, 14), (2, 100.0, 2.5f, 12), (2, 300.0, 2.5f, 10),
            (1, 100.0, 10f, 1), (1, 300.0, 10f, 2), (1, 1000.0, 10f, 3), (3, 5.0, 1.2f, 1), (3, 30.0, 1.2f, 2) })
            records.Add(Syn(0xA000UL + (ulong)records.Count, (ushort)cls, d, size, variant));
        Check(tileRecords > 0, "steep_eye: the review tile holds no scatter records");

        var focus = new RingCanonicalPosition(pose.Around, pose.Ground, pose.Across);
        RingCanonicalPosition current = focus;
        var geometry = new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero, ringRadius, 0.0, 0.0, Circ, 0.0, 0.0, focus);
        Vector3 CameraAt(RingCanonicalPosition f) => geometry.CanonicalToWorld(f.AroundMeters, f.VerticalMeters, f.AcrossMeters);
        double angle = pose.Around / ringRadius;
        Vector3 forward = new Vector3((float)-Math.Sin(angle), 0f, (float)Math.Cos(angle)), up = new Vector3((float)-Math.Cos(angle), 0f, (float)-Math.Sin(angle));
        _cam.Projection = Camera3D.ProjectionType.Perspective; _cam.KeepAspect = Camera3D.KeepAspectEnum.Width; _cam.Fov = 70f; _cam.Far = 6000f;
        void Aim(Vector3 direction) => _cam.LookAtFromPosition(CameraAt(current), CameraAt(current) + direction * 100f, up);
        Aim(forward);
        foreach (Node3D hidden in new Node3D[] { _ground, _wall, _roof }) hidden.Visible = false;
        _boulders.Multimesh = null; _outcrop.Multimesh = null; _caster.Multimesh = null;
        SetView(0);

        var layer = new RingScatterLayer
        {
            Name = "ScatterLayer", FrustumCulling = false,
            Host = new ScatterLayerHost
            {
                Geometry = () => geometry, Focus = () => current, Epoch = () => 1, CopyRecords = into => into.AddRange(records),
                RingAcrossWidthMeters = () => LandscapeWorld.Width, Sun = () => _sun, Surface = () => null,
            },
        };
        _vp.AddChild(layer);
        await WaitFrames(() => layer.DrawOutputs == 10 && !layer.SetPending, 600, "the layer to show its 10 outputs (draw outputs " + layer.DrawOutputs + ")");
        Check(layer.BatchCount == 10, $"layer: {layer.BatchCount} batches registered, expected 10");
        var outputs = layer.OutputInfo();
        Check(outputs.Count(o => !o.Caster) == 8 && outputs.Count(o => o.Caster) == 2, "layer: expected 8 camera outputs and 2 caster outputs");
        Check(layer.SourceBuilds == 1 && layer.AnchorRebuilds == 0, $"layer: {layer.SourceBuilds} builds and {layer.AnchorRebuilds} anchor rebuilds before any move");
        Check(layer.GetParent() == _vp && layer.GetChildren().OfType<Node3D>().All(n => n.Name.ToString().StartsWith("ScatterSet") || n.Name == "ScatterColliders"), "layer: renderers are not under the layer's sets");

        // Expected counts from the records and the B5 table (independent of the layer's own table).
        Vector3 camera = CameraAt(focus);
        RingScatterClassTable table = RingScatterClassTable.Production;
        var bands = new[] { new[] { (0f, 45f), (35f, 165f), (135f, 600f) }, new[] { (0f, 165f), (135f, 650f), (550f, 2500f) }, new[] { (0f, 13.5f), (10.5f, 50f) } };
        var caps = new[] { 1500, 150, 1000 };
        int[] slotOf = { 0, 1, 2 };
        int GroupOf(ScatterRecord r) => table.Find(r.ClassId)!.RenderGroup switch { RingScatterRenderGroup.Boulders => 0, RingScatterRenderGroup.Outcrops => 1, _ => 2 };
        float DistanceOf(ScatterRecord r) => (geometry.CanonicalToWorld(r.AroundCm * .01, r.SurfaceHeight, r.AcrossCm * .01) - camera).Length();
        var distance = records.Select(DistanceOf).ToArray();
        const float Slack = .02f;
        int ambiguousTotal = 0;
        var expected = new Dictionary<string, (int Min, int Max)>();
        for (int group = 0; group < 3; group++)
        {
            int remainingMin = caps[group], remainingMax = caps[group];
            for (int lod = 0; lod < bands[group].Length; lod++)
            {
                (float near, float far) = bands[group][lod];
                int exact = 0, ambiguous = 0;
                for (int i = 0; i < records.Count; i++)
                {
                    if (GroupOf(records[i]) != group) continue;
                    float d = distance[i];
                    if (d >= near && d < far) exact++;
                    if (MathF.Abs(d - near) < Slack || MathF.Abs(d - far) < Slack) ambiguous++;
                }
                ambiguousTotal += ambiguous;
                int min = Math.Min(Math.Max(exact - ambiguous, 0), remainingMin), max = Math.Min(exact + ambiguous, remainingMax);
                remainingMin = Math.Max(remainingMin - min, 0); remainingMax = Math.Max(remainingMax - max, 0);
                expected[$"ScatterRock_{(group == 0 ? "Boulders" : group == 1 ? "Outcrops" : "Cobbles")}_L{lod}"] = (min, max);
            }
        }
        float shadowDistance = _sun.DirectionalShadowMaxDistance;
        int casterRemaining = 1650;
        foreach (int group in new[] { 0, 1 })
        {
            int exact = 0, ambiguous = 0;
            for (int i = 0; i < records.Count; i++)
            {
                RingScatterClass cls = table.Find(records[i].ClassId)!;
                if (GroupOf(records[i]) != group || !(records[i].Size >= cls.ShadowMinSizeM)) continue;
                if (distance[i] < shadowDistance) exact++;
                if (MathF.Abs(distance[i] - shadowDistance) < Slack) ambiguous++;
            }
            int take = Math.Min(exact, casterRemaining);
            casterRemaining -= take;
            expected[$"ScatterRock_{(group == 0 ? "Boulders" : "Outcrops")}_Shadow"] = (Math.Min(Math.Max(exact - ambiguous, 0), take), take + ambiguous);
        }
        var full = new Dictionary<string, (uint[] Commands, float[] Rows)>();
        var lines = new List<string>();
        foreach (var o in outputs)
        {
            full[o.Name] = await ReadFull(layer, o.BatchId);
            uint n = full[o.Name].Commands.Length >= 2 ? full[o.Name].Commands[1] : 0u;
            (int min, int max) = expected[o.Name];
            lines.Add($"{o.Name} {n} (expect {min}-{max})");
            Check(n >= min && n <= max, $"layer: {o.Name} holds {n} rows, expected {min} to {max}");
            if (!o.Caster) Check(max > 0 ? n > 0 : n == 0, $"layer: {o.Name} empty although its band holds records");
        }
        _log.Add($"layer: 10 batches ({tileRecords} tile + 10 synthetic records); rows " + string.Join(", ", lines));

        // Casters: boulders from 2 m and outcrops only (the rows' up vector has the record's size as its length).
        foreach (var o in outputs.Where(o => o.Caster))
        {
            var rows = full[o.Name].Rows;
            uint n = full[o.Name].Commands[1];
            for (int i = 0; i < n; i++)
            {
                float size = new Vector3(rows[i * 16 + 1], rows[i * 16 + 5], rows[i * 16 + 9]).Length();
                if (o.Group == 0) Check(size >= 2f - 1e-3f, $"layer: {o.Name} holds a {size:0.00} m boulder");
                else Check(o.Group == 1, "layer: a cobble caster exists");
            }
        }
        int smallBoulders = 0;
        for (int i = 0; i < records.Count; i++) if (GroupOf(records[i]) == 0 && records[i].Size < 2f && distance[i] < shadowDistance) smallBoulders++;
        _log.Add($"layer casters: only boulders of 2 m and up and outcrops ({smallBoulders} smaller boulders inside the shadow distance are excluded)");

        // Band table and cross-fades: where the marked boulders went.
        float? Share(string name, int variant)
        {
            var (commands, rows) = full[name];
            float want = variant + 32f * MarkerTint666;
            for (int i = 0; i < commands[1]; i++)
                if (MathF.Abs(rows[i * 16 + 12] - want) < .5f) return KeptShare(rows[i * 16 + 13], rows[i * 16 + 15]);
            return null;
        }
        string L(int lod) => $"ScatterRock_Boulders_L{lod}";
        float s39 = Smooth(35f, 45f, 39f), s41 = Smooth(35f, 45f, 41f);
        float? a30 = Share(L(0), 11), b30 = Share(L(1), 11), a39 = Share(L(0), 13), b39 = Share(L(1), 13), a41 = Share(L(0), 14), b41 = Share(L(1), 14);
        float? a100 = Share(L(0), 12), b100 = Share(L(1), 12), c100 = Share(L(2), 12), b300 = Share(L(1), 10), c300 = Share(L(2), 10);
        Check(a30 is { } x30 && x30 > .999f && b30 == null, $"band: the 30 m boulder is LOD0 only at full coverage (LOD0 {a30}, LOD1 {b30})");
        Check(a39 is { } x39 && b39 is { } y39 && MathF.Abs(x39 - (1f - s39)) < .03f && MathF.Abs(y39 - s39) < .03f && x39 > y39, $"band: the 39 m boulder is mostly LOD0 in the cross-fade (LOD0 {a39}, LOD1 {b39}, expected {1f - s39:0.000}/{s39:0.000})");
        Check(a41 is { } x41 && b41 is { } y41 && MathF.Abs(x41 - (1f - s41)) < .03f && MathF.Abs(y41 - s41) < .03f && y41 > x41, $"band: the 41 m boulder is mostly LOD1 in the cross-fade (LOD0 {a41}, LOD1 {b41}, expected {1f - s41:0.000}/{s41:0.000})");
        Check(a39 + b39 is { } t39 && MathF.Abs(t39 - 1f) < .03f && a41 + b41 is { } t41 && MathF.Abs(t41 - 1f) < .03f, "band: the two LODs of a cross-fade do not add up to full coverage");
        Check(a100 == null && b100 is { } x100 && x100 > .999f && c100 == null, $"band: the 100 m boulder is LOD1 only at full coverage (LOD0 {a100}, LOD1 {b100}, LOD2 {c100})");
        Check(b300 == null && c300 is { } x300 && x300 > .999f, $"band: the 300 m boulder is LOD2 only at full coverage (LOD1 {b300}, LOD2 {c300})");
        _log.Add($"bands: 30 m L0 {a30:0.00}; 39 m L0 {a39:0.00} + L1 {b39:0.00}; 41 m L0 {a41:0.00} + L1 {b41:0.00}; 100 m L1 {b100:0.00}; 300 m L2 {c300:0.00}");

        // The camera list: the filter drops what is behind the camera.
        int boulderL1 = outputs.First(o => o.Name == L(1)).BatchId, boulderL0 = outputs.First(o => o.Name == L(0)).BatchId;
        layer.FrustumCulling = true;
        await Frames(20);
        uint ahead = await ReadCameraCount(layer, boulderL0), fullL0 = full[L(0)].Commands[1];
        Check(ahead >= 1 && ahead <= fullL0, $"cull: {ahead} rows ahead of {fullL0}");
        Aim(-forward);
        await Frames(20);
        uint behindL1 = await ReadCameraCount(layer, boulderL1);
        uint fullL1 = full[L(1)].Commands[1];
        Check(behindL1 < fullL1, $"cull: looking away still draws {behindL1} of {fullL1} LOD1 rows");
        _log.Add($"cull: camera list {ahead} of {fullL0} LOD0 rows ahead; {behindL1} of {fullL1} LOD1 rows looking away");
        Aim(forward);
        await Frames(10);

        // Row order under a binding cap: the boulder slot capped at half the in-band rows keeps the nearest rows (source rows are ordered by distance from the anchor centre).
        {
            int total = 0;
            for (int lod = 0; lod < 3; lod++) total += (int)full[L(lod)].Commands[1];
            layer.CapScale = (total / 2 + .2f) / 1500f;
            await Frames(30);
            var kept = new int[3];
            var keptDistance = new List<float>();
            RingLodGeometrySnapshot centred = geometry.CenteredAt(layer.Anchor.Around, layer.Anchor.Across);
            var boulderRecords = Enumerable.Range(0, records.Count).Where(i => GroupOf(records[i]) == 0).ToList();
            float AnchorKey(ScatterRecord r)
            {
                double da = r.AroundCm * .01 - layer.Anchor.Around, dz = r.AcrossCm * .01 - layer.Anchor.Across;
                return (float)Math.Sqrt(da * da + dz * dz);
            }
            float worstKept = 0f; int keptRows = 0;
            var inBandKeys = new List<float>();
            for (int lod = 0; lod < 3; lod++)
            {
                var (commands, rows) = await ReadFull(layer, outputs.First(o => o.Name == L(lod)).BatchId);
                kept[lod] = (int)commands[1];
                (float near, float far) = bands[0][lod];
                foreach (int i in boulderRecords) if (distance[i] >= near && distance[i] < far) inBandKeys.Add(AnchorKey(records[i]));
                for (int r = 0; r < kept[lod]; r++)
                {
                    var position = new Vector3(rows[r * 16 + 3], rows[r * 16 + 7], rows[r * 16 + 11]);
                    int best = -1; float bestD = 1.5f;
                    foreach (int i in boulderRecords)
                    {
                        float d = (centred.CanonicalToWorld(records[i].AroundCm * .01, records[i].SurfaceHeight, records[i].AcrossCm * .01) - position).Length();
                        if (d < bestD) { bestD = d; best = i; }
                    }
                    Check(best >= 0, "cap: a kept row matches no boulder record");
                    worstKept = MathF.Max(worstKept, AnchorKey(records[best]));
                    keptRows++;
                }
            }
            Check(kept.Sum() == total / 2, $"cap: boulder slot kept {kept.Sum()} rows, expected {total / 2} of {total}");
            inBandKeys.Sort();
            // Bands overlap in their fades, so rows are counted per LOD batch; the kept rows must all lie within the nearest keptRows of the in-band set, within one anchor snap.
            float cutoff = inBandKeys[Math.Min(inBandKeys.Count - 1, keptRows - 1)];
            Check(worstKept <= cutoff + AnchorMeters, $"cap: kept rows reach {worstKept:0} m from the anchor, nearest-{keptRows} cutoff {cutoff:0} m (+512)");
            Check(kept[2] < (int)full[L(2)].Commands[1], "cap: the far LOD2 rows were not the ones dropped");
            _log.Add($"cap: boulder slot at half ({total / 2} of {total}): kept L0/L1/L2 {kept[0]}/{kept[1]}/{kept[2]} (were {full[L(0)].Commands[1]}/{full[L(1)].Commands[1]}/{full[L(2)].Commands[1]}); farthest kept {worstKept:0} m from anchor, nearest-{keptRows} cutoff {cutoff:0} m");
            layer.CapScale = 1f;
            await Frames(20);
        }

        // Anchor: 200 m changes nothing, 700 m (past 512) one rebuild.
        current = new RingCanonicalPosition(focus.AroundMeters + 200.0, focus.VerticalMeters, focus.AcrossMeters);
        _cam.Position = CameraAt(current);
        await Frames(40);
        Check(layer.SourceBuilds == 1 && layer.AnchorRebuilds == 0, $"anchor: a 200 m move rebuilt ({layer.SourceBuilds} builds, {layer.AnchorRebuilds} anchor rebuilds)");
        current = new RingCanonicalPosition(focus.AroundMeters + 700.0, focus.VerticalMeters, focus.AcrossMeters);
        _cam.Position = CameraAt(current);
        await WaitFrames(() => layer.AnchorRebuilds >= 1 && layer.SourceBuilds >= 2 && !layer.SetPending && layer.DrawOutputs == 10, 600, "the anchor rebuild to land");
        await Frames(40);
        Check(layer.AnchorRebuilds == 1 && layer.SourceBuilds == 2, $"anchor: a 700 m move gave {layer.AnchorRebuilds} anchor rebuilds and {layer.SourceBuilds} builds");
        Check(layer.BatchCount == 10 && layer.DrawOutputs == 10, $"anchor: after the swap {layer.BatchCount} batches, {layer.DrawOutputs} draw outputs");
        _log.Add($"anchor: 200 m = 0 rebuilds, 700 m = {layer.AnchorRebuilds} rebuild; build {layer.LastSourceBuildMs:0.0} ms; {layer.RendererCount} renderers");

        layer.QueueFree();
        await Frames(3);
    }

    private static float MaxAbs(Vector3 v) => MathF.Max(MathF.Abs(v.X), MathF.Max(MathF.Abs(v.Y), MathF.Abs(v.Z)));
    private static string Fmt(Vector3 v) => $"({v.X:0.000},{v.Y:0.000},{v.Z:0.000})";
    private async Task Frames(int count) { for (int i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
