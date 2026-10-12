using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

public partial class FarCloudPatchContractTest : RefCounted
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public Godot.Collections.Dictionary Run()
    {
        int cases = 0;
        try
        {
            foreach (double radius in new[] { 80_000d, 5_000_000d })
            {
                using var far = new RingScaledFarViewport();
                Set(far, "_radiusMeters", radius); Set(far, "_circumferenceMeters", radius * Math.Tau);
                for (int layer = 0; layer < 3; layer++)
                {
                    var mesh = (Mesh)typeof(RingScaledFarViewport).GetMethod("CreateCurvedFarCloudClipmapMesh", Private)!.Invoke(far, new object[] { layer })!;
                    Verify(mesh, false); cases++; mesh.Dispose();
                }
                var viewport = new SubViewport(); far.AddChild(viewport);
                Set(far, "_farViewport", viewport);
                Set(far, "_cloudMaterial", new ShaderMaterial());
                typeof(RingScaledFarViewport).GetMethod("BuildCloudShell", Private)!.Invoke(far, null);
                var anchor = viewport.GetNode<MeshInstance3D>("ScaledCloudShell");
                Verify(anchor.Mesh!, true); cases++;
                far.ConfigureFarCloudPatchCulling(false);
                Check(anchor.Layers == 1 && !anchor.GetChild<MeshInstance3D>(0).Visible, "baseline restoration");
                far.ConfigureFarCloudPatchCulling(true);
                Check(anchor.Layers == 0 && anchor.GetChild<MeshInstance3D>(0).Visible, "patch activation");
                far.Free();
            }
            var planes = new Godot.Collections.Array<Plane> { new(Vector3.Right, 1) };
            Check(RingScaledFarViewport.CloudBoundsIntersectsFrustum(new Aabb(Vector3.Zero, Vector3.One), planes), "boundary intersection");
            Check(!RingScaledFarViewport.CloudBoundsIntersectsFrustum(new Aabb(new Vector3(2, 0, 0), Vector3.One), planes), "outside rejection");
            return new() { ["pass"] = true, ["production_mesh_cases"] = cases };
        }
        catch (Exception error) { return new() { ["pass"] = false, ["error"] = error.ToString() }; }
    }

    private static void Verify(Mesh source, bool shell)
    {
        var arrays = source.SurfaceGetArrays(0);
        Vector3[] original = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        Vector2[] uv = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        int[] indices = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil
            ? Array.Empty<int>() : arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        var expected = new Dictionary<string, int>();
        int n = indices.Length > 0 ? indices.Length : original.Length;
        for (int i = 0; i < n; i += 3)
        {
            int a = indices.Length > 0 ? indices[i] : i;
            int b = indices.Length > 0 ? indices[i + 1] : i + 1;
            int c = indices.Length > 0 ? indices[i + 2] : i + 2;
            Add(expected, Triangle(original[a], uv[a], original[b], uv[b], original[c], uv[c]));
        }
        var patches = RingScaledFarViewport.PartitionCloudMesh(source, shell);
        Check(patches.Count == (shell ? 8 : 4), "bounded patch count");
        foreach (var patch in patches)
        {
            var a = patch.Mesh.SurfaceGetArrays(0);
            Vector3[] v = a[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            Vector2[] u = a[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            for (int i = 0; i < v.Length; i++)
            {
                Check(patch.Mesh.GetAabb().Grow(.001f).HasPoint(v[i]), "local bounds containment");
                Check(patch.Origin == Vector3.Zero, "zero patch origin");
                var rotated = new Transform3D(new Basis(Vector3.Up, .83f), new Vector3(17, -3, 29));
                Check((rotated * patch.Mesh.GetAabb().Grow(.002f)).HasPoint(rotated * v[i]), "rotated/rebased bounds containment");
            }
            for (int i = 0; i < u.Length; i += 3)
            {
                string key = Triangle(v[i], u[i], v[i + 1], u[i + 1], v[i + 2], u[i + 2]);
                Check(expected.TryGetValue(key, out int count) && count > 0, "exact vertex/UV/triangle/winding preservation");
                expected[key] = count - 1;
            }
            patch.Mesh.Dispose();
        }
        foreach (int count in expected.Values) Check(count == 0, "full disk/shell coverage without omissions");
    }
    private static string Vertex(Vector3 p, Vector2 uv) => $"{p.X:R},{p.Y:R},{p.Z:R}/{uv.X:R},{uv.Y:R}";
    private static string Triangle(Vector3 a, Vector2 auv, Vector3 b, Vector2 buv, Vector3 c, Vector2 cuv)
        => $"{Vertex(a, auv)}|{Vertex(b, buv)}|{Vertex(c, cuv)}";
    private static void Add(Dictionary<string, int> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static void Check(bool passed, string detail) { if (!passed) throw new InvalidOperationException(detail); }
}
