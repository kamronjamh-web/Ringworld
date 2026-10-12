using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingScaledFarViewport
{
    [Export] public bool FarCloudPatchCullingEnabled { get; set; } = true;
    private readonly Dictionary<MeshInstance3D, List<MeshInstance3D>> _cloudPatches = new();
    private bool? _appliedCloudPatchCulling;

    public void ConfigureFarCloudPatchCulling(bool enabled)
    {
        FarCloudPatchCullingEnabled = enabled;
        if (_appliedCloudPatchCulling == enabled) return;
        _appliedCloudPatchCulling = enabled;
        foreach (var entry in _cloudPatches)
        {
            if (!GodotObject.IsInstanceValid(entry.Key)) continue;
            // Retain the named anchor and original mesh for diagnostics and the
            // reversible baseline. Its children inherit all motion/visibility.
            entry.Key.Layers = enabled ? 0u : 1u;
            RenderingServer.InstanceSetIgnoreCulling(entry.Key.GetInstance(), !enabled);
            foreach (MeshInstance3D patch in entry.Value) patch.Visible = enabled;
        }
    }

    private void ConfigureCloudPatches(MeshInstance3D anchor, bool shell)
    {
        foreach (MeshInstance3D retired in new List<MeshInstance3D>(_cloudPatches.Keys))
            if (!GodotObject.IsInstanceValid(retired) || retired.GetParent() is null) _cloudPatches.Remove(retired);
        if (_cloudPatches.Remove(anchor, out var previous))
            foreach (MeshInstance3D patch in previous) { anchor.RemoveChild(patch); patch.QueueFree(); }
        if (anchor.Mesh is null) return;
        var patches = new List<MeshInstance3D>();
        foreach (var data in PartitionCloudMesh(anchor.Mesh, shell))
        {
            var patch = new MeshInstance3D
            {
                Name = $"CloudCullPatch{patches.Count}", Mesh = data.Mesh,
                Position = data.Origin, MaterialOverride = anchor.MaterialOverride,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                IgnoreOcclusionCulling = false, ExtraCullMargin = .001f,
                Visible = FarCloudPatchCullingEnabled
            };
            anchor.AddChild(patch);
            patches.Add(patch);
        }
        _cloudPatches[anchor] = patches;
        anchor.Layers = FarCloudPatchCullingEnabled ? 0u : 1u;
        RenderingServer.InstanceSetIgnoreCulling(anchor.GetInstance(), !FarCloudPatchCullingEnabled);
        // A directly edited export may have changed since the last API call.
        // New meshes receive it immediately; the next call reconciles all anchors.
        _appliedCloudPatchCulling = null;
    }

    internal readonly record struct CloudPatchMesh(ArrayMesh Mesh, Vector3 Origin);

    // Preserve the exact source triangles and UVs. Every triangle is admitted
    // once, including the disk centre and triangles crossing a partition edge.
    internal static List<CloudPatchMesh> PartitionCloudMesh(Mesh source, bool shell)
    {
        int count = shell ? 8 : 4;
        var buckets = new List<int>[count];
        for (int i = 0; i < count; i++) buckets[i] = new();
        var arrays = source.SurfaceGetArrays(0);
        Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        Vector2[] uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        int[] indices = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil
            ? Array.Empty<int>() : arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        int length = indices.Length > 0 ? indices.Length : vertices.Length;
        for (int t = 0; t < length; t += 3)
        {
            int a = indices.Length > 0 ? indices[t] : t;
            int b = indices.Length > 0 ? indices[t + 1] : t + 1;
            int c = indices.Length > 0 ? indices[t + 2] : t + 2;
            Vector2 center = (uvs[a] + uvs[b] + uvs[c]) / 3;
            float unit = shell ? center.X : (Mathf.Atan2(center.Y - .5f, center.X - .5f) + Mathf.Pi) / Mathf.Tau;
            int bucket = Math.Clamp((int)(unit * count), 0, count - 1);
            buckets[bucket].Add(a); buckets[bucket].Add(b); buckets[bucket].Add(c);
        }
        var result = new List<CloudPatchMesh>();
        foreach (var bucket in buckets)
        {
            if (bucket.Count == 0) continue;
            // Keep the identical projection/interpolation arithmetic as the
            // baseline. Scaled shell coordinates are already small enough;
            // subtracting/readding patch origins introduced raster differences.
            Vector3 origin = Vector3.Zero;
            var patchVertices = new Vector3[bucket.Count];
            var patchUvs = new Vector2[bucket.Count];
            for (int i = 0; i < bucket.Count; i++)
            { patchVertices[i] = vertices[bucket[i]]; patchUvs[i] = uvs[bucket[i]]; }
            var patchArrays = new Godot.Collections.Array(); patchArrays.Resize((int)Mesh.ArrayType.Max);
            patchArrays[(int)Mesh.ArrayType.Vertex] = patchVertices;
            patchArrays[(int)Mesh.ArrayType.TexUV] = patchUvs;
            var mesh = new ArrayMesh(); mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, patchArrays);
            result.Add(new(mesh, origin));
        }
        return result;
    }

    public Godot.Collections.Dictionary GetFarCloudPatchDiagnostics()
    {
        int patches = 0, triangles = 0, intersecting = 0;
        Godot.Collections.Array<Plane>? planes = _farCamera?.GetFrustum();
        foreach (var entry in _cloudPatches)
        {
            if (!GodotObject.IsInstanceValid(entry.Key)) continue;
            foreach (MeshInstance3D patch in entry.Value)
            {
                patches++;
                if (patch.Mesh is ArrayMesh mesh)
                    for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
                        triangles += (mesh.SurfaceGetArrayIndexLen(surface) > 0
                            ? mesh.SurfaceGetArrayIndexLen(surface) : mesh.SurfaceGetArrayLen(surface)) / 3;
                if (planes is not null && CloudBoundsIntersectsFrustum(patch.GlobalTransform * patch.GetAabb().Grow(.001f), planes)) intersecting++;
            }
        }
        return new() { ["enabled"] = FarCloudPatchCullingEnabled, ["patches"] = patches,
            ["triangles"] = triangles, ["frustum_intersecting_patches"] = intersecting,
            ["viewport_visible_objects"] = _farViewport is null ? 0 : RenderingServer.ViewportGetRenderInfo(
                _farViewport.GetViewportRid(), RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.ObjectsInFrame) };
    }

    internal static bool CloudBoundsIntersectsFrustum(Aabb bounds, Godot.Collections.Array<Plane> planes)
    {
        foreach (Plane plane in planes)
        {
            bool outside = true;
            for (int i = 0; i < 8; i++)
                if (plane.DistanceTo(bounds.GetEndpoint(i)) <= 0) { outside = false; break; }
            if (outside) return false;
        }
        return true;
    }
}
