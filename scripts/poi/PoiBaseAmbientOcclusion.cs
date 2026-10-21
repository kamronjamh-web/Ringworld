using Godot;

namespace RingworldTechDemo.Poi;

/// <summary>
/// Feeds the base ambient occlusion of POI architecture (<c>poi_base_local</c> / <c>poi_base_ao_height</c>, materials/ring_poi_architecture.gdshaderinc):
/// ambient and direct sun fade in from the POI's ground contact over about 12% of its height. The anchor is stored in each mesh's own space, so it
/// stays correct when the streamer reuses a pooled slot or the floating origin rebases. Stand-ins do not use it.
/// </summary>
public static class PoiBaseAmbientOcclusion
{
    public const float HeightFraction = 0.12f, MinHeightMeters = 6f, MaxHeightMeters = 40f;
    private static readonly StringName BaseLocalName = "poi_base_local";
    private static readonly StringName HeightName = "poi_base_ao_height";

    /// <summary>Occlusion height for a POI of the given height (12%, clamped to 6-40 m).</summary>
    public static float OcclusionHeight(float poiHeightMeters) => Mathf.Clamp(poiHeightMeters * HeightFraction, MinHeightMeters, MaxHeightMeters);

    /// <summary>Height of the geometry under <paramref name="root"/> along the root's Y axis (the merged mesh AABB, root space).</summary>
    public static float MeasureHeight(Node3D root)
    {
        if (!root.IsInsideTree()) return 0f;
        Transform3D toRoot = root.GlobalTransform.AffineInverse();
        bool any = false;
        float min = 0f, max = 0f;
        foreach (Node node in root.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (node is not MeshInstance3D { Mesh: not null } mesh) continue;
            Aabb box = (toRoot * mesh.GlobalTransform) * mesh.GetAabb();
            if (!any) { min = box.Position.Y; max = box.End.Y; any = true; }
            else { min = Mathf.Min(min, box.Position.Y); max = Mathf.Max(max, box.End.Y); }
        }
        return any ? max - min : 0f;
    }

    /// <summary>
    /// Sets the instance uniforms on every POI-shader MeshInstance3D at or under <paramref name="scope"/>. <paramref name="anchorOwner"/> is the node whose origin is the
    /// POI's ground contact (the POI root, or a bore mesh); <paramref name="poiHeightMeters"/> its overall height.
    /// </summary>
    public static int Apply(Node3D anchorOwner, Node scope, float poiHeightMeters)
    {
        float height = OcclusionHeight(poiHeightMeters);
        Vector3 anchorWorld = anchorOwner.GlobalTransform.Origin;
        int count = 0;
        var stack = new System.Collections.Generic.Stack<Node>();
        stack.Push(scope);
        while (stack.Count > 0)
        {
            Node node = stack.Pop();
            foreach (Node child in node.GetChildren()) stack.Push(child);
            if (node is not MeshInstance3D mesh || mesh.Mesh == null || !UsesPoiShader(mesh)) continue;
            mesh.SetInstanceShaderParameter(BaseLocalName, mesh.GlobalTransform.AffineInverse() * anchorWorld);
            mesh.SetInstanceShaderParameter(HeightName, height);
            count++;
        }
        return count;
    }

    private static bool UsesPoiShader(MeshInstance3D mesh)
    {
        if (PoiMaterialParams.IsPoiShader(mesh.MaterialOverride)) return true;
        for (int surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
            if (PoiMaterialParams.IsPoiShader(mesh.GetSurfaceOverrideMaterial(surface)) || PoiMaterialParams.IsPoiShader(mesh.Mesh.SurfaceGetMaterial(surface))) return true;
        return false;
    }
}
