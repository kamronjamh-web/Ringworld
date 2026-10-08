using System;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

public partial class GpuVegetationSnapshotContractTest : RefCounted
{
    public Godot.Collections.Dictionary Run()
    {
        try
        {
            var shared = new[] { new Vector4(1, 2, 3, 4) };
            var distinct = (Vector4[])shared.Clone();
            var input = new RingGpuVegetationCompute.BatchFrameParameters[464];
            for (int i = 0; i < 461; i++) input[i] = Frame(i, shared);
            input[461] = Frame(461, distinct);
            input[462] = Frame(462, null);
            input[463] = Frame(463, Array.Empty<Vector4>());
            var first = RingGpuVegetationCompute.SnapshotBatchFrames(input, out int clones);
            Check(clones == 3, "one clone per distinct non-null input identity");
            for (int i = 1; i < 461; i++)
                Check(ReferenceEquals(first[0].GrassFrustumPlanes, first[i].GrassFrustumPlanes), "shared request clone");
            Check(!ReferenceEquals(first[0].GrassFrustumPlanes, shared), "caller isolation");
            Check(!ReferenceEquals(first[0].GrassFrustumPlanes, first[461].GrassFrustumPlanes), "equal-content distinct identities");
            Check(first[462].GrassFrustumPlanes is null && first[463].GrassFrustumPlanes!.Length == 0, "null and empty inputs");
            shared[0] = new Vector4(5, 6, 7, 8);
            distinct[0] = Vector4.Zero;
            input[1] = Frame(999, null);
            Check(first[0].GrassFrustumPlanes![0] == new Vector4(1, 2, 3, 4)
                && first[461].GrassFrustumPlanes![0] == new Vector4(1, 2, 3, 4)
                && first[1].BatchId == 1, "pending snapshot survives caller array/list mutation");
            var second = RingGpuVegetationCompute.SnapshotBatchFrames(input, out int nextClones);
            Check(nextClones == 3 && second[0].GrassFrustumPlanes![0] == shared[0], "next request captures new contents");
            Check(!ReferenceEquals(first[0].GrassFrustumPlanes, second[0].GrassFrustumPlanes), "request isolation");
            shared[0] = Vector4.Zero;
            Check(second[0].GrassFrustumPlanes![0] == new Vector4(5, 6, 7, 8)
                && first[0].GrassFrustumPlanes![0] == new Vector4(1, 2, 3, 4), "simultaneously pending snapshots remain independent");
            return new() { ["pass"] = true, ["shared_batches"] = 461, ["frustum_clones"] = clones };
        }
        catch (Exception error) { return new() { ["pass"] = false, ["error"] = error.Message }; }
    }

    private static RingGpuVegetationCompute.BatchFrameParameters Frame(int id, Vector4[]? planes)
        => new(id, Transform3D.Identity, true, 0, 0, 100, 10, 1, 1, true, 0, false, planes, 0);
    private static void Check(bool condition, string detail)
    { if (!condition) throw new InvalidOperationException(detail); }
}
