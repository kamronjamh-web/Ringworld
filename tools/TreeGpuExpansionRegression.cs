using System;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

/// <summary>GPU parity fixture: tree source rows expanded on the GPU from compact roots (shader phase 5) must match the CPU
/// CreateTransform rows, for plain (12) and shared-impostor (16 float) sources, away from the origin and across the ring seam.</summary>
public partial class TreeGpuExpansionRegression : Node
{
    private RingGpuVegetationCompute? _gpu;
    public string Result { get; private set; } = "RUNNING";

    public async void Start()
    {
        try { Result = "PASS " + await Run(); }
        catch (Exception error) { Result = "FAIL " + error.Message; }
    }

    private async Task<string> Run()
    {
        const double radius = 5_000_000, circumference = 2 * Math.PI * radius;
        var layer = new Transform3D(Basis.FromEuler(new Vector3(.1f, .2f, .3f)), new Vector3(30, 5, -20));
        Transform3D inverse = layer.AffineInverse();
        _gpu = new RingGpuVegetationCompute { HazeChannel = RingworldTechDemo.Atmosphere.RingVegetationHaze.Channel.None };
        ArrayMesh mesh = MakeMesh();
        float worstPosition = 0, worstBasis = 0;
        // Anchors: mid-ring, and a chunk that straddles the 0 / circumference seam.
        foreach ((long anchorAround, int anchorAcross, bool custom) in new[] { (12_345_000L, 51_000, false), (12_345_000L, 51_000, true), ((long)circumference - 150, 20, false) })
        {
            const int count = 300;
            // The CPU reference only keeps float precision near the floating origin, so each chunk gets its own origin (as in play).
            var geometry = new RingLodGeometrySnapshot(new Transform3D(Basis.FromEuler(new Vector3(.3f, -.2f, .5f)), new Vector3(10, -4, 7)), new Vector3(3, 2, -1),
                new Vector3(100, 0, -50), radius, 1234, 40, circumference, -100, 100, new RingCanonicalPosition(anchorAround + 200, 0, anchorAcross));
            var random = new Random(1234 + (int)anchorAround);
            var roots = new Vector4[count]; var metadata = new Vector4[count]; var around = new double[count]; var across = new double[count];
            for (int i = 0; i < count; i++)
            {
                double dAround = random.NextDouble() * 600 - 100, dAcross = random.NextDouble() * 500 - 250;
                around[i] = anchorAround + Math.Round(dAround); across[i] = anchorAcross + Math.Round(dAcross);
                float wrapped = (float)(around[i] - anchorAround);
                metadata[i] = new Vector4(i / (float)count, 0, wrapped, (float)(across[i] - anchorAcross));
                roots[i] = new Vector4((float)(random.NextDouble() * 80 - 5), (float)(random.NextDouble() * Math.PI * 2), (float)(5 + random.NextDouble() * 40), i % 7);
            }
            var expansion = new RingGpuVegetationCompute.TreeExpansion { Roots = roots, Frame = RingBiomeDecorationLayer.BuildGpuTreeExpansionFrame(geometry, layer, anchorAround, anchorAcross) };
            int id = _gpu.RegisterBatch(new RingGpuVegetationCompute.BatchRegistration("expansion-" + anchorAround + custom, new MultiMesh(), mesh, Array.Empty<float>(), metadata, count,
                new Aabb(new Vector3(-1000, -1000, -1000), new Vector3(2000, 2000, 2000)), false, custom, false, expansion));
            Check(id >= 0, "register");
            float[] rows = Array.Empty<float>();
            for (int frame = 0; frame < 300 && rows.Length == 0; frame++)
            {
                await Frame();
                var completion = new TaskCompletionSource<float[]>();
                _gpu.RequestSourceReadbackForTests(id, value => completion.TrySetResult(value));
                rows = await completion.Task;
            }
            int stride = custom ? RingGpuVegetationCompute.TreeCustomFloats : RingGpuVegetationCompute.TreeFloats;
            Check(rows.Length == count * stride, $"source rows {rows.Length} != {count * stride}");
            for (int i = 0; i < count; i++)
            {
                Transform3D expected = RingBiomeDecorationLayer.CreateTreeTransformForRegression(geometry, inverse, around[i], across[i], roots[i].X, roots[i].Z, roots[i].Y);
                int at = i * stride;
                var actual = new Transform3D(new Basis(new Vector3(rows[at], rows[at + 4], rows[at + 8]), new Vector3(rows[at + 1], rows[at + 5], rows[at + 9]), new Vector3(rows[at + 2], rows[at + 6], rows[at + 10])),
                    new Vector3(rows[at + 3], rows[at + 7], rows[at + 11]));
                float position = (actual.Origin - expected.Origin).Length();
                float basis = Math.Max((actual.Basis.X - expected.Basis.X).Length(), Math.Max((actual.Basis.Y - expected.Basis.Y).Length(), (actual.Basis.Z - expected.Basis.Z).Length())) / roots[i].Z;
                worstPosition = Math.Max(worstPosition, position); worstBasis = Math.Max(worstBasis, basis);
                Check(position < .02f && basis < 5e-4f, $"root {i} anchor {anchorAround} position error {position:F5} m, basis error {basis:E2}");
                if (custom) Check(rows[at + 12] == roots[i].W, "variant word");
            }
        }
        return $"GPU tree row expansion matches CPU CreateTransform (worst position {worstPosition:F5} m, worst basis {worstBasis:E2}); {Benchmark()}";
    }

    // The per-root CPU work the old group build did (CreateTransform, row write, wind-deformation bounds merge) against the compact record that
    // replaces it. Both run on this process's build configuration, so the ratio is what matters.
    private static string Benchmark()
    {
        const int count = 20_000;
        var geometry = new RingLodGeometrySnapshot(new Transform3D(Basis.FromEuler(new Vector3(.3f, -.2f, .5f)), new Vector3(10, -4, 7)), new Vector3(3, 2, -1),
            new Vector3(100, 0, -50), 5_000_000, 1234, 40, 2 * Math.PI * 5_000_000, -100, 100, new RingCanonicalPosition(12_345_200, 0, 51_000));
        Transform3D inverse = new Transform3D(Basis.FromEuler(new Vector3(.1f, .2f, .3f)), new Vector3(30, 5, -20)).AffineInverse();
        var prototype = new Aabb(new Vector3(-.6f, 0, -.6f), new Vector3(1.2f, 1, 1.2f));
        var random = new Random(7);
        var altitudes = new float[count]; var yaws = new float[count]; var heights = new float[count];
        for (int i = 0; i < count; i++) { altitudes[i] = (float)random.NextDouble() * 60; yaws[i] = (float)random.NextDouble() * 6.28f; heights[i] = 8 + (float)random.NextDouble() * 20; }
        double Run(bool legacy)
        {
            var rows = new float[count * 12]; var compact = new Vector4[count]; var meta = new Vector4[count]; Aabb bounds = default;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < count; i++)
            {
                if (legacy)
                {
                    Transform3D t = RingBiomeDecorationLayer.CreateTreeTransformForRegression(geometry, inverse, 12_345_000 + i % 600, 51_000 + i % 400, altitudes[i], heights[i], yaws[i]);
                    int at = i * 12;
                    rows[at] = t.Basis.X.X; rows[at + 1] = t.Basis.Y.X; rows[at + 2] = t.Basis.Z.X; rows[at + 3] = t.Origin.X;
                    rows[at + 4] = t.Basis.X.Y; rows[at + 5] = t.Basis.Y.Y; rows[at + 6] = t.Basis.Z.Y; rows[at + 7] = t.Origin.Y;
                    rows[at + 8] = t.Basis.X.Z; rows[at + 9] = t.Basis.Y.Z; rows[at + 10] = t.Basis.Z.Z; rows[at + 11] = t.Origin.Z;
                    Aabb current = RingBiomeDecorationLayer.TreeWindDeformationBounds(t, prototype);
                    bounds = i == 0 ? current : bounds.Merge(current);
                }
                else compact[i] = new Vector4(altitudes[i], yaws[i], heights[i], 0);
                meta[i] = new Vector4(i / (float)count, 0, i % 600, i % 400);
            }
            return (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency + (rows[7] + bounds.Size.X + compact[3].X) * 0;
        }
        Run(true); Run(false);
        double legacyMs = Math.Min(Run(true), Math.Min(Run(true), Run(true))), compactMs = Math.Min(Run(false), Math.Min(Run(false), Run(false)));
        return $"per-root prep for {count} roots: legacy CreateTransform+bounds {legacyMs:F2} ms ({legacyMs * 1000 / count:F2} us/root), compact records {compactMs:F2} ms ({compactMs * 1000 / count:F3} us/root)";
    }

    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    public override void _ExitTree() => _gpu?.Dispose();
    private static void Check(bool value, string text) { if (!value) throw new InvalidOperationException(text); }
    private static ArrayMesh MakeMesh()
    {
        var mesh = new ArrayMesh(); var vertices = new[] { new Vector3(-.1f, 0, 0), new Vector3(.1f, 0, 0), new Vector3(0, .2f, 0) }; var indices = new[] { 0, 1, 2 };
        var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max); arrays[(int)Mesh.ArrayType.Vertex] = vertices; arrays[(int)Mesh.ArrayType.Index] = indices;
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays); return mesh;
    }
}
