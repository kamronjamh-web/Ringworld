using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Tools;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tests;

/// <summary>Forward+ backend parity of the GPU card accept branch: GPU-compacted rows vs the C# reference
/// (RingGrassCardComputeParameters.ReferenceAccepts + frustum guard), which also anchors the GLSL curve twin to
/// RingGrassCardCurve.cs. Also the superset property vs the shader rule and the skip-unchanged contract.
/// Run via tools/gpu_grass_card_culling_test.gd.</summary>
public partial class GpuGrassCardCullingTest : Node
{
    private const int Stride = RingGpuVegetationCompute.GrassFloats;
    private const float Padding = .15f;
    public string Result { get; private set; } = "RUNNING";
    private RingGpuVegetationCompute? _gpu;
    private Camera3D? _camera;
    private Mesh? _mesh;
    private readonly List<string> _log = new();
    private int _ambiguous, _compared;

    private sealed record Case(string Name, int Batch, float[] Rows, Transform3D L2W, Vector3[] Cameras, Vector3[] LookAt);

    public async void Start()
    {
        try { await Run(); Result = "PASS " + string.Join("; ", _log); }
        catch (Exception error) { Result = "FAIL " + error.Message; }
        _gpu?.Dispose(); _camera?.QueueFree(); await Frames(3);
    }

    private async Task Run()
    {
        _camera = new Camera3D { Current = true, Near = .1f, Far = 1000, Fov = 60 };
        AddChild(_camera);
        _mesh = MakeMesh();
        _gpu = new RingGpuVegetationCompute();
        var l2w = new Transform3D(new Basis(new Vector3(.3f, .5f, .1f).Normalized(), .7f), new Vector3(120f, -20f, 40f));
        var camera = new Vector3(100f, 30f, -60f);

        // (a) synthetic rows; the boundary ranks depend on the margin, so they are built per margin.
        RingGrassCardComputeParameters p0 = Params(0f), p2 = Params(2f);
        float[] synth0 = Synthetic(l2w, camera, p0, 8192, 11, true, false);
        float[] synth2 = Synthetic(l2w, camera, p2, 8192, 12, true, false);
        float[] parity = Synthetic(l2w, camera, p2, 16384, 13, false, true);
        Vector3[] look = { camera + new Vector3(0, 0, -50) };
        var cases = new List<Case>
        {
            Register("synthetic-m0", synth0, l2w, new[] { camera }, look),
            Register("synthetic-m2", synth2, l2w, new[] { camera }, look),
            Register("curve-parity", parity, l2w, new[] { camera }, look),
        };
        // (b) real tiles
        using RingTerrainSampler sampler = EmitterSiteTestWorlds.CreateGen17(1337);
        var clean = new RingLodSurfaceEditSnapshot(1, EmitterSiteTestWorlds.Circumference, new Dictionary<RingTerrainColumnCoordinate, int>());
        foreach (int tier in new[] { 0, 8 })
        {
            RingGrassTileData tile = BuildMeadowTile(sampler, clean, (byte)tier);
            Check(tile.InstanceCount > 200, $"tile tier {tier} too sparse ({tile.InstanceCount})");
            Vector3 center = l2w * tile.LocalAabb.GetCenter();
            var cams = new List<Vector3>(); var looks = new List<Vector3>();
            foreach (float dist in new[] { 0f, 30f, 120f, 300f })
            {
                cams.Add(center + new Vector3(dist * .8f, 1.7f, dist * .6f));
                looks.Add(dist == 0f ? center + new Vector3(0, 1.7f, -10f) : center);
            }
            cases.Add(Register($"tile-t{tier}", tile.Buffer, l2w, cams.ToArray(), looks.ToArray()));
            _log.Add($"tile t{tier} {tile.InstanceCount} rows");
        }
        _gpu.Initialize(); await WaitReady();

        foreach (Case c in cases)
        {
            float[] margins = c.Name == "synthetic-m0" ? new[] { 0f } : c.Name.StartsWith("tile") ? new[] { 0f, 2f } : new[] { 2f };
            foreach (float margin in margins)
                for (int ci = 0; ci < c.Cameras.Length; ci++)
                    foreach (bool frustum in c.Name == "curve-parity" ? new[] { false } : new[] { false, true })
                        await Compare(c, ci, Params(margin), frustum);
        }
        Check(_compared > 0, "nothing compared");
        Check(_ambiguous <= Math.Max(1, _compared / 1000), $"ambiguous rows {_ambiguous}/{_compared} exceed 0.1%");
        _log.Add($"set parity ok ({_compared} rows, {_ambiguous} ambiguous)");

        Superset(synth2, l2w, camera, p2);
        await SkipUnchanged(cases[1], camera);
    }

    private async Task Compare(Case c, int cameraIndex, RingGrassCardComputeParameters card, bool frustum)
    {
        _camera!.GlobalTransform = new Transform3D(Basis.Identity, c.Cameras[cameraIndex]);
        if ((c.LookAt[cameraIndex] - c.Cameras[cameraIndex]).LengthSquared() > .01f) _camera.LookAt(c.LookAt[cameraIndex], Vector3.Up);
        Vector4[]? planes = frustum ? Planes() : null;
        await Submit(c.Batch, c.L2W, planes, card);
        (uint count, float[] rows) = await Read(c.Batch);
        int total = c.Rows.Length / Stride;
        Check(count <= total, $"{c.Name}: count {count} > rows {total}");
        var gpuAccepted = new bool[total];
        int cursor = 0;
        for (int o = 0; o < (int)count; o++)
        {
            bool found = false;
            for (; cursor < total; cursor++)
                if (RowEquals(c.Rows, cursor, rows, o)) { gpuAccepted[cursor++] = true; found = true; break; }
            Check(found, $"{c.Name}: output row {o} is not an in-order copy of a source row");
        }
        Vector3 cam = _camera.GlobalPosition;
        int mismatches = 0, ambiguous = 0, nanAccepted = 0;
        for (int i = 0; i < total; i++)
        {
            float x = c.Rows[i * Stride + 16];
            if (float.IsNaN(x)) { if (gpuAccepted[i]) nanAccepted++; continue; }
            (bool accept, bool ambig) = Reference(c.Rows, i, c.L2W, cam, planes, card);
            _compared++;
            if (accept == gpuAccepted[i]) continue;
            if (ambig) { ambiguous++; _ambiguous++; } else mismatches++;
        }
        Check(nanAccepted == 0, $"{c.Name}: {nanAccepted} NaN-rank rows accepted");
        Check(mismatches == 0, $"{c.Name} cam{cameraIndex} margin {card.DistanceMarginMeters} frustum={frustum}: {mismatches} non-ambiguous mismatches (gpu {count}, ambiguous {ambiguous})");
        _log.Add($"{c.Name}/c{cameraIndex}/m{card.DistanceMarginMeters}/{(frustum ? "fr" : "nofr")}={count}/{total}");
    }

    private static (bool Accept, bool Ambiguous) Reference(float[] rows, int i, Transform3D l2w, Vector3 camera, Vector4[]? planes, RingGrassCardComputeParameters card)
    {
        int at = i * Stride;
        Vector3 root = new(rows[at + 3], rows[at + 7], rows[at + 11]);
        Vector3 world = l2w.Basis * root + l2w.Origin;
        float distance = world.DistanceTo(camera);
        float custom = rows[at + 16];
        bool curve = RingGrassCardComputeParameters.ReferenceAccepts(custom, distance, card);
        bool ambiguous = RingGrassCardComputeParameters.ReferenceAmbiguous(custom, distance, card);
        if (!curve) return (false, ambiguous);
        if (planes is null) return (true, ambiguous);
        Vector3 wx = l2w.Basis * new Vector3(rows[at], rows[at + 4], rows[at + 8]);
        Vector3 wy = l2w.Basis * new Vector3(rows[at + 1], rows[at + 5], rows[at + 9]);
        Vector3 wz = l2w.Basis * new Vector3(rows[at + 2], rows[at + 6], rows[at + 10]);
        float guard = Padding + card.GuardWidthFactor * MathF.Sqrt(wx.Dot(wx) + wz.Dot(wz)) + card.GuardHeightFactor * wy.Length();
        if (float.IsNaN(guard) || float.IsInfinity(guard) || guard < 0f) return (true, ambiguous);
        bool inside = true;
        foreach (Vector4 plane in planes)
        {
            float signed = new Vector3(plane.X, plane.Y, plane.Z).Dot(world) - plane.W;
            if (MathF.Abs(signed - guard) <= 1e-3f * MathF.Max(1f, guard)) ambiguous = true;
            if (signed > guard) inside = false;
        }
        return (inside, ambiguous);
    }

    /// <summary>The compute set at the compute camera must contain everything the shader shows at any camera within margin.</summary>
    private void Superset(float[] rows, Transform3D l2w, Vector3 camera, RingGrassCardComputeParameters card)
    {
        RingGrassCardCurveParameters curve = CurveOf(card);
        var random = new Random(77);
        int violations = 0, total = rows.Length / Stride;
        for (int k = 0; k < 64; k++)
        {
            Vector3 dir = new((float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 2 - 1);
            dir = dir.LengthSquared() < 1e-6f ? Vector3.Right : dir.Normalized();
            Vector3 moved = camera + dir * card.DistanceMarginMeters * (float)random.NextDouble();
            for (int i = 0; i < total; i++)
            {
                int at = i * Stride;
                float custom = rows[at + 16];
                if (float.IsNaN(custom)) continue;
                Vector3 world = l2w.Basis * new Vector3(rows[at + 3], rows[at + 7], rows[at + 11]) + l2w.Origin;
                float slot = MathF.Floor(custom), rank = (custom - slot) / 0.999f;
                bool low = MathF.Floor(slot * .25f) > .5f;
                float d = world.DistanceTo(moved);
                bool shows = (low ? RingGrassCardCurve.LowScale(curve, rank, d) > 0f : RingGrassCardCurve.Accepts(curve, rank, d))
                    && RingGrassCardCurve.Fade(curve, d) > 0f;
                float original = world.DistanceTo(camera);
                if (shows && !RingGrassCardComputeParameters.ReferenceAccepts(custom, original, card)
                    && !RingGrassCardComputeParameters.ReferenceAmbiguous(custom, original, card)) violations++;
            }
        }
        Check(violations == 0, $"superset property violated {violations} times");
        _log.Add("superset 0 violations");
    }

    private async Task SkipUnchanged(Case c, Vector3 camera)
    {
        _camera!.GlobalTransform = new Transform3D(Basis.Identity, camera);
        RingGrassCardComputeParameters card = Params(2f);
        Vector4[] planes = Planes();
        await Submit(c.Batch, c.L2W, planes, card);
        long completed = _gpu!.CompletedUpdates, skipped = _gpu.SkippedUnchangedUpdates;
        await Submit(c.Batch, c.L2W, planes, card);
        Check(_gpu.CompletedUpdates == completed && _gpu.SkippedUnchangedUpdates == skipped + 1,
            $"identical frame: completed +{_gpu.CompletedUpdates - completed}, skipped +{_gpu.SkippedUnchangedUpdates - skipped}");
        RingGrassCardComputeParameters[] changes =
        {
            card with { ReachMeters = 300f }, card with { DistanceMarginMeters = 4f }, card with { BaseDensity = 3f },
        };
        string[] names = { "reach", "margin", "base density" };
        for (int i = 0; i < changes.Length; i++)
        {
            completed = _gpu.CompletedUpdates;
            await Submit(c.Batch, c.L2W, planes, changes[i]);
            Check(_gpu.CompletedUpdates == completed + 1, $"{names[i]} change: completed +{_gpu.CompletedUpdates - completed}, expected +1");
        }
        completed = _gpu.CompletedUpdates;
        await Submit(c.Batch, c.L2W, null, null);
        Check(_gpu.CompletedUpdates == completed + 1, "removing GrassCard did not dispatch");
        (uint count, _) = await Read(c.Batch);
        Check(count == c.Rows.Length / Stride, $"blade branch (no planes, no LOD) kept {count} of {c.Rows.Length / Stride}, expected all");
        _log.Add("skip-unchanged contract ok");
    }

    // Smooth(0,1,t) = MinimumGrowIn, so rank = keep - t * max(.02, .2 keep) is the grow-in rejection edge.
    private static readonly float GrowInThreshold = SolveGrowIn();
    private static float SolveGrowIn()
    {
        float lo = 0f, hi = 1f;
        for (int i = 0; i < 40; i++) { float mid = (lo + hi) * .5f; if (RingGrassCardCurve.Smooth(0f, 1f, mid) < RingGrassCardCurve.MinimumGrowIn) lo = mid; else hi = mid; }
        return (lo + hi) * .5f;
    }

    private static RingGrassCardComputeParameters Params(float margin)
        => RingGrassCardComputeParameters.From(RingGrassCardCurveParameters.Default, margin);

    private static RingGrassCardCurveParameters CurveOf(RingGrassCardComputeParameters card) => new(
        NearDensity: card.NearDensity, BaseDensity: card.BaseDensity, FloorDensity: card.FloorDensity,
        FalloffExponent: card.FalloffExponent, FalloffStartMeters: card.FalloffStartMeters, NearBoostStartMeters: card.NearBoostStartMeters,
        NearBoostEndMeters: card.NearBoostEndMeters, ReachMeters: card.ReachMeters, LowLayerFullMeters: card.LowLayerFullMeters,
        LowLayerEndMeters: card.LowLayerEndMeters);

    private Case Register(string name, float[] rows, Transform3D l2w, Vector3[] cameras, Vector3[] lookAt)
    {
        int id = _gpu!.RegisterBatch(new RingGpuVegetationCompute.BatchRegistration("card-cull-" + name, new MultiMesh(), _mesh!, rows, null,
            rows.Length / Stride, new Aabb(new Vector3(-600, -100, -600), new Vector3(1200, 400, 1200)), true));
        Check(id >= 0, name + " registration");
        return new Case(name, id, rows, l2w, cameras, lookAt);
    }

    private async Task Submit(int batch, Transform3D l2w, Vector4[]? planes, RingGrassCardComputeParameters? card)
    {
        var frame = new RingGpuVegetationCompute.BatchFrameParameters(batch, l2w, true, 0, 0, 1000, 1, 1, 1, false, 1, false, planes, Padding, GrassCard: card);
        Check(_gpu!.RequestFrame(new RingGpuVegetationCompute.FrameRequest(_camera!.GlobalPosition, Array.Empty<uint>(),
            new List<RingGpuVegetationCompute.BatchFrameParameters> { frame })), "submit");
        await Frames(4);
    }

    private async Task<(uint, float[])> Read(int batch)
    {
        var done = new TaskCompletionSource<(uint, float[])>();
        _gpu!.RequestReadbackForTests(batch, (commands, instances) => done.TrySetResult((commands.Length >= 2 ? commands[1] : 0, instances)));
        for (int i = 0; i < 300 && !done.Task.IsCompleted; i++) await Frames(1);
        Check(done.Task.IsCompleted, "readback timeout");
        return await done.Task;
    }

    private Vector4[] Planes()
    {
        Godot.Collections.Array<Plane> source = _camera!.GetFrustum();
        var result = new Vector4[source.Count];
        for (int i = 0; i < source.Count; i++) result[i] = new Vector4(source[i].Normal.X, source[i].Normal.Y, source[i].Normal.Z, source[i].D);
        return result;
    }

    private static bool RowEquals(float[] source, int sourceRow, float[] output, int outputRow)
    {
        for (int k = 0; k < Stride; k++) if (source[sourceRow * Stride + k] != output[outputRow * Stride + k]) return false;
        return true;
    }

    private static float[] Synthetic(Transform3D l2w, Vector3 camera, RingGrassCardComputeParameters card, int count, int seed, bool boundaries, bool alongDistance)
    {
        var random = new Random(seed);
        var rows = new float[count * Stride];
        Transform3D inverse = l2w.AffineInverse();
        RingGrassCardCurveParameters curve = CurveOf(card);
        for (int i = 0; i < count; i++)
        {
            double angle = random.NextDouble() * Math.Tau;
            float radius = alongDistance ? 460f * (i + .5f) / count : (float)(Math.Sqrt(random.NextDouble()) * 460.0);
            Vector3 world = camera + new Vector3((float)Math.Cos(angle) * radius, (float)(random.NextDouble() * 20.0 - 8.0), (float)Math.Sin(angle) * radius);
            Vector3 root = inverse * world;
            float yaw = (float)(random.NextDouble() * Math.Tau), width = .4f + (float)random.NextDouble() * 1.2f, height = .3f + (float)random.NextDouble() * .9f;
            int at = i * Stride;
            rows[at] = width * MathF.Cos(yaw); rows[at + 8] = -width * MathF.Sin(yaw);
            rows[at + 5] = height;
            rows[at + 2] = width * MathF.Sin(yaw); rows[at + 10] = width * MathF.Cos(yaw);
            rows[at + 3] = root.X; rows[at + 7] = root.Y; rows[at + 11] = root.Z;
            rows[at + 12] = (float)random.NextDouble(); rows[at + 13] = (float)random.NextDouble(); rows[at + 14] = (float)random.NextDouble(); rows[at + 15] = 1f;
            int slot = random.Next(2) * 4 + random.Next(4);
            bool low = slot >= 4;
            float rank = (i + .5f) / count;
            Vector3 w = l2w * root;
            float d = MathF.Max(0f, w.DistanceTo(camera) - card.DistanceMarginMeters);
            if (alongDistance || boundaries)
            {
                float keep = low ? RingGrassCardCurve.LowKeep(curve, d) : RingGrassCardCurve.Keep(curve, d);
                float eps = alongDistance ? 2e-4f : 1e-3f, sign = i % 2 == 0 ? -1f : 1f;
                // Three boundary families: rank at keep (hard edge), rank where grow-in crosses its minimum, and well inside.
                int family = i % 3 == 0 || (boundaries && i % 3 == 1 && i % 2 == 0) ? 0 : i % 3;
                rank = family switch
                {
                    0 => keep + sign * eps,
                    1 => keep - GrowInThreshold * Math.Max(.02f, .2f * keep) + sign * eps,
                    _ => keep * .5f,
                };
                rank = Mathf.Clamp(rank, 0f, .998f);
            }
            else rank = Mathf.Clamp((float)random.NextDouble(), 0f, .998f);
            rows[at + 16] = slot + rank * 0.999f;
            if (i % 256 == 255) rows[at + 16] = float.NaN;
        }
        return rows;
    }

    private static RingGrassTileData BuildMeadowTile(RingTerrainSampler sampler, RingLodSurfaceEditSnapshot clean, byte tier)
    {
        long circumference = EmitterSiteTestWorlds.Circumference; int width = EmitterSiteTestWorlds.Width;
        RingGrassTileData? best = null;
        for (int dz = 0; dz < 6; dz++) for (int da = 0; da < 6; da++)
        {
            long around = 186_000 / 32 + da * 5, across = 141_000 / 32 + dz * 5;
            var key = new RingGrassTileKey(around, across);
            RingGrassTileBounds bounds = RingGrassCoordinates.Bounds(around, across, 32f, circumference, width);
            var geometry = new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero, circumference / Math.Tau, 0, 0, circumference, -1000, 1000,
                new RingCanonicalPosition(bounds.AroundCenter, 0, bounds.AcrossCenter));
            RingGrassCardPlacement cards = new RingGrassCardPlacement() with { IncludeLowLayer = tier == 0, LowLayerMaxInstances = 100_000 };
            RingGrassTileData data = RingGrassTileBuilder.Build(key, bounds, 8, 4, 0, 100_000, 25, sampler, clean, geometry, CancellationToken.None,
                sourceFraction: RingGrassCardCurve.LadderFraction(tier), sourceTier: tier, cards: cards);
            if (best is null || data.InstanceCount > best.InstanceCount) best = data;
        }
        return best!;
    }

    private async Task WaitReady()
    {
        for (int i = 0; i < 300; i++) { if (_gpu!.Ready) return; await Frames(1); }
        throw new TimeoutException("compute readiness");
    }
    private async Task Frames(int count) { for (int i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static ArrayMesh MakeMesh()
    {
        var mesh = new ArrayMesh(); var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new[] { new Vector3(-.5f, 0, 0), new Vector3(.5f, 0, 0), new Vector3(0, 1, 0) };
        arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2 }; mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays); return mesh;
    }
}
