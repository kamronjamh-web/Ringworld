using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo;

/// <summary>
/// Opt-in, fixed-pose capture matrix for investigating the Certified-ring cloud
/// mirror. It is enabled only with --capture-certified-cloud-diagnostics.
/// The capture intentionally freezes the clock, weather, wind, and camera; it
/// never changes production rendering behaviour during normal play.
/// </summary>
public partial class RingCertifiedCloudDiagnosticCaptureDirector : Node
{
    [Export] public NodePath DayNightCyclePath { get; set; } = new("../RingDayNightCycle");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath FarViewportPath { get; set; } = new("../ScaledFarViewport");
    [Export] public NodePath SunshineDriverPath { get; set; } = new("../SunshineCloudsDriver");
    [Export] public NodePath SunshineBridgePath { get; set; } = new("../SunshineCloudBridge");
    [Export] public NodePath WeatherPath { get; set; } = new("../RingVolumetricClouds");
    [Export] public NodePath LocalWeatherEffectsPath { get; set; } = new("../LocalWeatherEffects");
    [Export] public NodePath LightningLightPath { get; set; } = new("../LocalWeatherEffects/LocalStormFlash");
    [Export] public NodePath CameraPath { get; set; } = new("../Explorer/Head/Camera3D");
    [Export] public NodePath SunPath { get; set; } = new("../StarLight");
    [Export] public NodePath WorldEnvironmentPath { get; set; } = new("../WorldEnvironment");
    [Export(PropertyHint.Range, "2,600,1")] public int InitialWarmupFrames { get; set; } = 180;
    [Export(PropertyHint.Range, "2,240,1")] public int SettleFrames { get; set; } = 24;
    [Export(PropertyHint.Range, "0.001,0.01,0.0005")] public float ContinuousCaptureFractionStep { get; set; } = 0.0025f;
    [Export(PropertyHint.Range, "1,600,1")] public int ContinuousStateSampleIntervalFrames { get; set; } = 30;

    private const float CaptureYawDegrees = -90.0f;
    private const float CapturePitchDegrees = -8.0f;
    private const float FarOnlyDiagnosticYawDegrees = 0.0f;
    // The normal ground-pose view looks down the ring. A low (2-5 km) canopy
    // at 48+ km can sit behind terrain in that view, so far-only validation
    // deliberately looks above the horizon without changing production.
    private const float FarOnlyDiagnosticPitchDegrees = 12.0f;
    private const float CriticalTransitionCaptureStep = 0.0005f;

    private readonly record struct Pass(string Name, bool Sunshine, bool FarClipmap);
    private static readonly (string Name, float Fraction)[] Phases =
    {
        ("noon", 0.0f), ("sunset", 0.24f), ("post_sunset", 0.27f),
        ("post_sunset_late", 0.33f), ("night_transition", 0.40f), ("midnight", 0.5f)
    };
    // Main captures are always the composited root viewport. The separate
    // FarRingViewport child texture is saved alongside every main capture.
    private static readonly Pass[] Passes =
    {
        new("sunshine_only_main", true, false),
        new("sunshine_only_history_off", true, false),
        new("sunshine_raw_density", true, false),
        new("sunshine_depth_density", true, false),
        new("far_composite_only", false, true),
        new("final_composite", true, true)
    };

    private Node? _cycle;
    private RingVoxelExplorerController? _explorer;
    private VoxelChunkGridManager? _grid;
    private RingScaledFarViewport? _far;
    private Node? _driver;
    private RingSunshineCloudBridge? _bridge;
    private Node? _weather;
    private RingLocalWeatherEffects? _localWeatherEffects;
    private OmniLight3D? _lightningLight;
    private Camera3D? _camera;
    private DirectionalLight3D? _sun;
    private WorldEnvironment? _worldEnvironment;
    private SubViewport? _farRenderViewport;
    private RingCanonicalPosition _capturePosition;
    private bool _active;
    private int _phaseIndex;
    private int _passIndex;
    private int _waitFrames;
    private bool _initialWarmupComplete;
    private readonly List<object> _captures = new();
    private bool _continuousTransition;
    private bool _continuousRawDensity;
    private bool _continuousDeepNightAlong;
    private bool _continuousFocusedDusk;
    private bool _continuousFocusedHistoryOff;
    private bool _continuousFocusedSunshineOnly;
    private bool _continuousFocusedFarOnly;
    private bool _continuousDensityInversionDetected;
    private double _continuousMaximumLowerUpperDensityRatio;
    private bool _continuousClockReleased;
    private int _continuousWarmupFrames;
    private int _continuousDrawnFrames;
    private int _continuousCaptureIndex;
    private double _continuousElapsed;
    private double _continuousDurationSeconds;
    private float _continuousStartFraction;
    private float _continuousEndFraction;
    private float _continuousNextCaptureFraction;
    private int _continuousLastStateSampleFrame = int.MinValue;
    private readonly List<object> _continuousFrames = new();
    private readonly List<object> _continuousCaptureMetrics = new();

    public override void _Ready()
    {
        string[] arguments = OS.GetCmdlineUserArgs();
        bool focusedRawDensity = Array.Exists(arguments, a => a == "--capture-certified-cloud-focused-raw-density");
        bool focusedFinal = Array.Exists(arguments, a => a == "--capture-certified-cloud-focused-transition");
        _continuousFocusedHistoryOff = Array.Exists(arguments, a => a == "--capture-certified-cloud-focused-history-off");
        _continuousFocusedSunshineOnly = Array.Exists(arguments, a => a == "--capture-certified-cloud-focused-sunshine-only");
        _continuousFocusedFarOnly = Array.Exists(arguments, a => a == "--capture-certified-cloud-focused-far-only");
        _continuousFocusedDusk = focusedRawDensity || focusedFinal
            || _continuousFocusedHistoryOff || _continuousFocusedSunshineOnly || _continuousFocusedFarOnly;
        _continuousRawDensity = focusedRawDensity || Array.Exists(arguments, a => a == "--capture-certified-cloud-continuous-raw-density");
        _continuousDeepNightAlong = Array.Exists(arguments, a => a == "--capture-certified-cloud-continuous-deep-night-along");
        _continuousTransition = _continuousRawDensity || _continuousDeepNightAlong || _continuousFocusedDusk || Array.Exists(arguments, a => a == "--capture-certified-cloud-continuous-transition");
        _active = _continuousTransition || Array.Exists(OS.GetCmdlineUserArgs(), a => a == "--capture-certified-cloud-diagnostics");
        if (!_active) { SetProcess(false); return; }
        ProcessMode = ProcessModeEnum.Always;
        _cycle = GetNodeOrNull<Node>(DayNightCyclePath);
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _far = GetNodeOrNull<RingScaledFarViewport>(FarViewportPath);
        _driver = GetNodeOrNull<Node>(SunshineDriverPath);
        _bridge = GetNodeOrNull<RingSunshineCloudBridge>(SunshineBridgePath);
        _weather = GetNodeOrNull<Node>(WeatherPath);
        _localWeatherEffects = GetNodeOrNull<RingLocalWeatherEffects>(LocalWeatherEffectsPath);
        _lightningLight = GetNodeOrNull<OmniLight3D>(LightningLightPath);
        _camera = GetNodeOrNull<Camera3D>(CameraPath);
        _sun = GetNodeOrNull<DirectionalLight3D>(SunPath);
        _worldEnvironment = GetNodeOrNull<WorldEnvironment>(WorldEnvironmentPath);
        if (_cycle == null || _explorer == null || _grid?.TerrainSampler == null || _far == null || _driver == null || _bridge == null || _weather == null || _camera == null || _sun == null)
        { Fail("one or more required Certified runtime nodes are unavailable"); return; }
        if (DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
        { Fail("headless display server cannot capture diagnostic images"); return; }

        _farRenderViewport = FindFarRenderViewport(_far);
        if (_farRenderViewport == null) { Fail("ScaledFarViewport has no FarRingViewport SubViewport child"); return; }

        // Use the exact known Certified-ring evidence position rather than the
        // integration scene's default spawn. Sampling its surface through the
        // grid keeps the pose valid for the selected Certified preset.
        double circumference = _grid.GetCanonicalRingCircumferenceMeters64();
        double around = 25_114_100.0 % circumference;
        double across = Math.Clamp(238_400.0, 0.0, _grid.RingAcrossWidthMeters);
        int surface = _grid.CaptureLodSurfaceEditSnapshot().GetSurfaceHeight(
            _grid.TerrainSampler, (long)Math.Round(around), Mathf.RoundToInt((float)across));
        _capturePosition = new RingCanonicalPosition(around, surface + 1.05, across);
        _explorer.SetBenchmarkCanonicalPosition(_capturePosition);

        if (_continuousTransition)
        {
            StartContinuousTransition();
            return;
        }

        // Freeze sources before choosing phases. The benchmark pose is renewed
        // every frame below so streaming and the far camera cannot drift it.
        _cycle.SetProcess(false);
        _weather.SetProcess(false);
        _driver.SetProcess(false);
        _driver.Set("update_continuously", false);
        _explorer.SetPhysicsProcess(false);
        _explorer.SetProcess(false);
        _explorer.SetProcessInput(false);
        _explorer.SetProcessUnhandledInput(false);
        _explorer.SetProcessUnhandledKeyInput(false);
        Input.MouseMode = Input.MouseModeEnum.Visible;
        ApplyFrozenPose();
        ApplyCurrentPass();
        GD.Print($"CERTIFIED_CLOUD_DIAGNOSTIC=START phases={Phases.Length} passes={Passes.Length} frozen=true");
    }

    public override void _Process(double _delta)
    {
        if (!_active || _cycle == null) return;
        if (_continuousTransition)
        {
            ProcessContinuousTransition(_delta);
            return;
        }
        ApplyFrozenPose();
        int requiredFrames = _initialWarmupComplete
            ? SettleFrames
            : Math.Max(SettleFrames, InitialWarmupFrames);
        if (Engine.GetFramesDrawn() <= 0 || ++_waitFrames < requiredFrames) return;
        _initialWarmupComplete = true;
        _waitFrames = 0;
        var phase = Phases[_phaseIndex];
        var pass = Passes[_passIndex];
        if (pass.Sunshine && _bridge!.RuntimeVisible && !_far!.ExactSunshineBackdropActive)
        {
            Fail($"Sunshine was visible without its exact far-backdrop cloud texture during {phase.Name}/{pass.Name}");
            return;
        }
        Image image = GetViewport().GetTexture().GetImage();
        if (image.IsEmpty()) { Fail("root viewport returned an empty image"); return; }
        string file = $"{_phaseIndex:00}_{phase.Name}_{_passIndex:00}_{pass.Name}.png";
        string path = Path.Combine(OutputDirectory(), file);
        if (image.SavePng(path) != Error.Ok) { Fail($"could not save {file}"); return; }
        string farFile = $"{_phaseIndex:00}_{phase.Name}_{_passIndex:00}_{pass.Name}_far_ring_viewport.png";
        Image farImage = _farRenderViewport!.GetTexture().GetImage();
        if (farImage.IsEmpty() || farImage.SavePng(Path.Combine(OutputDirectory(), farFile)) != Error.Ok)
        { Fail($"could not save FarRingViewport texture for {file}"); return; }
        _captures.Add(new { phase = phase.Name, cycle_fraction = phase.Fraction, pass = pass.Name, main_file = file, far_ring_viewport_file = farFile, state = StateSnapshot() });
        GD.Print($"CERTIFIED_CLOUD_DIAGNOSTIC=CAPTURE phase={phase.Name} pass={pass.Name} main={file} far={farFile}");
        if (++_passIndex == Passes.Length) { _passIndex = 0; _phaseIndex++; }
        if (_phaseIndex == Phases.Length) { WriteReport("PASS", null); GD.Print("CERTIFIED_CLOUD_DIAGNOSTIC=PASS"); GetTree().Quit(); return; }
        ApplyCurrentPass();
    }

    private void StartContinuousTransition()
    {
        // This is deliberately a single, uninterrupted renderer run. Unlike
        // the capture matrix it never changes provider/pass settings, resets
        // history, or pins a new phase after the initial sunset position.
        _weather!.SetProcess(false);
        // Keep the final-composite evidence deterministic. Lightning is an
        // independent renderer input and can otherwise resemble a one-frame
        // cloud discontinuity even though it does not change raw density.
        if (_localWeatherEffects != null)
        {
            _localWeatherEffects.EnableLightning = false;
            _localWeatherEffects.SetProcess(false);
        }
        if (_lightningLight != null)
        {
            _lightningLight.LightEnergy = 0.0f;
            _lightningLight.Visible = false;
        }
        _explorer!.SetPhysicsProcess(false);
        _explorer.SetProcess(false);
        _explorer.SetProcessInput(false);
        _explorer.SetProcessUnhandledInput(false);
        _explorer.SetProcessUnhandledKeyInput(false);
        Input.MouseMode = Input.MouseModeEnum.Visible;
        ApplyFrozenPose();

        bool farEnabled = !_continuousFocusedSunshineOnly;
        bool sunshineEnabled = !_continuousFocusedFarOnly;
        _far!.FarCloudClipmapEnabled = farEnabled;
        _far.SetFallbackCloudShellEnabled(farEnabled);
        if (_continuousFocusedFarOnly)
            GD.Print($"CERTIFIED_FAR_CLOUD_DIAGNOSTIC pitch={FarOnlyDiagnosticPitchDegrees:0.0} layers={_far.GetFarCloudClipmapDiagnostics()}");
        _bridge!.SetProviderActive(sunshineEnabled);
        // Select one invariant diagnostic presentation before warmup. It is
        // never toggled during the continuous history run.
        _bridge.SetDiagnosticDensityMattes(_continuousRawDensity, false);
        _bridge.SetDiagnosticTemporalHistoryDisabled(_continuousFocusedHistoryOff);
        _driver!.Set("update_continuously", true);
        _driver.SetProcess(true);
        _bridge.SetProcess(true);

        _continuousStartFraction = _continuousDeepNightAlong ? 0.345f : (_continuousFocusedDusk ? 0.295f : 0.255f);
        _continuousEndFraction = _continuousDeepNightAlong ? 0.500f : (_continuousFocusedDusk ? 0.322f : 0.355f);
        _cycle!.Call("set_cycle_fraction", _continuousStartFraction);
        float fullCycleSeconds = _cycle.Get("FullCycleSeconds").AsSingle();
        if (!float.IsFinite(fullCycleSeconds) || fullCycleSeconds <= 0.0f)
            fullCycleSeconds = (float)CelestialConfiguration.DefaultRingDaySeconds;
        _continuousDurationSeconds = Math.Max(
            1.0,
            (_continuousEndFraction - _continuousStartFraction) * fullCycleSeconds);
        // True 1x clock speed keeps Sunshine's ordinary solar-history path
        // active; accelerated diagnostic sweeps deliberately suppress it.
        _cycle.Call("set_speed_multiplier", 1.0f);
        // Warm the live Sunshine renderer at the exact sunset starting phase.
        // Do not advance the clock until its initial history has settled.
        _cycle.SetProcess(false);
        float diagnosticYaw = _continuousDeepNightAlong || _continuousFocusedFarOnly
            ? FarOnlyDiagnosticYawDegrees
            : CaptureYawDegrees;
        GD.Print($"CERTIFIED_CLOUD_CONTINUOUS=START mode={ContinuousModeName()} deep_night_along={_continuousDeepNightAlong} yaw={diagnosticYaw:0.0} start={_continuousStartFraction:0.000} end={_continuousEndFraction:0.000} seconds={_continuousDurationSeconds:0.0} clock_speed=1x fixed_pose=true history_continuous=true warmup_clock_held=true");
    }

    private void ProcessContinuousTransition(double delta)
    {
        // The pose was established once in StartContinuousTransition. Repeating
        // the benchmark placement/orientation call here would perturb the
        // camera's previous transform, which is the temporal state under test.
        // Explorer input/physics/process are disabled for this run, so it stays
        // fixed without issuing another transform write.
        if (Engine.GetFramesDrawn() <= 0) return;
        if (!_continuousClockReleased)
        {
            _continuousWarmupFrames++;
            if (_continuousWarmupFrames < Math.Max(2, InitialWarmupFrames)) return;
            // This is the sole release point. From here to completion no
            // capture mode changes provider, pass, temporal history, or clock.
            _continuousClockReleased = true;
            _continuousNextCaptureFraction = _continuousStartFraction;
            _cycle!.SetProcess(true);
            GD.Print($"CERTIFIED_CLOUD_CONTINUOUS=RELEASE frame={Engine.GetFramesDrawn()} phase={_cycle.Get("cycle_fraction").AsSingle():0.00000}");
            return;
        }

        _continuousElapsed += Math.Max(0.0, delta);
        _continuousDrawnFrames++;
        float fraction = _cycle!.Get("cycle_fraction").AsSingle();
        // The full snapshot repeats dozens of field names and transforms. A
        // bounded cadence preserves temporal evidence without producing a
        // 30+ MiB JSON report on every diagnostic run.
        if (_continuousDrawnFrames == 1
            || _continuousDrawnFrames % Math.Max(1, ContinuousStateSampleIntervalFrames) == 0)
            RecordContinuousFrameState(fraction, "interval");
        if (fraction + 0.000001f >= _continuousNextCaptureFraction)
        {
            Image image = GetViewport().GetTexture().GetImage();
            if (image.IsEmpty()) { FailContinuous("root viewport returned an empty image"); return; }
            // Stable names replace the previous run instead of leaving a new
            // phase-rounded PNG set on every execution.
            string file = $"{_continuousCaptureIndex:0000}.png";
            if (image.SavePng(Path.Combine(ContinuousOutputDirectory(), file)) != Error.Ok)
            { FailContinuous($"could not save {file}"); return; }
            string? farViewportFile = null;
            if (_continuousFocusedFarOnly)
            {
                Image farImage = _farRenderViewport!.GetTexture().GetImage();
                farViewportFile = $"far_viewport_{file}";
                if (farImage.IsEmpty() || farImage.SavePng(Path.Combine(ContinuousOutputDirectory(), farViewportFile)) != Error.Ok)
                { FailContinuous($"could not save {farViewportFile}"); return; }
            }
            if (_continuousRawDensity)
            {
                DensityOrientation density = AnalyzeRawDensityOrientation(image);
                _continuousDensityInversionDetected |= density.VerticallyInverted;
                _continuousMaximumLowerUpperDensityRatio = Math.Max(
                    _continuousMaximumLowerUpperDensityRatio,
                    density.LowerUpperRatio);
                _continuousCaptureMetrics.Add(new
                {
                    file,
                    cycle_fraction = fraction,
                    render_scale = GetViewport().Scaling3DScale,
                    density_upper_mass = density.UpperMass,
                    density_lower_mass = density.LowerMass,
                    density_lower_upper_ratio = density.LowerUpperRatio,
                    density_centroid_y_normalized = density.CentroidYNormalized,
                    density_vertically_inverted = density.VerticallyInverted
                });
            }
            else if (farViewportFile != null)
            {
                _continuousCaptureMetrics.Add(new { file, far_viewport_file = farViewportFile });
            }
            RecordContinuousFrameState(fraction, "capture");
            GD.Print($"CERTIFIED_CLOUD_CONTINUOUS=CAPTURE index={_continuousCaptureIndex} frame={Engine.GetFramesDrawn()} phase={fraction:0.00000} file={file}");
            _continuousCaptureIndex++;
            _continuousNextCaptureFraction += ContinuousCaptureStep(fraction);
        }
        if (_continuousElapsed < _continuousDurationSeconds && fraction < _continuousEndFraction) return;
        RecordContinuousFrameState(fraction, "final");
        _active = false;
        if (_bridge!.SunTemporalHistoryRefreshCount != 0)
        {
            FailContinuous($"solar history refreshed {_bridge.SunTemporalHistoryRefreshCount} time(s) during the fixed-camera 1x transition");
            return;
        }
        if (_continuousRawDensity && _continuousDensityInversionDetected)
        {
            FailContinuous($"raw-density cloud mask crossed into the lower screen (maximum lower/upper mass ratio {_continuousMaximumLowerUpperDensityRatio:0.000})");
            return;
        }
        try
        {
            string reportPath = WriteContinuousReport("PASS", null);
            GD.Print($"CERTIFIED_CLOUD_CONTINUOUS=PASS captures={_continuousCaptureIndex} frames={_continuousDrawnFrames} samples={_continuousFrames.Count} report={reportPath}");
        }
        catch (Exception exception)
        {
            GD.PushError("CERTIFIED_CLOUD_CONTINUOUS=FAIL: could not write report: " + exception.Message);
            GetTree().Quit(1);
            return;
        }
        GetTree().Quit();
    }

    private void ApplyCurrentPass()
    {
        var phase = Phases[_phaseIndex]; var pass = Passes[_passIndex];
        _cycle!.Call("set_cycle_fraction", phase.Fraction);
        // Do not toggle RingScaledFarViewport.Enabled: it owns the child
        // SubViewport we capture. Isolate only its cloud clipmap/fallback.
        _far!.FarCloudClipmapEnabled = pass.FarClipmap;
        _far.SetFallbackCloudShellEnabled(pass.FarClipmap);
        _bridge!.SetProviderActive(pass.Sunshine);
        _bridge.SetDiagnosticDensityMattes(
            rawDensity: pass.Name == "sunshine_raw_density",
            depthTestedDensity: pass.Name == "sunshine_depth_density");
        _bridge.SetDiagnosticTemporalHistoryDisabled(pass.Name == "sunshine_only_history_off");
        // The phase has moved the directional light. Synchronise Sunshine's
        // resource before freezing it; bridge activation is deliberately
        // earlier because it otherwise re-enables driver updates.
        _driver!.Call("retrieve_texture_data");
        _driver!.Set("update_continuously", false);
        _driver.SetProcess(false);
        // Keep the bridge processing while Sunshine owns the compositor. It
        // freezes neither wind nor weather, and it must observe the phase jump
        // so its temporal-history guard cannot carry a daylight accumulation
        // into the night capture.
    }

    private object StateSnapshot()
    {
        Vector3 up = _camera!.GlobalTransform.Basis.Y.Normalized();
        // Sunshine encodes the directional light's +Z axis as the vector from
        // the cloud toward the sun; report the same convention as the shader.
        Vector3 sunDirection = _sun!.GlobalTransform.Basis.Z.Normalized();
        Vector3 cloudFrameUp = _bridge!.CloudFrameUp;
        return new {
            render_scale = GetViewport().Scaling3DScale,
            camera_position = V(_camera.GlobalPosition), camera_basis_y = V(up), camera_basis_z = V(_camera.GlobalTransform.Basis.Z.Normalized()),
            sun_direction = V(sunDirection), sun_dot_camera_up = sunDirection.Dot(up), sun_visible = _sun.Visible, sun_energy = _sun.LightEnergy,
            cloud_frame_up = V(cloudFrameUp), sun_dot_cloud_frame_up = sunDirection.Dot(cloudFrameUp),
            sunshine_bridge_global_transform = NodeTransform(_bridge), sunshine_driver_global_transform = NodeTransform(_driver!),
            weather_global_transform = NodeTransform(_weather!), far_viewport_global_transform = NodeTransform(_far!),
            sunshine_driver_processing = _driver!.IsProcessing(), sunshine_bridge_enabled = _bridge!.Enabled,
            sunshine_bridge_processing = _bridge.IsProcessing(),
            sunshine_raw_density_matte = _bridge.DiagnosticRawDensityMatteEnabled,
            sunshine_depth_density_matte = _bridge.DiagnosticDepthDensityMatteEnabled,
            sunshine_temporal_history_disabled = _bridge.DiagnosticTemporalHistoryDisabled,
            sunshine_effective_history_weight = _bridge.EffectiveHistoryWeight,
            sunshine_stationary_history_weight = _bridge.StationaryHistoryWeight,
            sunshine_moving_history_weight = _bridge.MovingHistoryWeight,
            sunshine_applied_history_weight = _bridge.AppliedHistoryWeight,
            sunshine_solar_history_suppression = _bridge.SunHistorySuppression,
            sunshine_solar_history_suppressing = _bridge.SunHistoryWasSuppressed,
            sunshine_solar_history_refresh_count = _bridge.SunTemporalHistoryRefreshCount,
            sunshine_compute_history_decay_forced_zero = _bridge.DiagnosticTemporalHistoryDisabled,
            far_viewport_enabled = _far!.Enabled, far_cloud_clipmap_enabled = _far.FarCloudClipmapEnabled, far_cloud_mask_active = _far.CloudMaskActive, far_exact_sunshine_backdrop_active = _far.ExactSunshineBackdropActive,
            far_ring_viewport_found = _farRenderViewport != null,
            far_ring_viewport_update_mode = _farRenderViewport?.RenderTargetUpdateMode.ToString(),
            far_cloud_clipmap_diagnostics = _far.GetFarCloudClipmapDiagnostics(),
            main_world_environment_background_mode = _worldEnvironment?.Environment?.BackgroundMode.ToString(),
            weather_processing = _weather!.IsProcessing(), camera_visible = _camera.Visible
        };
    }

    private void RecordContinuousFrameState(float fraction, string reason)
    {
        int drawnFrame = Engine.GetFramesDrawn();
        if (_continuousLastStateSampleFrame == drawnFrame) return;
        _continuousLastStateSampleFrame = drawnFrame;
        _continuousFrames.Add(new
        {
            drawn_frame = drawnFrame,
            elapsed_seconds = _continuousElapsed,
            cycle_fraction = fraction,
            sample_reason = reason,
            state = StateSnapshot()
        });
    }

    private static object V(Vector3 value) => new { x = value.X, y = value.Y, z = value.Z };
    private static object T(Transform3D value) => new { origin = V(value.Origin), basis_x = V(value.Basis.X), basis_y = V(value.Basis.Y), basis_z = V(value.Basis.Z) };
    private static object? NodeTransform(Node node) => node is Node3D node3D ? T(node3D.GlobalTransform) : null;

    private readonly record struct DensityOrientation(
        double UpperMass,
        double LowerMass,
        double LowerUpperRatio,
        double CentroidYNormalized,
        bool VerticallyInverted);

    private static DensityOrientation AnalyzeRawDensityOrientation(Image image)
    {
        int width = image.GetWidth();
        int height = image.GetHeight();
        int firstX = width / 4; // Exclude the two left-side diagnostic HUD panels.
        double upperMass = 0.0;
        double lowerMass = 0.0;
        double weightedY = 0.0;
        double totalMass = 0.0;
        const int SampleStride = 8;
        for (int y = 0; y < height; y += SampleStride)
        {
            for (int x = firstX; x < width; x += SampleStride)
            {
                Color color = image.GetPixel(x, y);
                float minimum = Math.Min(color.R, Math.Min(color.G, color.B));
                float maximum = Math.Max(color.R, Math.Max(color.G, color.B));
                if (minimum < 0.20f || maximum - minimum > 0.08f) continue;
                double weight = Math.Clamp((minimum - 0.20f) / 0.80f, 0.0f, 1.0f);
                if (y < height * 0.5) upperMass += weight;
                else lowerMass += weight;
                weightedY += weight * y;
                totalMass += weight;
            }
        }

        double ratio = lowerMass / Math.Max(upperMass, 0.001);
        double centroid = totalMass > 0.001 && height > 1
            ? weightedY / totalMass / (height - 1)
            : 0.5;
        bool inverted = totalMass >= 100.0 && ratio > 1.5 && centroid > 0.55;
        return new DensityOrientation(upperMass, lowerMass, ratio, centroid, inverted);
    }

    private float ContinuousCaptureStep(float fraction)
    {
        // The inversion occurs within seconds of the local sun crossing the
        // cloud-frame horizon. Preserve sub-second evidence in that narrow
        // window without producing hundreds of full-resolution images for the
        // rest of the two-minute sweep.
        if (fraction >= 0.295f && fraction <= 0.310f)
            return Mathf.Min(ContinuousCaptureFractionStep, CriticalTransitionCaptureStep);
        return Mathf.Max(0.001f, ContinuousCaptureFractionStep);
    }

    private void ApplyFrozenPose()
    {
        _explorer!.SetBenchmarkCanonicalPosition(_capturePosition);
        if (!_explorer.ResetCanonicalOrientationForCapture())
        {
            Fail("could not reset explorer to the Certified ring-local capture basis");
            return;
        }
        _explorer.SetPresentationView(
            _continuousDeepNightAlong || _continuousFocusedFarOnly
                ? FarOnlyDiagnosticYawDegrees
                : CaptureYawDegrees,
            _continuousFocusedFarOnly ? FarOnlyDiagnosticPitchDegrees : CapturePitchDegrees);
    }

    private static string OutputDirectory()
    {
        return RingGeneratedOutputPaths.Directory("captures/certified_cloud_diagnostics");
    }
    private string ContinuousOutputDirectory()
    {
        string category = _continuousDeepNightAlong
            ? "captures/certified_cloud_continuous_deep_night_along"
            : _continuousFocusedHistoryOff
                ? "captures/certified_cloud_focused_history_off"
            : _continuousFocusedSunshineOnly
                ? "captures/certified_cloud_focused_sunshine_only"
            : _continuousFocusedFarOnly
                ? "captures/certified_cloud_focused_far_only"
            : _continuousFocusedDusk && _continuousRawDensity
                ? "captures/certified_cloud_focused_raw_density"
            : _continuousFocusedDusk
                    ? "captures/certified_cloud_focused_transition"
            : _continuousRawDensity
                ? "captures/certified_cloud_continuous_raw_density"
                : "captures/certified_cloud_continuous_transition";
        return RingGeneratedOutputPaths.Directory(category);
    }
    private string ContinuousModeName() => _continuousFocusedHistoryOff
        ? "focused_history_off"
        : _continuousFocusedSunshineOnly
            ? "focused_sunshine_only"
        : _continuousFocusedFarOnly
            ? "focused_far_only"
        : _continuousFocusedDusk
        ? (_continuousRawDensity ? "focused_raw_density" : "focused_final_composite")
        : (_continuousRawDensity ? "raw_density" : "final_composite");
    private string WriteContinuousReport(string status, string? error)
    {
        var report = new {
            status, error, renderer = RenderingServer.GetCurrentRenderingMethod(),
            capture_position = new { around_meters = _capturePosition.AroundMeters, vertical_meters = _capturePosition.VerticalMeters, across_meters = _capturePosition.AcrossMeters },
            transition = new { mode = ContinuousModeName(), deep_night_along = _continuousDeepNightAlong, fixed_yaw_degrees = _continuousDeepNightAlong ? 0.0f : CaptureYawDegrees, start_cycle_fraction = _continuousStartFraction, end_cycle_fraction = _continuousEndFraction, requested_seconds = _continuousDurationSeconds, clock_speed_multiplier = 1, captures_every_cycle_fraction = ContinuousCaptureFractionStep, warmup_frames_at_fixed_sunset = _continuousWarmupFrames, clock = "continuous real _process after fixed-terminator warmup; no phase jumps after release" },
            invariants = new { fixed_camera_and_location = true, weather_frozen = true, lightning_disabled = true, sunshine_provider_unchanged = true, far_cloud_clipmap_unchanged = true, temporal_history_never_disabled_or_reset = _bridge?.SunTemporalHistoryRefreshCount == 0 },
            continuous_frames_drawn = _continuousDrawnFrames,
            state_sample_interval_frames = Math.Max(1, ContinuousStateSampleIntervalFrames),
            density_orientation = new { automated_inversion_detected = _continuousDensityInversionDetected, maximum_lower_upper_mass_ratio = _continuousMaximumLowerUpperDensityRatio, capture_metrics = _continuousCaptureMetrics },
            frame_state = _continuousFrames
        };
        string directory = ContinuousOutputDirectory();
        string contents = JsonSerializer.Serialize(report);
        string reportPath = Path.Combine(directory, "report.json");
        try
        {
            File.WriteAllText(reportPath, contents);
            return reportPath;
        }
        catch (IOException exception)
        {
            // An open viewer or security scanner can briefly lock an earlier
            // report. Preserve this run under a unique name rather than
            // retrying every frame and trapping the diagnostic in-game.
            string fallbackPath = Path.Combine(
                directory,
                $"report_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.json");
            File.WriteAllText(fallbackPath, contents);
            GD.PushWarning($"Continuous report target was locked ({exception.Message}); wrote {fallbackPath} instead.");
            return fallbackPath;
        }
    }
    private void FailContinuous(string reason)
    {
        _active = false;
        try { WriteContinuousReport("FAIL", reason); }
        catch (Exception exception) { GD.PushError("Could not write failed continuous report: " + exception.Message); }
        GD.PushError("CERTIFIED_CLOUD_CONTINUOUS=FAIL: " + reason);
        GetTree().Quit(1);
    }
    private void WriteReport(string status, string? error)
    {
        var report = new { status, error, renderer = RenderingServer.GetCurrentRenderingMethod(), capture_position = new { around_meters = _capturePosition.AroundMeters, vertical_meters = _capturePosition.VerticalMeters, across_meters = _capturePosition.AcrossMeters }, freeze = new { camera = true, weather = true, wind = true, day_night = true }, main_capture = "root viewport final composite", far_capture = "FarRingViewport child SubViewport texture", phases = Phases, passes = Passes, captures = _captures };
        File.WriteAllText(Path.Combine(OutputDirectory(), "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
    private void Fail(string reason) { WriteReport("FAIL", reason); GD.PushError("CERTIFIED_CLOUD_DIAGNOSTIC=FAIL: " + reason); _active = false; GetTree().Quit(1); }

    private static SubViewport? FindFarRenderViewport(Node root)
    {
        if (root.GetNodeOrNull<SubViewport>("FarRingViewport") is { } direct) return direct;
        foreach (Node child in root.GetChildren())
            if (child is SubViewport viewport && viewport.Name == "FarRingViewport") return viewport;
            else if (FindFarRenderViewport(child) is { } nested) return nested;
        return null;
    }
}
