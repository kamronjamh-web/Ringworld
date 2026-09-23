using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tests;

/// <summary>
/// Isolated rendered grass-compute comparison.  This deliberately measures a
/// deterministic fixture, not full-game FPS.  It never touches save data.
/// </summary>
public partial class GrassComputeBenchmark : Node3D
{
    public string Result { get; private set; } = "RUNNING";
    private const int DefaultRoots = 262_144, TileSize = 32;
    private readonly List<Sample> _samples = new();
    private readonly List<object> _raw = new();
    private readonly List<Node> _renderers = new();
    private readonly List<int> _ids = new();
    private readonly Dictionary<int, Aabb> _tileBounds = new();
    private readonly Dictionary<int, MultiMeshInstance3D> _rendererById = new();
    private readonly Dictionary<int, int> _passById = new();
    private readonly List<GpuPoint> _caseGrassGpu = new();
    private RingGpuVegetationCompute? _compute;
    private Camera3D? _camera;
    private int _roots, _fixtureRoots, _fixtureSources, _candidatesPerTile, _warmup, _frames, _repetitions, _computeSources, _staticSources;
    private bool _quick;

    private sealed record Profile(string Name, bool TileReject, int SourceDivisor, bool StaticFar, bool Card, bool Grouped);
    private sealed record Sample(string Case, int Repetition, int Roots, int Sources, int ActiveBatches,
        int SubmittedBatches, long CandidateTests, long Dispatches, long RootBufferBytes, long OutputBytes,
        double CpuSubmitMedianMs, double WallMedianMs, double WallP95Ms, double? ViewportCpuMedianMs,
        double? ViewportGpuMedianMs, double? GrassGpuMedianMs, int GrassGpuSampleCount, int DrawCalls, long Primitives, int ComputeSources, int StaticSources, string GpuStatus);

    public async void Start()
    {
        try
        {
            Configure();
            GrassComputeBenchmarkAssets.ValidateMeshes();
            Engine.MaxFps = 0; DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            _camera = MakeCamera(); MakeWorld();
            RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);
            RenderingServer.GlobalShaderParameterSet("ring_local_wind_enabled", 1.0f); RenderingServer.GlobalShaderParameterSet("ring_local_wind_vector", new Vector2(4, .9f)); RenderingServer.GlobalShaderParameterSet("ring_local_wind_wave", Vector4.Zero);
            RingPerformanceTelemetry.Configure(true, 8192); RingPerformanceGpu.Configure(true);
            var profiles = new[] {
                new Profile("baseline_per_batch_3_phase", false, 1, false, false, false),
                new Profile("tile_frustum_whole_tile_impossible_lod", true, 1, false, false, false),
                new Profile("solid_four_blade_clumps", false, 4, false, false, false),
                new Profile("sparse_far_static_multimesh", false, 1, true, false, false),
                new Profile("alpha_card_clumps", false, 4, false, true, false),
                new Profile("grouped_grass_compute_phases", false, 1, false, false, true),
            };
            for (int repetition = 0; repetition < _repetitions; repetition++)
            {
                // Alternating direction prevents a fixed thermal/order bias.
                IEnumerable<Profile> order = (repetition & 1) == 0 ? profiles : profiles.Reverse();
                foreach (Profile profile in order) await Measure(profile, repetition + 1);
            }
            await DrainOutsideTimedWindow(12);
            WriteArtifacts(); Result = "PASS wrote artifacts/grass-compute-benchmark/report.json";
        }
        catch (Exception e) { Result = "FAIL " + e.GetType().Name + ": " + e.Message; }
        finally { TearDown(); RingPerformanceGpu.Configure(false); RingPerformanceTelemetry.Configure(false); }
    }

    private void Configure()
    {
        string[] args = OS.GetCmdlineUserArgs();
        _quick = args.Contains("--grass-compute-benchmark-quick");
        _roots = ReadInt(args, "--grass-compute-roots", DefaultRoots);
        if (_roots is < 32_768 or > 2_097_152) throw new ArgumentOutOfRangeException("--grass-compute-roots", "must be 32768..2097152");
        _warmup = _quick ? 12 : Math.Max(60, ReadInt(args, "--grass-compute-warmup", 60));
        _frames = _quick ? 24 : Math.Max(180, ReadInt(args, "--grass-compute-samples", 180));
        _repetitions = _quick ? 1 : Math.Max(3, ReadInt(args, "--grass-compute-repetitions", 3));
    }

    private async Task Measure(Profile profile, int repetition)
    {
        RingPerformanceGpu.Configure(true);
        TearDown(); await Frames(4); Build(profile);
        for (int i = 0; i < _warmup; i++) { Submit(profile, i, false, out _); await Frame(); RingPerformanceGpu.RequestReadback(); DrainTelemetry(false, null); }
        RingPerformanceGpu.Configure(false); DrainTelemetry(false, null); RingPerformanceGpu.Configure(true);
        _caseGrassGpu.Clear();
        var wall = new List<double>(_frames); var submit = new List<double>(_frames); var viewportCpu = new List<double>(); var viewportGpu = new List<double>(); var activeFrames = new List<int>(); var submittedFrames = new List<int>(); var drawFrames = new List<int>(); var primitiveFrames = new List<long>();
        long dispatches = _compute?.DispatchCount ?? 0, candidateTests = 0;
        for (int i = 0; i < _frames; i++)
        {
            ulong start = Time.GetTicksUsec(); Submit(profile, i, true, out int requested);
            candidateTests += (long)requested * _candidatesPerTile;
            await Frame(); RingPerformanceGpu.RequestReadback(); DrainTelemetry(true, _caseGrassGpu); wall.Add((Time.GetTicksUsec() - start) / 1000d);
            viewportCpu.Add(RenderingServer.ViewportGetMeasuredRenderTimeCpu(GetViewport().GetViewportRid()));
            viewportGpu.Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(GetViewport().GetViewportRid()));
            activeFrames.Add(requested); submittedFrames.Add(_compute?.LastFrameDispatchedBatchCount ?? 0); drawFrames.Add((int)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)); primitiveFrames.Add((long)Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame));
        }
        for (int i = 0; i < 8; i++) { RingPerformanceGpu.RequestReadback(); await Frame(); DrainTelemetry(true, _caseGrassGpu); } // tail readback after timed window, with no submissions.
        GpuTiming grassGpu = DrainGrassGpu();
        string timestampStatus = RingPerformanceGpu.Status;
        RingPerformanceGpu.Configure(false); // canonical screenshots are outside all timestamp windows.
        long timedDispatches = _compute?.DispatchCount ?? dispatches;
        await CanonicalCapture(profile, repetition);
        submit.AddRange(_pendingSubmit);
        var sample = new Sample(profile.Name, repetition, _fixtureRoots, _fixtureSources, (int)Median(activeFrames.Select(x => (double)x)), (int)Median(submittedFrames.Select(x => (double)x)),
            candidateTests, timedDispatches - dispatches, _compute?.SharedSourceBytes ?? 0, _compute?.OutputBytes ?? 0,
            Median(submit), Median(wall), P95(wall), MedianNullable(viewportCpu), MedianNullable(viewportGpu), grassGpu.Median, grassGpu.Count,
            (int)Median(drawFrames.Select(x => (double)x)), (long)Median(primitiveFrames.Select(x => (double)x)), _computeSources, _staticSources, timestampStatus);
        _samples.Add(sample); _raw.Add(new { profile = profile.Name, repetition, wall_ms = wall, cpu_submit_ms = submit, viewport_cpu_ms = viewportCpu, viewport_gpu_ms = viewportGpu, active_batches = activeFrames, submitted_batches = submittedFrames, draws = drawFrames, primitives = primitiveFrames, grass_gpu_timestamps = _caseGrassGpu.ToArray() });
        GD.Print($"GRASS_COMPUTE_BENCHMARK case={profile.Name} repetition={repetition} roots={_roots} wall_ms={sample.WallMedianMs:F3} gpu_grass_ms={(grassGpu.Median?.ToString("F3") ?? "unavailable")} gpu_samples={grassGpu.Count}");
    }

    private void Build(Profile p)
    {
        _compute = new RingGpuVegetationCompute(p.Grouped); // companion backend adds the optional diagnostic argument.
        Mesh mesh = p.Card ? GrassComputeBenchmarkAssets.MakeCardClump() : p.SourceDivisor == 4 ? GrassComputeBenchmarkAssets.MakeSolidClump(4) : RingGrassMeshFactory.Create();
        ShaderMaterial productionMaterial = MakeProductionMaterial();
        ShaderMaterial material = p.Card ? GrassComputeBenchmarkAssets.MakeCardMaterial(productionMaterial) : productionMaterial;
        int perTile = Math.Max(1, _roots / 64 / p.SourceDivisor); _candidatesPerTile = perTile; _computeSources = _staticSources = 0;
        _fixtureSources = perTile * 64; _fixtureRoots = _fixtureSources * p.SourceDivisor;
        for (int tz = -4; tz < 4; tz++) for (int tx = -4; tx < 4; tx++)
        {
            float[] roots = MakeTileRoots(tx, tz, perTile, p.SourceDivisor);
            var aabb = new Aabb(new Vector3(tx * TileSize - TileSize / 2, -1, tz * TileSize - TileSize / 2), new Vector3(TileSize, 6, TileSize));
            AddComputeOutput($"grass-benchmark-near-{tx}-{tz}", roots, mesh, material, aabb, 0);
            _computeSources += roots.Length / RingGpuVegetationCompute.GrassFloats;
            if (p.StaticFar) AddStaticFarTile(TakeEveryFour(roots), material);
            else { AddComputeOutput($"grass-benchmark-mid-{tx}-{tz}", roots, p.Card ? mesh : p.SourceDivisor == 4 ? GrassComputeBenchmarkAssets.MakeIntermediateClump() : RingGrassMeshFactory.CreateIntermediate(), material, aabb, 1); _computeSources += roots.Length / RingGpuVegetationCompute.GrassFloats; }
        }
        _compute.Initialize();
    }

    private void AddComputeOutput(string identity, float[] roots, Mesh mesh, Material material, Aabb aabb, int lodPass)
    {
        var mm = new MultiMesh(); var renderer = new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        renderer.SetInstanceShaderParameter("grass_gpu_culled", true); renderer.SetInstanceShaderParameter("grass_lod_pass", lodPass); renderer.SetInstanceShaderParameter("grass_instance_count", (float)(roots.Length / RingGpuVegetationCompute.GrassFloats));
        AddChild(renderer); _renderers.Add(renderer);
        int id = _compute!.RegisterBatch(new RingGpuVegetationCompute.BatchRegistration(identity, mm, mesh, roots, null, roots.Length / RingGpuVegetationCompute.GrassFloats, aabb.Grow(1), true));
        if (id < 0) throw new InvalidOperationException("grass batch registration failed"); _ids.Add(id); _tileBounds.Add(id, aabb.Grow(1.3f)); _rendererById.Add(id, renderer); _passById.Add(id, lodPass);
    }

    private void AddStaticFarTile(float[] roots, Material material)
    {
        int count = roots.Length / RingGpuVegetationCompute.GrassFloats;
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, UseCustomData = true, Mesh = RingGrassMeshFactory.CreateFarCluster(), InstanceCount = count };
        for (int i = 0; i < count; i++)
        {
            int at = i * RingGpuVegetationCompute.GrassFloats;
            var basis = new Basis(new Vector3(roots[at], roots[at + 4], roots[at + 8]), new Vector3(roots[at + 1], roots[at + 5], roots[at + 9]), new Vector3(roots[at + 2], roots[at + 6], roots[at + 10]));
            mm.SetInstanceTransform(i, new Transform3D(basis, new Vector3(roots[at + 3], roots[at + 7], roots[at + 11])));
            mm.SetInstanceColor(i, new Color(roots[at + 12], roots[at + 13], roots[at + 14], roots[at + 15])); mm.SetInstanceCustomData(i, new Color(roots[at + 16], roots[at + 17], roots[at + 18], roots[at + 19]));
        }
        var renderer = new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        renderer.SetInstanceShaderParameter("grass_gpu_culled", false); renderer.SetInstanceShaderParameter("grass_lod_pass", 1); renderer.SetInstanceShaderParameter("grass_instance_count", (float)count);
        AddChild(renderer); _renderers.Add(renderer); _staticSources += count;
    }
    private static float[] TakeEveryFour(float[] roots) { int stride = RingGpuVegetationCompute.GrassFloats, count = roots.Length / stride, selected = (count + 3) / 4; var result = new float[selected * stride]; for (int i = 0, dst = 0; i < count; i += 4, dst++) Array.Copy(roots, i * stride, result, dst * stride, stride); return result; }

    private void Submit(Profile p, int frame, bool timed, out int requested, bool canonical = false)
    {
        Stopwatch? watch = timed ? Stopwatch.StartNew() : null;
        if (_camera == null || _compute == null) throw new InvalidOperationException("fixture unavailable");
        float t = (frame % _frames) / (float)(_frames - 1);
        // Walk, pan, and a deliberately sharp turn each change request inputs.
        Vector3 position = canonical ? _camera.GlobalPosition : new Vector3(Mathf.Lerp(-82, 82, t), 4.5f, Mathf.Sin(t * Mathf.Tau) * 58);
        if (!canonical) { _camera.GlobalPosition = position; _camera.LookAt(new Vector3(Mathf.Sin(t * Mathf.Tau * 3) * 26, 0, 0), Vector3.Up); }
        Vector4[] planes = FrustumPlanes(_camera);
        var batches = new List<RingGpuVegetationCompute.BatchFrameParameters>(_ids.Count);
        foreach (int id in _ids)
        {
            bool include = !(p.TileReject && !ConservativeTileVisible(id, _camera, planes));
            if (_tileBounds.TryGetValue(id, out Aabb box))
            {
                Vector3 nearest = new(Mathf.Clamp(position.X, box.Position.X, box.End.X), Mathf.Clamp(position.Y, box.Position.Y, box.End.Y), Mathf.Clamp(position.Z, box.Position.Z, box.End.Z));
                Vector3 farthest = new(position.X < box.GetCenter().X ? box.End.X : box.Position.X, position.Y < box.GetCenter().Y ? box.End.Y : box.Position.Y, position.Z < box.GetCenter().Z ? box.End.Z : box.Position.Z);
                float minDistance = position.DistanceTo(nearest), maxDistance = position.DistanceTo(farthest); int pass = _passById[id];
                const float nearDistance = 30, transition = 28, farDistance = 145, rootGuard = 14;
                if (p.TileReject && ((pass == 0 && minDistance > nearDistance + transition / 2) || (pass == 1 && maxDistance < nearDistance - transition / 2) || minDistance > farDistance + rootGuard)) include = false;
            }
            _rendererById[id].Visible = include;
            if (!include) continue;
            batches.Add(new RingGpuVegetationCompute.BatchFrameParameters(id, Transform3D.Identity, true, _passById[id], 30, 145, 28, 1, .25f, true, 1, false, planes, 5.3f));
        }
        requested = batches.Count;
        if (!_compute.RequestFrame(new RingGpuVegetationCompute.FrameRequest(position, new uint[] { (uint)_roots, (uint)_roots, (uint)_roots, (uint)_roots, (uint)_roots }, batches, false))) throw new InvalidOperationException("compute request failed");
        watch?.Stop(); if (watch != null) _pendingSubmit.Add(watch.Elapsed.TotalMilliseconds);
    }
    private readonly List<double> _pendingSubmit = new();

    private async Task CanonicalCapture(Profile p, int repetition)
    {
        if (_camera == null) return; _camera.GlobalPosition = new Vector3(-9, 1.8f, -16); _camera.LookAt(new Vector3(3, 1, 25), Vector3.Up);
        for (int i = 0; i < 8; i++) { Submit(p, 91, false, out _, true); await Frame(); }
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        string path = ProjectSettings.GlobalizePath($"res://artifacts/grass-compute-benchmark/{p.Name}_{repetition}.png"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Image image = GetViewport().GetTexture().GetImage(); if (image != null && !image.IsEmpty()) image.SavePng(path);
    }

    private async Task DrainOutsideTimedWindow(int frames)
    {
        for (int i = 0; i < frames; i++) { RingPerformanceGpu.RequestReadback(); await Frame(); DrainTelemetry(false, null); }
    }
    private readonly record struct GpuPoint(double Milliseconds, long SourceFrame);
    private static void DrainTelemetry(bool retainGrass, List<GpuPoint>? grass)
    {
        var entries = new RingPerformanceTelemetry.Completed[512];
        int count = RingPerformanceTelemetry.Drain(entries, out _);
        if (!retainGrass || grass == null) return;
        for (int i = 0; i < count; i++) if (entries[i].Gpu && entries[i].Name == "GPU.GrassCulling") grass.Add(new GpuPoint(entries[i].Milliseconds, entries[i].SourceFrameId));
    }
    private readonly record struct GpuTiming(double? Median, int Count);
    private GpuTiming DrainGrassGpu()
    {
        int uniqueFrames = _caseGrassGpu.Select(x => x.SourceFrame).Where(x => x >= 0).Distinct().Count();
        return new GpuTiming(_caseGrassGpu.Count == 0 ? null : Median(_caseGrassGpu.Select(x => x.Milliseconds)), uniqueFrames);
    }
    private void WriteArtifacts()
    {
        string dir = ProjectSettings.GlobalizePath("res://artifacts/grass-compute-benchmark"); Directory.CreateDirectory(dir);
        Vector2 actual = GetViewport().GetVisibleRect().Size;
        var report = new { benchmark = "isolated rendered grass compute fixture; not full-game FPS", window_requested = "1280x720", viewport_resolution = new { width = actual.X, height = actual.Y }, renderer = RenderingServer.GetCurrentRenderingMethod(), gpu_adapter = RenderingServer.GetVideoAdapterName(), vsync = "disabled", fps_cap = "disabled", roots_requested = _roots, fixture_tiles = 64, fixture_compute_outputs = 128, production_reference = "grass-registration-fix: 1,118,430 unique roots / 464 batches; fixture differs intentionally", warmup_frames = _warmup, timed_frames = _frames, repetitions = _repetitions, gpu_timestamp_note = "asynchronous GrassCulling timestamps only; unavailable is never replaced with CPU time", cases = _samples };
        File.WriteAllText(Path.Combine(dir, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(dir, "raw-samples.json"), JsonSerializer.Serialize(_raw, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(dir, "README.md"), "# Grass compute benchmark\n\nThis is an isolated, repeatable rendered grass fixture, **not full-game FPS**. It uses a deterministic tiled field with a sloped surface and excluded center path, production grass shader/compute, and a normalized moving camera. GPU GrassCulling values are asynchronous timestamps; `null` means unavailable.\n");
    }
    private void TearDown()
    {
        if (_compute != null) { foreach (int id in _ids) _compute.RemoveBatch(id); _compute.Dispose(); _compute = null; }
        _ids.Clear(); _tileBounds.Clear(); _rendererById.Clear(); _passById.Clear(); foreach (Node n in _renderers) if (GodotObject.IsInstanceValid(n)) n.QueueFree(); _renderers.Clear(); _pendingSubmit.Clear();
    }
    private Camera3D MakeCamera() { var c = new Camera3D { Current = true, Near = .1f, Far = 260, Fov = 68 }; AddChild(c); return c; }
    private void MakeWorld()
    {
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-48, -32, 0), LightEnergy = 1.25f, ShadowEnabled = true });
        var env = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(.29f, .42f, .60f), AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = Colors.White, AmbientLightEnergy = .45f };
        AddChild(new WorldEnvironment { Environment = env });
        var ground = new MeshInstance3D { Mesh = MakeTerrainMesh() }; AddChild(ground);
        var path = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(7, .08f, 300), Material = new StandardMaterial3D { AlbedoColor = new Color(.28f, .21f, .12f) } }, Position = new Vector3(0, .03f, 0) }; AddChild(path);
    }
    private static ShaderMaterial MakeProductionMaterial()
    {
        var material = new ShaderMaterial { Shader = GD.Load<Shader>("res://materials/ring_standing_grass.gdshader") };
        material.SetShaderParameter("grass_lod_enabled", true); material.SetShaderParameter("grass_lod_near_distance", 30f); material.SetShaderParameter("grass_lod_far_distance", 145f); material.SetShaderParameter("grass_lod_transition_width", 28f); material.SetShaderParameter("grass_lod_near_density", 1f); material.SetShaderParameter("grass_lod_far_density", 1f);
        return material;
    }
    private static Mesh MakeTerrainMesh()
    {
        var tool = new SurfaceTool(); tool.Begin(Mesh.PrimitiveType.Triangles);
        var material = new StandardMaterial3D { AlbedoColor = new Color(.12f, .23f, .06f), Roughness = 1 }; tool.SetMaterial(material);
        const int cells = 64; const float half = 160;
        for (int z = 0; z < cells; z++) for (int x = 0; x < cells; x++)
        {
            Vector3 V(int ix, int iz) { float px = Mathf.Lerp(-half, half, ix / (float)cells), pz = Mathf.Lerp(-half, half, iz / (float)cells); return new Vector3(px, pz > 15 ? (pz - 15) * .035f : 0, pz); }
            Vector3 a = V(x, z), b = V(x + 1, z), c = V(x + 1, z + 1), d = V(x, z + 1);
            // Clockwise viewed from above, matching Godot's front-face convention.
            tool.SetNormal(Vector3.Up); tool.AddVertex(a); tool.SetNormal(Vector3.Up); tool.AddVertex(b); tool.SetNormal(Vector3.Up); tool.AddVertex(c);
            tool.SetNormal(Vector3.Up); tool.AddVertex(a); tool.SetNormal(Vector3.Up); tool.AddVertex(c); tool.SetNormal(Vector3.Up); tool.AddVertex(d);
        }
        return tool.Commit();
    }
    private static float[] MakeTileRoots(int tx, int tz, int perTile, int divisor)
    {
        var rows = new float[perTile * RingGpuVegetationCompute.GrassFloats];
        for (int i = 0; i < perTile; i++)
        {
            uint h = Hash((uint)(i * divisor + 1), (uint)(tx + 9), (uint)(tz + 9)); float x = 0, z = 0;
            // Rejection sampling preserves each source's own spatial tile while
            // keeping the 7m construction-path exclusion empty.
            for (int attempt = 0; attempt < 8; attempt++) { h = Hash(h, (uint)attempt, 0x9e3779b9); x = tx * TileSize + ((h & 0xffff) / 65535f - .5f) * TileSize; z = tz * TileSize + (((h >> 16) & 0xffff) / 65535f - .5f) * TileSize; if (MathF.Abs(x) >= 3.5f) break; }
            float yaw = ((Hash(h, 17, 23) & 0xffff) / 65535f) * Mathf.Tau, sx = Mathf.Lerp(.045f, .082f, (Hash(h, 31, 47) & 0xffff) / 65535f), sy = Mathf.Lerp(.42f, .92f, (Hash(h, 53, 71) & 0xffff) / 65535f); float c = Mathf.Cos(yaw), s = Mathf.Sin(yaw); int at = i * RingGpuVegetationCompute.GrassFloats;
            rows[at] = c * sx; rows[at + 1] = 0; rows[at + 2] = -s * sx; rows[at + 4] = 0; rows[at + 5] = sy; rows[at + 6] = 0; rows[at + 8] = s * sx; rows[at + 9] = 0; rows[at + 10] = c * sx;
            rows[at + 3] = x; rows[at + 7] = z > 15 ? (z - 15) * .035f : 0; rows[at + 11] = z; rows[at + 12] = .55f; rows[at + 16] = (Hash(h, 79, 97) & 255) / 255f; rows[at + 17] = .5f; rows[at + 18] = .25f; rows[at + 19] = .75f;
        }
        return rows;
    }
    private static uint Hash(uint x, uint y, uint z) { uint h = x * 0x9e3779b9u ^ y * 0x85ebca6bu ^ z * 0xc2b2ae35u; h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15; h *= 0x846ca68bu; return h ^ h >> 16; }
    private static Vector4[] FrustumPlanes(Camera3D c) { var p = c.GetFrustum(); return p.Select(x => new Vector4(x.Normal.X, x.Normal.Y, x.Normal.Z, x.D)).ToArray(); }
    private bool ConservativeTileVisible(int id, Camera3D camera, Vector4[] planes)
    {
        if (!_tileBounds.TryGetValue(id, out Aabb box)) return false;
        // Both rejections are conservative: baseline's shader culls these roots.
        foreach (Vector4 packed in planes)
        {
            Plane plane = new(new Vector3(packed.X, packed.Y, packed.Z), packed.W);
            // Godot frustum normals face outward. The minimum projection being
            // outside proves every padded tile root is invisible.
            Vector3 p = new(plane.Normal.X >= 0 ? box.Position.X : box.End.X,
                plane.Normal.Y >= 0 ? box.Position.Y : box.End.Y,
                plane.Normal.Z >= 0 ? box.Position.Z : box.End.Z);
            if (plane.DistanceTo(p) > 5.3f) return false;
        }
        Vector3 nearest = new(Mathf.Clamp(camera.GlobalPosition.X, box.Position.X, box.End.X),
            Mathf.Clamp(camera.GlobalPosition.Y, box.Position.Y, box.End.Y),
            Mathf.Clamp(camera.GlobalPosition.Z, box.Position.Z, box.End.Z));
        const float farDistance = 145, rootGuard = 14;
        return camera.GlobalPosition.DistanceTo(nearest) <= farDistance + rootGuard;
    }
    private static int ReadInt(string[] args, string name, int fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int value) ? value : fallback; }
    private async Task Frames(int count) { for (int i = 0; i < count; i++) await Frame(); }
    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    private static double Median(IEnumerable<double> values) { double[] a = values.OrderBy(x => x).ToArray(); return a.Length == 0 ? 0 : a[a.Length / 2]; }
    private static double P95(IEnumerable<double> values) { double[] a = values.OrderBy(x => x).ToArray(); return a.Length == 0 ? 0 : a[Math.Min(a.Length - 1, (int)Math.Ceiling(a.Length * .95) - 1)]; }
    private static double? MedianNullable(List<double> values) => values.Count == 0 ? null : Median(values);
}
