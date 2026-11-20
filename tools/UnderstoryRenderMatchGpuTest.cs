using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Tools;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Vegetation.Canopy;
using RingworldTechDemo.Vegetation.Scatter;
using RingworldTechDemo.Vegetation.Understory;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tests;

/// <summary>
/// T6-4b (T6 design A7, "Proof"): the understory carrier (materials/ring_understory.gdshader, UnderstoryCatalog) against the same sapling drawn through the base catalog's mesh and its own imported_tree.gdshader materials.
/// Maple sapling 0 and pine sapling 24, LOD0 (full shading) and LOD2 (simplified tree shading, as the tree layer sets it), each drawn once as a MeshInstance3D of the base mesh and once as a one-instance MultiMesh of the carrier,
/// same pose and light, 128 px frame, ambient off, linear tonemap, magenta background. Cases:
///   front-lit (sun behind the camera, forest lighting switches off), backlit (sun behind the sapling, translucency and crown occlusion on), cloud shadow (a full-field cloud map, front-lit),
///   and 500 m of haze (the camera at 500 m, the lens narrowed to keep the pixel size, air 30 times the default as in scatter_haze_shadow_gpu_test).
/// Shadow receive (2026-11-19 play test): every plant also has a "shade" case, a shadow-only occluder between the sun and the plant (sun shadows on, ambient 0.35); the carrier must match the reference there, and both must darken by at least 30 %
/// against the same light without the occluder ("sunamb"), so a carrier that ignores the sun shadow map fails (checked: ATTENUATION forced to 1 fails every shade case).
/// Checks: the mean linear colour of the covered pixels agrees within 3 % (luminance, and each channel as a share of the reference luminance); the covered pixel count within 5 %; the backlit translucency term moves the reference by at least 5 %
/// (so a carrier without it would fail the backlit check); T6-6 adds a snag (derived form 30, LOD1 bark) through the dead-wood near carrier against the derived mesh with its bark material (front, back, cloud, haze; same 3 %), and a dead-wood layer smoke
/// (a snag, a log and a stump through the layer: near and far camera batches plus the snag-and-log caster); then a layer smoke (synthetic sapling records through RingUnderstoryLayer on a fixed host: 2 camera batches, L0 holds the records inside 0 to 30 m, L2 those inside 20 to 150 m, none beyond),
/// and the feature-off rule (no understory layer for a world without bit 6, one for a world with it).
/// T6 sapling caster (slot 4, Very High and up): a layer with SaplingShadows on holds the saplings twice (rows[4] == 2, camera batches plus one "UnderstorySapling_Shadow" caster), and the sun shadow map really carries them: on a lit ground plane, frames with the caster on, off and on again,
/// pixels at least 25 % darker with the caster than without (at least 50, both times) and the two caster-on frames equal within 1 %.
/// T6-7 adds a broadleaf shrub (derived form 43, LOD0) through the shrub carrier against the derived mesh with its maple foliage material (front, back with translucency, cloud, haze; colour within 3 %, coverage within 5 %), and a shrub layer smoke (L0 0 to 45 m, L1 30 to 150 m, no caster).
/// Run windowed: godot --path . --script res://tools/understory_render_match_gpu_test.gd (or tools/run_tests.ps1 -Tests understory_render_match_gpu_test).
/// World frame (as scatter_haze_shadow_gpu_test): the ring axis is +Y, the surface point is the origin, up is -X (the ring centre at x = -R), around is +Z, across is +Y.
/// </summary>
public partial class UnderstoryRenderMatchGpuTest : Node
{
    private const float Radius = 5_000_000f;
    private const float HazeDensityScale = 30f;
    private const int Size = 128;
    private const float BarkChannelTolerance = 0.12f;
    private const float ColourTolerance = 0.03f, CoverageTolerance = 0.05f, TranslucencyMinimum = 0.05f;
    private static readonly Color Background = new(1f, 0f, 1f);
    private static readonly Vector3 Up = new(-1, 0, 0), AroundDir = new(0, 0, 1), AcrossDir = new(0, 1, 0);
    private static readonly Vector4 SwitchesOff = new(0, 0, 0, 0), SwitchesOn = new(1, 0, 0, 1); // translucency, tree shadow distance 0 (analytic crown), canopy shade, crown occlusion
    private static readonly int[] TestSources = { 0, 24 };
    private static readonly string[] CaseNames = { "front", "back", "cloud", "haze", "shade" };
    // Shadow receive: "shade" puts a shadow-only occluder between the sun and the plant (sun shadows on, a little ambient so the shaded plant is not black); "sunamb" is the same light and ambient without the occluder.
    private const float ShadeAmbientEnergy = .35f, ShadeMinimumDrop = .3f;

    public string Result { get; private set; } = "RUNNING";
    private readonly List<string> _log = new();

    private SubViewport _vp = null!;
    private Camera3D _cam = null!;
    private DirectionalLight3D _sun = null!;
    private MeshInstance3D _reference = null!;
    private MultiMeshInstance3D _carrier = null!;
    private MultiMesh _carrierMulti = null!;
    private IReadOnlyList<ImportedTreeVariant> _variants = null!;
    private UnderstoryCatalog _catalog = null!;
    private Texture2D? _cloudMap;
    private Godot.Environment _environment = null!;
    private MeshInstance3D _occluder = null!;

    private readonly record struct Stats(int Covered, Vector3 Mean, float Luminance);

    public async void Start()
    {
        try
        {
            Setup();
            await MatchChecks();
            await SnagChecks();
            await ShrubChecks();
            await StandFillCasterChecks();
            await LayerChecks();
            await SaplingCasterChecks();
            await ShrubLayerChecks();
            await StandFillLayerChecks();
            await DeadWoodLayerChecks();
            FeatureOffChecks();
            Result = "PASS " + string.Join("; ", _log);
        }
        catch (Exception error) { Result = "FAIL " + error.Message + " | " + string.Join("; ", _log); }
        finally { await Frames(2); }
    }

    // ------------------------------------------------------------------ scene

    private void Setup()
    {
        _variants = ImportedTreeCatalog.Load();
        _catalog = UnderstoryCatalog.Load();
        RenderingServer.GlobalShaderParameterSet("ringworld_haze_sun_irradiance", new Vector3(3.2f, 3.0f, 2.7f));
        RenderingServer.GlobalShaderParameterSet("ringworld_haze_sun_irradiance_remote", new Vector3(3.2f, 3.0f, 2.7f));
        RenderingServer.GlobalShaderParameterSet("ringworld_haze_ambient_day", new Vector3(.25f, .32f, .45f));
        RenderingServer.GlobalShaderParameterSet("ringworld_haze_ambient_night", new Vector3(.004f, .005f, .008f));
        RenderingServer.GlobalShaderParameterSet("ringworld_haze_storm", new Vector4(0, 0, 0, 0));
        RenderingServer.GlobalShaderParameterSet("ringworld_hub_light_weight", 0f);
        RenderingServer.GlobalShaderParameterSet("ringworld_hub_day", 0f);
        RenderingServer.GlobalShaderParameterSet("ringworld_solar_visibility", 1f);
        RenderingServer.GlobalShaderParameterSet("ring_local_wind_enabled", 0f);
        RenderingServer.GlobalShaderParameterSet("ring_atmosphere_ray_frame_valid", true);
        RenderingServer.GlobalShaderParameterSet("ring_atmosphere_outward_world", -Up);
        RenderingServer.GlobalShaderParameterSet("ring_atmosphere_axis_world", AcrossDir);
        RenderingServer.GlobalShaderParameterSet("ring_atmosphere_sidewall_negative_meters", 1_000_000f);
        RenderingServer.GlobalShaderParameterSet("ring_atmosphere_sidewall_positive_meters", 1_000_000f);
        RenderingServer.GlobalShaderParameterSet("ring_atmosphere_coordinate_scale", 1f);
        RenderingServer.GlobalShaderParameterSet("ringworld_forest_lighting", SwitchesOff);

        _vp = new SubViewport { Size = new Vector2I(Size, Size), OwnWorld3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        AddChild(_vp);
        var environment = _environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = Background,
            AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = Colors.White, AmbientLightEnergy = 0f,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Disabled, TonemapMode = Godot.Environment.ToneMapper.Linear,
        };
        _vp.AddChild(new WorldEnvironment { Environment = environment });
        _cam = new Camera3D { Near = .3f, Far = 6000f, Current = true };
        _vp.AddChild(_cam);
        _sun = new DirectionalLight3D { ShadowEnabled = false, LightEnergy = 1f };
        _vp.AddChild(_sun);
        // A thin square, shadow only (the camera never sees it); ApplyShade places it between the sun and the plant.
        _occluder = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(40f, 40f, .2f) }, CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly, Visible = false };
        _vp.AddChild(_occluder);
        _reference = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _vp.AddChild(_reference);
        _carrierMulti = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, InstanceCount = 1, CustomAabb = new Aabb(new Vector3(-50, -50, -50), new Vector3(100, 100, 100)) };
        _carrier = new MultiMeshInstance3D { Multimesh = _carrierMulti, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _vp.AddChild(_carrier);
    }

    private void ConfigureMaterial(ShaderMaterial m, bool haze, bool cloud, bool simplified)
    {
        m.SetShaderParameter("ring_center", new Vector3(-Radius, 0f, 0f));
        m.SetShaderParameter("vegetation_wind_enabled", false);
        m.SetShaderParameter("simplified_tree_shading", simplified);
        m.SetShaderParameter("night_tint", Vector3.One);
        m.SetShaderParameter("ring_atmosphere_enabled", haze);
        m.SetShaderParameter("ring_atmosphere_ray_frame_valid", true);
        m.SetShaderParameter("ring_atmosphere_outward_world", -Up);
        m.SetShaderParameter("ring_atmosphere_axis_world", AcrossDir);
        m.SetShaderParameter("ring_atmosphere_sidewall_negative_meters", 1_000_000f);
        m.SetShaderParameter("ring_atmosphere_sidewall_positive_meters", 1_000_000f);
        m.SetShaderParameter("ring_atmosphere_coordinate_scale", 1f);
        m.SetShaderParameter("ring_radius_meters", Radius);
        m.SetShaderParameter("ring_atmosphere_center_world", new Vector3(-Radius, 0, 0));
        m.SetShaderParameter("atmosphere_haze_model", 1);
        float scale = haze ? HazeDensityScale : 0f;
        m.SetShaderParameter("ring_haze_sigma_R", new Vector3(5.802e-6f, 13.558e-6f, 33.1e-6f) * scale);
        m.SetShaderParameter("ring_haze_sigma_A", 4.175e-6f * scale);
        m.SetShaderParameter("atmosphere_boundary_max_opacity", haze ? .35f : 0f);
        m.SetShaderParameter("atmosphere_upper_max_opacity", haze ? .1f : 0f);
        m.SetShaderParameter("atmosphere_distance_haze_max_opacity", haze ? .65f : 0f);
        m.SetShaderParameter("ring_width_meters", 20000f);
        m.SetShaderParameter("local_cloud_shadow_use_globals", false);
        m.SetShaderParameter("local_cloud_shadow_enabled", cloud);
        m.SetShaderParameter("local_cloud_shadow_near_enabled", cloud);
        m.SetShaderParameter("local_cloud_shadow_far_enabled", false);
        if (cloud)
        {
            if (_cloudMap == null)
            {
                var image = Image.CreateEmpty(64, 64, false, Image.Format.Rg8);
                image.Fill(new Color(1f, 0f, 0f, 0f)); // R = 1 everywhere: the whole field is under a cloud
                _cloudMap = ImageTexture.CreateFromImage(image);
            }
            m.SetShaderParameter("local_cloud_shadow_texture", _cloudMap);
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

    // ------------------------------------------------------------------ the match

    private async Task<Stats> Shot(int source, int lodIndex, string caseName, bool useCarrier, bool switchesOn, float yawDegrees)
    {
        ImportedTreeVariant variant = _variants[source];
        int lod = UnderstoryCatalog.SourceLods[lodIndex];
        bool haze = caseName == "haze", cloud = caseName == "cloud", back = caseName == "back", simplified = lod >= 2;
        foreach (ShaderMaterial material in variant.Materials) ConfigureMaterial(material, haze, cloud, UsesLod(variant, material, 2));
        foreach (UnderstoryLodCarrier carrier in _catalog.Lods) ConfigureMaterial(carrier.Material, haze, cloud, carrier.SourceLod >= 2);
        RenderingServer.GlobalShaderParameterSet("ringworld_forest_lighting", switchesOn ? SwitchesOn : SwitchesOff);
        // Light: 20 degrees above the horizon; azimuth 0 is the camera's side (front-lit), 180 the far side (backlit).
        float elevation = Mathf.DegToRad(20f);
        Vector3 horizontal = AroundDir;
        Vector3 toSun = (Up * Mathf.Sin(elevation) + (back ? -horizontal : horizontal) * Mathf.Cos(elevation)).Normalized();
        _sun.Transform = new Transform3D(Basis.LookingAt(-toSun, AcrossDir), Vector3.Zero);
        foreach (ShaderMaterial material in variant.Materials) { material.SetShaderParameter("sun_direction", toSun); material.SetShaderParameter("ring_atmosphere_to_sun", toSun); }
        foreach (UnderstoryLodCarrier carrier in _catalog.Lods) { carrier.Material.SetShaderParameter("sun_direction", toSun); carrier.Material.SetShaderParameter("ring_atmosphere_to_sun", toSun); }
        RenderingServer.GlobalShaderParameterSet("ring_atmosphere_to_sun", toSun);
        // Camera: at distance d on the camera's side, looking at the middle of the sapling; the lens narrows with distance so the sapling keeps its pixel size.
        float height = variant.HeightMeters;
        float distance = haze ? 500f : 6f * height;
        _cam.Fov = Mathf.RadToDeg(2f * MathF.Atan(.6f * height / distance)); // the same pixel size at every distance (the sapling fills the frame)
        Vector3 middle = Up * height * .5f;
        Vector3 position = middle + horizontal * distance;
        _cam.LookAtFromPosition(position, middle, Up);
        ApplyShade(caseName, toSun, middle);
        foreach (ShaderMaterial material in variant.Materials) SetCameraUniforms(material, position);
        foreach (UnderstoryLodCarrier carrier in _catalog.Lods) SetCameraUniforms(carrier.Material, position);
        RenderingServer.GlobalShaderParameterSet("ring_atmosphere_camera_world_position", position);
        RenderingServer.GlobalShaderParameterSet("ring_atmosphere_camera_height_meters", -position.X);
        // The sapling: model +Y becomes the world up (-X); the instance scale is its authored height (the catalog is normalised to a 1 unit tall tree).
        var basis = (new Basis(Up, Mathf.DegToRad(yawDegrees)) * new Basis(Vector3.Back, Mathf.Pi * .5f)).Scaled(Vector3.One * height);
        var placement = new Transform3D(basis, Vector3.Zero);
        _reference.Visible = !useCarrier; _carrier.Visible = useCarrier;
        if (useCarrier)
        {
            UnderstoryLodCarrier carrier = _catalog.Lods[lodIndex];
            _carrierMulti.Mesh = carrier.Carrier;
            _carrier.MaterialOverride = carrier.Material;
            _carrierMulti.SetInstanceTransform(0, placement);
            _carrierMulti.SetInstanceCustomData(0, new Color(UnderstoryCatalog.VariantRowOf(source), 1f, 0f, 0f));
        }
        else
        {
            _reference.Mesh = variant.Lods[lod];
            _reference.Transform = placement;
            // The tree renderers carry the variant's crown hull as instance uniforms (RingBiomeDecorationLayer); the carrier reads the same rows from its parameter texture.
            TreeCrownHullTable.Pack(TreeCrownHullTable.Get()[source], out Vector4 hull, out Vector4 extra, out Vector4 lad);
            _reference.SetInstanceShaderParameter("tree_crown_hull", hull);
            _reference.SetInstanceShaderParameter("tree_crown_hull_extra", extra);
            _reference.SetInstanceShaderParameter("tree_crown_lad", lad);
        }
        await Frames(1);
        for (int i = 0; i < 3; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image image = _vp.GetTexture().GetImage();
        if (image == null || image.IsEmpty()) throw new InvalidOperationException("empty capture");
        return Measure(image);
    }

    /// <summary>Shadow receive cases: "shade" and "sunamb" turn the sun shadow on and add ambient; "shade" also shows the occluder 12 m towards the sun from the plant's middle, facing the sun. Every other case: no shadow, no ambient, no occluder.</summary>
    private void ApplyShade(string caseName, Vector3 toSun, Vector3 middle)
    {
        bool shade = caseName == "shade", lit = shade || caseName == "sunamb";
        _sun.ShadowEnabled = lit;
        _sun.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Orthogonal;
        _sun.DirectionalShadowMaxDistance = 200f;
        _environment.AmbientLightEnergy = lit ? ShadeAmbientEnergy : 0f;
        _occluder.Visible = shade;
        _occluder.Transform = new Transform3D(Basis.LookingAt(-toSun, AcrossDir), middle + toSun * 12f);
    }

    /// <summary>Shadow receive sensitivity: the reference must darken by <see cref="ShadeMinimumDrop"/> in the occluder's shadow (else the shade match could not catch a carrier that ignores the shadow map), and so must the carrier.</summary>
    private static void ShadeDrop(string tag, Stats referenceShade, Stats carrierShade, Stats referenceSun, Stats carrierSun, List<string> failures)
    {
        float referenceDrop = 1f - referenceShade.Luminance / MathF.Max(referenceSun.Luminance, 1e-6f);
        float carrierDrop = 1f - carrierShade.Luminance / MathF.Max(carrierSun.Luminance, 1e-6f);
        GD.Print($"UNDERSTORY_RENDER_MATCH {tag}: the occluder's shadow darkens the reference by {referenceDrop * 100f:0.0} % (lum {referenceSun.Luminance:0.0000} -> {referenceShade.Luminance:0.0000}), the carrier by {carrierDrop * 100f:0.0} % (lum {carrierSun.Luminance:0.0000} -> {carrierShade.Luminance:0.0000})");
        if (referenceDrop < ShadeMinimumDrop) failures.Add($"{tag}: the occluder darkens the reference by only {referenceDrop * 100f:0.0} % (< {ShadeMinimumDrop * 100f:0} %): the shade check cannot detect a missing shadow");
        if (carrierDrop < ShadeMinimumDrop) failures.Add($"{tag}: the carrier does not receive the sun shadow (darkens by {carrierDrop * 100f:0.0} %, < {ShadeMinimumDrop * 100f:0} %)");
    }

    private static bool UsesLod(ImportedTreeVariant variant, ShaderMaterial material, int lod)
    {
        Mesh mesh = variant.Lods[lod];
        for (int s = 0; s < mesh.GetSurfaceCount(); s++) if (ReferenceEquals(mesh.SurfaceGetMaterial(s), material)) return true;
        return false;
    }

    private static void SetCameraUniforms(ShaderMaterial m, Vector3 position)
    {
        m.SetShaderParameter("ring_atmosphere_camera_world", position);
        m.SetShaderParameter("ring_atmosphere_camera_world_position", position);
        m.SetShaderParameter("ring_atmosphere_camera_height_meters", -position.X);
        m.SetShaderParameter("ring_viewer_world_position", position);
    }

    private static Stats Measure(Image image)
    {
        int covered = 0;
        double r = 0, g = 0, b = 0;
        for (int y = 0; y < image.GetHeight(); y++)
            for (int x = 0; x < image.GetWidth(); x++)
            {
                Color c = image.GetPixel(x, y);
                if (MathF.Abs(c.R - Background.R) < .01f && MathF.Abs(c.G - Background.G) < .01f && MathF.Abs(c.B - Background.B) < .01f) continue;
                Color linear = c.SrgbToLinear();
                covered++; r += linear.R; g += linear.G; b += linear.B;
            }
        if (covered == 0) return new Stats(0, Vector3.Zero, 0f);
        var mean = new Vector3((float)(r / covered), (float)(g / covered), (float)(b / covered));
        return new Stats(covered, mean, .2126f * mean.X + .7152f * mean.Y + .0722f * mean.Z);
    }

    private async Task MatchChecks()
    {
        var failures = new List<string>();
        var lines = new List<string>();
        foreach (int source in TestSources)
            for (int l = 0; l < _catalog.Lods.Length; l++)
                foreach (string caseName in CaseNames)
                {
                    bool on = caseName == "back";
                    float yaw = 35f;
                    Stats reference = await Shot(source, l, caseName, false, on, yaw);
                    Stats carrier = await Shot(source, l, caseName, true, on, yaw);
                    string tag = $"sapling {source} L{_catalog.Lods[l].SourceLod} {caseName}";
                    if (reference.Covered < 500) { failures.Add($"{tag}: the reference covers only {reference.Covered} px"); continue; }
                    float coverage = MathF.Abs(carrier.Covered - reference.Covered) / (float)reference.Covered;
                    float lum = MathF.Abs(carrier.Luminance - reference.Luminance) / MathF.Max(reference.Luminance, 1e-6f);
                    Vector3 delta = (carrier.Mean - reference.Mean).Abs() / MathF.Max(reference.Luminance, 1e-6f);
                    float channel = MathF.Max(delta.X, MathF.Max(delta.Y, delta.Z));
                    lines.Add($"{tag}: ref {reference.Covered} px lum {reference.Luminance:0.0000}; carrier {carrier.Covered} px lum {carrier.Luminance:0.0000}; coverage {coverage * 100f:0.00} %, lum {lum * 100f:0.00} %, channel {channel * 100f:0.00} %");
                    if (coverage > CoverageTolerance) failures.Add($"{tag}: coverage differs by {coverage * 100f:0.0} % (> {CoverageTolerance * 100f:0} %)");
                    if (lum > ColourTolerance || channel > ColourTolerance) failures.Add($"{tag}: mean colour differs by {lum * 100f:0.0} % luminance, {channel * 100f:0.0} % channel (> {ColourTolerance * 100f:0} %)");
                    if (caseName == "shade") ShadeDrop(tag, reference, carrier, await Shot(source, l, "sunamb", false, false, yaw), await Shot(source, l, "sunamb", true, false, yaw), failures);
                    // Sensitivity of the backlit case: the translucency and crown terms must move the reference, or the check could not fail.
                    if (caseName == "back")
                    {
                        Stats referenceOff = await Shot(source, l, caseName, false, false, yaw);
                        float moved = MathF.Abs(reference.Luminance - referenceOff.Luminance) / MathF.Max(referenceOff.Luminance, 1e-6f);
                        lines.Add($"{tag}: switches on move the reference by {moved * 100f:0.0} %");
                        if (moved < TranslucencyMinimum) failures.Add($"{tag}: translucency moves the reference by only {moved * 100f:0.0} % (< {TranslucencyMinimum * 100f:0} %): the backlit check cannot detect a missing term");
                    }
                }
        foreach (string line in lines) GD.Print("UNDERSTORY_RENDER_MATCH " + line);
        _log.Add($"{lines.Count} matches ({string.Join(" | ", lines.Take(2))} ...)");
        if (failures.Count > 0) throw new InvalidOperationException(string.Join(" ; ", failures));
    }

    // ------------------------------------------------------------------ the snag (T6-6)

    /// <summary>One snag shot: the derived LOD1 mesh with its bark material (the reference) or the dead-wood near carrier's variant row 0, same pose and light as <see cref="Shot"/>.</summary>
    private async Task<Stats> SnagShot(string caseName, bool useCarrier, bool switchesOn, float yawDegrees)
    {
        UnderstoryDeadWood dead = UnderstoryCatalog.LoadDeadWood();
        DerivedTreeForm form = DerivedTreeCatalog.Form(UnderstoryCatalog.DeadWoodFirstIndex);
        ShaderMaterial bark = form.Materials[0];
        bool haze = caseName == "haze", cloud = caseName == "cloud", back = caseName == "back";
        ConfigureMaterial(bark, haze, cloud, false);
        ConfigureMaterial(dead.Near.Material, haze, cloud, false);
        RenderingServer.GlobalShaderParameterSet("ringworld_forest_lighting", switchesOn ? SwitchesOn : SwitchesOff);
        float elevation = Mathf.DegToRad(20f);
        Vector3 horizontal = AroundDir;
        Vector3 toSun = (Up * Mathf.Sin(elevation) + (back ? -horizontal : horizontal) * Mathf.Cos(elevation)).Normalized();
        _sun.Transform = new Transform3D(Basis.LookingAt(-toSun, AcrossDir), Vector3.Zero);
        foreach (ShaderMaterial material in new[] { bark, dead.Near.Material }) { material.SetShaderParameter("sun_direction", toSun); material.SetShaderParameter("ring_atmosphere_to_sun", toSun); }
        RenderingServer.GlobalShaderParameterSet("ring_atmosphere_to_sun", toSun);
        float height = dead.HeightMeters[0];
        float distance = haze ? 500f : 6f * height;
        _cam.Fov = Mathf.RadToDeg(2f * MathF.Atan(.3f * height / distance)); // a snag is slim: the lens is half as wide as for a sapling so that it covers enough pixels
        Vector3 middle = Up * height * .5f;
        Vector3 position = middle + horizontal * distance;
        _cam.LookAtFromPosition(position, middle, Up);
        ApplyShade(caseName, toSun, middle);
        SetCameraUniforms(bark, position); SetCameraUniforms(dead.Near.Material, position);
        RenderingServer.GlobalShaderParameterSet("ring_atmosphere_camera_world_position", position);
        RenderingServer.GlobalShaderParameterSet("ring_atmosphere_camera_height_meters", -position.X);
        var basis = (new Basis(Up, Mathf.DegToRad(yawDegrees)) * new Basis(Vector3.Back, Mathf.Pi * .5f)).Scaled(Vector3.One * height);
        var placement = new Transform3D(basis, Vector3.Zero);
        _reference.Visible = !useCarrier; _carrier.Visible = useCarrier;
        if (useCarrier)
        {
            _carrierMulti.Mesh = dead.Near.Carrier;
            _carrier.MaterialOverride = dead.Near.Material;
            _carrierMulti.SetInstanceTransform(0, placement);
            _carrierMulti.SetInstanceCustomData(0, new Color(0f, 1f, 0f, 0f)); // variant row 0 = form 30, the first snag
        }
        else
        {
            _reference.Mesh = form.Lods[1];
            _reference.Transform = placement;
        }
        await Frames(1);
        for (int i = 0; i < 3; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image image = _vp.GetTexture().GetImage();
        if (image == null || image.IsEmpty()) throw new InvalidOperationException("empty capture");
        return Measure(image);
    }

    private async Task SnagChecks()
    {
        var failures = new List<string>();
        foreach (string caseName in CaseNames)
        {
            bool on = caseName == "back";
            Stats reference = await SnagShot(caseName, false, on, 35f);
            Stats carrier = await SnagShot(caseName, true, on, 35f);
            string tag = $"snag 30 LOD1 {caseName}";
            if (reference.Covered < 500) { failures.Add($"{tag}: the reference covers only {reference.Covered} px"); continue; }
            float coverage = MathF.Abs(carrier.Covered - reference.Covered) / (float)reference.Covered;
            float lum = MathF.Abs(carrier.Luminance - reference.Luminance) / MathF.Max(reference.Luminance, 1e-6f);
            Vector3 delta = (carrier.Mean - reference.Mean).Abs() / MathF.Max(reference.Luminance, 1e-6f);
            float channel = MathF.Max(delta.X, MathF.Max(delta.Y, delta.Z));
            GD.Print($"UNDERSTORY_RENDER_MATCH {tag}: ref {reference.Covered} px lum {reference.Luminance:0.0000}; carrier {carrier.Covered} px lum {carrier.Luminance:0.0000}; coverage {coverage * 100f:0.00} %, lum {lum * 100f:0.00} %, channel {channel * 100f:0.00} % (mean ref {reference.Mean}, carrier {carrier.Mean})");
            if (coverage > CoverageTolerance) failures.Add($"{tag}: coverage differs by {coverage * 100f:0.0} % (> {CoverageTolerance * 100f:0} %)");
            // Luminance within 3 %. A snag is nothing but dark bark (linear blue about 0.03), where the 512 px DXT5 bark layer and the imported 2048 px bark quantise differently (5:6:5 endpoints): a single channel, as a share of the
            // reference luminance, is allowed 12 % (measured 4 to 11 %, hue only; the saplings, which are mostly foliage, hold 3 %).
            if (lum > ColourTolerance || channel > BarkChannelTolerance) failures.Add($"{tag}: mean colour differs by {lum * 100f:0.0} % luminance, {channel * 100f:0.0} % channel (> {ColourTolerance * 100f:0} % and {BarkChannelTolerance * 100f:0} %)");
            if (caseName == "shade") ShadeDrop(tag, reference, carrier, await SnagShot("sunamb", false, false, 35f), await SnagShot("sunamb", true, false, 35f), failures);
        }
        if (failures.Count > 0) throw new InvalidOperationException(string.Join(" ; ", failures));
        _log.Add("snag 30 (LOD1 bark) through the dead-wood near carrier matches the derived mesh with its bark material in 5 cases (front, back, cloud, haze, shade)");
    }

    // ------------------------------------------------------------------ the shrub (T6-7)

    /// <summary>One shrub shot: the derived LOD0 mesh (form 43, broadleaf) with its maple foliage material (the reference) or the shrub LOD0 carrier's variant row 0, same pose and light as <see cref="Shot"/>. The shrub is 1 unit tall, scaled to 2 m.</summary>
    private async Task<Stats> ShrubShot(string caseName, bool useCarrier, bool switchesOn, float yawDegrees)
    {
        UnderstoryShrubs shrubs = UnderstoryCatalog.LoadShrubs();
        DerivedTreeForm form = DerivedTreeCatalog.Form(UnderstoryCatalog.ShrubFirstIndex);
        ShaderMaterial foliage = form.Materials[0];
        bool haze = caseName == "haze", cloud = caseName == "cloud", back = caseName == "back";
        ConfigureMaterial(foliage, haze, cloud, false);
        ConfigureMaterial(shrubs.Lod0.Material, haze, cloud, false);
        RenderingServer.GlobalShaderParameterSet("ringworld_forest_lighting", switchesOn ? SwitchesOn : SwitchesOff);
        float elevation = Mathf.DegToRad(20f);
        Vector3 horizontal = AroundDir;
        Vector3 toSun = (Up * Mathf.Sin(elevation) + (back ? -horizontal : horizontal) * Mathf.Cos(elevation)).Normalized();
        _sun.Transform = new Transform3D(Basis.LookingAt(-toSun, AcrossDir), Vector3.Zero);
        foreach (ShaderMaterial material in new[] { foliage, shrubs.Lod0.Material }) { material.SetShaderParameter("sun_direction", toSun); material.SetShaderParameter("ring_atmosphere_to_sun", toSun); }
        RenderingServer.GlobalShaderParameterSet("ring_atmosphere_to_sun", toSun);
        const float height = 2f;
        float distance = haze ? 500f : 6f * height;
        _cam.Fov = Mathf.RadToDeg(2f * MathF.Atan(.7f * height / distance));
        Vector3 middle = Up * height * .5f;
        Vector3 position = middle + horizontal * distance;
        _cam.LookAtFromPosition(position, middle, Up);
        ApplyShade(caseName, toSun, middle);
        SetCameraUniforms(foliage, position); SetCameraUniforms(shrubs.Lod0.Material, position);
        RenderingServer.GlobalShaderParameterSet("ring_atmosphere_camera_world_position", position);
        RenderingServer.GlobalShaderParameterSet("ring_atmosphere_camera_height_meters", -position.X);
        var basis = (new Basis(Up, Mathf.DegToRad(yawDegrees)) * new Basis(Vector3.Back, Mathf.Pi * .5f)).Scaled(Vector3.One * height);
        var placement = new Transform3D(basis, Vector3.Zero);
        _reference.Visible = !useCarrier; _carrier.Visible = useCarrier;
        if (useCarrier)
        {
            _carrierMulti.Mesh = shrubs.Lod0.Carrier;
            _carrier.MaterialOverride = shrubs.Lod0.Material;
            _carrierMulti.SetInstanceTransform(0, placement);
            _carrierMulti.SetInstanceCustomData(0, new Color(0f, 1f, 0f, 0f)); // variant row 0 = form 43, the first broadleaf shrub
        }
        else
        {
            _reference.Mesh = form.Lods[0];
            _reference.Transform = placement;
            TreeCrownHullTable.Pack(shrubs.Hulls[0], out Vector4 hull, out Vector4 extra, out Vector4 lad);
            _reference.SetInstanceShaderParameter("tree_crown_hull", hull);
            _reference.SetInstanceShaderParameter("tree_crown_hull_extra", extra);
            _reference.SetInstanceShaderParameter("tree_crown_lad", lad);
        }
        await Frames(1);
        for (int i = 0; i < 3; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image image = _vp.GetTexture().GetImage();
        if (image == null || image.IsEmpty()) throw new InvalidOperationException("empty capture");
        return Measure(image);
    }

    private async Task ShrubChecks()
    {
        var failures = new List<string>();
        foreach (string caseName in CaseNames)
        {
            bool on = caseName == "back";
            Stats reference = await ShrubShot(caseName, false, on, 35f);
            Stats carrier = await ShrubShot(caseName, true, on, 35f);
            string tag = $"shrub 43 LOD0 {caseName}";
            if (reference.Covered < 500) { failures.Add($"{tag}: the reference covers only {reference.Covered} px"); continue; }
            float coverage = MathF.Abs(carrier.Covered - reference.Covered) / (float)reference.Covered;
            float lum = MathF.Abs(carrier.Luminance - reference.Luminance) / MathF.Max(reference.Luminance, 1e-6f);
            Vector3 delta = (carrier.Mean - reference.Mean).Abs() / MathF.Max(reference.Luminance, 1e-6f);
            float channel = MathF.Max(delta.X, MathF.Max(delta.Y, delta.Z));
            GD.Print($"UNDERSTORY_RENDER_MATCH {tag}: ref {reference.Covered} px lum {reference.Luminance:0.0000}; carrier {carrier.Covered} px lum {carrier.Luminance:0.0000}; coverage {coverage * 100f:0.00} %, lum {lum * 100f:0.00} %, channel {channel * 100f:0.00} %");
            if (coverage > CoverageTolerance) failures.Add($"{tag}: coverage differs by {coverage * 100f:0.0} % (> {CoverageTolerance * 100f:0} %)");
            if (lum > ColourTolerance || channel > ColourTolerance) failures.Add($"{tag}: mean colour differs by {lum * 100f:0.0} % luminance, {channel * 100f:0.0} % channel (> {ColourTolerance * 100f:0} %)");
            if (caseName == "shade") ShadeDrop(tag, reference, carrier, await ShrubShot("sunamb", false, false, 35f), await ShrubShot("sunamb", true, false, 35f), failures);
            if (caseName == "back")
            {
                Stats referenceOff = await ShrubShot(caseName, false, false, 35f);
                float moved = MathF.Abs(reference.Luminance - referenceOff.Luminance) / MathF.Max(referenceOff.Luminance, 1e-6f);
                GD.Print($"UNDERSTORY_RENDER_MATCH {tag}: switches on move the reference by {moved * 100f:0.0} %");
                if (moved < TranslucencyMinimum) failures.Add($"{tag}: translucency moves the reference by only {moved * 100f:0.0} % (< {TranslucencyMinimum * 100f:0} %): the backlit check cannot detect a missing term");
            }
        }
        if (failures.Count > 0) throw new InvalidOperationException(string.Join(" ; ", failures));
        _log.Add("shrub 43 (broadleaf LOD0) through the shrub carrier matches the derived mesh with the maple foliage material in 5 cases (front, back, cloud, haze, shade)");
    }

    // ------------------------------------------------------------------ the stand filler caster (SF-1)

    /// <summary>One shot of a stand filler billboard (the authored LOD3 of base variant <paramref name="source"/>): the tree material's own mesh (the reference) or the caster carrier's variant row, front-lit, switches off.</summary>
    private async Task<Stats> StandFillShot(int source, bool useCarrier, float yawDegrees)
    {
        UnderstoryStandFill fill = UnderstoryCatalog.LoadStandFill();
        ImportedTreeVariant variant = _variants[source];
        int row = UnderstoryCatalog.StandFillRowOf(source);
        Mesh billboard = variant.Lods[3];
        foreach (ShaderMaterial material in ImportedTreeRenderMaterials.ShaderMaterials(billboard)) ConfigureMaterial(material, false, false, false);
        ConfigureMaterial(fill.Caster.Material, false, false, false);
        RenderingServer.GlobalShaderParameterSet("ringworld_forest_lighting", SwitchesOff);
        float elevation = Mathf.DegToRad(20f);
        Vector3 toSun = (Up * Mathf.Sin(elevation) + AroundDir * Mathf.Cos(elevation)).Normalized();
        _sun.Transform = new Transform3D(Basis.LookingAt(-toSun, AcrossDir), Vector3.Zero);
        foreach (ShaderMaterial material in ImportedTreeRenderMaterials.ShaderMaterials(billboard).Append(fill.Caster.Material)) { material.SetShaderParameter("sun_direction", toSun); material.SetShaderParameter("ring_atmosphere_to_sun", toSun); }
        RenderingServer.GlobalShaderParameterSet("ring_atmosphere_to_sun", toSun);
        float height = variant.HeightMeters, distance = 6f * height;
        _cam.Fov = Mathf.RadToDeg(2f * MathF.Atan(.6f * height / distance));
        Vector3 middle = Up * height * .5f;
        Vector3 position = middle + AroundDir * distance;
        _cam.LookAtFromPosition(position, middle, Up);
        ApplyShade("front", toSun, middle);
        foreach (ShaderMaterial material in ImportedTreeRenderMaterials.ShaderMaterials(billboard).Append(fill.Caster.Material)) SetCameraUniforms(material, position);
        RenderingServer.GlobalShaderParameterSet("ring_atmosphere_camera_world_position", position);
        RenderingServer.GlobalShaderParameterSet("ring_atmosphere_camera_height_meters", -position.X);
        var basis = (new Basis(Up, Mathf.DegToRad(yawDegrees)) * new Basis(Vector3.Back, Mathf.Pi * .5f)).Scaled(Vector3.One * height);
        var placement = new Transform3D(basis, Vector3.Zero);
        _reference.Visible = !useCarrier; _carrier.Visible = useCarrier;
        if (useCarrier)
        {
            _carrierMulti.Mesh = fill.Caster.Carrier;
            _carrier.MaterialOverride = fill.Caster.Material;
            _carrierMulti.SetInstanceTransform(0, placement);
            _carrierMulti.SetInstanceCustomData(0, new Color(row, 1f, 0f, 0f));
        }
        else
        {
            _reference.Mesh = billboard;
            _reference.Transform = placement;
            TreeCrownHullTable.Pack(TreeCrownHullTable.Get()[source], out Vector4 hull, out Vector4 extra, out Vector4 lad);
            _reference.SetInstanceShaderParameter("tree_crown_hull", hull);
            _reference.SetInstanceShaderParameter("tree_crown_hull_extra", extra);
            _reference.SetInstanceShaderParameter("tree_crown_lad", lad);
        }
        await Frames(1);
        for (int i = 0; i < 3; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image image = _vp.GetTexture().GetImage();
        if (image == null || image.IsEmpty()) throw new InvalidOperationException("empty capture");
        return Measure(image);
    }

    /// <summary>Each of the six stand filler variants' billboards through the caster carrier covers the same pixels as the tree material's own billboard mesh (the cutout from billboard_albedo's alpha); the colour is only bounded (the caster has no billboard normal map).</summary>
    private async Task StandFillCasterChecks()
    {
        const float CasterColourTolerance = 0.5f; // the caster has no billboard normal map and takes the species roughness and metallic constants, so it is brighter than the tree material (measured 14 to 40 %); the cutout (coverage) is the contract
        var failures = new List<string>();
        foreach (int source in UnderstoryCatalog.StandFillSources)
            foreach (float yaw in new[] { 0f, 40f })
            {
                Stats reference = await StandFillShot(source, false, yaw);
                Stats carrier = await StandFillShot(source, true, yaw);
                string tag = $"stand filler caster {source} yaw {yaw:0}";
                if (reference.Covered < 500) { failures.Add($"{tag}: the reference covers only {reference.Covered} px"); continue; }
                float coverage = MathF.Abs(carrier.Covered - reference.Covered) / (float)reference.Covered;
                float lum = MathF.Abs(carrier.Luminance - reference.Luminance) / MathF.Max(reference.Luminance, 1e-6f);
                Vector3 delta = (carrier.Mean - reference.Mean).Abs() / MathF.Max(reference.Luminance, 1e-6f);
                float channel = MathF.Max(delta.X, MathF.Max(delta.Y, delta.Z));
                GD.Print($"UNDERSTORY_RENDER_MATCH {tag}: ref {reference.Covered} px lum {reference.Luminance:0.0000}; carrier {carrier.Covered} px lum {carrier.Luminance:0.0000}; coverage {coverage * 100f:0.00} %, lum {lum * 100f:0.00} %, channel {channel * 100f:0.00} %");
                if (coverage > CoverageTolerance) failures.Add($"{tag}: coverage differs by {coverage * 100f:0.0} % (> {CoverageTolerance * 100f:0} %)");
                if (lum > CasterColourTolerance || channel > CasterColourTolerance) failures.Add($"{tag}: mean colour differs by {lum * 100f:0.0} % luminance, {channel * 100f:0.0} % channel (> {CasterColourTolerance * 100f:0} %)");
            }
        if (failures.Count > 0) throw new InvalidOperationException(string.Join(" ; ", failures));
        _log.Add("stand filler caster carrier (LOD3 billboards of 9-11 and 27-29) matches the tree material's billboard mesh in coverage (colour within 50 %: no billboard normal map)");
    }

    // ------------------------------------------------------------------ the layer

    /// <summary>Shrubs through the layer: L0 (0 to 45 m) and L1 (30 to 150 m), no caster, the sapling rows untouched (a sapling and a boulder alongside).</summary>
    private async Task ShrubLayerChecks()
    {
        RenderingServer.GlobalShaderParameterSet("ringworld_forest_lighting", SwitchesOff);
        const double Circ = LandscapeWorld.Circumference;
        double ringRadius = Circ / Math.Tau;
        LandscapePose pose = LandscapePoseFinder.ReadFixture(ProjectSettings.GlobalizePath("res://tools/fixtures/landscape_poses_dressing_s1337.json")).Poses.First(p => p.Name == "steep_eye");
        ScatterRecord Rec(ulong id, double distance, ushort cls, int mesh, float size)
            => new(id, (long)Math.Round((pose.Around + distance) * 100.0), (int)Math.Round(pose.Across * 100.0), (float)pose.Ground, cls, (ushort)mesh, size, .3f, ScatterRecord.EncodeUpOct(0, 1, 0), 0);
        var records = new List<ScatterRecord>
        {
            Rec(0xC000, 10, 11, 43, 1.5f), Rec(0xC001, 40, 11, 47, 2f), Rec(0xC002, 100, 11, 51, 1f), Rec(0xC003, 200, 11, 44, 1f), Rec(0xC004, 20, 12, 0, 1f), Rec(0xC005, 12, 2, 3, 2f),
        };
        var focus = new RingCanonicalPosition(pose.Around, pose.Ground, pose.Across);
        var geometry = new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero, ringRadius, 0.0, 0.0, Circ, 0.0, 0.0, focus);
        Vector3 camera = geometry.CanonicalToWorld(focus.AroundMeters, focus.VerticalMeters, focus.AcrossMeters);
        double angle = pose.Around / ringRadius;
        Vector3 forward = new Vector3((float)-Math.Sin(angle), 0f, (float)Math.Cos(angle)), up = new Vector3((float)-Math.Cos(angle), 0f, (float)-Math.Sin(angle));
        _cam.Fov = 70f; _cam.Far = 6000f;
        _cam.LookAtFromPosition(camera, camera + forward * 100f, up);
        _reference.Visible = false; _carrier.Visible = false;
        var layer = new RingUnderstoryLayer
        {
            Name = "UnderstoryLayer", FrustumCulling = false,
            Host = new ScatterLayerHost
            {
                Geometry = () => geometry, Focus = () => focus, Epoch = () => 1, CopyRecords = into => into.AddRange(records),
                RingAcrossWidthMeters = () => LandscapeWorld.Width, Sun = () => _sun,
            },
        };
        _vp.AddChild(layer);
        // Outputs: sapling L0 and L2 (1 sapling record), shrub L0 and L1 (4 shrub records).
        await WaitUntil(() => layer.DrawOutputs == 4 && !layer.SetPending);
        if (layer.DrawOutputs != 4) throw new InvalidOperationException($"shrub layer: {layer.DrawOutputs} draw outputs, expected 4 (batches {layer.BatchCount}, failed {layer.Failed})");
        int[] rows = layer.SourceRows;
        if (rows[0] != 1 || rows[1] != 4) throw new InvalidOperationException($"shrub layer: rows {string.Join("/", rows)}, expected 1 sapling and 4 shrubs (the boulder is ignored)");
        var info = layer.OutputInfo();
        if (info.Any(o => o.Caster)) throw new InvalidOperationException("shrub layer: shrubs must not cast");
        var shrubOutputs = info.Where(o => o.Name.StartsWith("UnderstoryShrub")).OrderBy(o => o.Name).ToList(); // "UnderstoryShrub_L0", "UnderstoryShrub_L1"
        if (shrubOutputs.Count != 2) throw new InvalidOperationException($"shrub layer: {shrubOutputs.Count} shrub outputs, expected 2");
        uint[] counts = new uint[2];
        for (int l = 0; l < 2; l++)
        {
            var done = new TaskCompletionSource<uint[]>();
            layer.Compute!.RequestCameraReadbackForTests(shrubOutputs[l].BatchId, (c, _) => done.TrySetResult(c));
            uint[] commands = await done.Task;
            counts[l] = commands.Length >= 2 ? commands[1] : 0u;
        }
        // L0 (0 to 45 m): the shrubs at 10 and 40 m. L1 (30 to 150 m): 40 and 100 m. The one at 200 m is beyond both.
        if (counts[0] != 2 || counts[1] != 2) throw new InvalidOperationException($"shrub layer: L0 draws {counts[0]} and L1 {counts[1]} instances, expected 2 and 2");
        _log.Add($"shrub layer: L0 {counts[0]} / L1 {counts[1]} instances of 4 shrub records, no caster");
        layer.Free();
    }

    /// <summary>T6 SF-3: stand fillers through the layer. Slot 5 holds the four class-15 records, the L0 (0 to 35 m) and L2 (25 to 150 m) outputs draw 2 and 2 of them; with StandFillShadows slot 6 holds them again and one ShadowsOnly "UnderstoryStandFill_Shadow" caster appears (absent with it off, switched at runtime).</summary>
    private async Task StandFillLayerChecks()
    {
        RenderingServer.GlobalShaderParameterSet("ringworld_forest_lighting", SwitchesOff);
        const double Circ = LandscapeWorld.Circumference;
        double ringRadius = Circ / Math.Tau;
        LandscapePose pose = LandscapePoseFinder.ReadFixture(ProjectSettings.GlobalizePath("res://tools/fixtures/landscape_poses_dressing_s1337.json")).Poses.First(p => p.Name == "steep_eye");
        ScatterRecord Fill(ulong id, double distance, float size)
            => new(id, (long)Math.Round((pose.Around + distance) * 100.0), (int)Math.Round(pose.Across * 100.0), (float)pose.Ground, 15, (ushort)UnderstoryCatalog.StandFillSources[0], size, .3f, ScatterRecord.EncodeUpOct(0, 1, 0), 0);
        var records = new List<ScatterRecord> { Fill(0xC000, 10, 1f), Fill(0xC001, 30, 1f), Fill(0xC002, 100, 1f), Fill(0xC003, 200, 1f) };
        var focus = new RingCanonicalPosition(pose.Around, pose.Ground, pose.Across);
        var geometry = new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero, ringRadius, 0.0, 0.0, Circ, 0.0, 0.0, focus);
        Vector3 camera = geometry.CanonicalToWorld(focus.AroundMeters, focus.VerticalMeters, focus.AcrossMeters);
        double angle = pose.Around / ringRadius;
        Vector3 forward = new Vector3((float)-Math.Sin(angle), 0f, (float)Math.Cos(angle)), up = new Vector3((float)-Math.Cos(angle), 0f, (float)-Math.Sin(angle));
        _cam.Fov = 70f; _cam.Far = 6000f;
        _cam.LookAtFromPosition(camera, camera + forward * 100f, up);
        _reference.Visible = false; _carrier.Visible = false;
        var layer = new RingUnderstoryLayer
        {
            Name = "UnderstoryLayer", FrustumCulling = false, StandFillShadows = true,
            Host = new ScatterLayerHost
            {
                Geometry = () => geometry, Focus = () => focus, Epoch = () => 1, CopyRecords = into => into.AddRange(records),
                RingAcrossWidthMeters = () => LandscapeWorld.Width, Sun = () => _sun,
            },
        };
        _vp.AddChild(layer);
        await WaitUntil(() => layer.OutputInfo().Count == 3 && layer.DrawOutputs == 3 && !layer.SetPending);
        if (layer.DrawOutputs != 3) throw new InvalidOperationException($"stand fill layer: {layer.DrawOutputs} draw outputs, expected 3 (batches {layer.BatchCount}, failed {layer.Failed})");
        int[] rows = layer.SourceRows;
        if (rows[5] != 4 || rows[6] != 4 || rows[0] != 0) throw new InvalidOperationException($"stand fill layer: rows {string.Join("/", rows)}, expected 4 in slot 5 and 4 in slot 6");
        var info = layer.OutputInfo();
        var fill = info.Where(o => !o.Caster).OrderBy(o => o.Name).ToList(); // "UnderstoryStandFill_L0", "UnderstoryStandFill_L2"
        if (fill.Count != 2 || fill[0].Name != "UnderstoryStandFill_L0" || fill[1].Name != "UnderstoryStandFill_L2" || fill.Any(o => o.Group != 5)) throw new InvalidOperationException("stand fill layer: expected the L0 and L2 outputs of group 5");
        var caster = info.Where(o => o.Caster).ToList();
        if (caster.Count != 1 || caster[0].Name != "UnderstoryStandFill_Shadow" || caster[0].Group != 6) throw new InvalidOperationException("stand fill layer: expected one UnderstoryStandFill_Shadow caster of group 6");
        if (layer.FindChild("UnderstoryStandFill_Shadow", true, false) is not GeometryInstance3D shadow || shadow.CastShadow != GeometryInstance3D.ShadowCastingSetting.ShadowsOnly) throw new InvalidOperationException("stand fill layer: the caster does not draw ShadowsOnly");
        uint[] counts = new uint[2];
        for (int l = 0; l < 2; l++)
        {
            var done = new TaskCompletionSource<uint[]>();
            layer.Compute!.RequestCameraReadbackForTests(fill[l].BatchId, (c, _) => done.TrySetResult(c));
            uint[] commands = await done.Task;
            counts[l] = commands.Length >= 2 ? commands[1] : 0u;
        }
        // L0 (0 to 35 m): 10 and 30 m. L2 (25 to 150 m): 30 and 100 m. 200 m is beyond both.
        if (counts[0] != 2 || counts[1] != 2) throw new InvalidOperationException($"stand fill layer: L0 draws {counts[0]} and L2 {counts[1]} instances, expected 2 and 2");
        layer.StandFillShadows = false;
        await WaitUntil(() => layer.OutputInfo().Count == 2 && !layer.SetPending);
        if (layer.OutputInfo().Count != 2 || layer.OutputInfo().Any(o => o.Caster) || layer.SourceRows[6] != 0) throw new InvalidOperationException("stand fill layer: the caster remained after StandFillShadows was switched off");
        _log.Add($"stand fill layer: slot 5 holds 4 rows, L0 {counts[0]} / L2 {counts[1]} instances, ShadowsOnly caster present with StandFillShadows and gone without");
        layer.Free();
    }

    private async Task LayerChecks()
    {
        RenderingServer.GlobalShaderParameterSet("ringworld_forest_lighting", SwitchesOff);
        const double Circ = LandscapeWorld.Circumference;
        double ringRadius = Circ / Math.Tau;
        LandscapePose pose = LandscapePoseFinder.ReadFixture(ProjectSettings.GlobalizePath("res://tools/fixtures/landscape_poses_dressing_s1337.json")).Poses.First(p => p.Name == "steep_eye");
        ScatterRecord Syn(ulong id, double distance, float size, int source)
            => new(id, (long)Math.Round((pose.Around + distance) * 100.0), (int)Math.Round(pose.Across * 100.0), (float)pose.Ground, 12, (ushort)source, size, .3f, ScatterRecord.EncodeUpOct(0, 1, 0), 0);
        var records = new List<ScatterRecord>
        {
            Syn(0xB000, 10, 1f, 0), Syn(0xB001, 25, 1f, 24), Syn(0xB002, 100, 1f, 1), Syn(0xB003, 200, 1f, 25),
            new(0xB004, (long)Math.Round((pose.Around + 12) * 100.0), (int)Math.Round(pose.Across * 100.0), (float)pose.Ground, 2, 3, 2f, 0f, ScatterRecord.EncodeUpOct(0, 1, 0), 0), // a boulder: ignored
        };
        var focus = new RingCanonicalPosition(pose.Around, pose.Ground, pose.Across);
        var geometry = new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero, ringRadius, 0.0, 0.0, Circ, 0.0, 0.0, focus);
        Vector3 camera = geometry.CanonicalToWorld(focus.AroundMeters, focus.VerticalMeters, focus.AcrossMeters);
        double angle = pose.Around / ringRadius;
        Vector3 forward = new Vector3((float)-Math.Sin(angle), 0f, (float)Math.Cos(angle)), up = new Vector3((float)-Math.Cos(angle), 0f, (float)-Math.Sin(angle));
        _cam.Fov = 70f; _cam.Far = 6000f;
        _cam.LookAtFromPosition(camera, camera + forward * 100f, up);
        _reference.Visible = false; _carrier.Visible = false;
        var layer = new RingUnderstoryLayer
        {
            Name = "UnderstoryLayer", FrustumCulling = false,
            Host = new ScatterLayerHost
            {
                Geometry = () => geometry, Focus = () => focus, Epoch = () => 1, CopyRecords = into => into.AddRange(records),
                RingAcrossWidthMeters = () => LandscapeWorld.Width, Sun = () => _sun,
            },
        };
        _vp.AddChild(layer);
        await WaitUntil(() => layer.DrawOutputs == 2 && !layer.SetPending);
        if (layer.DrawOutputs != 2) throw new InvalidOperationException($"layer: {layer.DrawOutputs} draw outputs, expected 2 (batches {layer.BatchCount}, failed {layer.Failed})");
        if (layer.BatchCount != 2) throw new InvalidOperationException($"layer: {layer.BatchCount} batches, expected 2");
        if (layer.Compute!.GetType().Name != "RingGpuVegetationCompute" || layer.SourceRows[0] != 4) throw new InvalidOperationException($"layer: {layer.SourceRows[0]} sapling rows, expected 4 (the boulder is ignored)");
        var info = layer.OutputInfo();
        if (info.Count(o => !o.Caster) != 2 || info.Any(o => o.Caster)) throw new InvalidOperationException("layer: expected 2 camera outputs and no caster");
        uint[] counts = new uint[2];
        for (int l = 0; l < 2; l++)
        {
            var done = new TaskCompletionSource<uint[]>();
            layer.Compute!.RequestCameraReadbackForTests(info[l].BatchId, (c, _) => done.TrySetResult(c));
            uint[] commands = await done.Task;
            counts[l] = commands.Length >= 2 ? commands[1] : 0u;
        }
        // Records at 10 and 25 m are inside L0 (0 to 30 m); 25 and 100 m inside L2 (20 to 150 m); 200 m is beyond both.
        if (counts[0] != 2 || counts[1] != 2) throw new InvalidOperationException($"layer: L0 draws {counts[0]} and L2 {counts[1]} instances, expected 2 and 2");
        if (layer.GetParent() != _vp || !layer.GetChildren().OfType<Node3D>().All(n => n.Name.ToString().StartsWith("UnderstorySet") || n.Name.ToString() == "UnderstoryColliders")) throw new InvalidOperationException("layer: renderers are not under the layer's set");
        _log.Add($"layer: 2 batches, L0 {counts[0]} / L2 {counts[1]} instances of 4 sapling records, boulder ignored");
        layer.Free();
    }

    /// <summary>Sapling caster: SaplingShadows adds slot 4 and the "UnderstorySapling_Shadow" output; the sun shadow map shows it on a lit ground plane, and switching it off and on again at runtime removes and restores it.</summary>
    private async Task SaplingCasterChecks()
    {
        RenderingServer.GlobalShaderParameterSet("ringworld_forest_lighting", SwitchesOff);
        const double Circ = LandscapeWorld.Circumference;
        double ringRadius = Circ / Math.Tau;
        LandscapePose pose = LandscapePoseFinder.ReadFixture(ProjectSettings.GlobalizePath("res://tools/fixtures/landscape_poses_dressing_s1337.json")).Poses.First(p => p.Name == "steep_eye");
        ScatterRecord Syn(ulong id, double distance, float size, int source)
            => new(id, (long)Math.Round((pose.Around + distance) * 100.0), (int)Math.Round(pose.Across * 100.0), (float)pose.Ground, 12, (ushort)source, size, .3f, ScatterRecord.EncodeUpOct(0, 1, 0), 0);
        var records = new List<ScatterRecord> { Syn(0xC000, 15, 1f, 0), Syn(0xC001, 200, 1f, 0) };
        var focus = new RingCanonicalPosition(pose.Around, pose.Ground, pose.Across);
        var geometry = new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero, ringRadius, 0.0, 0.0, Circ, 0.0, 0.0, focus);
        Vector3 camera = geometry.CanonicalToWorld(focus.AroundMeters, focus.VerticalMeters, focus.AcrossMeters);
        double angle = pose.Around / ringRadius;
        Vector3 forward = new Vector3((float)-Math.Sin(angle), 0f, (float)Math.Cos(angle)), up = new Vector3((float)-Math.Cos(angle), 0f, (float)-Math.Sin(angle));
        _cam.Fov = 70f; _cam.Far = 6000f;
        _cam.LookAtFromPosition(camera, camera + forward * 100f, up);
        _reference.Visible = false; _carrier.Visible = false;
        // Sun 60 degrees above the horizon of the pose, shadow map on, a little ambient so the shaded ground is not black.
        Vector3 toSun = (up * Mathf.Sin(Mathf.DegToRad(60f)) + forward * Mathf.Cos(Mathf.DegToRad(60f))).Normalized();
        _sun.Transform = new Transform3D(Basis.LookingAt(-toSun, AcrossDir), Vector3.Zero);
        _sun.ShadowEnabled = true;
        _sun.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Orthogonal;
        _sun.DirectionalShadowMaxDistance = 200f;
        _environment.AmbientLightEnergy = ShadeAmbientEnergy;
        RenderingServer.GlobalShaderParameterSet("ring_atmosphere_to_sun", toSun);
        foreach (UnderstoryLodCarrier carrier in _catalog.Lods) { ConfigureMaterial(carrier.Material, false, false, carrier.SourceLod >= 2); carrier.Material.SetShaderParameter("sun_direction", toSun); carrier.Material.SetShaderParameter("ring_atmosphere_to_sun", toSun); }
        // The receiver: a 60 m square lit ground centred on the near sapling's root, +Y along the pose's up.
        Vector3 root = geometry.CanonicalToWorld(pose.Around + 15, pose.Ground, pose.Across);
        var ground = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(60f, 60f) }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        ground.Transform = new Transform3D(new Basis(forward, up, forward.Cross(up)), root);
        _vp.AddChild(ground);
        Vector3 shotPosition = root + up * 15f - forward * 5f;
        _cam.Fov = 60f;
        _cam.LookAtFromPosition(shotPosition, root, forward);
        foreach (UnderstoryLodCarrier carrier in _catalog.Lods) SetCameraUniforms(carrier.Material, shotPosition);
        RenderingServer.GlobalShaderParameterSet("ring_atmosphere_camera_world_position", shotPosition);
        RenderingServer.GlobalShaderParameterSet("ring_atmosphere_camera_height_meters", (shotPosition - geometry.CanonicalToWorld(pose.Around, 0.0, pose.Across)).Dot(up));
        var layer = new RingUnderstoryLayer
        {
            Name = "UnderstoryLayer", FrustumCulling = false, SaplingShadows = true,
            Host = new ScatterLayerHost
            {
                Geometry = () => geometry, Focus = () => focus, Epoch = () => 1, CopyRecords = into => into.AddRange(records),
                RingAcrossWidthMeters = () => LandscapeWorld.Width, Sun = () => _sun,
            },
        };
        _vp.AddChild(layer);
        await WaitUntil(() => layer.DrawOutputs == 3 && !layer.SetPending);
        if (layer.DrawOutputs != 3) throw new InvalidOperationException($"sapling caster: {layer.DrawOutputs} draw outputs, expected 3 (batches {layer.BatchCount}, failed {layer.Failed})");
        if (layer.SourceRows[4] != 2) throw new InvalidOperationException($"sapling caster: {layer.SourceRows[4]} caster rows, expected 2");
        var info = layer.OutputInfo();
        if (info.Count(o => !o.Caster) != 2 || info.Count(o => o.Caster) != 1 || info.First(o => o.Caster).Name != "UnderstorySapling_Shadow" || info.First(o => o.Caster).Group != 4) throw new InvalidOperationException("sapling caster: expected 2 camera outputs and one UnderstorySapling_Shadow caster of group 4");
        async Task<Image> Capture()
        {
            await Frames(1);
            for (int i = 0; i < 3; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            Image image = _vp.GetTexture().GetImage();
            if (image == null || image.IsEmpty()) throw new InvalidOperationException("empty capture");
            return image;
        }
        // T6-11: the switch takes effect through a source rebuild (RequestRebuild, then the 0.25 s epoch throttle, an async build and the set promotion). Wait on wall-clock time for a build that
        // STARTED after the switch to be installed and shown (SourceBuilds counts starts), not on a frame count: with VSync off 600 frames can pass inside the throttle alone (flaky both ways).
        async Task WaitCasters(int expected, int buildsBefore)
        {
            await WaitUntil(() => layer.SourceBuilds > buildsBefore && !layer.SetPending);
            if (layer.OutputInfo().Count(o => o.Caster) != expected) throw new InvalidOperationException($"sapling caster: expected {expected} casters after the switch, found {layer.OutputInfo().Count(o => o.Caster)}");
        }
        Image shotA = await Capture();
        int builds = layer.SourceBuilds;
        layer.SaplingShadows = false;
        await WaitCasters(0, builds);
        Image shotB = await Capture();
        builds = layer.SourceBuilds;
        layer.SaplingShadows = true;
        await WaitCasters(1, builds);
        Image shotC = await Capture();
        static float Lum(Color c) => .2126f * c.R + .7152f * c.G + .0722f * c.B;
        int Darker(Image on, Image off)
        {
            int count = 0;
            for (int y = 0; y < on.GetHeight(); y++)
                for (int x = 0; x < on.GetWidth(); x++)
                {
                    float lit = Lum(off.GetPixel(x, y));
                    if (lit > .02f && Lum(on.GetPixel(x, y)) <= lit * .75f) count++;
                }
            return count;
        }
        int darkerA = Darker(shotA, shotB), darkerC = Darker(shotC, shotB), different = 0, total = shotA.GetWidth() * shotA.GetHeight();
        for (int y = 0; y < shotA.GetHeight(); y++)
            for (int x = 0; x < shotA.GetWidth(); x++)
            {
                Color a = shotA.GetPixel(x, y), c = shotC.GetPixel(x, y);
                if (Math.Abs(a.R - c.R) > 2f / 255f || Math.Abs(a.G - c.G) > 2f / 255f || Math.Abs(a.B - c.B) > 2f / 255f) different++;
            }
        string line = $"UNDERSTORY_RENDER_MATCH sapling caster: darker on {darkerA}, darker after re-enable {darkerC}, on-vs-on differing {different} of {total}";
        GD.Print(line);
        bool pass = darkerA >= 50 && darkerC >= 50 && different <= total / 100;
        _sun.ShadowEnabled = false; _environment.AmbientLightEnergy = 0f;
        ground.Free(); layer.Free();
        if (!pass) throw new InvalidOperationException("sapling caster: " + line);
        _log.Add($"sapling caster: slot 4 holds 2 rows, one UnderstorySapling_Shadow output; shadow-map darkening {darkerA} px (on), {darkerC} px (re-enabled), on-vs-on differing {different}/{total}");
    }

    /// <summary>A snag, a log and a stump through the layer: a near batch (0 to 70 m), a far batch (50 to 300 m) and the snag-and-log caster.</summary>
    private async Task DeadWoodLayerChecks()
    {
        RenderingServer.GlobalShaderParameterSet("ringworld_forest_lighting", SwitchesOff);
        const double Circ = LandscapeWorld.Circumference;
        double ringRadius = Circ / Math.Tau;
        LandscapePose pose = LandscapePoseFinder.ReadFixture(ProjectSettings.GlobalizePath("res://tools/fixtures/landscape_poses_dressing_s1337.json")).Poses.First(p => p.Name == "steep_eye");
        ScatterRecord Wood(ulong id, double distance, ushort cls, int mesh)
            => new(id, (long)Math.Round((pose.Around + distance) * 100.0), (int)Math.Round(pose.Across * 100.0), (float)pose.Ground, cls, (ushort)mesh, 1f, .3f, ScatterRecord.EncodeUpOct(0, 1, 0), 0);
        var records = new List<ScatterRecord>
        {
            Wood(0xD000, 10, 10, 30), Wood(0xD001, 20, 14, 40), Wood(0xD002, 40, 13, 36), Wood(0xD003, 140, 13, 36), Wood(0xD004, 200, 10, 31), Wood(0xD005, 400, 10, 32),
        };
        var focus = new RingCanonicalPosition(pose.Around, pose.Ground, pose.Across);
        var geometry = new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero, ringRadius, 0.0, 0.0, Circ, 0.0, 0.0, focus);
        Vector3 camera = geometry.CanonicalToWorld(focus.AroundMeters, focus.VerticalMeters, focus.AcrossMeters);
        double angle = pose.Around / ringRadius;
        Vector3 forward = new Vector3((float)-Math.Sin(angle), 0f, (float)Math.Cos(angle)), up = new Vector3((float)-Math.Cos(angle), 0f, (float)-Math.Sin(angle));
        _cam.Fov = 70f; _cam.Far = 6000f;
        _cam.LookAtFromPosition(camera, camera + forward * 100f, up);
        _reference.Visible = false; _carrier.Visible = false;
        var layer = new RingUnderstoryLayer
        {
            Name = "UnderstoryLayer", FrustumCulling = false,
            Host = new ScatterLayerHost
            {
                Geometry = () => geometry, Focus = () => focus, Epoch = () => 1, CopyRecords = into => into.AddRange(records),
                RingAcrossWidthMeters = () => LandscapeWorld.Width, Sun = () => _sun,
            },
        };
        _vp.AddChild(layer);
        await WaitUntil(() => layer.DrawOutputs == 3 && !layer.SetPending);
        if (layer.DrawOutputs != 3) throw new InvalidOperationException($"dead wood layer: {layer.DrawOutputs} draw outputs, expected 3 (near, far, caster; batches {layer.BatchCount}, failed {layer.Failed})");
        int[] rows = layer.SourceRows;
        if (rows[0] != 0 || rows[2] != 6 || rows[3] != 5) throw new InvalidOperationException($"dead wood layer: rows {string.Join("/", rows)}, expected 0 saplings, 6 dead wood and 5 casters (snags and logs; the stump casts nothing)");
        var info = layer.OutputInfo();
        if (info.Count(o => !o.Caster) != 2 || info.Count(o => o.Caster) != 1) throw new InvalidOperationException("dead wood layer: expected 2 camera outputs and 1 caster");
        var cameraOutputs = info.Where(o => !o.Caster).OrderBy(o => o.Name).ToList(); // "UnderstoryDeadWood_Far", "UnderstoryDeadWood_Near"
        uint[] counts = new uint[2];
        for (int l = 0; l < 2; l++)
        {
            var done = new TaskCompletionSource<uint[]>();
            layer.Compute!.RequestCameraReadbackForTests(cameraOutputs[l].BatchId, (c, _) => done.TrySetResult(c));
            uint[] commands = await done.Task;
            counts[l] = commands.Length >= 2 ? commands[1] : 0u;
        }
        // Near (0 to 70 m): the snag at 10, the stump at 20, the log at 40. Far (50 to 300 m): the log at 140 and the snag at 200; the snag at 400 m is beyond both.
        if (counts[1] != 3 || counts[0] != 2) throw new InvalidOperationException($"dead wood layer: near draws {counts[1]} and far {counts[0]} instances, expected 3 and 2");
        _log.Add($"dead wood layer: near {counts[1]} / far {counts[0]} instances of 6 records, 5 caster rows, {info.Count} outputs");
        layer.Free();
    }

    private void FeatureOffChecks()
    {
        using RingTerrainSampler gen1 = LandscapeWorld.For(1).CreateSampler();
        bool made = false;
        RingUnderstoryLayer? none = RingBiomeDecorationLayer.CreateUnderstoryLayerFor(gen1, () => { made = true; return new RingUnderstoryLayer(); });
        if (none != null || made) throw new InvalidOperationException("feature off: a generation 1 world created an understory layer");
        using RingTerrainSampler rocks = LandscapeWorld.For(1, new[] { "ground.surface", "scatter.rocks" }).CreateSampler();
        RingUnderstoryLayer? rocksOnly = RingBiomeDecorationLayer.CreateUnderstoryLayerFor(rocks, () => { made = true; return new RingUnderstoryLayer(); });
        if (rocksOnly != null || made) throw new InvalidOperationException("feature off: a rocks-only world created an understory layer");
        using RingTerrainSampler forest = LandscapeWorld.For(1, new[] { "vegetation.forest_structure" }).CreateSampler();
        RingUnderstoryLayer? some = RingBiomeDecorationLayer.CreateUnderstoryLayerFor(forest, () => { made = true; return new RingUnderstoryLayer(); });
        if (some == null || !made) throw new InvalidOperationException("feature on: a vegetation.forest_structure world did not create an understory layer");
        if (some.BatchCount != 0 || some.Compute != null) throw new InvalidOperationException("feature on: a fresh layer already owns batches");
        some.Free();
        _log.Add("feature off: generation 1 and a rocks-only world register no understory layer or batch, a bit 6 world creates one");
    }

    private async Task Frames(int count) { for (int i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    /// <summary>T6-11: waits frames until the condition holds or 30 s of wall-clock time pass (frame counts are not a time budget with VSync off); the caller checks the outcome.</summary>
    private async Task WaitUntil(Func<bool> done, double seconds = 30.0) { ulong end = Time.GetTicksMsec() + (ulong)(seconds * 1000.0); while (!done() && Time.GetTicksMsec() < end) await Frames(1); }
}
