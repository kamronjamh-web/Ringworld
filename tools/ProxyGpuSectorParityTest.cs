using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Generation.Gpu;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo;

/// <summary>
/// WO-G2 of docs/gpu-offload-plan.md: the K2 sector build (materials/terrain_gpu/ring_proxy_sector_k2.glsl) against
/// RingTerrainProxySector.BuildRawSector on the same sectors. Real worker sectors are packed into slot blocks (the CPU reference);
/// the GPU backend fills a second pool through its production path (K1 sample, biome, assemble, skirts); both pools are read back
/// and compared texel by texel, together with the summary (class, bounds, ocean masks). Rotated grid and owner bases and non-zero
/// underlays are used so the matrix and underlay arithmetic is exercised.
/// </summary>
public partial class ProxyGpuSectorParityTest : Node
{
    private const int Cells = 64;
    private static readonly int[] Spacings = { 8, 16, 32 };

    public string Result { get; private set; } = "RUNNING";
    private readonly ConcurrentDictionary<(int Texture, int Slot), byte[]> _readbacks = new();
    private readonly List<string> _lines = new();

    public async void Start()
    {
        string result;
        try { result = await Run(); }
        catch (Exception error) { result = "FAIL " + error; }
        Result = result;
    }

    private void Log(string line) { _lines.Add(line); GD.Print("PROXY_GPU_SECTOR_DETAIL " + line); }

    private async Task<string> Run()
    {
        if (RenderingServer.GetRenderingDevice() == null) return "FAIL RenderingDevice required";
        string showcase = JsonDocument.Parse(Godot.FileAccess.GetFileAsString("res://resources/menu/showcase_world.json"))
            .RootElement.GetProperty("generation_settings").GetProperty("surface_generation_recipe_json").GetString()!;
        TerrainGenerationParameters recipe = TerrainGenerationParameters.FromJson(showcase);
        using var settings = new VoxelTerrainSettings
        {
            Seed = unchecked((int)recipe.Seed), SurfaceGenerationRecipeJson = recipe.CanonicalJson,
            SurfaceGenerationContractId = RingTerrainSampler.RuntimeSurfaceGenerationContractId,
            BedrockY = -8000, SeaLevelY = 0, EnableUnified3dDensityTerrain = false,
            EnableProceduralCaves = false, EnableNaturalOverhangs = false, EnableDesignedCaveDensityComposition = false,
        };
        using var sampler = new RingTerrainSampler(settings, true, CertifiedRingScale.AroundVoxelLength,
            CertifiedRingScale.EffectiveRadiusMeters, CertifiedRingScale.RingWidthMeters, 8400);
        ProxyGpuClimateSettings climate = sampler.CreateGpuClimateSettings()!;
        var failures = new List<string>();
        int sectorsTotal = 0, dry = 0, ocean = 0, coastal = 0;
        foreach (int spacing in Spacings)
        {
            (int n, int d, int o, int c) = await RunLevel(sampler, recipe, climate, spacing, failures);
            sectorsTotal += n; dry += d; ocean += o; coastal += c;
        }
        string detail = string.Join("\n", _lines);
        string dir = ProjectSettings.GlobalizePath("res://.godot/proxy_gpu_sector_parity");
        System.IO.Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "report.txt"), detail + "\n");
        if (failures.Count != 0) return "FAIL " + string.Join(" | ", failures.Take(16));
        return $"PASS sectors={sectorsTotal} dry={dry} ocean={ocean} coastal={coastal}";
    }

    private static string ClassOf(RingTerrainProxySector.VertexTableFixtureSector s)
        => s.OceanIndexCount == 0 ? "dry" : s.FullyOceanSurface ? "ocean" : "coastal";

    private async Task<(int Sectors, int Dry, int Ocean, int Coastal)> RunLevel(RingTerrainSampler sampler, TerrainGenerationParameters recipe,
        ProxyGpuClimateSettings climate, int spacing, List<string> failures)
    {
        int size = Cells * spacing, ringWidth = sampler.RingAcrossWidthMeters;
        long aroundSectors = (sampler.AroundRingVoxelLength + size - 1) / size;
        int acrossSectors = (ringWidth + size - 1) / size;
        // Rotated bases and non-trivial level constants: the production builder resolves them from these same inputs.
        var gridBasis = new Basis(Quaternion.FromEuler(new Vector3(0.21f, 0.9f, -0.33f)));
        var ownerBasis = new Basis(Quaternion.FromEuler(new Vector3(-0.4f, 0.15f, 0.7f)));
        float underlay = 0.2f, parentUnderlay = 0.35f, skirtDepth = 8.0f;
        bool parentRestores = true;
        Color low = new(0.19f, 0.28f, 0.16f, 1.0f), high = new(0.42f, 0.38f, 0.27f, 1.0f);
        var edits = new RingLodSurfaceEditSnapshot(0, sampler.AroundRingVoxelLength, new Dictionary<RingTerrainColumnCoordinate, int>(),
            new Dictionary<RingTerrainColumnCoordinate, float>(), sampler.UsesRuntimeSurfaceGeneration);
        var session = RingTerrainProxySector.CreateVertexTableFixtureSession(edits, sampler, ringWidth);

        // Candidate sectors: deterministic low-discrepancy points over the ring, ring seam and last sector included.
        var wanted = new List<RingTerrainSectorCoordinate>();
        var fixtures = new List<(RingTerrainSectorCoordinate Coordinate, RingTerrainProxySector.VertexTableFixtureSector Sector, RingLodGeometrySnapshot Geometry)>();
        int dryCount = 0, oceanCount = 0, coastalCount = 0;
        for (int j = 0; j < 200 && fixtures.Count < ProxyGpuSectorBackend.MaxBatch; j++)
        {
            long around = j == 0 ? aroundSectors - 1 : j == 1 ? 0 : (long)(((j * 0.6180339887) % 1.0) * aroundSectors);
            int across = Math.Clamp((int)(((j * 0.7548776662) % 1.0) * acrossSectors), 2, acrossSectors - 3);
            var coordinate = new RingTerrainSectorCoordinate(around, across);
            long around0 = around * size;
            bool site = sampler.IntersectsSiteInfluence(around0 - 4L * spacing, around0 + size + 4L * spacing, (double)across * size - 4.0 * spacing, (double)across * size + size + 4.0 * spacing);
            if (!RingTerrainProxySector.GpuSectorRoutable(coordinate, size, spacing, ringWidth, false, site, false)) continue;
            var geometry = new RingLodGeometrySnapshot(new Transform3D(gridBasis, new Vector3(3, 4, 5)), new Vector3(1, 2, 3), new Vector3(11, 12, 13),
                CertifiedRingScale.EffectiveRadiusMeters, 17.0, 120.5, sampler.AroundRingVoxelLength,
                CertifiedRingScale.TerrainFloorMeters, CertifiedRingScale.StructuralWallTopMeters,
                new RingCanonicalPosition(around0 + 7.0, 0.0, across * size + 3.0));
            var fixture = session.Build(coordinate, spacing, Cells, geometry, buildWalls: false, buildSkirts: true, underlay, parentUnderlay,
                new Transform3D(ownerBasis, Vector3.Zero), skirtDepth, low, high, parentRestores);
            if (fixture == null) continue;
            string cls = ClassOf(fixture);
            // Keep a spread: at most 6 of each class so coastal sectors (GPU declines them) do not crowd out the content checks.
            if (cls == "dry" && dryCount >= 7) continue;
            if (cls == "ocean" && oceanCount >= 7) continue;
            if (cls == "coastal" && coastalCount >= 4) continue;
            if (cls == "dry") dryCount++; else if (cls == "ocean") oceanCount++; else coastalCount++;
            fixtures.Add((coordinate, fixture, geometry));
            if (j % 5 == 4) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        if (fixtures.Count == 0) { failures.Add($"spacing {spacing}: no routable fixture sector found"); return (0, 0, 0, 0); }

        // GPU side: the production backend into its own pool.
        using var backend = new ProxyGpuSectorBackend(spacing, recipe, climate);
        for (int frame = 0; frame < 1200 && !backend.Ready && !backend.Failed; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (!backend.Ready) { failures.Add($"spacing {spacing}: backend not ready: {backend.Error}"); return (0, 0, 0, 0); }
        var pool = new RingTerrainProxySector.VertexTablePool(Cells, fixtures.Count + 2, hasChord: true, gpuWritable: true);
        var cpuPool = new RingTerrainProxySector.VertexTablePool(Cells, fixtures.Count + 2, hasChord: true);
        try
        {
            for (int frame = 0; frame < 600 && !(pool.Ready && cpuPool.Ready); frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!pool.Ready) { failures.Add($"spacing {spacing}: pool not ready"); return (0, 0, 0, 0); }
            RingLodGeometrySnapshot g0 = fixtures[0].Geometry;
            ProxyGpuSectorGlobals globals = RingTerrainProxySector.CreateGpuGlobals(spacing, size, spacing * 2, underlay, parentUnderlay, parentRestores,
                skirtDepth, g0, new Transform3D(ownerBasis, Vector3.Zero), sampler, low, high);
            var gpuSlots = new int[fixtures.Count];
            var cpuSlots = new int[fixtures.Count];
            for (int j = 0; j < fixtures.Count; j++)
            {
                if (!pool.TryAcquire(out gpuSlots[j]) || !cpuPool.TryAcquire(out cpuSlots[j])) { failures.Add("pool exhausted"); return (0, 0, 0, 0); }
                cpuPool.Upload(cpuSlots[j], fixtures[j].Sector.Blocks);
                backend.Enqueue(new ProxyGpuSectorJob(fixtures[j].Coordinate.AroundSector, fixtures[j].Coordinate.AcrossSector, gpuSlots[j], 1UL, pool.Generation, SelfCheck: j == 0));
            }
            // The first fixture rides alone (self-check); pumping again after it completes sends the rest.
            var results = new Dictionary<(long, int), ProxyGpuSectorResult>();
            int generation = pool.Generation;
            for (int frame = 0; frame < 3000 && results.Count < fixtures.Count; frame++)
            {
                backend.Pump(globals, () => new ProxyGpuSlotTargets(pool.PosRid, pool.ChordRid, pool.AttrRid, generation, pool.Ready));
                backend.PollTimes();
                while (backend.TryTakeResult(out ProxyGpuSectorResult? r)) results[(r!.Job.AroundSector, r.Job.AcrossSector)] = r;
                if (backend.Failed) { failures.Add($"spacing {spacing}: backend failed: {backend.Error}"); return (0, 0, 0, 0); }
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            if (results.Count != fixtures.Count) { failures.Add($"spacing {spacing}: GPU results {results.Count}/{fixtures.Count}"); return (0, 0, 0, 0); }

            // Read both pools back.
            RenderingDevice rd = RenderingServer.GetRenderingDevice()!;
            _readbacks.Clear();
            int expected = fixtures.Count * 6;
            RenderingServer.CallOnRenderThread(Callable.From(() =>
            {
                for (int j = 0; j < fixtures.Count; j++)
                {
                    int g = gpuSlots[j], c = cpuSlots[j];
                    rd.TextureGetDataAsync(pool.PosRid, (uint)g, Callable.From<byte[]>(b => _readbacks[(0, g)] = b));
                    rd.TextureGetDataAsync(pool.ChordRid, (uint)g, Callable.From<byte[]>(b => _readbacks[(1, g)] = b));
                    rd.TextureGetDataAsync(pool.AttrRid, (uint)g, Callable.From<byte[]>(b => _readbacks[(2, g)] = b));
                    rd.TextureGetDataAsync(cpuPool.PosRid, (uint)c, Callable.From<byte[]>(b => _readbacks[(3, c)] = b));
                    rd.TextureGetDataAsync(cpuPool.ChordRid, (uint)c, Callable.From<byte[]>(b => _readbacks[(4, c)] = b));
                    rd.TextureGetDataAsync(cpuPool.AttrRid, (uint)c, Callable.From<byte[]>(b => _readbacks[(5, c)] = b));
                }
            }));
            for (int frame = 0; frame < 900 && _readbacks.Count < expected; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (_readbacks.Count != expected) { failures.Add($"spacing {spacing}: readbacks {_readbacks.Count}/{expected}"); return (0, 0, 0, 0); }

            var worst = new Dictionary<string, double>();
            void Track(string key, double v) { worst[key] = Math.Max(worst.GetValueOrDefault(key), v); }
            int dry = 0, ocean = 0, coastal = 0, classMismatch = 0;
            var normalAngles = new List<double>();
            for (int j = 0; j < fixtures.Count; j++)
            {
                var sector = fixtures[j].Sector;
                ProxyGpuSectorResult r = results[(fixtures[j].Coordinate.AroundSector, fixtures[j].Coordinate.AcrossSector)];
                string cpuClass = ClassOf(sector);
                string gpuClass = r.Class.ToString().ToLowerInvariant();
                if (cpuClass == "dry") dry++; else if (cpuClass == "ocean") ocean++; else coastal++;
                // GPU Coastal covers "any wet vertex": the CPU may still discard a one-vertex clip, so only Dry and Ocean claims are binding.
                bool agree = gpuClass == cpuClass || (gpuClass == "coastal" && cpuClass != "ocean" && cpuClass != "dry") || (gpuClass == "coastal" && cpuClass == "dry" && sector.OceanIndexCount == 0);
                if (!agree) { classMismatch++; failures.Add($"spacing {spacing} sector {fixtures[j].Coordinate}: class gpu={gpuClass} cpu={cpuClass}"); }
                if (r.Heights != null) CheckHeights(sampler, fixtures[j].Coordinate, spacing, size, r.Heights, worst, failures);
                if (gpuClass == "coastal") continue; // the GPU declines these; their slot content is not used
                CompareSlot(_readbacks[(0, gpuSlots[j])], _readbacks[(1, gpuSlots[j])], _readbacks[(2, gpuSlots[j])],
                    _readbacks[(3, cpuSlots[j])], _readbacks[(4, cpuSlots[j])], _readbacks[(5, cpuSlots[j])], cpuClass == "ocean", Track, normalAngles);
                // Summary against the worker's bounds and masks.
                Aabb cpuVisual = sector.VisualBounds;
                Vector3 gMin = r.VisualMin, gMax = r.VisualMax;
                if (r.Reach > 0.0f) { gMin -= Vector3.One * r.Reach; gMax += Vector3.One * r.Reach; }
                Track("visual_bounds_min", MaxAbs(gMin - cpuVisual.Position));
                Track("visual_bounds_max", MaxAbs(gMax - (cpuVisual.Position + cpuVisual.Size)));
                Aabb cpuMesh = sector.MeshBounds;
                Track("mesh_bounds_min", MaxAbs(r.MeshMin - cpuMesh.Position));
                Track("mesh_bounds_max", MaxAbs(r.MeshMax - (cpuMesh.Position + cpuMesh.Size)));
                if (cpuClass == "ocean")
                {
                    int cellDiff = 0, depthDiff = 0;
                    bool[] cpuCells = sector.OceanCells; byte[] cpuDepth = sector.OceanDepthBytes;
                    for (int i = 0; i < cpuCells.Length; i++) if (cpuCells[i] != r.OceanCells[i]) cellDiff++;
                    for (int i = 0; i < cpuDepth.Length; i++) depthDiff = Math.Max(depthDiff, Math.Abs(cpuDepth[i] - r.DepthBytes[i]));
                    Track("ocean_cell_flips", cellDiff);
                    Track("ocean_depth_byte_diff", depthDiff);
                }
            }
            ProxyGpuSectorBackend.PassTimingEnabled = true;
            // Warm timing: the same sectors again (the slots are rewritten with identical data) to read steady GPU cost per sector.
            for (int round = 0; round < 12; round++)
            {
                for (int j = 0; j < fixtures.Count; j++)
                    backend.Enqueue(new ProxyGpuSectorJob(fixtures[j].Coordinate.AroundSector, fixtures[j].Coordinate.AcrossSector, gpuSlots[j], 1UL, pool.Generation));
                int taken = 0;
                for (int frame = 0; frame < 3000 && taken < fixtures.Count; frame++)
                {
                    backend.Pump(globals, () => new ProxyGpuSlotTargets(pool.PosRid, pool.ChordRid, pool.AttrRid, generation, pool.Ready));
                    backend.PollTimes();
                    while (backend.TryTakeResult(out _)) taken++;
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                }
            }
            for (int frame = 0; frame < 20; frame++) { backend.PollTimes(); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
            var recent = backend.RecentBatches().Skip(Math.Max(0, backend.RecentBatches().Length - 40)).ToArray();
            if (recent.Length > 0)
            {
                double[] perSector = recent.Select(b => b.Microseconds / b.Sectors).OrderBy(v => v).ToArray();
                double[] batch = recent.Select(b => b.Microseconds).OrderBy(v => v).ToArray();
                Log($"spacing={spacing} warm_batches={recent.Length} sectors_per_batch_median={recent.Select(b => b.Sectors).OrderBy(v => v).ElementAt(recent.Length / 2)} "
                    + $"us_per_sector_median={perSector[perSector.Length / 2]:0.0} us_per_sector_p90={perSector[(int)(perSector.Length * 0.9)]:0.0} batch_us_median={batch[batch.Length / 2]:0.0} batch_us_max={batch[^1]:0.0} pass_us_mean_SBAK={string.Join("/", backend.PassMicrosecondsMean().Select(v => v.ToString("0.0")))}");
            }
            ProxyGpuSectorBackend.PassTimingEnabled = false;
            normalAngles.Sort();
            double p999 = normalAngles.Count == 0 ? 0.0 : normalAngles[Math.Min(normalAngles.Count - 1, (int)(normalAngles.Count * 0.999))];
            Log($"spacing={spacing} sectors={fixtures.Count} dry={dry} ocean={ocean} coastal={coastal} class_mismatch={classMismatch} "
                + string.Join(" ", worst.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value:0.0000}")) + $" normal_deg_p99.9={p999:0.0000}"
                + $" target_batch_sectors={backend.TargetBatchSectors} max_batch_us={backend.MaxMeasuredBatchMicroseconds:0.0} batches={backend.BatchesDispatched}");
            void Limit(string key, double limit) { if (worst.GetValueOrDefault(key) > limit) failures.Add($"spacing {spacing}: {key}={worst.GetValueOrDefault(key):0.0000} > {limit}"); }
            Limit("terrain_pos_m", 0.025); Limit("water_pos_m", 0.025); Limit("skirt_pos_m", 0.025);
            Limit("terrain_morph_m", 0.04); Limit("water_morph_m", 0.04); Limit("skirt_morph_m", 0.04);
            Limit("chord_m", 0.06);
            Limit("normal_deg_max", 2.0);
            if (p999 > 0.5) failures.Add($"spacing {spacing}: normal p99.9 {p999:0.0000} deg > 0.5");
            Limit("terrain_color_byte", 3); Limit("water_color_byte", 2); Limit("skirt_color_byte", 1);
            Limit("flag_word_mismatch", 0); Limit("skirt_uv_word_mismatch", 0);
            Limit("visual_bounds_min", 0.3); Limit("visual_bounds_max", 0.3); Limit("mesh_bounds_min", 0.1); Limit("mesh_bounds_max", 0.1);
            Limit("ocean_cell_flips", 0); Limit("ocean_depth_byte_diff", 1);
            Limit("height_window_m", 0.02);
            if (worst.GetValueOrDefault("textures_nonzero_checked") == 0 && fixtures.Count > coastal) failures.Add($"spacing {spacing}: nothing compared");
            return (fixtures.Count, dry, ocean, coastal);
        }
        finally
        {
            pool.Dispose(); cpuPool.Dispose();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static double MaxAbs(Vector3 v) => Math.Max(Math.Abs(v.X), Math.Max(Math.Abs(v.Y), Math.Abs(v.Z)));

    private static void CheckHeights(RingTerrainSampler sampler, RingTerrainSectorCoordinate coordinate, int spacing, int size, float[] heights,
        Dictionary<string, double> worst, List<string> failures)
    {
        double max = 0.0;
        for (int z = -1; z <= 65; z += 4)
        for (int x = -1; x <= 65; x += 4)
        {
            long around = coordinate.AroundSector * (long)size + x * spacing;
            int across = coordinate.AcrossSector * size + z * spacing;
            float cpu = sampler.GetPresentationSurfaceHeight(around, across);
            max = Math.Max(max, Math.Abs(cpu - heights[(z + 1) * 67 + (x + 1)]));
        }
        worst["height_window_m"] = Math.Max(worst.GetValueOrDefault("height_window_m"), max);
    }

    private static void CompareSlot(byte[] gPos, byte[] gChord, byte[] gAttr, byte[] cPos, byte[] cChord, byte[] cAttr, bool ocean,
        Action<string, double> track, List<double> normalAngles)
    {
        const int lattice = 65 * 65;
        int texels = 2 * lattice + 4 * 64 * 4;
        var gp = new float[gPos.Length / 4]; Buffer.BlockCopy(gPos, 0, gp, 0, gPos.Length);
        var cp = new float[cPos.Length / 4]; Buffer.BlockCopy(cPos, 0, cp, 0, cPos.Length);
        var gc = new float[gChord.Length / 4]; Buffer.BlockCopy(gChord, 0, gc, 0, gChord.Length);
        var cc = new float[cChord.Length / 4]; Buffer.BlockCopy(cChord, 0, cc, 0, cChord.Length);
        var ga = new uint[gAttr.Length / 4]; Buffer.BlockCopy(gAttr, 0, ga, 0, gAttr.Length);
        var ca = new uint[cAttr.Length / 4]; Buffer.BlockCopy(cAttr, 0, ca, 0, cAttr.Length);
        for (int t = 0; t < texels; t++)
        {
            string group = t < lattice ? "terrain" : t < 2 * lattice ? "water" : "skirt";
            double pos = Math.Max(Math.Abs(gp[t * 4] - cp[t * 4]), Math.Max(Math.Abs(gp[t * 4 + 1] - cp[t * 4 + 1]), Math.Abs(gp[t * 4 + 2] - cp[t * 4 + 2])));
            if (!double.IsFinite(pos)) pos = 1e9;
            track(group + "_pos_m", pos);
            double morph = Math.Abs(gp[t * 4 + 3] - cp[t * 4 + 3]);
            if (!double.IsFinite(morph)) morph = 1e9;
            track(group + "_morph_m", morph);
            if (t < lattice)
            {
                double chord = Math.Max(Math.Abs(gc[t * 4] - cc[t * 4]), Math.Max(Math.Abs(gc[t * 4 + 1] - cc[t * 4 + 1]), Math.Abs(gc[t * 4 + 2] - cc[t * 4 + 2])));
                if (!double.IsFinite(chord)) chord = 1e9;
                track("chord_m", chord);
            }
            if (group != "skirt" && (group == "terrain" || ocean))
            {
                Vector3 a = RingTerrainProxySector.DecodeOctNormal16(ga[t * 4]), b = RingTerrainProxySector.DecodeOctNormal16(ca[t * 4]);
                double degrees = Math.Atan2((double)a.Cross(b).Length(), a.Dot(b)) * 180.0 / Math.PI;
                normalAngles.Add(degrees);
                track("normal_deg_max", degrees);
            }
            uint gColor = ga[t * 4 + 1], cColor = ca[t * 4 + 1];
            if (group == "terrain" || group == "skirt" || ocean)
            {
                int diff = 0;
                for (int c = 0; c < 4; c++) diff = Math.Max(diff, Math.Abs((int)((gColor >> (c * 8)) & 255u) - (int)((cColor >> (c * 8)) & 255u)));
                track(group + "_color_byte", diff);
            }
            if (ga[t * 4 + 2] != ca[t * 4 + 2]) track("flag_word_mismatch", 1);
            if (group == "skirt" && ga[t * 4 + 3] != ca[t * 4 + 3]) track("skirt_uv_word_mismatch", 1);
            track("textures_nonzero_checked", 1);
        }
    }
}
