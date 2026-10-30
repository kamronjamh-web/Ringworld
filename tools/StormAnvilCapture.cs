using System;
using Godot;
using RingworldTechDemo;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

/// <summary>
/// Storm anvil capture helper (docs/storm-revamp-s4-design.md WO-A). The camera stays at one fixed canonical position (so terrain
/// streaming never changes between shots) and the manual preview storm is placed relative to it. Clocks are paused.
/// Driven by tools/storm_anvil_capture.gd.
/// </summary>
public partial class StormAnvilCapture : RefCounted
{
    private const double BaseAround = 8150667.255809009;
    private const double BaseAcross = 119474.33742621914;
    private const double BaseHeight = 397.3149763485417;
    private const string OwnerKey = "sunshine_preview";
    private const int PreviewSystemId = -1003;
    public string ErrorText { get; private set; } = "";

    private static readonly Vector2 DefaultWind = new(2.2f, 0.7f);

    /// <summary>Unit downwind direction in (around, across).</summary>
    public Vector2 WindDirection(Node world)
        => world.GetNode<RingVolumetricCloudLayer>("RingVolumetricClouds").WeatherWindMetersPerSecond.Normalized();

    public bool Setup(Node world, double cycleFraction, double heightMeters)
    {
        try
        {
            var explorer = world.GetNode<RingVoxelExplorerController>("Explorer");
            explorer.RestoreSavedState(new RingCanonicalPosition(BaseAround, heightMeters, BaseAcross), Vector3.Zero, 0f, 0f, false, 0);
            explorer.SetPhysicsProcess(false);
            explorer.SetProcessInput(false);
            explorer.SetProcessUnhandledInput(false);
            var cycle = world.GetNode<RingDayNightCycle>("RingDayNightCycle");
            cycle.SetSimulationSeconds(4506.117868);
            cycle.SetCycleFraction((float)cycleFraction);
            cycle.Paused = true;
            var clouds = world.GetNode<RingVolumetricCloudLayer>("RingVolumetricClouds");
            clouds.CloudSimulationTimeSeconds = 4506.117868;
            clouds.CloudSimulationPaused = true;
            return true;
        }
        catch (Exception error) { ErrorText = error.ToString(); return false; }
    }

    /// <summary>Camera at the fixed around/across, at <paramref name="heightMeters"/>, looking along yaw (radians; 0 = +around, pi/2 = +across).</summary>
    public bool PlaceCamera(Node world, double heightMeters, double yawRadians, double pitchDegrees)
    {
        try
        {
            var explorer = world.GetNode<RingVoxelExplorerController>("Explorer");
            explorer.RestoreSavedState(new RingCanonicalPosition(BaseAround, heightMeters, BaseAcross), Vector3.Zero,
                (float)yawRadians, (float)pitchDegrees, false, 0);
            return true;
        }
        catch (Exception error) { ErrorText = error.ToString(); return false; }
    }

    /// <summary>Camera at the fixed base position plus an (around, across) offset in canonical metres (hero-cell shots).</summary>
    public bool PlaceCameraAt(Node world, double offsetAround, double offsetAcross, double heightMeters, double yawRadians, double pitchDegrees)
    {
        try
        {
            var explorer = world.GetNode<RingVoxelExplorerController>("Explorer");
            var grid = world.GetNode<VoxelChunkGridManager>("ChunkGridManager");
            double circumference = grid.GetCanonicalRingCircumferenceMeters64();
            double around = (BaseAround + offsetAround) % circumference;
            if (around < 0.0) around += circumference;
            explorer.RestoreSavedState(new RingCanonicalPosition(around, heightMeters, BaseAcross + offsetAcross), Vector3.Zero,
                (float)yawRadians, (float)pitchDegrees, false, 0);
            return true;
        }
        catch (Exception error) { ErrorText = error.ToString(); return false; }
    }

    /// <summary>Rain off for storm-shape captures: PrecipitationStart 0.99 on the local rain effects and the distant rain curtains.</summary>
    public int RainOff(Node world)
    {
        int changed = 0;
        void Visit(Node node)
        {
            if (node is RingLocalWeatherEffects local) { local.PrecipitationStart = 0.99f; changed++; }
            else if (node is RingDistantRainCurtains curtains) { curtains.PrecipitationStart = 0.99f; changed++; }
            foreach (Node child in node.GetChildren()) Visit(child);
        }
        Visit(world);
        RingworldTechDemo.Weather.RingRainHazeSettings.Current.Enabled = false; // the near rain veil is a separate compositor effect
        return changed;
    }

    /// <summary>Registers the manual anvil-cluster preview storm at an offset (around, across metres) from the camera.</summary>
    public bool PlaceStorm(Node world, double offsetAround, double offsetAcross, double orientationRadians, double radiusMeters)
    {
        try
        {
            var clouds = world.GetNode<RingVolumetricCloudLayer>("RingVolumetricClouds");
            var grid = world.GetNode<VoxelChunkGridManager>("ChunkGridManager");
            double circumference = grid.GetCanonicalRingCircumferenceMeters64();
            double around = (BaseAround + offsetAround) % circumference;
            if (around < 0.0) around += circumference;
            float radius = (float)radiusMeters;
            clouds.RegisterManualWeather(OwnerKey, new RingVolumetricCloudLayer.LocalWeatherState(
                PreviewSystemId, RingVolumetricCloudLayer.WeatherSystemKind.ExceptionalFront, 1.0f, radius * 1.25f,
                new Vector2((float)around, (float)(BaseAcross + offsetAcross)), new Vector2(radius * 1.25f, radius),
                (float)orientationRadians, 0.97f, 0.94f, Vector2.Zero, CloudFormationProfile.AnvilStorm,
                500.0f, 18000.0f) { InheritWind = true });
            return true;
        }
        catch (Exception error) { ErrorText = error.ToString(); return false; }
    }

    /// <summary>Debug lifecycle code for every uploaded storm (NaN = natural). See RingVolumetricCloudLayer.StormLifecycleOverride.</summary>
    public void SetLifecycle(double code) => RingVolumetricCloudLayer.StormLifecycleOverride = double.IsNaN(code) ? null : (float)code;

    /// <summary>Debug A/B: switches the baked storm profile map off (HEAD's analytic storm loop).</summary>
    public void SetLegacyShape(bool legacy) => RingSunshineCloudBridge.DebugLegacyStormShape = legacy;

    /// <summary>Starts a perf window: enables the detailed GPU scopes and clears the history.</summary>
    public void PerfBegin(Node world)
    {
        var monitor = world.GetNode<RingworldTechDemo.Diagnostics.RingPerformanceMonitor>("RingPerformanceMonitor");
        monitor.Enabled = true;
        monitor.HistorySeconds = 60;
        monitor.DetailedGpuEnabled = true;
        monitor.ClearHistory();
    }

    /// <summary>Ends the perf window: mean/p95 of the cloud GPU scopes and of the viewport GPU time, as one line.</summary>
    public string PerfEnd(Node world)
    {
        var monitor = world.GetNode<RingworldTechDemo.Diagnostics.RingPerformanceMonitor>("RingPerformanceMonitor");
        var snapshot = monitor.GetSnapshot();
        var parts = new System.Collections.Generic.List<string>();
        // amort = mean * n / frames: the per-frame cost of scopes that only run on some frames (map slices).
        int frames = 1;
        foreach (var row in snapshot.Rows) if (row.Gpu && row.Name == "GPU.Clouds") frames = Math.Max(1, row.Count);
        foreach (string name in new[] { "GPU.Clouds", "GPU.CloudRaymarch", "GPU.CloudPrepass", "GPU.CloudReconstruct", "GPU.CloudComposite", "GPU.CloudShadowNear", "GPU.CloudShadowFar", "GPU.CloudMacroMap", "GPU.CloudStormMap" })
            foreach (var row in snapshot.Rows)
                if (row.Gpu && row.Name == name) parts.Add($"{name}=mean {row.MeanMilliseconds:F3} p95 {row.P95Milliseconds:F3} n {row.Count} amort {row.MeanMilliseconds * row.Count / frames:F3}");
        var gpu = new System.Collections.Generic.List<double>();
        foreach (var frame in snapshot.History) if (frame.ViewportGpuMilliseconds is { } value && value > 0.0) gpu.Add(value);
        gpu.Sort();
        if (gpu.Count > 0) parts.Add($"viewport_gpu=median {gpu[gpu.Count / 2]:F3} p95 {gpu[(int)(gpu.Count * 0.95)]:F3} n {gpu.Count}");
        return string.Join(" | ", parts);
    }

    /// <summary>Storm level D (CPU mirror of the receivers' map fetch), the D at the camera and the eye-adaptation cap, as one line (look pass diagnostics).</summary>
    /// <summary>Perf A/B only: republishes ringworld_storm_map_frame with (on) or without (off: w = 0) the storm-light layer, so receivers skip
    /// the second (ownership / front rain) map fetch. The bridge only republishes on change, so the override holds until the map frame moves.
    /// Returns the w now published.</summary>
    public float SetStormLightFetch(Node world, bool on)
    {
        var bridge = world.GetNode<RingSunshineCloudBridge>("SunshineCloudBridge");
        var field = typeof(RingSunshineCloudBridge).GetField("_stormPublishedMapFrame", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (field?.GetValue(bridge) is not Vector4 frame) return -1.0f;
        var published = on ? frame : new Vector4(frame.X, frame.Y, frame.Z, 0.0f);
        RenderingServer.GlobalShaderParameterSet("ringworld_storm_map_frame", published);
        return published.W;
    }

    public string StormLevels(Node world, double offsetAround, double offsetAcross)
    {
        var bridge = world.GetNode<RingSunshineCloudBridge>("SunshineCloudBridge");
        return $"D_at={bridge.GetStormLevelAt(BaseAround + offsetAround, BaseAcross + offsetAcross):F3} D_camera={bridge.GetCameraStormLevel():F3} eye_cap={bridge.GetStormEyeCap():F3} ambient_A={bridge.GetStormAmbientAtCamera():F3}";
    }

    /// <summary>
    /// Far-viewport storm-map mapping check (CPU float mirror of ring_storm_light.gdshaderinc). For canonical points around the camera it compares the map
    /// position the main-world receivers compute (ring_storm_level, from the published storm frame) and the one the scaled far viewport computes
    /// (ring_storm_level_far, from ScaledPoint positions) against the exact canonical map position (point - published map origin). Returns one line.
    /// </summary>
    public string FarStormMappingCheck(Node world)
    {
        var bridge = world.GetNode<RingSunshineCloudBridge>("SunshineCloudBridge");
        var far = world.GetNode<RingScaledFarViewport>("ScaledFarViewport");
        var explorer = world.GetNode<RingVoxelExplorerController>("Explorer");
        var grid = world.GetNode<VoxelChunkGridManager>("ChunkGridManager");
        if (!bridge.TryGetStormShaderState(out _, out Projection frame, out _, out _, out _)
            || !bridge.TryGetStormMapDescription(out _, out Vector2 origin, out _, out _, out _, out _, out double circumference, out _))
            return "far_map_check=unavailable";
        float scale = far.CoordinateScale;
        RingCanonicalPosition camera = explorer.CanonicalPosition;
        Vector3 farCamera = far.ScaledPoint(camera.AroundMeters, camera.VerticalMeters, camera.AcrossMeters);
        double maxMain = 0.0, maxFar = 0.0;
        string worst = "";
        foreach (double da in new[] { -150000.0, -60000.0, -5000.0, 0.0, 3000.0, 40000.0, 120000.0, 200000.0 })
        foreach (double dc in new[] { -100000.0, -2000.0, 0.0, 1500.0, 80000.0 })
        {
            double around = camera.AroundMeters + da, across = camera.AcrossMeters + dc;
            if (across < 0.0 || across > grid.RingAcrossWidthMeters) continue;
            double expectAround = around - origin.X; expectAround -= circumference * Math.Floor(expectAround / circumference);
            if (expectAround > circumference * 0.5) expectAround -= circumference;
            double expectAcross = across - origin.Y;
            // main world (ring_storm_level)
            Vector3 w = bridge.GetStormProbeWorldPosition(around, 0.0, across);
            Vector3 rel = w - new Vector3(frame.W.X, frame.W.Y, frame.W.Z);
            float tangent = rel.Dot(new Vector3(frame.X.X, frame.X.Y, frame.X.Z));
            float acrossM = rel.Dot(new Vector3(frame.Y.X, frame.Y.Y, frame.Y.Z));
            float inward = rel.Dot(new Vector3(frame.Z.X, frame.Z.Y, frame.Z.Z));
            float radius = frame.Z.W;
            float arc = radius * Mathf.Atan2(tangent, radius - inward);
            double mainErr = Math.Max(Math.Abs(frame.X.W + arc - expectAround), Math.Abs(frame.Y.W + acrossM - expectAcross));
            // far viewport (ring_storm_level_far, shared tier: ring centre at the origin)
            Vector3 fw = far.ScaledPoint(around, 0.0, across);
            Vector2 camRadial = new(farCamera.X, farCamera.Z);
            Vector2 outward = camRadial / camRadial.Length();
            Vector3 off = (fw - farCamera) / scale;
            float t2 = new Vector2(off.X, off.Z).Dot(new Vector2(-outward.Y, outward.X));
            float o2 = new Vector2(off.X, off.Z).Dot(outward);
            float arc2 = radius * Mathf.Atan2(t2, camRadial.Length() / scale + o2);
            double farErr = Math.Max(Math.Abs(frame.X.W + arc2 - expectAround), Math.Abs(frame.Y.W + off.Y - expectAcross));
            if (farErr > maxFar) { maxFar = farErr; worst = $"da={da:F0} dc={dc:F0}"; }
            maxMain = Math.Max(maxMain, mainErr);
        }
        return $"far_map_check max_err_main_m={maxMain:F1} max_err_far_m={maxFar:F1} worst_far=({worst}) scale={scale} radius_m={frame.Z.W:F0}";
    }

    /// <summary>Sun elevation in degrees above the local horizontal plane at the camera (diagnostic).</summary>
    public float SunElevationDegrees(Node world)
    {
        var sun = world.GetNode<DirectionalLight3D>("StarLight");
        var explorer = world.GetNode<RingVoxelExplorerController>("Explorer");
        Vector3 toSun = sun.GlobalBasis.Z;
        Vector3 up = explorer.GlobalBasis.Y;
        return Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(toSun.Normalized().Dot(up), -1f, 1f)));
    }
}
