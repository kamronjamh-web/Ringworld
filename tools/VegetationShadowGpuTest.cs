using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Atmosphere;
using RingworldTechDemo.Shadows;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tests;

/// <summary>Per-instance vegetation shadows (docs/shadow-revamp-plan.md S7b), end to end on the GPU. A synthetic heightfield (RG16F layers written into the
/// ringworld_height_shadow_* globals) and a synthetic cloud cascade pair drive the real vegetation compute (grass and tree channels); the shadow atlas it writes is read
/// back and the reader math (materials/ring_vegetation_shadow_read.gdshaderinc) is compared with the exact per-vertex code (ring_height_shadow_vertex_term,
/// sample_local_cloud_shadow) by a canvas probe: mean abs visibility difference &lt;= 0.02, max &lt;= 0.1, sun and planet layers. Stale tags and light mismatches must
/// select the per-vertex fallback. The GLSL twin (ring_vegetation_shadow_terms.glslinc) is compared with the Godot includes on &gt;= 64 points (err &lt;= 1e-3).
/// Run windowed: godot --path . --script res://tools/vegetation_shadow_gpu_test.gd</summary>
public partial class VegetationShadowGpuTest : Node
{
    private const float RingRadius = 5_000_000f;
    private static readonly Vector3 CameraWorld = new(0f, 60f, 0f);
    private static readonly Vector3 SunDirection = new Vector3(.3f, .9f, .1f).Normalized();
    private static readonly Vector3 PlanetDirection = new Vector3(-.5f, .55f, .2f).Normalized();
    private static readonly Vector4 EmptyBounds = new(1e9f, -1e9f, 1e9f, -1e9f);
    public string Result { get; private set; } = "RUNNING";
    private readonly List<string> _log = new();
    private RingGpuVegetationCompute? _grass, _trees;
    private Texture2DArray? _l0, _l1;
    private ImageTexture? _nearCloud, _farCloud;
    private SubViewport? _probeViewport;
    private ColorRect? _probeRect;
    private ShaderMaterial? _probeMaterial;
    private Vector4 _state = new(0, -1, 1, 1);
    private Vector4 _light;

    private sealed class Probe
    {
        public required string Name;
        public required RingGpuVegetationCompute Gpu;
        public required int Batch;
        public required float[] Rows;
        public required int Stride;
        public required Transform3D L2W;
        public bool Grass;
        public int Count => Rows.Length / Stride;
        public Vector2 Slot => Gpu.GetHazeSlot(Batch);
    }

    public async void Start()
    {
        bool oldEnabled = RingVegetationShadow.Enabled;
        try
        {
            await Run();
            Result = "PASS " + string.Join("; ", _log);
        }
        catch (Exception error) { Result = "FAIL " + error.Message; }
        finally
        {
            RingVegetationShadow.Enabled = oldEnabled;
            _grass?.Dispose(); _trees?.Dispose();
            await Frames(3);
        }
    }

    // ------------------------------------------------------------------ synthetic fields

    private static float FieldR(float x, float z) => 4f + 14f * MathF.Sin(x / 420f) * MathF.Cos(z / 360f) + 2f * MathF.Sin((x + z) / 150f);
    private static float FieldG(float x, float z) { float g = 320f + 80f * MathF.Cos(x / 500f); return x > 700f ? -g : g; }

    private static Texture2DArray MakeArray(int texel, float layerOffsetStep)
    {
        var layers = new Godot.Collections.Array<Image>();
        for (int layer = 0; layer < 3; layer++)
        {
            var bytes = new byte[1024 * 1024 * 4];
            for (int j = 0; j < 1024; j++)
                for (int i = 0; i < 1024; i++)
                {
                    float x = (i < 512 ? i : i - 1024) * texel, z = (j < 512 ? j : j - 1024) * texel;
                    int at = (j * 1024 + i) * 4;
                    BitConverter.TryWriteBytes(bytes.AsSpan(at, 2), (Half)(FieldR(x, z) + layer * layerOffsetStep));
                    BitConverter.TryWriteBytes(bytes.AsSpan(at + 2, 2), (Half)FieldG(x, z));
                }
            layers.Add(Image.CreateFromData(1024, 1024, false, Image.Format.Rgh, bytes));
        }
        var array = new Texture2DArray();
        Error error = array.CreateFromImages(layers);
        if (error != Error.Ok) throw new InvalidOperationException("Texture2DArray.CreateFromImages failed: " + error);
        return array;
    }

    private static ImageTexture MakeCloud(int size, float phase)
    {
        var bytes = new byte[size * size * 8];
        for (int j = 0; j < size; j++)
            for (int i = 0; i < size; i++)
            {
                float u = i / (float)(size - 1), v = j / (float)(size - 1);
                float r = Math.Clamp(.5f + .5f * MathF.Sin(u * 11f + phase) * MathF.Cos(v * 8f), 0f, 1f);
                float g = Math.Clamp(.35f * (.5f + .5f * MathF.Sin(u * 5f - v * 7f + phase)), 0f, 1f);
                BitConverter.TryWriteBytes(bytes.AsSpan((j * size + i) * 8, 4), r);
                BitConverter.TryWriteBytes(bytes.AsSpan((j * size + i) * 8 + 4, 4), g);
            }
        return ImageTexture.CreateFromImage(Image.CreateFromData(size, size, false, Image.Format.Rgf, bytes));
    }

    private void SetHeightGlobals(float front, float prev, float t, float lightId, Vector3 lightDirection)
    {
        var frame = new Projection(new Vector4(1, 0, 0, 0), new Vector4(0, 0, 1, 0), new Vector4(0, 1, 0, 0), new Vector4(0, 0, 0, 1));
        _state = new Vector4(front, prev, t, lightId);
        _light = new Vector4(lightDirection.X, lightDirection.Y, lightDirection.Z, 1f);
        RenderingServer.GlobalShaderParameterSet("ringworld_height_shadow_l0", _l0!);
        RenderingServer.GlobalShaderParameterSet("ringworld_height_shadow_l1", _l1!);
        RenderingServer.GlobalShaderParameterSet("ringworld_height_shadow_frame", frame);
        RenderingServer.GlobalShaderParameterSet("ringworld_height_shadow_l0_params", new Vector4(4f, 1f / 4096f, 1f, 0f));
        RenderingServer.GlobalShaderParameterSet("ringworld_height_shadow_l1_params", new Vector4(16f, 1f / 16384f, 1f, 0f));
        RenderingServer.GlobalShaderParameterSet("ringworld_height_shadow_l0_bounds", new Vector4(-2048f, 2044f, -2048f, 2044f));
        RenderingServer.GlobalShaderParameterSet("ringworld_height_shadow_l0_prev_bounds", new Vector4(-2048f, 2044f, -2048f, 2044f));
        RenderingServer.GlobalShaderParameterSet("ringworld_height_shadow_l1_bounds", new Vector4(-8192f, 8176f, -8192f, 8176f));
        RenderingServer.GlobalShaderParameterSet("ringworld_height_shadow_l1_prev_bounds", EmptyBounds);
        RenderingServer.GlobalShaderParameterSet("ringworld_height_shadow_state", _state);
        RenderingServer.GlobalShaderParameterSet("ringworld_height_shadow_light", _light);
        RenderingServer.GlobalShaderParameterSet("ringworld_height_shadow_shape", Shape);
        RenderingServer.GlobalShaderParameterSet("ringworld_hub_light_weight", 0f);
    }

    private static readonly Vector4 Shape = new(MathF.Tan(.5f * MathF.PI / 180f), .5f, RingRadius, 0f);

    private RingTerrainHeightShadow.GlobalsSnapshot HeightSnapshot()
    {
        var frame = new Projection(new Vector4(1, 0, 0, 0), new Vector4(0, 0, 1, 0), new Vector4(0, 1, 0, 0), new Vector4(0, 0, 0, 1));
        return new RingTerrainHeightShadow.GlobalsSnapshot(frame, new Vector4(4f, 1f / 4096f, 1f, 0f), new Vector4(16f, 1f / 16384f, 1f, 0f),
            new Vector4(-2048f, 2044f, -2048f, 2044f), new Vector4(-2048f, 2044f, -2048f, 2044f), new Vector4(-8192f, 8176f, -8192f, 8176f), EmptyBounds,
            _state, Shape, _light, RenderingServer.TextureGetRdTexture(_l0!.GetRid()), RenderingServer.TextureGetRdTexture(_l1!.GetRid()));
    }

    private RingVegetationShadowFrame MakeFrame() => RingVegetationShadow.Build(true, CameraWorld, HeightSnapshot(), RingCloudShadowProjector.GetVegetationFrame());

    // ------------------------------------------------------------------ run

    private async Task Run()
    {
        var random = new Random(11);
        _l0 = MakeArray(4, .8f); _l1 = MakeArray(16, .8f);
        _nearCloud = MakeCloud(256, 0f); _farCloud = MakeCloud(128, 1.3f);
        SetHeightGlobals(0, -1, 1, 1, SunDirection);
        RingCloudShadowProjector.UpdateCascades(_nearCloud, Vector3.Zero, Vector3.Right, Vector3.Back, Vector3.Down, 40000f, new Vector2(.003f, -.002f),
            _farCloud, Vector3.Zero, Vector3.Right, Vector3.Back, Vector3.Down, 96000f, new Vector2(-.001f, .002f), RingRadius, SunDirection, .8f);
        BuildProbe();
        RingVegetationShadow.Enabled = true;
        Check(RingCloudShadowProjector.GetVegetationFrame().Enabled, "cloud frame not enabled");
        { float[] sanity = await RunProbe(9, new List<Vector4> { new(250f, 0, 0, 0) }, new List<Vector4> { default }, new List<Vector4> { default }, new List<Vector4> { default }, 0f); Check(MathF.Abs(sanity[0] - .25f) < .01f && MathF.Abs(sanity[1] - .5f) < .01f, $"probe sanity {sanity[0]} {sanity[1]} {sanity[2]} {sanity[3]}"); }

        Mesh quad = MakeQuad(), box = MakeBox();
        var l2w = new Transform3D(new Basis(Vector3.Up, .4f), new Vector3(5f, 0f, -7f));
        var bounds = new Aabb(new Vector3(-20000, -200, -20000), new Vector3(40000, 600, 40000));
        _grass = new RingGpuVegetationCompute { HazeChannel = RingVegetationHaze.Channel.Grass };
        _trees = new RingGpuVegetationCompute { HazeChannel = RingVegetationHaze.Channel.Trees };
        float[] grassRows = GrassRows(random, 2048), treeRows = TreeRows(random, 256), smallRows = TreeRows(random, 40);
        int grassId = _grass.RegisterBatch(new RingGpuVegetationCompute.BatchRegistration("vs-grass", new MultiMesh(), quad, grassRows, null, 2048, bounds, true));
        int treeId = _trees.RegisterBatch(new RingGpuVegetationCompute.BatchRegistration("vs-tree", new MultiMesh(), box, treeRows, null, 256, bounds, false));
        int smallId = _trees.RegisterBatch(new RingGpuVegetationCompute.BatchRegistration("vs-tree-small", new MultiMesh(), box, smallRows, null, 40, bounds, false));
        Check(grassId >= 0 && treeId >= 0 && smallId >= 0, "registration");
        var probes = new[]
        {
            new Probe { Name = "grass", Gpu = _grass, Batch = grassId, Rows = grassRows, Stride = 20, L2W = l2w, Grass = true },
            new Probe { Name = "tree", Gpu = _trees, Batch = treeId, Rows = treeRows, Stride = 12, L2W = l2w },
            new Probe { Name = "tree-small", Gpu = _trees, Batch = smallId, Rows = smallRows, Stride = 12, L2W = l2w },
        };
        _grass.Initialize(); _trees.Initialize();
        await WaitReady(_grass); await WaitReady(_trees);

        // Toggle off first: no atlas, channels not live, nothing published.
        RingVegetationShadow.Enabled = false;
        _grass.RequestShadow(RingVegetationShadow.Build(false, CameraWorld, HeightSnapshot(), default));
        await Frames(2);
        Check(!_grass.ShadowAtlasReady && RingVegetationShadow.State.X == 0f && RingVegetationShadow.State.Y == 0f, "channels live with the toggle off");
        RingVegetationShadow.Enabled = true;

        var treeBudgets = new uint[] { 1024, 1024, 1024, 1024, 1024 };
        _grass.RequestFrame(new RingGpuVegetationCompute.FrameRequest(CameraWorld, Array.Empty<uint>(),
            new List<RingGpuVegetationCompute.BatchFrameParameters> { new(grassId, l2w, true, 0, 0, 1e6f, 1, 1, 1, false, 1, false, null, 0) }));
        _trees.RequestFrame(new RingGpuVegetationCompute.FrameRequest(CameraWorld, treeBudgets, new List<RingGpuVegetationCompute.BatchFrameParameters>
            { new(treeId, l2w, false, 0, 0, 1e6f, 1, 1, 1, false, 1, false, null, 0), new(smallId, l2w, false, 0, 0, 1e6f, 1, 1, 1, false, 1, false, null, 0) }));
        foreach (Probe probe in probes) await WaitBatch(probe);
        await PumpShadow(6);
        Check(RingVegetationShadow.State.X == 1f && RingVegetationShadow.State.Y == 1f, $"channels not live: {RingVegetationShadow.State}");
        foreach (Probe probe in probes)
        {
            Vector2 slot = probe.Slot;
            Check(slot.X >= 0 && slot.Y is >= 2 and <= 2047, $"{probe.Name}: slot {slot}");
        }

        await CompareAll(probes, "sun", 1);

        // Stale tag and light mismatch select the per-vertex fallback.
        await FallbackChecks(probes[1]);

        // Mid-crossfade (front 0, previous 1, t 0.4): refresh runs every frame while crossfading.
        long refreshes = _trees.ShadowRefreshCount;
        SetHeightGlobals(0, 1, .4f, 1, SunDirection);
        await PumpShadow(4);
        Check(_trees.ShadowRefreshCount >= refreshes + 3, $"crossfade did not refresh every frame ({_trees.ShadowRefreshCount - refreshes})");
        await CompareAll(probes, "crossfade", 1);

        // Planet layer (state.w = 2): rows rewritten with light id 2.
        SetHeightGlobals(0, -1, 1, 2, PlanetDirection);
        refreshes = _trees.ShadowRefreshCount;
        await PumpShadow(4);
        Check(_trees.ShadowRefreshCount > refreshes, "planet switch did not refresh");
        await CompareAll(probes, "planet", 2);
        SetHeightGlobals(0, -1, 1, 1, SunDirection);
        await PumpShadow(4);

        await ParityTwin();

        // Camera move refresh, then toggle off again frees the atlas and drops the channels.
        refreshes = _grass.ShadowRefreshCount;
        RingVegetationShadowFrame moved = RingVegetationShadow.Build(true, CameraWorld + new Vector3(20, 0, 0), HeightSnapshot(), RingCloudShadowProjector.GetVegetationFrame());
        _grass.RequestShadow(moved);
        await Frames(3);
        Check(_grass.ShadowRefreshCount > refreshes, "camera move did not refresh");
        RingVegetationShadow.Enabled = false;
        _grass.RequestShadow(RingVegetationShadow.Build(false, CameraWorld, HeightSnapshot(), default));
        _trees.RequestShadow(RingVegetationShadow.Build(false, CameraWorld, HeightSnapshot(), default));
        await Frames(3);
        Check(!_grass.ShadowAtlasReady && !_trees.ShadowAtlasReady && RingVegetationShadow.State.X == 0f && RingVegetationShadow.State.Y == 0f, "toggle off left a channel live");
        _log.Add("toggle ok");
    }

    private async Task PumpShadow(int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            RingVegetationShadowFrame frame = MakeFrame();
            _grass!.RequestShadow(frame); _trees!.RequestShadow(frame);
            await Frames(1);
        }
        await Frames(2);
    }

    // ------------------------------------------------------------------ comparisons

    private async Task CompareAll(Probe[] probes, string label, int lightId)
    {
        foreach (Probe probe in probes)
        {
            (byte[] atlas, int rows) = await ReadAtlas(probe.Gpu);
            Check(rows > 0 && atlas.Length >= rows * RingVegetationHaze.AtlasWidth * 8, $"{probe.Name}: shadow atlas readback {atlas.Length} bytes, {rows} rows");
            await Compare(probe, atlas, rows, label, lightId);
        }
    }

    private static Vector3 Row(float[] r, int s, int a) => new(r[s + a], r[s + a + 4], r[s + a + 8]);

    private async Task Compare(Probe probe, byte[] atlas, int rows, string label, int lightId)
    {
        Vector2 slot = probe.Slot;
        float tag = slot.Y;
        var heights = probe.Grass ? new[] { 0f } : new[] { 0f, 4f, 10f, 20f, 30f };
        var offsets = new[] { new Vector2(0, 0), new Vector2(1.5f, -1.5f) };
        var worlds = new List<Vector4>(); var roots = new List<Vector4>(); var t0s = new List<Vector4>(); var t1s = new List<Vector4>(); var slopes = new List<float>();
        for (int i = 0; i < probe.Count; i++)
        {
            int s = i * probe.Stride;
            Vector3 rootLocal = Row(probe.Rows, s, 3), x = Row(probe.Rows, s, 0), y = Row(probe.Rows, s, 1), z = Row(probe.Rows, s, 2);
            Vector3 root = probe.L2W * rootLocal;
            float slope = 0f;
            if (probe.Grass)
            {
                Vector3 n = (probe.L2W.Basis * z).Cross(probe.L2W.Basis * x);
                if (n.Dot(probe.L2W.Basis * y) < 0) n = -n;
                float cos = Math.Clamp(n.Normalized().Dot(Vector3.Up), .05f, 1f);
                slope = MathF.Sqrt(MathF.Max(1f - cos * cos, 0f)) / cos;
            }
            int slotIndex = (int)slot.X + i;
            int texel = (slotIndex % RingVegetationHaze.SlotsPerRow) * 2, row = slotIndex / RingVegetationHaze.SlotsPerRow;
            Check(row < rows, $"{probe.Name}: slot row {row} beyond atlas {rows}");
            Vector4 t0 = Texel(atlas, texel, row), t1 = Texel(atlas, texel + 1, row);
            Check(MathF.Abs(t0.W - tag) < .5f, $"{label}/{probe.Name} row {i}: tag {t0.W} != {tag}");
            Check(t1.W > .5f, $"{label}/{probe.Name} row {i}: texel1.w {t1.W}");
            Check(MathF.Abs(t1.Z - lightId) < .5f, $"{label}/{probe.Name} row {i}: light id {t1.Z} != {lightId}");
            foreach (float h in heights)
                foreach (Vector2 o in offsets)
                {
                    worlds.Add(new Vector4(root.X + o.X, root.Y + h, root.Z + o.Y, h)); roots.Add(new Vector4(root.X, root.Y, root.Z, slope));
                    t0s.Add(t0); t1s.Add(t1); slopes.Add(slope);
                }
        }
        float[] height = await RunProbe(0, worlds, roots, t0s, t1s, tag);
        float[] cloud = await RunProbe(1, worlds, roots, t0s, t1s, tag);
        double hMean = 0, cMean = 0; float hMax = 0, cMax = 0; int lit = 0, dark = 0;
        for (int i = 0; i < worlds.Count; i++)
        {
            Check(height[i * 4 + 2] > .5f && cloud[i * 4 + 2] > .5f, $"{label}/{probe.Name} point {i}: instance data reported unusable (height {height[i * 4]},{height[i * 4 + 1]},{height[i * 4 + 2]}; cloud {cloud[i * 4]},{cloud[i * 4 + 1]},{cloud[i * 4 + 2]}; t0 {t0s[i]} t1 {t1s[i]} tag {tag})");
            float dh = MathF.Abs(height[i * 4] - height[i * 4 + 1]), dc = MathF.Abs(cloud[i * 4] - cloud[i * 4 + 1]);
            hMean += dh; cMean += dc; hMax = MathF.Max(hMax, dh); cMax = MathF.Max(cMax, dc);
            if (height[i * 4] > .95f) lit++; else if (height[i * 4] < .05f) dark++;
        }
        hMean /= worlds.Count; cMean /= worlds.Count;
        if (!probe.Grass) Check(hMean <= .02 && hMax <= .1f, $"{label}/{probe.Name}: height term mean {hMean:G3} max {hMax:G3} vs per-vertex");
        Check(cMean <= .02 && cMax <= .1f, $"{label}/{probe.Name}: cloud term mean {cMean:G3} max {cMax:G3} vs per-vertex");
        if (probe.Grass)
        {
            float[] ground = await RunProbe(2, worlds, roots, t0s, t1s, tag), instance = await RunProbe(5, worlds, roots, t0s, t1s, tag);
            double gMean = 0; float gMax = 0;
            for (int i = 0; i < worlds.Count; i++)
                for (int c = 0; c < 3; c++) { float d = MathF.Abs(ground[i * 4 + c] - instance[i * 4 + c]); gMean += d / 3.0; gMax = MathF.Max(gMax, d); }
            gMean /= worlds.Count;
            Check(gMean <= .02 && gMax <= .1f, $"{label}/{probe.Name}: ground sample mean {gMean:G3} max {gMax:G3} (visibility, weight, unknown)");
            int gLit = 0, gDark = 0;
            for (int i = 0; i < worlds.Count; i++) { if (ground[i * 4] > .95f) gLit++; else if (ground[i * 4] < .05f) gDark++; }
            if (label == "sun") Check(gLit > 0 && gDark > 0, $"grass ground field does not exercise both lit and shadowed (lit {gLit}, dark {gDark})");
            _log.Add($"{label}/{probe.Name} ground mean={gMean:G2} max={gMax:G2} (lit {gLit}, dark {gDark})");
        }
        _log.Add($"{label}/{probe.Name} n={worlds.Count} height mean={hMean:G2} max={hMax:G2} cloud mean={cMean:G2} max={cMax:G2} (lit {lit}, dark {dark})");
        if (label == "sun" && !probe.Grass) Check(lit > 0 && dark > 0, $"{probe.Name}: synthetic field does not exercise both lit and shadowed points (lit {lit}, dark {dark})");
    }

    private async Task FallbackChecks(Probe tree)
    {
        (byte[] atlas, int rows) = await ReadAtlas(tree.Gpu);
        Vector2 slot = tree.Slot;
        var worlds = new List<Vector4>(); var roots = new List<Vector4>(); var t0s = new List<Vector4>(); var t1s = new List<Vector4>();
        for (int i = 0; i < 32; i++)
        {
            int s = i * tree.Stride;
            Vector3 root = tree.L2W * Row(tree.Rows, s, 3);
            int slotIndex = (int)slot.X + i;
            Vector4 t0 = Texel(atlas, (slotIndex % RingVegetationHaze.SlotsPerRow) * 2, slotIndex / RingVegetationHaze.SlotsPerRow);
            Vector4 t1 = Texel(atlas, (slotIndex % RingVegetationHaze.SlotsPerRow) * 2 + 1, slotIndex / RingVegetationHaze.SlotsPerRow);
            worlds.Add(new Vector4(root.X + 1, root.Y + 8, root.Z, 8)); roots.Add(new Vector4(root.X, root.Y, root.Z, 0)); t0s.Add(t0); t1s.Add(t1);
        }
        // Stale tag: the probe's renderer tag differs from the stored one.
        foreach (int mode in new[] { 0, 1 })
        {
            float[] stale = await RunProbe(mode, worlds, roots, t0s, t1s, slot.Y + 1f);
            for (int i = 0; i < worlds.Count; i++)
            {
                Check(stale[i * 4 + 2] < .5f, $"stale tag accepted (mode {mode}, point {i})");
                Check(stale[i * 4] == stale[i * 4 + 1], $"stale tag did not fall back (mode {mode}, point {i})");
            }
        }
        // Light mismatch: atlas built for the sun, the heightfield now publishes the planet. The height term falls back; the cloud term stays usable.
        SetHeightGlobals(0, -1, 1, 2, PlanetDirection);
        float[] mismatch = await RunProbe(0, worlds, roots, t0s, t1s, slot.Y);
        float[] cloud = await RunProbe(1, worlds, roots, t0s, t1s, slot.Y);
        for (int i = 0; i < worlds.Count; i++)
        {
            Check(mismatch[i * 4 + 2] < .5f && mismatch[i * 4] == mismatch[i * 4 + 1], $"light mismatch did not fall back (point {i})");
            Check(cloud[i * 4 + 2] > .5f, $"light mismatch dropped the cloud term (point {i})");
        }
        SetHeightGlobals(0, -1, 1, 1, SunDirection);
        _log.Add("fallbacks ok");
    }

    // ------------------------------------------------------------------ twin parity

    private const string TwinSource = @"#version 450
layout(local_size_x = 64) in;
layout(set = 0, binding = 0, std430) readonly buffer Inputs { vec4 inp[]; };
layout(set = 0, binding = 1, std430) writeonly buffer Outputs { vec4 outp[]; };
#include ""res://materials/vegetation_gpu/ring_vegetation_shadow_terms.glslinc""
layout(push_constant, std430) uniform Push { uint count; } q;
void main() {
    uint i = gl_GlobalInvocationID.x;
    if (i >= q.count) return;
    vec4 w = inp[i * 2u], r = inp[i * 2u + 1u];
    RvsHeight h = rvs_height_sample(w.xyz, w.w, r.w, distance(w.xyz, shadow.camera.xyz));
    vec2 c = rvs_cloud_raw(w.xyz);
    outp[i * 2u] = vec4(h.visibility, h.weight, h.unknown, 0.0);
    outp[i * 2u + 1u] = vec4(c, h.r_eff, h.penumbra);
}
";

    private async Task ParityTwin()
    {
        // Points: inside L0, in the L0 -> L1 hand-off band, inside L1 only, the unresolved region (x > 700), outside every window; heights 0..40 m; cloud near, blend band, far only.
        var random = new Random(5);
        var worlds = new List<Vector4>(); var roots = new List<Vector4>();
        for (int i = 0; i < 160; i++)
        {
            float span = i % 4 == 0 ? 600f : i % 4 == 1 ? 2200f : i % 4 == 2 ? 9000f : 60000f;
            float x = (float)(random.NextDouble() * 2 - 1) * span, z = (float)(random.NextDouble() * 2 - 1) * span, h = (float)random.NextDouble() * 40f;
            float slope = i % 3 == 0 ? .3f : 0f;
            worlds.Add(new Vector4(x, h, z, h)); roots.Add(new Vector4(x, 0, z, slope));
        }
        foreach (var crossfade in new[] { false, true })
        {
            if (crossfade) SetHeightGlobals(0, 1, .4f, 1, SunDirection); else SetHeightGlobals(0, -1, 1, 1, SunDirection);
            await Frames(2);
            float[] twin = await RunTwin(worlds, roots);
            float[] original = await RunProbe(3, worlds, roots, new List<Vector4>(new Vector4[worlds.Count]), new List<Vector4>(new Vector4[worlds.Count]), 0f);
            float[] cloudOriginal = await RunProbe(6, worlds, roots, new List<Vector4>(new Vector4[worlds.Count]), new List<Vector4>(new Vector4[worlds.Count]), 0f);
            var raws = new List<Vector4>();
            for (int i = 0; i < worlds.Count; i++) raws.Add(new Vector4(twin[(i * 2 + 1) * 4], twin[(i * 2 + 1) * 4 + 1], 0, 1));
            float[] resolved = await RunProbe(4, worlds, roots, new List<Vector4>(new Vector4[worlds.Count]), raws, 0f);
            float maxHeight = 0, maxCloud = 0; int weighted = 0, clouded = 0; string worst = "", worstCloud = ""; int offenders = 0;
            for (int i = 0; i < worlds.Count; i++)
            {
                for (int c = 0; c < 3; c++) { float e = MathF.Abs(twin[i * 8 + c] - original[i * 4 + c]); if (e > maxHeight) { maxHeight = e; worst = $"point {i} {worlds[i]} slope {roots[i].W} ch{c} twin ({twin[i * 8]},{twin[i * 8 + 1]},{twin[i * 8 + 2]}) original ({original[i * 4]},{original[i * 4 + 1]},{original[i * 4 + 2]})"; } }
                { float e = MathF.Abs(resolved[i * 4] - cloudOriginal[i * 4]); if (e > 0.05f && offenders++ < 6) worstCloud += $" [point {i} {worlds[i]} raw ({raws[i].X},{raws[i].Y}) resolved {resolved[i * 4]} original {cloudOriginal[i * 4]}]"; if (e > maxCloud) { maxCloud = e; }  if (false) { worstCloud = $"point {i} {worlds[i]} raw ({raws[i].X},{raws[i].Y}) resolved {resolved[i * 4]} original {cloudOriginal[i * 4]}"; } }
                if (original[i * 4 + 1] > .01f) weighted++;
                if (cloudOriginal[i * 4] > .01f) clouded++;
            }
            Check(maxHeight <= 1e-3f, $"twin height terms differ from ring_height_shadow_sample by {maxHeight:G3} (crossfade {crossfade}) worst {worst}");
            Check(maxCloud <= 1e-3f, $"twin cloud raw + reader differ from sample_local_cloud_shadow by {maxCloud:G3} (crossfade {crossfade}) worst {worstCloud}");
            Check(weighted >= 40 && clouded >= 40, $"parity points do not exercise the terms (weighted {weighted}, clouded {clouded})");
            _log.Add($"twin n={worlds.Count} crossfade={crossfade} height err={maxHeight:G2} cloud err={maxCloud:G2} (weighted {weighted}, clouded {clouded})");
        }
    }

    private async Task<float[]> RunTwin(List<Vector4> worlds, List<Vector4> roots)
    {
        RingVegetationShadowFrame frame = MakeFrame();
        int count = worlds.Count;
        var input = new float[count * 8];
        for (int i = 0; i < count; i++) { Put(input, i * 8, worlds[i]); Put(input, i * 8 + 4, roots[i]); }
        string source = ExpandSource(TwinSource);
        byte[] uniformBytes = Bytes(frame.Parameters), inputBytes = Bytes(input);
        var done = new TaskCompletionSource<float[]>();
        Rid l0 = frame.HeightL0, l1 = frame.HeightL1, near = frame.CloudNear, far = frame.CloudFar;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            float[] result = Array.Empty<float>(); string error = "";
            RenderingDevice? rd = RenderingServer.GetRenderingDevice();
            var owned = new List<Rid>(); Rid set0 = default, set1 = default;
            try
            {
                if (rd is null) throw new InvalidOperationException("no RenderingDevice");
                using var shaderSource = new RDShaderSource { SourceCompute = source };
                using RDShaderSpirV spirv = rd.ShaderCompileSpirVFromSource(shaderSource);
                if (!string.IsNullOrWhiteSpace(spirv.CompileErrorCompute)) throw new InvalidOperationException("twin probe shader: " + spirv.CompileErrorCompute);
                Rid shader = rd.ShaderCreateFromSpirV(spirv); owned.Add(shader);
                Rid ubo = rd.UniformBufferCreate((uint)uniformBytes.Length, uniformBytes); owned.Add(ubo);
                Rid inputs = rd.StorageBufferCreate((uint)inputBytes.Length, inputBytes); owned.Add(inputs);
                Rid outputs = rd.StorageBufferCreate((uint)(count * 32)); owned.Add(outputs);
                var nearest = new RDSamplerState { MinFilter = RenderingDevice.SamplerFilter.Nearest, MagFilter = RenderingDevice.SamplerFilter.Nearest };
                var linear = new RDSamplerState { MinFilter = RenderingDevice.SamplerFilter.Linear, MagFilter = RenderingDevice.SamplerFilter.Linear,
                    RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge, RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge };
                Rid nearestSampler = rd.SamplerCreate(nearest), linearSampler = rd.SamplerCreate(linear); owned.Add(nearestSampler); owned.Add(linearSampler);
                var u0 = new Godot.Collections.Array<RDUniform>();
                foreach ((int b, Rid r) in new[] { (0, inputs), (1, outputs) }) { var u = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = b }; u.AddId(r); u0.Add(u); }
                set0 = rd.UniformSetCreate(u0, shader, 0);
                var u1 = new Godot.Collections.Array<RDUniform>();
                var block = new RDUniform { UniformType = RenderingDevice.UniformType.UniformBuffer, Binding = 2 }; block.AddId(ubo); u1.Add(block);
                foreach ((int b, Rid sampler, Rid texture) in new[] { (4, nearestSampler, l0), (5, nearestSampler, l1), (6, linearSampler, near), (7, linearSampler, far) })
                { var u = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = b }; u.AddId(sampler); u.AddId(texture); u1.Add(u); }
                set1 = rd.UniformSetCreate(u1, shader, 1);
                Rid pipeline = rd.ComputePipelineCreate(shader); owned.Add(pipeline);
                long list = rd.ComputeListBegin();
                rd.ComputeListBindComputePipeline(list, pipeline);
                rd.ComputeListBindUniformSet(list, set0, 0);
                rd.ComputeListBindUniformSet(list, set1, 1);
                rd.ComputeListSetPushConstant(list, BitConverter.GetBytes((uint)count), 4);
                rd.ComputeListDispatch(list, (uint)((count + 63) / 64), 1, 1);
                rd.ComputeListEnd();
                byte[] data = rd.BufferGetData(outputs);
                result = new float[data.Length / 4];
                Buffer.BlockCopy(data, 0, result, 0, data.Length);
            }
            catch (Exception e) { error = e.Message; }
            if (rd is not null)
            {
                foreach (Rid set in new[] { set1, set0 }) if (set.IsValid && rd.UniformSetIsValid(set)) rd.FreeRid(set);
                for (int i = owned.Count - 1; i >= 0; i--) if (owned[i].IsValid) rd.FreeRid(owned[i]);
            }
            Callable.From(() => { if (error.Length > 0) done.TrySetException(new InvalidOperationException(error)); else done.TrySetResult(result); }).CallDeferred();
        }));
        for (int i = 0; i < 600 && !done.Task.IsCompleted; i++) await Frames(1);
        Check(done.Task.IsCompleted, "twin probe timeout");
        return await done.Task;
    }

    // ------------------------------------------------------------------ canvas probe (Godot shading language, exact per-vertex code)

    private const string ProbeShader = @"shader_type canvas_item;
render_mode unshaded, blend_disabled;
#include ""res://materials/ring_shadow_compose.gdshaderinc""
#include ""res://materials/ring_vegetation_shadow_read.gdshaderinc""
uniform sampler2D in_world : filter_nearest;
uniform sampler2D in_root : filter_nearest;
uniform sampler2D in_tex0 : filter_nearest;
uniform sampler2D in_tex1 : filter_nearest;
uniform vec3 in_camera = vec3(0.0);
uniform float in_tag = 0.0;
uniform int mode = 0;
void fragment() {
	ivec2 px = ivec2(FRAGCOORD.xy);
	vec4 w = texelFetch(in_world, px, 0);
	vec4 r = texelFetch(in_root, px, 0);
	vec4 t0 = texelFetch(in_tex0, px, 0);
	vec4 t1 = texelFetch(in_tex1, px, 0);
	vec3 up = vec3(0.0, 1.0, 0.0);
	vec4 o = vec4(0.0);
	if (mode == 0) {
		float vertex_term = ring_height_shadow_vertex_term(w.xyz, r.xyz, up, in_camera);
		float instance_term = vertex_term;
		bool usable = ring_vegetation_shadow_height_usable(t0, t1, in_tag);
		if (usable) {
			RingHeightShadow hs = ring_vegetation_shadow_height(t0, t1, max(dot(w.xyz - r.xyz, up), 0.0));
			instance_term = mix(1.0, hs.visibility, hs.weight);
		}
		o = vec4(vertex_term, instance_term, usable ? 1.0 : 0.0, 0.0);
	} else if (mode == 1) {
		float vertex_term = sample_local_cloud_shadow(w.xyz);
		float instance_term = vertex_term;
		bool usable = ring_vegetation_shadow_cloud_usable(t0, t1, in_tag);
		if (usable) {
			instance_term = ring_vegetation_shadow_cloud(t1.xy, w.xyz);
		}
		o = vec4(vertex_term, instance_term, usable ? 1.0 : 0.0, 0.0);
	} else if (mode == 2) {
		RingHeightShadow a = ring_height_shadow_sample(r.xyz, 0.0, r.w, distance(r.xyz, in_camera));
		o = vec4(a.visibility, a.weight, a.unknown, 1.0);
	} else if (mode == 3) {
		RingHeightShadow a = ring_height_shadow_sample(w.xyz, w.w, r.w, distance(w.xyz, in_camera));
		o = vec4(a.visibility, a.weight, a.unknown, sample_local_cloud_shadow(w.xyz));
	} else if (mode == 6) {
		o = vec4(sample_local_cloud_shadow(w.xyz), 0.0, 0.0, 1.0);
	} else if (mode == 9) {
		o = vec4(w.x * 0.001, 0.5, 0.75, 1.0);
	} else if (mode == 4) {
		o = vec4(ring_vegetation_shadow_cloud(t1.xy, w.xyz), 0.0, 0.0, 1.0);
	} else {
		RingHeightShadow b = ring_vegetation_shadow_height(t0, t1, 0.0);
		o = vec4(b.visibility, b.weight, b.unknown, 1.0);
	}
	COLOR = o;
}
";

    private const int ProbeWidth = 64;

    private void BuildProbe()
    {
        _probeViewport = new SubViewport { Size = new Vector2I(ProbeWidth, 64), UseHdr2D = true, TransparentBg = false, RenderTargetUpdateMode = SubViewport.UpdateMode.Always, Disable3D = true };
        AddChild(_probeViewport);
        _probeMaterial = new ShaderMaterial { Shader = new Shader { Code = ProbeShader } };
        _probeRect = new ColorRect { Size = new Vector2(ProbeWidth, 64), Material = _probeMaterial };
        _probeViewport.AddChild(_probeRect);
        RingCloudShadowProjector.Register(_probeMaterial);
    }

    /// <summary>One probe pass: 4 floats per point (see ProbeShader modes).</summary>
    private async Task<float[]> RunProbe(int mode, List<Vector4> worlds, List<Vector4> roots, List<Vector4> t0s, List<Vector4> t1s, float tag)
    {
        int count = worlds.Count, height = Math.Max(4, (count + ProbeWidth - 1) / ProbeWidth);
        _probeViewport!.Size = new Vector2I(ProbeWidth, height);
        _probeRect!.Size = new Vector2(ProbeWidth, height);
        _probeMaterial!.SetShaderParameter("in_world", Pack(worlds, height)); _probeMaterial.SetShaderParameter("in_root", Pack(roots, height));
        _probeMaterial.SetShaderParameter("in_tex0", Pack(t0s, height)); _probeMaterial.SetShaderParameter("in_tex1", Pack(t1s, height));
        _probeMaterial.SetShaderParameter("in_camera", CameraWorld); _probeMaterial.SetShaderParameter("in_tag", tag); _probeMaterial.SetShaderParameter("mode", mode);
        RingCloudShadowProjector.Register(_probeMaterial);
        await Frames(4);
        await RenderingServer.Singleton.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image image = _probeViewport.GetTexture().GetImage();
        Check(image is not null && !image.IsEmpty() && image.GetSize() == new Vector2I(ProbeWidth, height), "probe capture failed");
        var result = new float[count * 4];
        for (int i = 0; i < count; i++)
        {
            Color c = image.GetPixel(i % ProbeWidth, i / ProbeWidth);
            result[i * 4] = c.R; result[i * 4 + 1] = c.G; result[i * 4 + 2] = c.B; result[i * 4 + 3] = c.A;
        }
        return result;
    }

    private static ImageTexture Pack(List<Vector4> values, int height)
    {
        var data = new float[ProbeWidth * height * 4];
        for (int i = 0; i < values.Count; i++) Put(data, i * 4, values[i]);
        return ImageTexture.CreateFromImage(Image.CreateFromData(ProbeWidth, height, false, Image.Format.Rgbaf, Bytes(data)));
    }

    // ------------------------------------------------------------------ data

    private static Vector4 Texel(byte[] atlas, int x, int row)
    {
        int at = (row * RingVegetationHaze.AtlasWidth + x) * 8;
        return new Vector4((float)BitConverter.ToHalf(atlas, at), (float)BitConverter.ToHalf(atlas, at + 2), (float)BitConverter.ToHalf(atlas, at + 4), (float)BitConverter.ToHalf(atlas, at + 6));
    }

    private static void Put(float[] target, int at, Vector4 v) { target[at] = v.X; target[at + 1] = v.Y; target[at + 2] = v.Z; target[at + 3] = v.W; }

    private static float[] GrassRows(Random random, int count)
    {
        var rows = new float[count * 20];
        for (int i = 0; i < count; i++)
        {
            int s = i * 20;
            float tilt = i % 5 == 0 ? .12f : 0f;
            rows[s] = .5f; rows[s + 4] = tilt; rows[s + 3] = (float)(random.NextDouble() * 1400 - 700);
            rows[s + 5] = .6f; rows[s + 7] = 0f;
            rows[s + 10] = .5f; rows[s + 11] = (float)(random.NextDouble() * 1400 - 700);
            rows[s + 12] = 1f; rows[s + 16] = .5f; rows[s + 17] = .5f;
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
            rows[s] = cos; rows[s + 2] = sin; rows[s + 3] = (float)(random.NextDouble() * 1400 - 700);
            rows[s + 5] = scale; rows[s + 7] = 0f;
            rows[s + 8] = -sin; rows[s + 10] = cos; rows[s + 11] = (float)(random.NextDouble() * 1400 - 700);
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

    // ------------------------------------------------------------------ waiting / readback

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

    private async Task<(byte[], int)> ReadAtlas(RingGpuVegetationCompute gpu)
    {
        var done = new TaskCompletionSource<(byte[], int)>();
        gpu.RequestShadowAtlasReadbackForTests((bytes, rows) => done.TrySetResult((bytes, rows)));
        for (int i = 0; i < 300 && !done.Task.IsCompleted; i++) await Frames(1);
        Check(done.Task.IsCompleted, "shadow atlas readback timeout");
        return await done.Task;
    }

    private async Task Frames(int count) { for (int i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

internal static class PushBytesExtensions
{
    /// <summary>Pads a 4-byte push constant to the 16 bytes the compute list requires.</summary>
    public static byte[] Concat16(this byte[] value) { var padded = new byte[16]; Buffer.BlockCopy(value, 0, padded, 0, value.Length); return padded; }
}
