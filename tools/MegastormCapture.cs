using System;
using Godot;
using RingworldTechDemo;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;

/// <summary>
/// Megastorm capture helper (docs/megastorm-design.md WO4 captures; WO9 extends the harness). Driven by tools/megastorm_capture.gd on top of
/// StormAnvilCapture (fixed camera base position, paused clocks). The megastorm is triggered once through the world's RingMegastorm node and then
/// MOVED relative to the camera by rewriting its anchor (debug only), so the camera and the streamed terrain stay put between shots.
/// </summary>
public partial class MegastormCapture : RefCounted
{
    private const double AgeSeconds = 100000.0;
    public string ErrorText { get; private set; } = "";

    private static RingMegastorm Node(Node world) => world.GetNode<RingMegastorm>("RingMegastorm");
    private static RingVolumetricCloudLayer Clouds(Node world) => world.GetNode<RingVolumetricCloudLayer>("RingVolumetricClouds");
    private static RingCanonicalPosition Camera(Node world) => world.GetNode<RingVoxelExplorerController>("Explorer").CanonicalPosition;

    /// <summary>Triggers a fully formed megastorm (+around, seed, speed m/s) placed so the camera is <paramref name="xiMeters"/> behind the edge.</summary>
    public bool Trigger(Node world, double xiMeters, double speed, int seed)
    {
        try
        {
            RingMegastorm node = Node(world);
            double now = Clouds(world).CloudSimulationTimeSeconds;
            var scratch = new MegastormModel(node.Model.Settings);
            scratch.Trigger(new MegastormTrigger("capture_scratch", 0.0, 1, now, (float)speed, 1800f, seed));
            double s = LineFor(scratch, Camera(world), xiMeters, now);
            double c = node.Model.Settings.CircumferenceMeters;
            bool ok = node.Trigger(new MegastormTrigger("megastorm_capture", MegastormModel.WrapPositive(s - speed * AgeSeconds, c), 1, now - AgeSeconds,
                (float)speed, 1800f, seed));
            if (!ok) ErrorText = "RingMegastorm.Trigger refused";
            return ok;
        }
        catch (Exception error) { ErrorText = error.ToString(); return false; }
    }

    private static double LineFor(MegastormModel model, in RingCanonicalPosition camera, double xi, double now)
        => MegastormModel.WrapPositive(camera.AroundMeters + model.State.Direction * (xi + model.LeadOffset(camera.AcrossMeters, now)),
            model.Settings.CircumferenceMeters);

    /// <summary>Moves the (triggered) megastorm so the camera is <paramref name="xiMeters"/> behind the edge now (one anchor; debug only).</summary>
    public bool SetXi(Node world, double xiMeters)
    {
        try
        {
            MegastormModel model = Node(world).Model;
            if (!model.Triggered) { ErrorText = "not triggered"; return false; }
            double now = Clouds(world).CloudSimulationTimeSeconds;
            float v = model.SpeedAt(now);
            double s = LineFor(model, Camera(world), xiMeters, now);
            model.State.Anchors.Clear();
            model.State.Anchors.Add(new MegastormAnchor
            {
                TimeSeconds = model.State.TriggerTimeSeconds,
                AroundMeters = MegastormModel.WrapPositive(s - model.State.Direction * v * (now - model.State.TriggerTimeSeconds), model.Settings.CircumferenceMeters),
                Speed = v
            });
            return true;
        }
        catch (Exception error) { ErrorText = error.ToString(); return false; }
    }

    /// <summary>xi of the camera now (m), NaN when inactive.</summary>
    public double Xi(Node world)
    {
        MegastormModel model = Node(world).Model;
        double now = Clouds(world).CloudSimulationTimeSeconds;
        if (!model.Evaluate(now).Active) return double.NaN;
        RingCanonicalPosition camera = Camera(world);
        return model.Xi(camera.AroundMeters, camera.AcrossMeters, now);
    }

    /// <summary>Advances the (paused) weather clock by dt: a fixed-step motion capture moves the wall exactly v x dt per saved frame.</summary>
    public void StepClock(Node world, double dt) => Clouds(world).CloudSimulationTimeSeconds += dt;

    /// <summary>World position at the camera plus an around offset, at a canonical height (density probes).</summary>
    public Vector3 WorldAtOffset(Node world, double aroundOffset, double heightMeters)
    {
        RingCanonicalPosition camera = Camera(world);
        var grid = world.GetNode<RingworldTechDemo.Voxel.Phase2.VoxelChunkGridManager>("ChunkGridManager");
        return grid.CaptureLodGeometrySnapshot().CanonicalToWorld(camera.AroundMeters + aroundOffset, heightMeters, camera.AcrossMeters);
    }

    /// <summary>World position of the camera's (around, across) at a canonical height (density probes).</summary>
    public Vector3 WorldAtCamera(Node world, double heightMeters)
    {
        RingCanonicalPosition camera = Camera(world);
        var grid = world.GetNode<RingworldTechDemo.Voxel.Phase2.VoxelChunkGridManager>("ChunkGridManager");
        return grid.CaptureLodGeometrySnapshot().CanonicalToWorld(camera.AroundMeters, heightMeters, camera.AcrossMeters);
    }

    /// <summary>Debug: the active camera's forward vector dotted with the world +around and +across tangents at the camera.</summary>
    public string CameraAxes(Node world)
    {
        RingCanonicalPosition camera = Camera(world);
        var grid = world.GetNode<RingworldTechDemo.Voxel.Phase2.VoxelChunkGridManager>("ChunkGridManager");
        var g = grid.CaptureLodGeometrySnapshot();
        Vector3 p0 = g.CanonicalToWorld(camera.AroundMeters, camera.VerticalMeters, camera.AcrossMeters);
        Vector3 around = (g.CanonicalToWorld(camera.AroundMeters + 100.0, camera.VerticalMeters, camera.AcrossMeters) - p0).Normalized();
        Vector3 across = (g.CanonicalToWorld(camera.AroundMeters, camera.VerticalMeters, camera.AcrossMeters + 100.0) - p0).Normalized();
        Camera3D? cam = world.GetViewport().GetCamera3D();
        if (cam == null) return "no camera";
        Vector3 fwd = -cam.GlobalBasis.Z;
        return $"fwd.around={fwd.Dot(around):0.###} fwd.across={fwd.Dot(across):0.###}";
    }

    /// <summary>Debug line: uploaded megastorm records and the strip/frame state the bridge holds.</summary>
    public string Records(Node world)
    {
        var clouds = Clouds(world);
        var list = new System.Collections.Generic.List<MegastormRecord>();
        RingCanonicalPosition camera = Camera(world);
        clouds.GetMegastormBundle(clouds.CloudSimulationTimeSeconds, camera.AcrossMeters, list);
        var parts = new System.Collections.Generic.List<string>();
        foreach (var r in list) parts.Add($"{r.Id}:xi={r.CenterXiMeters:0} half={r.SemiAlongMeters:0} dAround={MegastormModel.WrapSigned(r.CenterAroundMeters - camera.AroundMeters, 31415936.0):0}");
        GodotObject? sun = world.GetNode<RingSunshineCloudBridge>("SunshineCloudBridge").GetSunshineCloudsForTests();
        string frame = sun == null ? "" : $" input={sun.Get("megastorm_input")} uploaded={world.GetNode<RingSunshineCloudBridge>("SunshineCloudBridge").UploadedWeatherSystemCount}";
        if (sun != null)
        {
            byte[] bytes = sun.Get("weather_systems_bytes").AsByteArray();
            int count = (int)BitConverter.ToSingle(bytes, 0);
            for (int i = 0; i < count && 64 + i * 64 + 64 <= bytes.Length; i++)
            {
                int o = 64 + i * 64;
                float F(int k) => BitConverter.ToSingle(bytes, o + 4 * k);
                if ((int)Math.Round(F(10)) != 12 && !(F(1) > 0 && i < 4)) continue;
                parts.Add($"slot{i}=[a {F(0):0} {F(1):0} {F(2):0} {F(3):0} | b {F(4):0.###} {F(5):0.###} {F(6):0.###} {F(7):0.###} | c {F(8):0} {F(9):0} {F(10):0} {F(11):0} | d {F(12):0.##} {F(13):0.##} {F(14):0.##} {F(15):0.##}]");
            }
            parts.Add($"wframe={sun.Get("cloud_weather_frame")} camera=({camera.AroundMeters:0},{camera.AcrossMeters:0})");
        }
        return string.Join(" ", parts) + frame;
    }

    /// <summary>Debug experiment: replaces the far layer sky material's shader (e.g. a scratch copy with a fix); false when the material is missing.</summary>
    public bool SetLayerSkyShader(Node world, string path)
    {
        var far = world.GetNode<RingworldTechDemo.Voxel.LOD.RingScaledFarViewport>("ScaledFarViewport");
        var field = typeof(RingworldTechDemo.Voxel.LOD.RingScaledFarViewport).GetField("_farLayerSkyMaterial", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (field?.GetValue(far) is not ShaderMaterial material) return false;
        Shader? shader = GD.Load<Shader>(path);
        if (shader == null) return false;
        material.Shader = shader;
        return true;
    }

    /// <summary>Perf A/B: suppresses (true) or restores the far wedge pass.</summary>
    public void SetFarPassSuppressed(Node world, bool suppressed)
        => world.GetNode<RingworldTechDemo.Voxel.LOD.RingScaledFarViewport>("ScaledFarViewport").FarMegastormSuppressed = suppressed;

    /// <summary>Perf A/B: suppresses (true) or restores only the main-view companion of the wedge pass.</summary>
    public void SetMainPassSuppressed(Node world, bool suppressed)
        => world.GetNode<RingworldTechDemo.Voxel.LOD.RingScaledFarViewport>("ScaledFarViewport").FarMegastormMainSuppressed = suppressed;

    /// <summary>Ends a perf window opened with StormAnvilCapture.PerfBegin: median / p95 of the far (outer) and main viewport GPU time and the CPU frame.</summary>
    public string FarPerfEnd(Node world)
    {
        var monitor = world.GetNode<RingworldTechDemo.Diagnostics.RingPerformanceMonitor>("RingPerformanceMonitor");
        var snapshot = monitor.GetSnapshot();
        var far = new System.Collections.Generic.List<double>();
        var main = new System.Collections.Generic.List<double>();
        foreach (var frame in snapshot.History)
        {
            if (frame.FarViewportGpuMilliseconds is { } f && f > 0.0) far.Add(f);
            if (frame.ViewportGpuMilliseconds is { } m && m > 0.0) main.Add(m);
        }
        string Stat(System.Collections.Generic.List<double> v)
        {
            if (v.Count == 0) return "n 0";
            v.Sort();
            return $"median {v[v.Count / 2]:F3} p95 {v[(int)(v.Count * 0.95)]:F3} n {v.Count}";
        }
        return $"far_gpu={Stat(far)} | main_gpu={Stat(main)}";
    }

    public double RingWidth(Node world) => world.GetNode<RingworldTechDemo.Voxel.Phase2.VoxelChunkGridManager>("ChunkGridManager").RingAcrossWidthMeters;

    /// <summary>Strip driver state (valid, builds) as one line.</summary>
    public string StripState(Node world)
    {
        GodotObject? driver = world.GetNode<RingSunshineCloudBridge>("SunshineCloudBridge").GetMegastormStripDriver();
        if (driver == null) return "strip=none";
        return $"strip valid={driver.Get("valid").AsBool()} builds={driver.Get("builds_completed").AsInt64()} real={driver.Get("real_texture").AsBool()}";
    }

    // ---- Lightning scythes (WO12) ----
    private MegastormScythe _pick;
    private double _pickAround, _pickAcross, _pickHeight, _pickYaw, _pickOffsetAround, _pickOffsetAcross;
    private readonly ScythePoint[] _pickPoints = new ScythePoint[16];
    private int _pickN;

    /// <summary>
    /// Picks a scythe of the (triggered, placed) megastorm that starts within <paramref name="searchSeconds"/> of now and attaches within 14 km of the camera
    /// (the <paramref name="skip"/>-th longest-ish one), and works out a standing pose <paramref name="distanceMeters"/> from the middle of its sheet, 2 m above
    /// the ground, looking at it. <paramref name="yawAcrossSign"/> (+1 or -1) is the harness's yaw sense toward +across.
    /// </summary>
    public bool PickScythe(Node world, double distanceMeters, double yawAcrossSign, double searchSeconds, int skip)
    {
        try
        {
            RingMegastorm node = Node(world);
            MegastormModel m = node.Model;
            double now = Clouds(world).CloudSimulationTimeSeconds, c = m.Settings.CircumferenceMeters, w = m.Settings.WidthMeters;
            RingCanonicalPosition cam = Camera(world);
            var list = new System.Collections.Generic.List<MegastormScythe>();
            MegastormScythes.ScythesInWindow(m, now, now + searchSeconds, cam.AroundMeters, cam.AcrossMeters, 14000.0, RingworldTechDemo.Lightning.LightningVisualPolicy.NaturalStrikeChanceScale, list);
            var good = new System.Collections.Generic.List<MegastormScythe>();
            foreach (MegastormScythe candidate in list) if (candidate.LengthMeters >= 3200.0f && candidate.WidthMeters >= 90.0f && candidate.LifetimeSeconds >= 1.6f && candidate.AttachAcrossMeters > 12000.0 && candidate.AttachAcrossMeters < w - 12000.0) good.Add(candidate);
            if (good.Count <= skip) { ErrorText = $"no scythe to pick ({list.Count} in the window, {good.Count} usable)"; return false; }
            _pick = good[skip];
            ScythePoint[] points = _pickPoints;
            Func<double, double, double>? surface = Clouds(world).CreateSurfaceHeightSampler();
            int n = _pickN = MegastormScythes.PathPoints(m, _pick, surface != null ? (a, z) => MegastormScythes.SmoothedGround(surface, a, z) : null, points);
            (double midAround, double midAcross) = MegastormScythes.PositionAt(points, n, _pick, _pick.LengthMeters * 0.5f, c);
            double fx = _pick.Direction * Math.Cos(_pick.HeadingRadians), fz = Math.Sin(_pick.HeadingRadians);
            double nx = -fz, nz = fx;                         // perpendicular in (around, across) is (-fz, fx) up to sign
            double sign = midAcross + nz * distanceMeters < w - 3000.0 && midAcross + nz * distanceMeters > 3000.0 ? 1.0 : -1.0;
            _pickAround = MegastormModel.WrapPositive(midAround + nx * sign * distanceMeters, c);
            _pickAcross = midAcross + nz * sign * distanceMeters;
            Func<double, double, double>? ground = Clouds(world).CreateSurfaceHeightSampler();
            _pickHeight = (ground != null ? ground(_pickAround, _pickAcross) : 0.0) + 2.0;
            _pickOffsetAround = MegastormModel.WrapSigned(_pickAround - cam.AroundMeters, c);
            _pickOffsetAcross = _pickAcross - cam.AcrossMeters;
            _pickYaw = Math.Atan2(yawAcrossSign * (midAcross - _pickAcross), MegastormModel.WrapSigned(midAround - _pickAround, c));
            return true;
        }
        catch (Exception error) { ErrorText = error.ToString(); return false; }
    }
    /// <summary>World position of the picked sheet at a path fraction (0 attachment, 1 end), at its mid thickness.</summary>
    public Vector3 PickWorld(Node world, double fraction)
    {
        var grid = world.GetNode<RingworldTechDemo.Voxel.Phase2.VoxelChunkGridManager>("ChunkGridManager");
        double c = Node(world).Model.Settings.CircumferenceMeters;
        float d = (float)(_pick.LengthMeters * fraction);
        (double a, double z) = MegastormScythes.PositionAt(_pickPoints, _pickN, _pick, d, c);
        int i = Math.Clamp((int)Math.Round(fraction * (_pickN - 1)), 0, _pickN - 1);
        return grid.CaptureLodGeometrySnapshot().CanonicalToWorld(a, _pickPoints[i].LowerEdgeMeters + 0.5 * _pick.ThicknessMeters, z);
    }
    public void SetReducedFlashes(bool reduced) => RingworldTechDemo.Lightning.LightningVisualPolicy.Apply(1.0f, 1.0f, reduced);
    public long PickId() => _pick.Id;
    public int PickStrokeCount(Node world)
    {
        Span<float> times = stackalloc float[8], amps = stackalloc float[8];
        return MegastormScythes.StrokeTimes(Node(world).Model, _pick, times, amps);
    }
    public string DescribeScythes(Node world, Vector3 camera) => world.GetNodeOrNull<RingMegastormScythes>("MegastormScythes")?.DescribeActive(camera) ?? "";
    public double PickStart() => _pick.StartTimeSeconds;
    public double PickLife() => _pick.LifetimeSeconds;
    public double PickTipSeconds() => _pick.LengthMeters / _pick.TipSpeedMetersPerSecond;
    public double PickOffsetAround() => _pickOffsetAround;
    public double PickOffsetAcross() => _pickOffsetAcross;
    public double PickHeight() => _pickHeight;
    public double PickYaw() => _pickYaw;
    /// <summary>Seconds after the start of the k-th return stroke of the picked scythe.</summary>
    public double PickStrokeAge(Node world, int k)
    {
        Span<float> times = stackalloc float[8], amps = stackalloc float[8];
        int n = MegastormScythes.StrokeTimes(Node(world).Model, _pick, times, amps);
        return times[Math.Min(k, n - 1)];
    }
    public string PickInfo() => $"id={_pick.Id} start={_pick.StartTimeSeconds:F2} len={_pick.LengthMeters:F0} width={_pick.WidthMeters:F0} thick={_pick.ThicknessMeters:F1} life={_pick.LifetimeSeconds:F2} strokes={_pick.StrokeCount} points={_pick.PointCount}";
    public void SetScythesEnabled(Node world, bool enabled)
    {
        var s = world.GetNodeOrNull<RingMegastormScythes>("MegastormScythes");
        if (s != null) s.Enabled = enabled;
    }
    public double ScythesProcessMicros(Node world) => world.GetNodeOrNull<RingMegastormScythes>("MegastormScythes")?.ProcessMicrosAverage ?? -1.0;
    public void SetClockTo(Node world, double t) => Clouds(world).CloudSimulationTimeSeconds = t;
    public string ScytheState(Node world)
    {
        var s = world.GetNodeOrNull<RingMegastormScythes>("MegastormScythes");
        return s == null ? "no node" : s.GetScytheDiagnostics().ToString();
    }
}
