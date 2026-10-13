using System;
using System.Reflection;
using Godot;
using RingworldTechDemo.Voxel.LOD;

public partial class MeadowGrassMeshTest : RefCounted
{
    public bool Run()
    {
        try
        {
            using var library = new RingMeadowGrassMeshLibrary();
            foreach (int variant in new[] { 0, 1, 2 })
            foreach (string tier in new[] { "near", "near_reduced", "intermediate", "far" })
            {
                Mesh mesh = library.Get(variant, tier, 1f);
                Require(ReferenceEquals(mesh, library.Get(variant, tier, 1f)), "mesh cache reread an unchanged asset");
                var arrays = mesh.SurfaceGetArrays(0);
                int[] indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
                Require(indices.Length == (tier == "near" ? 32 : tier == "near_reduced" ? 16 : tier == "intermediate" ? 12 : 8) * 3, "authored triangle count changed");
                Require(arrays[(int)Mesh.ArrayType.Custom0].AsFloat32Array().Length
                    == arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length * 4, "source frame channel missing");
            }
            Mesh baseline = library.Get(0, "near", 1f);
            var original = baseline.SurfaceGetArrays(0);
            Mesh spread = library.Get(0, "near", 2f);
            var expanded = spread.SurfaceGetArrays(0);
            Vector3[] originalVertices = original[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            Vector3[] spreadVertices = expanded[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            float[] originalFrames = original[(int)Mesh.ArrayType.Custom0].AsFloat32Array();
            float[] spreadFrames = expanded[(int)Mesh.ArrayType.Custom0].AsFloat32Array();
            for (int i = 0; i < originalVertices.Length; i++)
            {
                Require(spreadVertices[i].IsEqualApprox(new Vector3(originalVertices[i].X * 2, originalVertices[i].Y, originalVertices[i].Z * 2)), "spread altered normalized height");
                Require(Mathf.IsEqualApprox(spreadFrames[i * 4], originalFrames[i * 4] * 2)
                    && Mathf.IsEqualApprox(spreadFrames[i * 4 + 1], originalFrames[i * 4 + 1] * 2)
                    && spreadFrames[i * 4 + 2] == originalFrames[i * 4 + 2]
                    && spreadFrames[i * 4 + 3] == originalFrames[i * 4 + 3], "spread broke camera-facing source frames");
            }
            var layer = new RingStandingGrassLayer { BladesPerSquareMeter = 24, MaximumInstances = 18000 };
            try
            {
                layer.SetProcess(false);
                var tree = Engine.GetMainLoop() as SceneTree ?? throw new InvalidOperationException("mesh test requires a SceneTree");
                tree.Root.AddChild(layer);
                Require(layer.GrassBladesPerSource == 8 && layer.GrassPlacementUnitsPerSource == 4 && layer.MaximumGrassSourceInstances == 4500, "meadow changed placement budgets or misreported blades");
                layer.InitializeForTests();
                var data = new RingGrassTileData(new RingGrassTileKey(2, 3), default, 0, Array.Empty<float>(), default, 0, bladesPerSource: 8);
                MethodInfo near = typeof(RingStandingGrassLayer).GetMethod("NearMeshFor", BindingFlags.NonPublic | BindingFlags.Instance)!;
                Mesh selected = (Mesh)near.Invoke(layer, new object[] { data })!;
                Require(ReferenceEquals(selected, near.Invoke(layer, new object[] { data })), "mixed variant is unstable");
                layer.UseReducedNearGrassGeometry = true;
                Mesh reduced = (Mesh)near.Invoke(layer, new object[] { data })!;
                Require(reduced.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Index].AsInt32Array().Length == 48, "reduced meadow near option increased triangle cost");
                layer.EnableMeadowTufts = false;
                Require(layer.GrassBladesPerSource == 4 && layer.MaximumGrassSourceInstances == 4500, "legacy A/B changed source budget");
                Require(!ReferenceEquals(selected, near.Invoke(layer, new object[] { data })), "legacy switch retained meadow geometry");
            }
            finally { layer.Free(); }
            GD.Print("MEADOW_GRASS_MESH=PASS");
            return true;
        }
        catch (Exception error) { GD.PushError("MEADOW_GRASS_MESH=FAIL " + error); return false; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
