using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

/// <summary>Shadow parity benchmark for hybrid GPU-assisted grass surface resolution.</summary>
public partial class GpuGrassGenerationPrototypeTest : Node
{
    public string Result { get; private set; } = "RUNNING";
    public async void Start()
    {
        try { await Run(); Result = "PASS hybrid GPU-assisted surface-resolution parity"; }
        catch (Exception error) { Result = "FAIL " + error; }
    }
    private async Task Run()
    {
        const long circumference = 981_748L * 32L; const int width = 318_000;
        var settings = (VoxelTerrainSettings)(GD.Load<VoxelTerrainSettings>("res://resources/voxel/voxel_terrain_settings_ring_integration.tres")?.Duplicate(true)
            ?? throw new InvalidOperationException("terrain settings missing"));
        using var sampler = new RingTerrainSampler(settings, true, circumference, 5_000_000d, width, 8000);
        var field = new SmoothTerrainVolumetricDensitySnapshot(new Plane(circumference, width), new SmoothTerrainEditSnapshot(Array.Empty<SmoothTerrainEditCommand>(), 1, circumference));
        try
        {
            using var session = new RingGrassGpuGenerationPrototype.Session();
            bool meaningful = false;
            var reports = new List<RingGrassGpuGenerationPrototype.TileReport>();
            foreach (long around in new[] { 186_000L, circumference - 16L, 0L, 186_016L })
            {
                const int across = 141_000; var focus = new RingCanonicalPosition(around, 42.25, across);
                var heights = new Dictionary<RingTerrainColumnCoordinate, int>();
                for (long x=around-2;x<=around+18;x++) for(int z=across-2;z<=across+18;z++)
                    heights[new RingTerrainColumnCoordinate((long)RingGrassCoordinates.Canonicalize(x,circumference), z)] = 42;
                var bounds = RingGrassCoordinates.Bounds(RingGrassCoordinates.WrapTileIndex((long)Math.Floor((double)around/16), RingGrassCoordinates.AroundTileCount(circumference,16)), across/16, 16, circumference,width);
                var geometry = new RingLodGeometrySnapshot(Transform3D.Identity,Vector3.Zero,Vector3.Zero,circumference/Math.Tau,0,0,circumference,-1000,1000,focus);
                var report=RingGrassGpuGenerationPrototype.Run(session, new RingGrassGpuGenerationPrototype.Input(new RingGrassTileKey((long)Math.Floor(bounds.AroundMin/16),across/16),bounds,8,9,96,24_576,35,sampler,new RingLodSurfaceEditSnapshot(1,circumference,heights),geometry,field,null,RingGrassAppearanceSnapshot.Default));
                reports.Add(report);
                GD.Print($"GPU_GRASS_GENERATION_TILE around={around} status={report.Status} queries={report.Queries} cache_bytes={report.CacheBytes} baseline_ms={report.CpuBaselineMilliseconds:F2} warm_baseline_ms={report.CpuWarmBaselineMilliseconds:F2} collect_ms={report.CollectionMilliseconds:F2} cache_ms={report.CacheMilliseconds:F2} cached_cpu_ms={report.CachedCpuMilliseconds:F2} init_ms={report.InitializationMilliseconds:F2} upload_compute_ms={report.UploadAndComputeMilliseconds:F2} readback_ms={report.ReadbackMilliseconds:F2} replay_ms={report.ReplayMilliseconds:F2} counts={report.CpuInstances}/{report.ReplayInstances} errors={report.MaximumPositionError:F6}/{report.MaximumBasisError:F6}");
                if (!report.Supported || !report.Parity) throw new InvalidOperationException(report.Status);
                meaningful |= report.CpuInstances > 0 && report.Queries >= 100;
            }
            if (!meaningful) throw new InvalidOperationException("prototype fixture was vacuous: no tile produced roots and 100 surface queries");
            VerifyCacheCases(session, sampler, circumference, width);
            VerifyEditedSurfaces(session, sampler, circumference, width);
            string directory = ProjectSettings.GlobalizePath("res://artifacts/grass-gpu-generation-prototype");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "prototype-test.json"), JsonSerializer.Serialize(new { status = "PASS", reports, session_init_ms = session.InitializationMilliseconds }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { field.DisposePooledWorkerContexts(); }
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
    private sealed class Plane : ISmoothTerrainBaseline
    { public long AroundLength { get; } public int AcrossWidth { get; } public ulong Revision => 1; public Plane(long around, int across) { AroundLength=around; AcrossWidth=across; }
      public SmoothTerrainSample Sample(long around,int vertical,int across) => new(vertical-42.25f,(int)(vertical<40.25f?SmoothTerrainMaterial.Rock:SmoothTerrainMaterial.Soil),Revision); }

    private static void VerifyCacheCases(RingGrassGpuGenerationPrototype.Session session, RingTerrainSampler sampler, long circumference, int width)
    {
        var hints = new Dictionary<RingTerrainColumnCoordinate, int>();
        for (long x = circumference - 4; x <= circumference + 4; x++) for (int z = 20; z <= 28; z++)
            hints[new RingTerrainColumnCoordinate((long)RingGrassCoordinates.Canonicalize(x, circumference), z)] = 42;
        var edits = new RingLodSurfaceEditSnapshot(9, circumference, hints);
        var field = new SmoothTerrainVolumetricDensitySnapshot(new TiltedMixedBaseline(circumference, width), new SmoothTerrainEditSnapshot(Array.Empty<SmoothTerrainEditCommand>(), 9, circumference));
        try
        {
            var resolver = new RingGrassSurfaceResolver(field, edits, sampler);
            var queries = new List<(double Around, double Across)> { (circumference - .25, 22.25), (0.25, 22.25), (circumference - .25, 26.25), (circumference - .25, -1) };
            RingGrassGpuQueryCache cache = resolver.CaptureGpuQueryCache(queries);
            RingGrassSurfaceHit?[] cpu = RingGrassSurfaceResolver.ResolveGpuQueryCacheOnCpu(cache);
            RingGrassSurfaceHit?[] gpu = session.Resolve(cache, default).Hits;
            Check(cpu.Length == gpu.Length && cpu[0].HasValue && cpu[1].HasValue && !cpu[3].HasValue, "cache seam/miss setup invalid");
            Check(cpu[2]?.Material == SmoothTerrainMaterial.Rock, "mixed material query did not exercise rock");
            VerifyHits(resolver, queries, cpu, gpu);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { RingGrassSurfaceResolver.ResolveGpuQueryCacheOnCpu(cache, canceled.Token); throw new InvalidOperationException("cancelled cache evaluation completed"); }
            catch (OperationCanceledException) { }
            var empty = resolver.CaptureGpuQueryCache(Array.Empty<(double Around, double Across)>());
            Check(empty.ColumnCount == 0 && RingGrassSurfaceResolver.ResolveGpuQueryCacheOnCpu(empty).Length == 0
                && session.Resolve(empty, default).Hits.Length == 0, "empty cache did not remain empty");
            try { session.Resolve(cache, canceled.Token); throw new InvalidOperationException("cancelled GPU evaluation completed"); }
            catch (OperationCanceledException) { }
        }
        finally { field.DisposePooledWorkerContexts(); }
    }

    private static void VerifyEditedSurfaces(RingGrassGpuGenerationPrototype.Session session, RingTerrainSampler sampler, long circumference, int width)
    {
        var queries = new List<(double Around, double Across)> { (2.25, 24.25), (3.2, 24.2), (0.2, 24.2) };
        var scenarios = new[] {
            (Name: "rock fill", Add: true, Height: 46, Radius: 4, Material: SmoothTerrainMaterial.Rock, Hint: 51),
            (Name: "soil fill", Add: true, Height: 46, Radius: 4, Material: SmoothTerrainMaterial.Soil, Hint: 51),
            (Name: "excavation", Add: false, Height: 42, Radius: 3, Material: SmoothTerrainMaterial.Soil, Hint: 43),
            (Name: "buried cave", Add: false, Height: 38, Radius: 2, Material: SmoothTerrainMaterial.Rock, Hint: 43)
        };
        foreach (var scenario in scenarios)
        {
            var hints = new Dictionary<RingTerrainColumnCoordinate, int>();
            for (long x = -4; x <= 8; x++) for (int z = 18; z <= 30; z++)
                hints[new RingTerrainColumnCoordinate((long)RingGrassCoordinates.Canonicalize(x, circumference), z)] = scenario.Hint;
            var edits = new SmoothTerrainEditSnapshot(new[] {
                new SmoothTerrainEditCommand(1, 10, 10, scenario.Add, 2, scenario.Height, 24, scenario.Radius, (int)scenario.Material)
            }, 10, circumference);
            var field = new SmoothTerrainVolumetricDensitySnapshot(new TiltedMixedBaseline(circumference, width), edits);
            try
            {
                var resolver = new RingGrassSurfaceResolver(field, new RingLodSurfaceEditSnapshot(10, circumference, hints), sampler);
                var cache = resolver.CaptureGpuQueryCache(queries);
                var cpu = RingGrassSurfaceResolver.ResolveGpuQueryCacheOnCpu(cache);
                VerifyHits(resolver, queries, cpu, session.Resolve(cache, default).Hits);
                Check(cpu[0].HasValue, scenario.Name + " did not exercise a surface hit");
                if (scenario.Add) Check(cpu[0]!.Value.Material == scenario.Material, scenario.Name + " lost added material");
                if (scenario.Name == "buried cave") Check(cpu[0]!.Value.Height > 41, "GPU cache descended through cave roof");
            }
            finally { field.DisposePooledWorkerContexts(); }
        }
        GD.Print("GPU_GRASS_GENERATION_EDITS=PASS rock/soil fill, excavation, exterior cave roof");
    }

    private static void VerifyHits(RingGrassSurfaceResolver resolver, IReadOnlyList<(double Around, double Across)> queries,
        RingGrassSurfaceHit?[] cpu, RingGrassSurfaceHit?[] gpu)
    {
        Check(cpu.Length == gpu.Length, "GPU query count changed");
        for (int i = 0; i < cpu.Length; i++)
        {
            bool found = resolver.TryResolve(queries[i].Around, queries[i].Across, out RingGrassSurfaceHit original);
            Check(found == cpu[i].HasValue && found == gpu[i].HasValue, "GPU cache changed surface eligibility");
            if (!found) continue;
            Check(cpu[i]!.Value == original, "CPU cache differs from authoritative resolver");
            RingGrassSurfaceHit hit = gpu[i]!.Value;
            Check(double.IsFinite(hit.Height) && hit.CanonicalNormal.IsFinite()
                && hit.Material == original.Material && Math.Abs(hit.Height - original.Height) <= .001
                && hit.CanonicalNormal.DistanceTo(original.CanonicalNormal) <= .001f,
                "GPU changed surface height, normal or material");
        }
    }
    private sealed class TiltedMixedBaseline : ISmoothTerrainBaseline
    {
        public long AroundLength { get; } public int AcrossWidth { get; } public ulong Revision => 19;
        public TiltedMixedBaseline(long length, int width) { AroundLength = length; AcrossWidth = width; }
        public SmoothTerrainSample Sample(long around, int vertical, int across)
        {
            float surface = 42.1f + (float)((around % 3 + 3) % 3) * .08f + (across % 2) * .04f;
            SmoothTerrainMaterial material = across == 26 ? SmoothTerrainMaterial.Rock : vertical < surface - 2 ? SmoothTerrainMaterial.Rock : SmoothTerrainMaterial.Soil;
            return new SmoothTerrainSample(vertical - surface, (int)material, Revision);
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
