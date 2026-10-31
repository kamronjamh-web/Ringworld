using System;
using Godot;
using RingworldTechDemo.Lightning;

namespace RingworldTechDemo.Voxel.LOD;

// Megastorm far wedge pass and analytic far coverage (docs/megastorm-design.md 3.3, 3.4; WO5, WO5b). Node FarMegastorm: a convex prism hull
// around the strip's wedge (a screen-space bound, rebuilt only when the wall's shape changes) running materials/ring_far_megastorm.gdshader
// (priority 15: after the whole-ring shell, before the far layer sky), which draws the wall body and the leading anvil from the per-frame
// moving frame beyond the Sunshine handoff, at any distance, on the sky pixels (the far image is injected only where the main view has no
// depth). Node MainMegastorm is its companion in the main world (ring_main_megastorm.gdshader, priority 127): the same hull moved into main
// space, running the same shader body on pixels that have main depth, so the wall composites over terrain within the main terrain's
// 500-750 km reach. Both work in the far frame, so they agree across the terrain silhouette. The far layer sky gets the same
// frame uniforms (far_megastorm_*: one uniform branch), which add the trailing / precursor coverage analytically (the far atlases exclude
// the megastorm), draw that coverage beyond the disk fade, and end its integration at the wall body.
//
// Zero cost when no megastorm is active: the node stays hidden and the layer sky's branch is off (written once on the transition).
// All positions reach the GPU as camera-relative values computed in double: x_cam is the far camera's own reference-line distance.
public partial class RingScaledFarViewport
{
    private const string FarMegastormShaderPath = "res://materials/ring_far_megastorm.gdshader";
    private const string MainMegastormShaderPath = "res://materials/ring_main_megastorm.gdshader";
    // The strip's x range (ring_far_megastorm_pass.gdshaderinc FAR_MS_X_MIN / FAR_MS_X_MAX) and the hull's margins.
    private const double FarMegastormXMin = -72000.0;
    private const double FarMegastormXMax = 56000.0;
    private const double FarMegastormHullPadMeters = 2000.0;
    private const double FarMegastormHullOuterMeters = 2000.0;
    private const double FarMegastormHullInnerMeters = 20000.0;
    private const int FarMegastormHullSegments = 16;
    private const int FarMegastormAcrossTexels = 1024;
    private const int FarMegastormAcrossSlices = 4;
    private const double FarMegastormTableRefreshSeconds = 1.0;
    private const float FarMegastormRefreshSeconds = 0.25f;

    private static readonly StringName FarMegastormEnabledName = "far_megastorm_enabled";
    private static readonly StringName FarMegastormAName = "far_megastorm_a";
    private static readonly StringName FarMegastormBName = "far_megastorm_b";
    private static readonly StringName FarMegastormCName = "far_megastorm_c";
    private static readonly StringName FarMegastormFrameName = "far_megastorm_frame";
    private static readonly StringName FarMegastormCoverageName = "far_megastorm_coverage";
    private static readonly StringName FarMegastormAcrossName = "far_megastorm_across";
    private static readonly StringName FarMegastormStormAmbientName = "far_megastorm_storm_ambient";
    private static readonly StringName FarMegastormMediumNoiseName = "far_megastorm_medium_noise";
    private static readonly StringName FarMegastormSmallNoiseName = "far_megastorm_small_noise";
    private static readonly StringName FarMegastormNoiseName = "far_megastorm_noise";
    private static readonly StringName FarMegastormMediumPositionName = "far_megastorm_medium_position";
    private static readonly StringName FarMegastormSmallPositionName = "far_megastorm_small_position";
    private static readonly StringName FarMegastormFlashName = "far_megastorm_flash";
    private static readonly StringName FarMegastormFlashSlotName = "far_megastorm_flash_slot";
    private static readonly StringName FarMegastormFlashChanceName = "far_megastorm_flash_chance";
    private static readonly StringName FarMegastormLookName = "far_megastorm_look";
    private static readonly StringName FarMegastormDebugName = "far_megastorm_debug";
    private static readonly StringName FarMegastormMainToFarName = "far_megastorm_main_to_far";
    private static readonly StringName FarMegastormFarCameraName = "far_megastorm_far_camera";
    /// <summary>Debug A/B (environment RING_MEGASTORM_MAIN_OFF=1): hides the main-view companion. Debug tooling only.</summary>
    private static readonly bool DebugMainMegastormOff = OS.GetEnvironment("RING_MEGASTORM_MAIN_OFF") == "1";
    /// <summary>Debug view (environment RING_MEGASTORM_FAR_DEBUG=1 wedge interval, 2 coarse occupancy hit). Debug tooling only.</summary>
    private static readonly int DebugFarMegastormView = int.TryParse(OS.GetEnvironment("RING_MEGASTORM_FAR_DEBUG"), out int view) ? view : 0;
    private static readonly StringName FarLayersMarchUniformName = "far_layers_march";
    private static readonly StringName RingStormAmbientName = "ring_storm_ambient";
    private static readonly StringName WeatherSystemsBytesName = "weather_systems_bytes";

    private MeshInstance3D? _farMegastorm;
    private ShaderMaterial? _farMegastormMaterial;
    private MeshInstance3D? _mainMegastorm;
    private ShaderMaterial? _mainMegastormMaterial;
    private ArrayMesh? _farMegastormHull;
    private (double Dir, double Width, double Bow, double Meander, double Radius, float Scale) _farMegastormHullKey;
    // This frame's far -> main mapping, stored by SyncCameraAndLighting (same frame as UpdateFarMegastorm): the canonical -> world basis,
    // the main camera origin and the far camera position (scaled).
    private Basis _megastormCanonicalToWorld = Basis.Identity;
    private Vector3 _megastormMainCameraOrigin;
    private Vector3 _megastormFarCameraPosition;
    private double _farMegastormNextCreateAttempt;
    private bool _farMegastormActive;
    private bool _farMegastormLayerSkyEnabled;
    private float _farMegastormRefreshElapsed = float.PositiveInfinity;
    private float _farMegastormSculpt = 1.0f;
    private Vector4 _farMegastormStormAmbient;

    // Across noise table: staged over a few frames, published whole.
    private readonly float[] _farMegastormAcrossStage = new float[FarMegastormAcrossTexels * 4];
    private readonly byte[] _farMegastormAcrossBytes = new byte[FarMegastormAcrossTexels * 16];
    private ImageTexture? _farMegastormAcrossTexture;
    private int _farMegastormAcrossSlice = -1;
    private double _farMegastormAcrossBuildTime = double.NaN;
    private double _farMegastormAcrossPublishedTime = double.NaN;
    // Trailing / precursor coverage table over xi.
    private readonly float[] _farMegastormCoverage = new float[MegastormFarCoverage.Texels * 4];
    private readonly byte[] _farMegastormCoverageBytes = new byte[MegastormFarCoverage.Texels * 16];
    private ImageTexture? _farMegastormCoverageTexture;
    private double _farMegastormCoverageTime = double.NaN;
    private Vector2 _farMegastormCoverageRange = new(0.0f, 1.0f);
    private int _farMegastormTableSeed = int.MinValue;
    private MegastormModel? _farMegastormTableModel;
    private (double Trailing, double Reach) _farMegastormTableSettings;

    /// <summary>Look gains of the wedge pass against the near march (x sun, y ambient, z flash, w opacity); tuned with the handoff captures.</summary>
    public Vector4 FarMegastormLook { get; set; } = new(1.0f, 1.0f, 1.0f, 1.0f);

    /// <summary>Test diagnostics: whether the wedge pass is drawing and its tables are published.</summary>
    public string FarMegastormStatus => _farMegastorm == null
        ? "FarMegastorm: not created"
        : $"FarMegastorm: visible={_farMegastorm.Visible} main_visible={_mainMegastorm?.Visible} main_suppressed={FarMegastormMainSuppressed} main_off={DebugMainMegastormOff} active={_farMegastormActive} layer_sky={_farMegastormLayerSkyEnabled} across_table={_farMegastormAcrossTexture != null} coverage_table={_farMegastormCoverageTexture != null} xi_range=({_farMegastormCoverageRange.X:0},{_farMegastormCoverageRange.Y:0}) debug_off={RingMegastorm.DebugFarOff}";

    /// <summary>Perf A/B only (capture harness): hides the wedge pass and the layer sky's megastorm branch while true. Not a setting.</summary>
    public bool FarMegastormSuppressed { get; set; }

    /// <summary>Perf A/B only (capture harness): hides only the main-view companion of the wedge pass while true. Not a setting.</summary>
    public bool FarMegastormMainSuppressed { get; set; }

    /// <summary>True while the wedge pass is visible (a megastorm is active and the far pass is not debug-disabled).</summary>
    public bool FarMegastormVisible => _farMegastorm?.Visible ?? false;

    private MegastormModel? FarMegastormModel => _weatherSource?.MegastormSource switch
    {
        RingMegastorm node => node.Model,
        MegastormModel model => model,
        _ => null
    };

    private void UpdateFarMegastorm(double delta)
    {
        MegastormModel? model = RingMegastorm.DebugFarOff || FarMegastormSuppressed ? null : FarMegastormModel;
        double t = ResolveFarWeatherRawTimeSeconds();
        MegastormFrame frame = model != null ? model.Evaluate(t) : default;
        bool active = model != null && frame.Active && _farCamera != null && _farViewport != null;
        if (!active)
        {
            if (_farMegastormActive) DisableFarMegastorm();
            return;
        }
        EnsureFarMegastorm();
        if (_farMegastorm == null || _farMegastormMaterial == null) return;
        ShaderMaterial material = _farMegastormMaterial;

        _farMegastormRefreshElapsed += (float)delta;
        if (_farMegastormRefreshElapsed >= FarMegastormRefreshSeconds)
        {
            _farMegastormRefreshElapsed = 0.0f;
            RefreshFarMegastormSunshineInputs();
        }
        UpdateFarMegastormTables(model!, t);

        MegastormSettings settings = model!.Settings;
        double c = settings.CircumferenceMeters;
        // The far camera's own around (ScaledPoint: angle = around / R, angle 0 on +X increasing toward +Z), so x_cam and the shader's
        // per-sample angle offsets (R x angle) refer to the same point.
        Vector3 cam = _farCamera!.GlobalPosition;
        double angle = Math.Atan2(cam.Z, cam.X);
        double cameraAround = MegastormModel.WrapPositive(angle * _radiusMeters, c);
        double xCam = frame.Direction * MegastormModel.WrapSigned(frame.AroundMeters - cameraAround, c);
        UpdateFarMegastormHull(model, frame.Direction, angle, xCam);
        var a = new Vector4((float)t, frame.Level, frame.Strength, model.State.Seed);
        var b = new Vector4((float)settings.WidthMeters, (float)settings.BowMeters, (float)settings.MeanderAmplitudeMeters, (float)settings.MeanderWavelengthMeters);
        var cv = new Vector4((float)model.MeanderPhaseWrapped(t), RingCloudLayers.CeilingMeters, _farMegastormSculpt, 0.0f);
        var frameVector = new Vector4((float)xCam, frame.Direction, _farMegastormCoverageRange.X, _farMegastormCoverageRange.Y);
        SetFarMegastormFrame(material, a, b, cv, frameVector);
        if (_mainMegastormMaterial != null) SetFarMegastormFrame(_mainMegastormMaterial, a, b, cv, frameVector);
        if (_farLayerSkyMaterial != null) SetFarMegastormFrame(_farLayerSkyMaterial, a, b, cv, frameVector);
        if (_farLayerSkyMaterial != null && !_farMegastormLayerSkyEnabled && _farMegastormCoverageTexture != null)
        {
            _farLayerSkyMaterial.SetShaderParameter(FarMegastormCoverageName, _farMegastormCoverageTexture);
            if (_farMegastormAcrossTexture != null) _farLayerSkyMaterial.SetShaderParameter(FarMegastormAcrossName, _farMegastormAcrossTexture);
            _farLayerSkyMaterial.SetShaderParameter(FarMegastormEnabledName, true);
            _farMegastormLayerSkyEnabled = true;
        }

        // Moving-frame erosion noise: the strip path's offsets (wall travel modulo each noise scale, in double).
        double travel = model.UnwrappedAroundAt(t);
        float mediumScale = Math.Max(1.0f, _lastFarCloudDetailStatic.MediumScale);
        float smallScale = Math.Max(1.0f, _lastFarCloudDetailStatic.SmallScale);
        SetFarMegastormParameter(FarMegastormNoiseName, new Vector4(mediumScale, smallScale,
            (float)MegastormModel.WrapPositive(travel, mediumScale), (float)MegastormModel.WrapPositive(travel, smallScale)));
        SetFarMegastormParameter(FarMegastormMediumPositionName, _lastFarCloudDetailPositions.Medium);
        SetFarMegastormParameter(FarMegastormSmallPositionName, _lastFarCloudDetailPositions.Detail);

        // Flash field: the slot index and the time within it, split in double.
        double slot = Math.Floor(t / MegastormLightning.SlotSeconds);
        SetFarMegastormParameter(FarMegastormFlashSlotName, (int)Math.Clamp(slot, int.MinValue + 2.0, int.MaxValue));
        SetFarMegastormParameter(FarMegastormFlashName, new Vector4((float)cameraAround, (float)(t - slot * MegastormLightning.SlotSeconds),
            LightningVisualPolicy.ReducedFlashes ? 1.0f : 0.0f, LightningVisualPolicy.ReducedPeakScale));
        // The menu's Lightning frequency (halved under Reduced flashes), on the same cell roll as the CPU MegastormLightning.Flash(..., chanceScale).
        SetFarMegastormParameter(FarMegastormFlashChanceName, LightningVisualPolicy.NaturalStrikeChanceScale);
        SetFarMegastormParameter(NearHandoffName, _nearHandoffValue);
        SetFarMegastormParameter(FarLayersMarchUniformName, _farLayersMarchValue);
        SetFarMegastormParameter(FarMegastormLookName, FarMegastormLook);
        if (DebugFarMegastormView != 0) SetFarMegastormParameter(FarMegastormDebugName, DebugFarMegastormView);
        SetFarMegastormParameter(FarMegastormStormAmbientName, _farMegastormStormAmbient);
        _farLayerSkyMaterial?.SetShaderParameter(FarMegastormStormAmbientName, _farMegastormStormAmbient);

        bool ready = _farMegastormAcrossTexture != null && _farMegastormCoverageTexture != null;
        if (!_farMegastormActive && ready)
        {
            SetFarMegastormParameter(FarMegastormEnabledName, true);
            _farMegastormActive = true;
        }
        bool visible = _farMegastormActive && ready;
        if (_farMegastorm.Visible != visible) _farMegastorm.Visible = visible;
        UpdateMainMegastorm(visible && !FarMegastormMainSuppressed && !DebugMainMegastormOff);
    }

    private void SetFarMegastormParameter(StringName name, Variant value)
    {
        _farMegastormMaterial?.SetShaderParameter(name, value);
        _mainMegastormMaterial?.SetShaderParameter(name, value);
    }

    /// <summary>
    /// Moves the companion into the main world each frame: its transform is main_from_far x the far node's, with main_from_far mapping far
    /// (scaled canonical) space to main world space around the camera, and writes the two frame uniforms of ring_main_megastorm.gdshader.
    /// </summary>
    private void UpdateMainMegastorm(bool visible)
    {
        if (_mainMegastorm == null || _mainMegastormMaterial == null || _farMegastorm == null) return;
        if (_mainMegastorm.Visible != visible) _mainMegastorm.Visible = visible;
        if (!visible) return;
        float scale = Math.Max(CoordinateScale, 0.000001f);
        Basis canonicalToWorld = _megastormCanonicalToWorld;
        var mainFromFar = new Transform3D(canonicalToWorld.Scaled(Vector3.One / scale),
            _megastormMainCameraOrigin - canonicalToWorld * _megastormFarCameraPosition / scale);
        _mainMegastorm.GlobalTransform = mainFromFar * _farMegastorm.Transform;
        Basis worldToCanonical = canonicalToWorld.Inverse();
        _mainMegastormMaterial.SetShaderParameter(FarMegastormMainToFarName, worldToCanonical);
        _mainMegastormMaterial.SetShaderParameter(FarMegastormFarCameraName, _megastormFarCameraPosition);
    }

    /// <summary>
    /// The convex proxy hull (the far node's mesh, shared with the companion): a prism over the strip's x range in the far frame, built in a
    /// local frame where the wall reference x = 0 sits at angle 0 (angle alpha = -dir x / R) and rotated about +Y to the wall's real angle
    /// theta0 = theta_cam + dir x_cam / R each frame. The XZ cross-section is convex so every ray exits it once (the shaders draw back
    /// faces): a 16-segment polyline circumscribing radius R + 2 km outside, one straight chord at R - 20 km inside. Rebuilt only when the
    /// direction sign, width, bow / meander, radius or coordinate scale changes.
    /// </summary>
    private void UpdateFarMegastormHull(MegastormModel model, double direction, double cameraAngle, double xCam)
    {
        if (_farMegastorm == null) return;
        MegastormSettings settings = model.Settings;
        var key = (direction, settings.WidthMeters, settings.BowMeters, settings.MeanderAmplitudeMeters, _radiusMeters, CoordinateScale);
        if (_farMegastormHull == null || key != _farMegastormHullKey)
        {
            _farMegastormHullKey = key;
            _farMegastormHull = BuildFarMegastormHull(direction, settings.WidthMeters, settings.BowMeters + settings.MeanderAmplitudeMeters, _radiusMeters, CoordinateScale);
            _farMegastorm.Mesh = _farMegastormHull;
            if (_mainMegastorm != null) _mainMegastorm.Mesh = _farMegastormHull;
        }
        double theta0 = cameraAngle + direction * xCam / _radiusMeters;
        theta0 -= Math.Tau * Math.Round(theta0 / Math.Tau);
        // Godot's rotation about +Y is right-handed (x -> -z), so the local angle alpha (atan2(z, x)) moves to alpha - phi.
        _farMegastorm.Transform = new Transform3D(new Basis(Vector3.Up, (float)-theta0), Vector3.Zero);
    }

    private static ArrayMesh BuildFarMegastormHull(double direction, double width, double lead, double radius, float scale)
    {
        double pad = lead + FarMegastormHullPadMeters;
        double alphaA = -direction * (FarMegastormXMin - pad) / radius;
        double alphaB = -direction * (FarMegastormXMax + pad) / radius;
        if (alphaA > alphaB) (alphaA, alphaB) = (alphaB, alphaA);
        int n = FarMegastormHullSegments;
        double step = (alphaB - alphaA) / n;
        double outer = (radius + FarMegastormHullOuterMeters) * scale / Math.Cos(0.5 * step);
        double inner = (radius - FarMegastormHullInnerMeters) * scale;
        // Convex XZ polygon: the outer polyline (angle increasing), then the inner chord back.
        var ring = new Vector2[n + 3];
        for (int i = 0; i <= n; i++)
        {
            double alpha = alphaA + i * step;
            ring[i] = new Vector2((float)(outer * Math.Cos(alpha)), (float)(outer * Math.Sin(alpha)));
        }
        ring[n + 1] = new Vector2((float)(inner * Math.Cos(alphaB)), (float)(inner * Math.Sin(alphaB)));
        ring[n + 2] = new Vector2((float)(inner * Math.Cos(alphaA)), (float)(inner * Math.Sin(alphaA)));
        float y0 = (float)((-0.5 * width - 2000.0) * scale);
        float y1 = (float)((0.5 * width + 2000.0) * scale);

        int count = ring.Length;
        var vertices = new Vector3[count * 2];
        for (int i = 0; i < count; i++)
        {
            vertices[i] = new Vector3(ring[i].X, y0, ring[i].Y);
            vertices[count + i] = new Vector3(ring[i].X, y1, ring[i].Y);
        }
        Vector3 center = Vector3.Zero;
        foreach (Vector3 v in vertices) center += v;
        center /= vertices.Length;

        var indices = new System.Collections.Generic.List<int>();
        void Triangle(int a, int b, int c)
        {
            // Godot front faces are clockwise seen from outside: the right-handed normal points inward. The shaders cull_front, so the
            // near side is culled and the far side drawn, also from inside the hull.
            Vector3 normal = (vertices[b] - vertices[a]).Cross(vertices[c] - vertices[a]);
            Vector3 mid = (vertices[a] + vertices[b] + vertices[c]) / 3.0f - center;
            if (normal.Dot(mid) > 0.0f) (b, c) = (c, b);
            indices.Add(a);
            indices.Add(b);
            indices.Add(c);
        }
        for (int i = 0; i < count; i++)
        {
            int j = (i + 1) % count;
            Triangle(i, j, count + j);
            Triangle(i, count + j, count + i);
        }
        for (int i = 1; i < count - 1; i++)
        {
            Triangle(0, i, i + 1);
            Triangle(count, count + i, count + i + 1);
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>Debug: the wedge pass's ray inputs for the far camera's forward ray (CPU replica of the shader's set-up).</summary>
    public string FarMegastormRayProbe()
    {
        if (_farCamera == null || _farMegastormMaterial == null) return "no far camera / material";
        Vector3 cam = _farCamera!.GlobalPosition;
        Vector3 dir = -_farCamera.GlobalBasis.Z.Normalized();
        double scale = CoordinateScale;
        double camR = Math.Sqrt((double)cam.X * cam.X + (double)cam.Z * cam.Z);
        double ux = cam.X / camR, uz = cam.Z / camR;
        double rc = camR / scale, h = _radiusMeters - rc;
        double dr = dir.X * ux + dir.Z * uz, dt = dir.X * -uz + dir.Z * ux;
        Vector4 frame = _farMegastormMaterial.GetShaderParameter(FarMegastormFrameName).AsVector4();
        Variant radius = _farMegastormMaterial.GetShaderParameter("ring_radius_meters");
        Variant coord = _farMegastormMaterial.GetShaderParameter("ring_atmosphere_coordinate_scale");
        return $"cam=({cam.X:0.###},{cam.Y:0.###},{cam.Z:0.###}) rc={rc:0} h={h:0.0} dr={dr:0.0000} dt={dt:0.0000} dy={dir.Y:0.0000} x_cam={frame.X:0} dir={frame.Y} R={_radiusMeters:0} mat_R={radius} mat_scale={coord} sky_R={_farLayerSkyMaterial?.GetShaderParameter("ring_radius_meters")} sky_scale={_farLayerSkyMaterial?.GetShaderParameter("ring_atmosphere_coordinate_scale")} sky_visible={_farLayerSkyVisible}";
    }

    private static void SetFarMegastormFrame(ShaderMaterial target, Vector4 a, Vector4 b, Vector4 c, Vector4 frame)
    {
        target.SetShaderParameter(FarMegastormAName, a);
        target.SetShaderParameter(FarMegastormBName, b);
        target.SetShaderParameter(FarMegastormCName, c);
        target.SetShaderParameter(FarMegastormFrameName, frame);
    }

    private void DisableFarMegastorm()
    {
        _farMegastormActive = false;
        if (_farMegastorm != null) _farMegastorm.Visible = false;
        if (_mainMegastorm != null) _mainMegastorm.Visible = false;
        SetFarMegastormParameter(FarMegastormEnabledName, false);
        if (_farMegastormLayerSkyEnabled) _farLayerSkyMaterial?.SetShaderParameter(FarMegastormEnabledName, false);
        _farMegastormLayerSkyEnabled = false;
        _farMegastormAcrossSlice = -1;
        _farMegastormAcrossPublishedTime = double.NaN;
        _farMegastormCoverageTime = double.NaN;
    }

    private void EnsureFarMegastorm()
    {
        if (_farMegastorm != null || _farViewport == null || _farCamera == null) return;
        double now = Time.GetTicksMsec() * 0.001;
        if (now < _farMegastormNextCreateAttempt) return;
        _farMegastormNextCreateAttempt = now + 1.0;
        Shader? shader = GD.Load<Shader>(FarMegastormShaderPath);
        if (shader == null) return;
        var material = new ShaderMaterial { Shader = shader, RenderPriority = 15 };
        RingAtmosphereModel.ConfigureMaterial(material, _radiusMeters, CoordinateScale);
        var instance = new MeshInstance3D
        {
            Name = "FarMegastorm",
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false
        };
        _farViewport.AddChild(instance);
        DisableWholeRingFrustumCulling(instance);
        _farMegastorm = instance;
        _farMegastormMaterial = material;
        Shader? mainShader = GD.Load<Shader>(MainMegastormShaderPath);
        if (mainShader != null)
        {
            var mainMaterial = new ShaderMaterial { Shader = mainShader, RenderPriority = 127 };
            RingAtmosphereModel.ConfigureMaterial(mainMaterial, _radiusMeters, CoordinateScale);
            var mainInstance = new MeshInstance3D
            {
                Name = "MainMegastorm",
                MaterialOverride = mainMaterial,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Visible = false
            };
            AddChild(mainInstance);
            DisableWholeRingFrustumCulling(mainInstance);
            _mainMegastorm = mainInstance;
            _mainMegastormMaterial = mainMaterial;
        }
        _farMegastormRefreshElapsed = float.PositiveInfinity;
    }

    /// <summary>Sunshine inputs on the 0.25 s timer: the per-ray storm ambient, the sculpt strength (weather header1.y) and the noise textures.</summary>
    private void RefreshFarMegastormSunshineInputs()
    {
        Resource? clouds = _sunshineFarCloudResource;
        if (clouds == null) return;
        using (Variant value = clouds.Get(RingStormAmbientName)) _farMegastormStormAmbient = value.AsVector4();
        byte[] bytes;
        using (Variant value = clouds.Get(WeatherSystemsBytesName)) bytes = value.AsByteArray();
        if (bytes.Length >= 64) _farMegastormSculpt = Math.Clamp(BitConverter.ToSingle(bytes, 5 * 4), 0.0f, 1.0f);
        if (_sunshineMediumTexture is Texture3D medium) SetFarMegastormParameter(FarMegastormMediumNoiseName, medium);
        if (_sunshineSmallTexture is Texture3D small) SetFarMegastormParameter(FarMegastormSmallNoiseName, small);
    }

    /// <summary>
    /// The two lookup tables, rebuilt every second of weather time (and at once on a seed, settings or clock discontinuity): the across noise
    /// table (MegastormProfile.AcrossTerms, staged over FarMegastormAcrossSlices frames) and the T / P coverage over xi (MegastormFarCoverage).
    /// Both change by under 0.5% per second (noise cells live 600-2400 s; formation and breathing take minutes to hours).
    /// </summary>
    private void UpdateFarMegastormTables(MegastormModel model, double t)
    {
        bool changed = !ReferenceEquals(model, _farMegastormTableModel) || model.State.Seed != _farMegastormTableSeed
            || _farMegastormTableSettings != (model.Settings.TrailingLengthMeters, model.Settings.WarningReachMeters);
        if (changed)
        {
            _farMegastormTableModel = model;
            _farMegastormTableSeed = model.State.Seed;
            _farMegastormTableSettings = (model.Settings.TrailingLengthMeters, model.Settings.WarningReachMeters);
            _farMegastormAcrossSlice = -1;
            _farMegastormAcrossPublishedTime = double.NaN;
            _farMegastormCoverageTime = double.NaN;
        }

        // Across noise table.
        bool acrossDue = double.IsNaN(_farMegastormAcrossPublishedTime) || Math.Abs(t - _farMegastormAcrossPublishedTime) >= FarMegastormTableRefreshSeconds;
        if (_farMegastormAcrossSlice < 0 && acrossDue)
        {
            _farMegastormAcrossSlice = 0;
            _farMegastormAcrossBuildTime = t;
        }
        if (_farMegastormAcrossSlice >= 0)
        {
            // A first build (nothing published yet) runs whole so the pass can show this frame.
            int slices = _farMegastormAcrossTexture == null ? 1 : FarMegastormAcrossSlices;
            int per = FarMegastormAcrossTexels / slices;
            int first = _farMegastormAcrossSlice * per;
            int last = _farMegastormAcrossTexture == null ? FarMegastormAcrossTexels : Math.Min(FarMegastormAcrossTexels, first + per);
            double width = model.Settings.WidthMeters;
            for (int i = first; i < last; i++)
            {
                var terms = MegastormProfile.AcrossTerms(model, (i + 0.5) * width / FarMegastormAcrossTexels, _farMegastormAcrossBuildTime);
                _farMegastormAcrossStage[i * 4] = (float)terms.Tower;
                _farMegastormAcrossStage[i * 4 + 1] = (float)terms.Mammatus;
                _farMegastormAcrossStage[i * 4 + 2] = (float)terms.Shaft;
                _farMegastormAcrossStage[i * 4 + 3] = 0.0f;
            }
            if (last >= FarMegastormAcrossTexels)
            {
                Buffer.BlockCopy(_farMegastormAcrossStage, 0, _farMegastormAcrossBytes, 0, _farMegastormAcrossBytes.Length);
                PublishFarMegastormTable(ref _farMegastormAcrossTexture, _farMegastormAcrossBytes, FarMegastormAcrossTexels, FarMegastormAcrossName, _farMegastormMaterial, _mainMegastormMaterial, _farLayerSkyMaterial);
                _farMegastormAcrossPublishedTime = _farMegastormAcrossBuildTime;
                _farMegastormAcrossSlice = -1;
            }
            else _farMegastormAcrossSlice++;
        }

        // Coverage table (one build: 256 texels x 2 records).
        bool coverageDue = double.IsNaN(_farMegastormCoverageTime) || Math.Abs(t - _farMegastormCoverageTime) >= FarMegastormTableRefreshSeconds;
        if (coverageDue && _weatherSource != null)
        {
            double queryAcross = _explorer != null ? _explorer.CanonicalPosition.AcrossMeters : 0.5 * model.Settings.WidthMeters;
            MegastormFarCoverage.Build(_weatherSource, model, t, queryAcross, Vector3.Zero, _farMegastormCoverage, out double xiMin, out double xiMax);
            Buffer.BlockCopy(_farMegastormCoverage, 0, _farMegastormCoverageBytes, 0, _farMegastormCoverageBytes.Length);
            PublishFarMegastormTable(ref _farMegastormCoverageTexture, _farMegastormCoverageBytes, MegastormFarCoverage.Texels, FarMegastormCoverageName, _farLayerSkyMaterial);
            _farMegastormCoverageRange = new Vector2((float)xiMin, (float)xiMax);
            _farMegastormCoverageTime = t;
        }
    }

    private static void PublishFarMegastormTable(ref ImageTexture? texture, byte[] bytes, int width, StringName name, params ShaderMaterial?[] materials)
    {
        Image image = Image.CreateFromData(width, 1, false, Image.Format.Rgbaf, bytes);
        if (texture != null && texture.GetWidth() == width) texture.Update(image);
        else
        {
            texture = ImageTexture.CreateFromImage(image);
            foreach (ShaderMaterial? material in materials) material?.SetShaderParameter(name, texture);
        }
        image.Dispose();
    }
}
