using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Atmosphere;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tests;

/// <summary>Per-instance vegetation haze, end to end on the GPU: the vegetation compute (grass and tree channels) writes one atlas slot per
/// compacted row; those slots are decoded and compared with the exact per-vertex Physical path (ring_haze_eval_target_split, the path
/// ring_haze_sample takes with hub weight 0) evaluated by a runtime-compiled probe shader at each row's anchor. Covers the culling scatter sites
/// (grass and large trees: phase 2; small trees: the single-group phase 3), the storm path and key-driven refresh, the cull-free refresh pass after
/// the camera moves, the Enabled toggle and the slot allocator lifecycle. The per-vertex spread inside an instance is logged for information.
/// Needs a real RenderingDevice, so run windowed (no --headless; the runner fails fast without a RenderingDevice):
/// godot --path . --script res://tools/vegetation_haze_gpu_test.gd</summary>
public partial class VegetationHazeGpuTest : Node
{
    private const double RadiusMeters = 5_000_000.0;
    private static readonly Vector3 CameraBase = new(30f, 61.7f, -20f);
    private static readonly Vector3 TreeMeshAnchor = new(0f, 10f, 0f);
    private static readonly Vector3 LightSun = new(3.2f, 3.0f, 2.7f), LightAmbient = new(.25f, .32f, .45f), LightNight = new(.004f, .005f, .008f);
    private const float LightDay = .8f;
    public string Result { get; private set; } = "RUNNING";
    private readonly List<string> _log = new();
    private RingGpuVegetationCompute? _grass, _trees;

    private sealed class Probe
    {
        public required string Name;
        public required RingGpuVegetationCompute Gpu;
        public required int Batch;
        public required float[] Rows;
        public required int Stride;
        public required Transform3D L2W;
        public required Vector3 MeshAnchor, SpreadA, SpreadB;
        public int Count => Rows.Length / Stride;
        public Vector2 Slot => Gpu.GetHazeSlot(Batch);
    }

    public async void Start()
    {
        bool oldEnabled = RingAtmosphereModel.Enabled, oldVegetation = RingVegetationHaze.Enabled;
        var oldModel = RingAtmosphereModel.HazeModel;
        try
        {
            RingAtmosphereModel.HazeModel = RingAtmosphereModel.HazeModelKind.Physical;
            RingAtmosphereModel.Enabled = true;
            RingVegetationHaze.Enabled = true;
            RingAtmosphereModel.SetHazeStorm(0f);
            await Run();
            Result = "PASS " + string.Join("; ", _log);
        }
        catch (Exception error) { Result = "FAIL " + error.Message; }
        finally
        {
            RingAtmosphereModel.SetHazeStorm(0f);
            RingAtmosphereModel.HazeModel = oldModel;
            RingAtmosphereModel.Enabled = oldEnabled;
            RingVegetationHaze.Enabled = oldVegetation;
            _grass?.Dispose(); _trees?.Dispose();
            await Frames(3);
        }
    }

    private async Task Run()
    {
        var random = new Random(7);
        Mesh quad = MakeQuad(), box = MakeBox();
        Check(box.GetAabb().GetCenter().IsEqualApprox(TreeMeshAnchor), "tree mesh AABB centre");
        var l2w = new Transform3D(new Basis(Vector3.Up, .4f), new Vector3(5f, 0f, -7f));
        var bounds = new Aabb(new Vector3(-20000, -200, -20000), new Vector3(40000, 600, 40000));
        _grass = new RingGpuVegetationCompute { HazeChannel = RingVegetationHaze.Channel.Grass };
        _trees = new RingGpuVegetationCompute { HazeChannel = RingVegetationHaze.Channel.Trees };

        float[] grassRows = GrassRows(random, 4096);
        int grassId = _grass.RegisterBatch(new RingGpuVegetationCompute.BatchRegistration("haze-grass", new MultiMesh(), quad, grassRows, null, 4096, bounds, true));
        float[] treeRows = TreeRows(random, 512), smallRows = TreeRows(random, 48);
        int treeId = _trees.RegisterBatch(new RingGpuVegetationCompute.BatchRegistration("haze-tree", new MultiMesh(), box, treeRows, null, 512, bounds, false));
        int smallId = _trees.RegisterBatch(new RingGpuVegetationCompute.BatchRegistration("haze-tree-small", new MultiMesh(), box, smallRows, null, 48, bounds, false));
        Check(grassId >= 0 && treeId >= 0 && smallId >= 0, "registration");
        var probes = new[]
        {
            new Probe { Name = "grass", Gpu = _grass, Batch = grassId, Rows = grassRows, Stride = 20, L2W = l2w, MeshAnchor = Vector3.Zero,
                SpreadA = new Vector3(.5f, .6f, 0), SpreadB = new Vector3(-.5f, 0, 0) },
            new Probe { Name = "tree", Gpu = _trees, Batch = treeId, Rows = treeRows, Stride = 12, L2W = l2w, MeshAnchor = TreeMeshAnchor,
                SpreadA = new Vector3(0, 10, 0), SpreadB = new Vector3(0, -10, 0) },
            new Probe { Name = "tree-small", Gpu = _trees, Batch = smallId, Rows = smallRows, Stride = 12, L2W = l2w, MeshAnchor = TreeMeshAnchor,
                SpreadA = new Vector3(0, 10, 0), SpreadB = new Vector3(0, -10, 0) },
        };
        foreach (Probe probe in probes)
        {
            Vector2 slot = probe.Slot;
            Check(slot.X >= 0 && slot.Y is >= 2 and <= 2047, $"{probe.Name}: slot {slot}");
        }

        RingAtmosphereRayFrame ray = Frame(CameraBase);
        RingVegetationHazeFrame frame = RingVegetationHaze.CreateFrame(ray, RadiusMeters);
        Check(frame.Enabled && frame.Parameters.Length == RingVegetationHaze.ParameterFloats, "haze frame not enabled (Physical model gates)");
        _grass.Initialize(); _trees.Initialize();
        await WaitReady(_grass); await WaitReady(_trees);
        _grass.RequestHaze(frame); _trees.RequestHaze(frame);
        var grassBudgets = Array.Empty<uint>();
        var treeBudgets = new uint[] { 1024, 1024, 1024, 1024, 1024 };
        _grass.RequestFrame(new RingGpuVegetationCompute.FrameRequest(CameraBase, grassBudgets,
            new List<RingGpuVegetationCompute.BatchFrameParameters> { new(grassId, l2w, true, 0, 0, 1e6f, 1, 1, 1, false, 1, false, null, 0) }));
        _trees.RequestFrame(new RingGpuVegetationCompute.FrameRequest(CameraBase, treeBudgets,
            new List<RingGpuVegetationCompute.BatchFrameParameters>
            {
                new(treeId, l2w, false, 0, 0, 1e6f, 1, 1, 1, false, 1, false, null, 0),
                new(smallId, l2w, false, 0, 0, 1e6f, 1, 1, 1, false, 1, false, null, 0),
            }));
        foreach (Probe probe in probes) await WaitBatch(probe);
        await Frames(3);
        _grass.RequestHaze(frame); _trees.RequestHaze(frame);
        await Frames(2);
        var state = RingVegetationHaze.State;
        Check(state.X == 1f && state.Y == 1f, $"channels not live: {state}");
        foreach (Probe probe in probes)
        {
            (uint visible, _) = await ReadRows(probe);
            Check(visible == probe.Count, $"{probe.Name}: visible {visible} != {probe.Count}");
        }
        await CompareAll(probes, frame, "clear");

        // Storm: the unstormed parameters stay, the key changes and the refresh pass rewrites every slot with the stormed terms.
        RingAtmosphereModel.SetHazeStorm(.8f);
        RingVegetationHazeFrame storm = RingVegetationHaze.CreateFrame(ray, RadiusMeters);
        Check(storm.Key != frame.Key, "storm did not change the frame key");
        long refreshes = _grass.HazeRefreshCount;
        _grass.RequestHaze(storm); _trees.RequestHaze(storm);
        await Frames(3);
        Check(_grass.HazeRefreshCount > refreshes, "storm did not trigger a refresh");
        await CompareAll(probes, storm, "storm");
        RingAtmosphereModel.SetHazeStorm(0f);
        _grass.RequestHaze(frame); _trees.RequestHaze(frame);
        await Frames(3);
        await CompareAll(probes, frame, "clear-again");

        // Refresh without culling: the camera moved 20 m, nothing requested a frame.
        RingAtmosphereRayFrame moved = Frame(CameraBase + new Vector3(20f, 0f, 0f));
        RingVegetationHazeFrame movedFrame = RingVegetationHaze.CreateFrame(moved, RadiusMeters);
        long updates = _grass.CompletedUpdates, count = _grass.HazeRefreshCount, treeCount = _trees.HazeRefreshCount;
        _grass.RequestHaze(movedFrame); _trees.RequestHaze(movedFrame);
        await Frames(3);
        Check(_grass.CompletedUpdates == updates, "refresh unexpectedly culled");
        Check(_grass.HazeRefreshCount > count && _trees.HazeRefreshCount > treeCount, "camera move did not refresh");
        await CompareAll(probes, movedFrame, "moved");

        // Toggle: disabled -> channels not live; enabled -> live again after the next RequestHaze.
        RingVegetationHaze.Enabled = false;
        RingVegetationHazeFrame off = RingVegetationHaze.CreateFrame(moved, RadiusMeters);
        Check(!off.Enabled, "frame enabled while toggle off");
        _grass.RequestHaze(off); _trees.RequestHaze(off);
        state = RingVegetationHaze.State;
        Check(state.X == 0f && state.Y == 0f, $"channels live while disabled: {state}");
        RingVegetationHaze.Enabled = true;
        _grass.RequestHaze(RingVegetationHaze.CreateFrame(moved, RadiusMeters)); _trees.RequestHaze(RingVegetationHaze.CreateFrame(moved, RadiusMeters));
        await Frames(3);
        state = RingVegetationHaze.State;
        Check(state.X == 1f && state.Y == 1f, $"channels not live after re-enable: {state}");
        await CompareAll(probes, RingVegetationHaze.CreateFrame(moved, RadiusMeters), "re-enabled");
        _log.Add("toggle ok");

        // Slot lifecycle on the grass compute.
        Vector2 first = _grass.GetHazeSlot(grassId);
        float[] extraRows = GrassRows(random, 256);
        int second = _grass.RegisterBatch(new RingGpuVegetationCompute.BatchRegistration("haze-grass-2", new MultiMesh(), quad, extraRows, null, 256, bounds, true));
        Vector2 secondSlot = _grass.GetHazeSlot(second);
        Check(secondSlot.X >= 0, "second batch has no slot");
        Check(secondSlot.X >= first.X + 4096 || secondSlot.X + 256 <= first.X, $"second range overlaps the first ({first}, {secondSlot})");
        Check(_grass.RemoveBatch(second), "remove second");
        int third = _grass.RegisterBatch(new RingGpuVegetationCompute.BatchRegistration("haze-grass-3", new MultiMesh(), quad, GrassRows(random, 256), null, 256, bounds, true));
        Vector2 thirdSlot = _grass.GetHazeSlot(third);
        Check(thirdSlot.X == secondSlot.X, $"freed range not reused ({secondSlot} -> {thirdSlot})");
        Check(thirdSlot.Y != secondSlot.Y, "reallocated range kept its tag");
        Check(_grass.GetHazeSlot(second) == new Vector2(-1, 0), "removed batch still reports a slot");
        _log.Add($"slots {first}/{secondSlot}/{thirdSlot}");
    }

    private static RingAtmosphereRayFrame Frame(Vector3 camera)
        => new(true, camera, camera.Y, new Vector3(0, -1, 0), new Vector3(0, 0, 1), 200000f, 300000f, 1f, new Vector3(.35f, .8f, .25f).Normalized());

    private async Task CompareAll(Probe[] probes, RingVegetationHazeFrame frame, string label)
    {
        foreach (Probe probe in probes)
        {
            Check(probe.Gpu.GetHazeSlot(probe.Batch).X >= 0, probe.Name + " lost its slot");
            (byte[] atlas, int rows) = await ReadAtlas(probe.Gpu);
            Check(atlas.Length >= rows * RingVegetationHaze.AtlasWidth * 8 && rows > 0, $"{probe.Name}: atlas readback {atlas.Length} bytes, {rows} rows");
            await Compare(probe, atlas, rows, frame, label);
        }
    }

    private async Task Compare(Probe probe, byte[] atlas, int rows, RingVegetationHazeFrame frame, string label)
    {
        Vector2 slot = probe.Slot;
        int count = probe.Count, baseSlot = (int)slot.X;
        float tag = slot.Y;
        var input = new float[count * 5 * 4];
        Vector3 offsetA = probe.L2W.Basis * probe.SpreadA, offsetB = probe.L2W.Basis * probe.SpreadB;
        for (int i = 0; i < count; i++)
        {
            int s = i * probe.Stride;
            Vector3 root = new(probe.Rows[s + 3], probe.Rows[s + 7], probe.Rows[s + 11]);
            Vector3 x = new(probe.Rows[s], probe.Rows[s + 4], probe.Rows[s + 8]), y = new(probe.Rows[s + 1], probe.Rows[s + 5], probe.Rows[s + 9]),
                z = new(probe.Rows[s + 2], probe.Rows[s + 6], probe.Rows[s + 10]);
            Vector3 world = probe.L2W * (root + x * probe.MeshAnchor.X + y * probe.MeshAnchor.Y + z * probe.MeshAnchor.Z);
            int slotIndex = baseSlot + i;
            int texel = (slotIndex % RingVegetationHaze.SlotsPerRow) * 2, row = slotIndex / RingVegetationHaze.SlotsPerRow;
            Check(row < rows, $"{probe.Name}: slot row {row} beyond atlas {rows}");
            Vector4 t0 = Texel(atlas, texel, row), t1 = Texel(atlas, texel + 1, row);
            Check(MathF.Abs(t0.W - tag) < .5f, $"{label}/{probe.Name} row {i}: tag {t0.W} != {tag}");
            Check(t1.W > .5f, $"{label}/{probe.Name} row {i}: texel1.w {t1.W}");
            int o = i * 20;
            Put(input, o, world, 0); Put(input, o + 4, t0); Put(input, o + 8, t1); Put(input, o + 12, world + offsetA, 0); Put(input, o + 16, world + offsetB, 0);
        }
        float[] result = await RunReference(frame.Parameters, input, count);
        float maxT = 0, maxSun = 0, maxAmb = 0, spreadT = 0, spreadS = 0;
        for (int i = 0; i < count; i++)
        {
            int o = i * 40;
            for (int c = 0; c < 3; c++)
            {
                float tr = result[o + c], ta = result[o + 12 + c];
                float sr = result[o + 4 + c], sa = result[o + 16 + c], ar = result[o + 8 + c], aa = result[o + 20 + c];
                float dT = MathF.Abs(ta - tr), dSun = MathF.Abs(sa - sr), dAmb = MathF.Abs(aa - ar);
                float limSun = 3e-3f * MathF.Max(MathF.Abs(sr), 1e-3f) + 1e-6f, limAmb = 3e-3f * MathF.Max(MathF.Abs(ar), 1e-3f) + 1e-6f;
                Check(dT <= 1e-3f, $"{label}/{probe.Name} row {i} ch{c}: T {ta} vs {tr}");
                Check(dSun <= limSun, $"{label}/{probe.Name} row {i} ch{c}: S_sun {sa} vs {sr}");
                Check(dAmb <= limAmb, $"{label}/{probe.Name} row {i} ch{c}: S_amb {aa} vs {ar}");
                maxT = MathF.Max(maxT, dT); maxSun = MathF.Max(maxSun, dSun / limSun); maxAmb = MathF.Max(maxAmb, dAmb / limAmb);
                float sTotal = sr + ar;
                foreach (int v in new[] { 24, 32 })
                {
                    spreadT = MathF.Max(spreadT, MathF.Abs(result[o + v + c] - tr));
                    spreadS = MathF.Max(spreadS, MathF.Abs(result[o + v + 4 + c] - sTotal) / MathF.Max(sTotal, 1e-5f));
                }
            }
        }
        _log.Add($"{label}/{probe.Name} n={count} maxdT={maxT:G3} S_sun={maxSun:G2}x S_amb={maxAmb:G2}x of limit; per-vertex spread dT={spreadT:G3} relS={spreadS:G3}");
    }

    private static Vector4 Texel(byte[] atlas, int x, int row)
    {
        int at = (row * RingVegetationHaze.AtlasWidth + x) * 8;
        return new Vector4((float)BitConverter.ToHalf(atlas, at), (float)BitConverter.ToHalf(atlas, at + 2),
            (float)BitConverter.ToHalf(atlas, at + 4), (float)BitConverter.ToHalf(atlas, at + 6));
    }

    private static void Put(float[] target, int at, Vector3 v, float w) { target[at] = v.X; target[at + 1] = v.Y; target[at + 2] = v.Z; target[at + 3] = w; }
    private static void Put(float[] target, int at, Vector4 v) { target[at] = v.X; target[at + 1] = v.Y; target[at + 2] = v.Z; target[at + 3] = v.W; }

    // ---- reference probe ----

    private const string ProbeSource = @"#version 450
layout(local_size_x = 64) in;
layout(set = 0, binding = 0, std140) uniform Frame {
    vec4 sigma_R_H_R; vec4 aerosol; vec4 mist; vec4 geometry; vec4 sides; vec4 outward; vec4 axis; vec4 to_sun; vec4 camera; vec4 storm;
    vec4 e_sun; vec4 ambient; vec4 night_ambient; vec4 day;
} f;
layout(set = 0, binding = 1, std430) readonly buffer Inputs { vec4 inp[]; };
layout(set = 0, binding = 2, std430) writeonly buffer Outputs { vec4 outp[]; };
#include ""res://materials/ring_atmosphere_shared.gdshaderinc""
#include ""res://materials/ring_haze_shared.gdshaderinc""
#include ""res://materials/atmosphere/ring_vegetation_haze_terms.gdshaderinc""
RingHaze reference(RingHazeParams p_storm, RingHazeParams p, vec3 target) {
    return ring_haze_eval_target_split(p_storm, p, p.to_sun, 0.0, 0.0, 1.0, target, f.camera.xyz, f.geometry.z, f.outward.xyz, f.axis.xyz,
        f.sides.x, f.sides.y, 1.0, f.geometry.x, f.geometry.y);
}
void main() {
    uint i = gl_GlobalInvocationID.x;
    if (i >= uint(f.day.y + 0.5)) return;
    RingHazeParams p = ring_haze_params_default();
    p.sigma_R = f.sigma_R_H_R.xyz; p.H_R = f.sigma_R_H_R.w;
    p.sigma_A = f.aerosol.x; p.omega_A = f.aerosol.y; p.H_A = f.aerosol.z; p.sigma_M0 = f.aerosol.w;
    p.omega_M = f.mist.x; p.H_mist = f.mist.y; p.cap_height = f.mist.z; p.g = f.mist.w;
    p.quality = int(f.geometry.w + 0.5); p.to_sun = normalize(f.to_sun.xyz);
    p.E_sun = f.e_sun.xyz; p.ambient = f.ambient.xyz; p.night_ambient = f.night_ambient.xyz; p.day = f.day.x;
    RingHazeParams p_storm = ring_haze_apply_storm(p, f.storm);
    uint a = i * 5u, o = i * 10u;
    RingHaze r = reference(p_storm, p, inp[a].xyz);
    RingHaze s = ring_vegetation_haze_assemble(p_storm, ring_vegetation_haze_unpack(inp[a + 1u], inp[a + 2u]), 1.0);
    RingHaze ra = reference(p_storm, p, inp[a + 3u].xyz);
    RingHaze rb = reference(p_storm, p, inp[a + 4u].xyz);
    outp[o] = vec4(r.T, 0.0); outp[o + 1u] = vec4(r.S_sun, 0.0); outp[o + 2u] = vec4(r.S_amb, 0.0);
    outp[o + 3u] = vec4(s.T, 0.0); outp[o + 4u] = vec4(s.S_sun, 0.0); outp[o + 5u] = vec4(s.S_amb, 0.0);
    outp[o + 6u] = vec4(ra.T, 0.0); outp[o + 7u] = vec4(ra.S_sun + ra.S_amb, 0.0);
    outp[o + 8u] = vec4(rb.T, 0.0); outp[o + 9u] = vec4(rb.S_sun + rb.S_amb, 0.0);
}
";

    /// <summary>RDShaderSource has no include support: paste each #include line's file (none of the three nests further includes).</summary>
    private static string ExpandSource(string source)
    {
        var builder = new System.Text.StringBuilder();
        foreach (string line in source.Split('\n'))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("#include", StringComparison.Ordinal)) { builder.Append(line.TrimEnd('\r')).Append('\n'); continue; }
            string path = trimmed.Substring(trimmed.IndexOf('"') + 1, trimmed.LastIndexOf('"') - trimmed.IndexOf('"') - 1);
            string text = RingAtmosphereLuts.ExpandIncludes(path);
            if (text.Length == 0) throw new InvalidOperationException("include missing: " + path);
            builder.Append(text);
        }
        return builder.ToString();
    }

    /// <summary>Returns 40 floats per probe: reference T, S_sun, S_amb; assembled T, S_sun, S_amb; spread A (T, S); spread B (T, S); each as a padded vec4.</summary>
    private async Task<float[]> RunReference(float[] frameParameters, float[] input, int count)
    {
        string source = ExpandSource(ProbeSource);
        var uniform = new float[56];
        Array.Copy(frameParameters, uniform, RingVegetationHaze.ParameterFloats);
        Put(uniform, 40, LightSun, 0); Put(uniform, 44, LightAmbient, 0); Put(uniform, 48, LightNight, 0);
        uniform[52] = LightDay; uniform[53] = count;
        byte[] uniformBytes = Bytes(uniform), inputBytes = Bytes(input);
        var done = new TaskCompletionSource<float[]>();
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            float[] result = Array.Empty<float>(); string error = "";
            RenderingDevice? rd = RenderingServer.GetRenderingDevice();
            Rid shader = default, ubo = default, inputs = default, outputs = default, set = default, pipeline = default;
            try
            {
                if (rd is null) throw new InvalidOperationException("no RenderingDevice");
                using var shaderSource = new RDShaderSource { SourceCompute = source };
                using RDShaderSpirV spirv = rd.ShaderCompileSpirVFromSource(shaderSource);
                if (!string.IsNullOrWhiteSpace(spirv.CompileErrorCompute)) throw new InvalidOperationException("probe shader: " + spirv.CompileErrorCompute);
                shader = rd.ShaderCreateFromSpirV(spirv);
                ubo = rd.UniformBufferCreate((uint)uniformBytes.Length, uniformBytes);
                inputs = rd.StorageBufferCreate((uint)inputBytes.Length, inputBytes);
                outputs = rd.StorageBufferCreate((uint)(count * 10 * 16));
                var uniforms = new Godot.Collections.Array<RDUniform>();
                (RenderingDevice.UniformType, Rid)[] bindings =
                    { (RenderingDevice.UniformType.UniformBuffer, ubo), (RenderingDevice.UniformType.StorageBuffer, inputs), (RenderingDevice.UniformType.StorageBuffer, outputs) };
                for (int b = 0; b < bindings.Length; b++)
                {
                    var u = new RDUniform { UniformType = bindings[b].Item1, Binding = b };
                    u.AddId(bindings[b].Item2);
                    uniforms.Add(u);
                }
                set = rd.UniformSetCreate(uniforms, shader, 0);
                pipeline = rd.ComputePipelineCreate(shader);
                long list = rd.ComputeListBegin();
                rd.ComputeListBindComputePipeline(list, pipeline);
                rd.ComputeListBindUniformSet(list, set, 0);
                rd.ComputeListDispatch(list, (uint)((count + 63) / 64), 1, 1);
                rd.ComputeListEnd();
                byte[] data = rd.BufferGetData(outputs);
                result = new float[data.Length / 4];
                Buffer.BlockCopy(data, 0, result, 0, data.Length);
            }
            catch (Exception e) { error = e.Message; }
            if (rd is not null)
            {
                if (set.IsValid && rd.UniformSetIsValid(set)) rd.FreeRid(set);
                foreach (Rid rid in new[] { pipeline, outputs, inputs, ubo, shader }) if (rid.IsValid) rd.FreeRid(rid);
            }
            Callable.From(() =>
            {
                if (error.Length > 0) done.TrySetException(new InvalidOperationException(error)); else done.TrySetResult(result);
            }).CallDeferred();
        }));
        for (int i = 0; i < 600 && !done.Task.IsCompleted; i++) await Frames(1);
        Check(done.Task.IsCompleted, "reference probe timeout");
        return await done.Task;
    }

    // ---- data ----

    private static float[] GrassRows(Random random, int count)
    {
        var rows = new float[count * 20];
        for (int i = 0; i < count; i++)
        {
            int s = i * 20;
            rows[s] = .5f; rows[s + 3] = CameraBase.X + (float)(random.NextDouble() * 800 - 400);
            rows[s + 5] = .6f; rows[s + 7] = (float)(random.NextDouble() * 80 - 40);
            rows[s + 10] = .5f; rows[s + 11] = CameraBase.Z + (float)(random.NextDouble() * 800 - 400);
            rows[s + 12] = 1f; // colour (1,0,0,0)
            rows[s + 16] = .5f; rows[s + 17] = .5f;
        }
        return rows;
    }

    private static float[] TreeRows(Random random, int count)
    {
        var rows = new float[count * 12];
        for (int i = 0; i < count; i++)
        {
            int s = i * 12;
            float scale = 1f + (float)random.NextDouble(), yaw = (float)(random.NextDouble() * Math.Tau);
            float cos = MathF.Cos(yaw) * scale, sin = MathF.Sin(yaw) * scale;
            rows[s] = cos; rows[s + 2] = sin; rows[s + 3] = (float)(random.NextDouble() * 16000 - 8000);
            rows[s + 5] = scale; rows[s + 7] = (float)(random.NextDouble() * 80 - 40);
            rows[s + 8] = -sin; rows[s + 10] = cos; rows[s + 11] = (float)(random.NextDouble() * 16000 - 8000);
        }
        return rows;
    }

    private static ArrayMesh MakeQuad()
    {
        var mesh = new ArrayMesh(); var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new[] { new Vector3(-.5f, 0, 0), new Vector3(.5f, 0, 0), new Vector3(0, 1, 0) };
        arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2 }; mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays); return mesh;
    }

    private static ArrayMesh MakeBox()
    {
        var mesh = new ArrayMesh(); var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max);
        var vertices = new Vector3[8];
        for (int i = 0; i < 8; i++) vertices[i] = new Vector3((i & 1) == 0 ? -2 : 2, (i & 2) == 0 ? 0 : 20, (i & 4) == 0 ? -2 : 2);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3 };
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays); return mesh;
    }

    private static byte[] Bytes(float[] data) { var bytes = new byte[data.Length * 4]; Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length); return bytes; }

    // ---- waiting / readback ----

    private async Task WaitReady(RingGpuVegetationCompute gpu)
    {
        for (int i = 0; i < 300; i++) { if (gpu.Ready) return; if (gpu.Failed) throw new InvalidOperationException("compute failed: " + gpu.Status); await Frames(1); }
        throw new TimeoutException("compute readiness: " + gpu.Status);
    }

    private async Task WaitBatch(Probe probe)
    {
        for (int i = 0; i < 300; i++)
        {
            if (probe.Gpu.IsBatchReady(probe.Batch)) return;
            if (probe.Gpu.Failed) throw new InvalidOperationException("compute failed: " + probe.Gpu.Status);
            await Frames(1);
        }
        throw new TimeoutException(probe.Name + " batch readiness");
    }

    private async Task<(uint, float[])> ReadRows(Probe probe)
    {
        var done = new TaskCompletionSource<(uint, float[])>();
        probe.Gpu.RequestReadbackForTests(probe.Batch, (commands, instances) => done.TrySetResult((commands.Length >= 2 ? commands[1] : 0, instances)));
        for (int i = 0; i < 300 && !done.Task.IsCompleted; i++) await Frames(1);
        Check(done.Task.IsCompleted, probe.Name + " row readback timeout");
        return await done.Task;
    }

    private async Task<(byte[], int)> ReadAtlas(RingGpuVegetationCompute gpu)
    {
        var done = new TaskCompletionSource<(byte[], int)>();
        gpu.RequestHazeAtlasReadbackForTests((bytes, rows) => done.TrySetResult((bytes, rows)));
        for (int i = 0; i < 300 && !done.Task.IsCompleted; i++) await Frames(1);
        Check(done.Task.IsCompleted, "atlas readback timeout");
        return await done.Task;
    }

    private async Task Frames(int count) { for (int i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
