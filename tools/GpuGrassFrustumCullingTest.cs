using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.LOD;

/// <summary>Forward+ contract fixture for conservative GPU grass frustum selection.
/// It intentionally uses Camera3D.GetFrustum(), rather than synthetic planes.</summary>
public partial class GpuGrassFrustumCullingTest : Node
{
    public string Result { get; private set; } = "RUNNING";
    private RingGpuVegetationCompute? _gpu;
    private Camera3D? _camera;
    private Mesh? _mesh;
    private int _batch;

    public async void Start()
    {
        try { await Run(); Result = "PASS forward-plus grass frustum bounds, camera invalidation, orthographic, transformed roots, null planes, and stationary cache"; }
        catch (Exception error) { Result = "FAIL " + error.Message; }
        _gpu?.Dispose(); _camera?.QueueFree(); await Frames(3);
    }

    private async Task Run()
    {
        _camera = new Camera3D { Current = true, Near = .1f, Far = 100, Fov = 60 };
        AddChild(_camera);
        _mesh = MakeWideMesh();
        _gpu = new RingGpuVegetationCompute();
        // visible, offscreen, and a tall source whose root sits just outside a
        // 60 degree side plane. Its 2.25-height plus widened-card sphere must
        // retain it, but must not retain it after a turn/near/far rejection.
        _batch = _gpu.RegisterBatch(new RingGpuVegetationCompute.BatchRegistration("frustum-contract", new MultiMesh(), _mesh,
            Roots(), null, 4, new Aabb(new Vector3(-20, -2, -30), new Vector3(40, 24, 40)), true));
        Check(_batch >= 0, "registration"); _gpu.Initialize(); await WaitReady();
        var readiness = new HashSet<int>();
        var expected = new List<RingGpuVegetationCompute.BatchFrameParameters>
            { new(_batch, Transform3D.Identity, true, 0, 0, 1000, 1, 1, 1, false, 1, false, null, .15f) };
        _gpu.CaptureReadyBatches(expected, readiness);
        Check(readiness.Count == 0, "allocated output was published before its first dispatch");

        _camera.GlobalTransform = Transform3D.Identity;
        await Submit("perspective", Transform3D.Identity, Planes(), .15f, true);
        Check(await Count() == 2, "perspective should retain centered root and tangent tall envelope");
        _gpu.CaptureReadyBatches(expected, readiness);
        Check(readiness.Contains(_batch), "bulk snapshot missed the completed output");
        expected[0] = expected[0] with { VisibilityRevision = 99 };
        _gpu.CaptureReadyBatches(expected, readiness);
        Check(readiness.Count == 0, "bulk snapshot exposed a stale visibility decision");

        long stationary = _gpu.CompletedUpdates;
        await Submit("stationary", Transform3D.Identity, Planes(), .15f, true);
        Check(_gpu.CompletedUpdates == stationary && _gpu.SkippedUnchangedUpdates > 0, "identical frustum request should skip");

        _camera.RotateY(Mathf.Pi); await Submit("rotation", Transform3D.Identity, Planes(), .15f, true);
        Check(_gpu.CompletedUpdates > stationary && await Count() == 0, "camera rotation did not invalidate cached frustum request");

        _camera.GlobalTransform = Transform3D.Identity; _camera.Fov = 20;
        await Submit("fov", Transform3D.Identity, Planes(), .15f, true);
        Check(await Count() == 1, "narrow FOV should retain only the centered root after the tangent envelope leaves view");

        _camera.Near = 50; await Submit("near", Transform3D.Identity, Planes(), .15f, true);
        Check(await Count() == 0, "near plane change did not refresh root rejection");

        _camera.Near = .1f; _camera.Fov = 60; _camera.Far = 8;
        await Submit("far", Transform3D.Identity, Planes(), .15f, true);
        Check(await Count() == 0, "far plane change did not refresh root rejection");

        _camera.Far = 100; _camera.Projection = Camera3D.ProjectionType.Orthogonal; _camera.Size = 40;
        await Submit("orthographic", Transform3D.Identity, Planes(), .15f, true);
        Check(await Count() == 2, "orthographic frustum did not retain conservative envelope");

        // A sheared/non-uniform parent retains the tangent blade. This exercises
        // the shader's Frobenius horizontal source bound.
        Transform3D sheared = new(new Basis(new Vector3(1, 0, 0), new Vector3(.5f, 1, 0), new Vector3(0, 0, .5f)), Vector3.Zero);
        await Submit("sheared-parent", sheared, Planes(), .15f, true);
        Check(await Count() == 2, "sheared parent clipped the conservative tall envelope");

        Transform3D translated = new(Basis.Identity, new Vector3(1000, 0, 0));
        await Submit("transformed-offscreen", translated, Planes(), .15f, true);
        Check(await Count() == 0, "parent transformed batch should be offscreen");

        await Submit("null-planes", translated, null, .15f, true);
        Check(await Count() == 4, "disabled frustum planes should retain every root");
    }

    private async Task Submit(string label, Transform3D transform, Vector4[]? planes, float padding, bool skip)
    {
        Check(_gpu!.RequestFrame(new RingGpuVegetationCompute.FrameRequest(_camera!.GlobalPosition, Array.Empty<uint>(),
            new List<RingGpuVegetationCompute.BatchFrameParameters> { new(_batch, transform, true, 0, 0, 1000, 1, 1, 1, false, 1, false, planes, padding) }, skip)), label + " submit");
        await Frames(3);
    }

    private Vector4[] Planes()
    {
        Godot.Collections.Array<Plane> source = _camera!.GetFrustum();
        var result = new Vector4[source.Count];
        for (int i = 0; i < source.Count; i++) result[i] = new Vector4(source[i].Normal.X, source[i].Normal.Y, source[i].Normal.Z, source[i].D);
        return result;
    }

    private async Task<uint> Count()
    {
        var completion = new TaskCompletionSource<uint>();
        _gpu!.RequestReadbackForTests(_batch, (commands, _) => completion.TrySetResult(commands.Length >= 2 ? commands[1] : 0));
        for (int i = 0; i < 180 && !completion.Task.IsCompleted; i++) await Frames(1);
        Check(completion.Task.IsCompleted, "readback timeout"); return await completion.Task;
    }

    private async Task WaitReady()
    {
        for (int i = 0; i < 180; i++) { if (_gpu!.Ready) return; await Frames(1); }
        throw new TimeoutException("compute readiness");
    }
    private async Task Frames(int count) { for (int i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static float[] Roots()
    {
        var rows = new float[4 * RingGpuVegetationCompute.GrassFloats];
        // X, Y, Z basis columns plus origin. The tall root is tangent at 60
        // degrees (x=24,z=-30): its roughly 9.4m conservative sphere enters
        // the frustum, while ordinary +/-40m side roots do not.
        Vector3[] positions = { new(0, 0, -30), new(40, 0, -30), new(24, 0, -30), new(-40, 0, -30) };
        for (int i = 0; i < positions.Length; i++)
        {
            int at = i * RingGpuVegetationCompute.GrassFloats;
            rows[at] = rows[at + 10] = i == 2 ? .08f : .04f;
            rows[at + 5] = i == 2 ? 4 : .8f;
            rows[at + 3] = positions[i].X; rows[at + 7] = positions[i].Y; rows[at + 11] = positions[i].Z;
        }
        return rows;
    }
    private static ArrayMesh MakeWideMesh()
    {
        var mesh = new ArrayMesh(); var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new[] { new Vector3(-2, 0, 0), new Vector3(2, 0, 0), new Vector3(0, 1, 0) };
        arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2 }; mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays); return mesh;
    }
}
