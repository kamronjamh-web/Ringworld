using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.LOD;

/// <summary>Opt-in, real-Vulkan batch scaling measurement. It does no GPU readbacks.
/// Frame numbers include Godot presentation/scheduling, while submit_ms measures only
/// RequestFrame construction and submission on the main thread.</summary>
public partial class GpuVegetationDiagnostics : Node
{
    private const int TotalRoots = 16_384;
    private static readonly int[] TreeBatches = { 1, 32, 128, 256, 512, 1024 };
    private static readonly int[] GrassBatches = { 1, 32, 128, 256 };
    public string Result { get; private set; } = "RUNNING";

    private sealed record Sample(string Scenario, string Mode, int Batches, int Roots, double IdleMedian, double IdleP95, double ActiveMedian, double ActiveP95, double SubmitMedian, double SubmitP95, long AllocatedPerFrame, long ComputeUpdates, long Dispatches, long ParameterUploads);
    public async void Start()
    {
        try
        {
            Engine.MaxFps = 0;
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            var samples = new List<Sample>();
            foreach (int count in TreeBatches) samples.Add(await Measure("forced", "tree", count, false));
            foreach (int count in GrassBatches) samples.Add(await Measure("forced", "grass", count, true));
            samples.Add(await Measure("forced", "tree", 1024, false, 65536));
            foreach (int roots in new[] { 65536, 262144, 524288 }) samples.Add(await Measure("forced", "grass", 96, true, roots));
            samples.Add(await Measure("stationary", "tree", 1024, false, 65536, true));
            samples.Add(await Measure("moving", "tree", 1024, false, 65536, true, true));
            samples.Add(await Measure("stationary", "grass", 96, true, 262144, true));
            samples.Add(await Measure("moving", "grass", 96, true, 262144, true, true));
            WriteReport(samples);
            Result = "PASS wrote artifacts/gpu-vegetation-optimization/after-backend.json";
        }
        catch (Exception error) { Result = "FAIL " + error.Message; }
    }

    private async Task<Sample> Measure(string scenario, string mode, int batchCount, bool grass, int totalRoots = TotalRoots, bool skipUnchanged = false, bool moving = false)
    {
        using RingGpuVegetationCompute gpu = new();
        Mesh mesh = MakeMesh();
        var ids = new List<int>(batchCount);
        int baseCount = totalRoots / batchCount, remainder = totalRoots % batchCount;
        for (int batch = 0; batch < batchCount; batch++)
        {
            int count = baseCount + (batch < remainder ? 1 : 0);
            float[] roots = Rows(count, grass, batch);
            Vector4[]? metadata = grass ? null : Metadata(count);
            int id = gpu.RegisterBatch(new RingGpuVegetationCompute.BatchRegistration(
                $"diagnostic-{mode}-{batch}", new MultiMesh(), mesh, roots, metadata, count,
                new Aabb(new Vector3(-96, -8, -96), new Vector3(192, 24, 192)), grass));
            Require(id >= 0, "batch registration"); ids.Add(id);
        }
        gpu.Initialize();
        for (int i = 0; !gpu.Ready && i < 240; i++) await Frame();
        Require(gpu.Ready, "GPU pipeline readiness");
        var parameters = new RingGpuVegetationCompute.BatchFrameParameters[ids.Count];
        for (int i = 0; i < ids.Count; i++) parameters[i] = new(ids[i], Transform3D.Identity, grass, 0,
            grass ? 200 : 0, grass ? 400 : 200, 48, 1, 1, true, 1, false, null, 0);
        var request = new RingGpuVegetationCompute.FrameRequest(Vector3.Zero,
            new uint[] { (uint)totalRoots, (uint)totalRoots, (uint)totalRoots, (uint)totalRoots, (uint)totalRoots }, parameters, skipUnchanged);

        // Idle timing is frame pacing with allocations live but no compute submission.
        for (int i = 0; i < 24; i++) await Frame();
        double[] idle = await Frames(40, null);
        // Warm the resident source/metadata and pipeline before measuring cache windows.
        var warmRequest = request with { SkipUnchanged = false };
        for (int i = 0; i < 12; i++) { gpu.RequestFrame(warmRequest); await Frame(); }
        var submit = new List<double>(50);
        long allocations = GC.GetTotalAllocatedBytes(false), updates = gpu.CompletedUpdates, dispatches = gpu.DispatchCount, uploads = gpu.ParameterUploads;
        int submitted = 0;
        double[] active = await Frames(50, () => { var watch = Stopwatch.StartNew(); var next = moving ? request with { CameraWorld = new Vector3((submitted++ & 1) == 0 ? .01f : -.01f, 0, 0) } : request; Require(gpu.RequestFrame(next), "submit"); watch.Stop(); submit.Add(watch.Elapsed.TotalMilliseconds); });
        long allocatedPerFrame = (GC.GetTotalAllocatedBytes(false) - allocations) / active.Length;
        long completed = gpu.CompletedUpdates - updates;
        long dispatched = gpu.DispatchCount - dispatches, parameterUploads = gpu.ParameterUploads - uploads;
        if (scenario == "forced") Require(completed == 50, "forced requests must dispatch every sample");
        if (scenario == "stationary") Require(completed == 0, "unchanged requests must not dispatch compute");
        if (scenario == "moving") Require(completed == 50, "changed camera must dispatch every sample");
        Require(!gpu.Failed && ids.All(gpu.IsBatchReady), "all requested batches must have completed compute");
        foreach (int id in ids) gpu.RemoveBatch(id);
        gpu.Dispose();
        for (int i = 0; i < 4; i++) await Frame();
        var sample = new Sample(scenario, mode, batchCount, totalRoots, Median(idle), P95(idle), Median(active), P95(active), Median(submit), P95(submit), allocatedPerFrame, completed, dispatched, parameterUploads);
        GD.Print($"GPU_VEGETATION_SAMPLE scenario={scenario} {mode} batches={batchCount} roots={totalRoots} idle_ms={sample.IdleMedian:F3} active_ms={sample.ActiveMedian:F3} p95_ms={sample.ActiveP95:F3} submit_ms={sample.SubmitMedian:F3} allocated_B_per_frame={allocatedPerFrame} updates={completed} dispatches={dispatched} uploads={parameterUploads}");
        return sample;
    }

    private async Task<double[]> Frames(int count, Action? before)
    {
        var values = new double[count];
        for (int i = 0; i < count; i++)
        {
            ulong start = Time.GetTicksUsec(); before?.Invoke(); await Frame();
            values[i] = (Time.GetTicksUsec() - start) / 1000.0;
        }
        return values;
    }
    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    private static double Median(IEnumerable<double> values) { double[] a = values.OrderBy(x => x).ToArray(); return a[a.Length / 2]; }
    private static double P95(IEnumerable<double> values) { double[] a = values.OrderBy(x => x).ToArray(); return a[Math.Min(a.Length - 1, (int)Math.Ceiling(a.Length * .95) - 1)]; }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static Vector4[] Metadata(int count) { var data = new Vector4[count]; for (int i = 0; i < count; i++) data[i] = new Vector4((i + .5f) / count, 0, 0, 0); return data; }
    private static float[] Rows(int count, bool grass, int batch)
    {
        int stride = grass ? RingGpuVegetationCompute.GrassFloats : RingGpuVegetationCompute.TreeFloats;
        var rows = new float[count * stride];
        for (int i = 0; i < count; i++)
        {
            int at = i * stride; rows[at] = rows[at + 5] = rows[at + 10] = 1;
            rows[at + 3] = (i % 128) - 64; rows[at + 7] = 0; rows[at + 11] = ((i / 128) % 128) - 64;
            if (grass) { rows[at + 12] = .4f; rows[at + 16] = (batch % 17) / 17f; rows[at + 17] = .5f; rows[at + 18] = .25f; rows[at + 19] = .75f; }
        }
        return rows;
    }
    private static ArrayMesh MakeMesh()
    {
        var mesh = new ArrayMesh(); var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new[] { Vector3.Zero, Vector3.Right, Vector3.Up };
        arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2 }; mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays); return mesh;
    }
    private static void WriteReport(IReadOnlyList<Sample> samples)
    {
        string path = ProjectSettings.GlobalizePath("res://artifacts/gpu-vegetation-optimization/after-backend.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = new StringBuilder("{\n  \"measurement\": \"real Vulkan frame pacing; no instance-buffer readbacks; managed allocations include fixture\",\n  \"samples\": [\n");
        for (int i = 0; i < samples.Count; i++)
        {
            Sample s = samples[i];
            json.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                "    {{\"scenario\":\"{0}\",\"mode\":\"{1}\",\"batches\":{2},\"roots\":{3},\"idle_median_ms\":{4:F3},\"idle_p95_ms\":{5:F3},\"active_median_ms\":{6:F3},\"active_p95_ms\":{7:F3},\"submit_median_ms\":{8:F3},\"submit_p95_ms\":{9:F3},\"managed_bytes_per_frame\":{10},\"completed_compute_updates\":{11},\"dispatch_count\":{12},\"parameter_uploads\":{13}}}{14}\n",
                s.Scenario, s.Mode, s.Batches, s.Roots, s.IdleMedian, s.IdleP95, s.ActiveMedian, s.ActiveP95,
                s.SubmitMedian, s.SubmitP95, s.AllocatedPerFrame, s.ComputeUpdates, s.Dispatches, s.ParameterUploads, i + 1 == samples.Count ? "" : ",");
        }
        json.Append("  ]\n}\n"); File.WriteAllText(path, json.ToString());
    }
}
