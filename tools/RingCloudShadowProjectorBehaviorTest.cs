using Godot;

namespace RingworldTechDemo;

[GlobalClass]
public partial class RingCloudShadowProjectorBehaviorTest : RefCounted
{
    public string Run()
    {
        using Shader shader = GD.Load<Shader>("res://materials/voxel_ring_terrain.gdshader");
        using ShaderMaterial first = new() { Shader = shader };
        using ShaderMaterial second = new() { Shader = shader };
        using ImageTexture near = MakeTexture(32);
        using ImageTexture replacement = MakeTexture(96);
        using ImageTexture far = MakeTexture(160);
        Vector3 around = Vector3.Forward, across = Vector3.Up, up = Vector3.Left;
        RingCloudShadowProjector.ReleaseTextures();
        RingCloudShadowProjector.ResetParameterWriteCount();
        RingCloudShadowProjector.Register(first);
        Require(RingCloudShadowProjector.ParameterWriteCount == 27, $"new receiver did not full-sync: {RingCloudShadowProjector.ParameterWriteCount} writes");
        RingCloudShadowProjector.ResetParameterWriteCount();
        RingCloudShadowProjector.UpdateCascades(near, Vector3.Zero, around, across, up, 100, Vector2.Zero,
            far, Vector3.Right, around, across, up, 200, Vector2.Zero, 1000, Vector3.Right, .5f);
        RingCloudShadowProjector.ResetParameterWriteCount();
        RingCloudShadowProjector.UpdateCascades(near, Vector3.Zero, around, across, up, 100, Vector2.Zero,
            far, Vector3.Right, around, across, up, 200, Vector2.Zero, 1000, Vector3.Right, .5f);
        Require(RingCloudShadowProjector.ParameterWriteCount == 0, "identical cascade update wrote parameters");
        RingCloudShadowProjector.UpdateCascadeTransforms(Vector3.Zero, around, across, up, new Vector2(.1f, 0),
            Vector3.Right, around, across, up, new Vector2(0, .1f));
        Require(RingCloudShadowProjector.ParameterWriteCount == 2, "offset-only update was not two writes");
        Require(first.GetShaderParameter("local_cloud_shadow_uv_offset").AsVector2() == new Vector2(.1f, 0), "near offset value is stale");
        Require(first.GetShaderParameter("local_cloud_shadow_far_uv_offset").AsVector2() == new Vector2(0, .1f), "far offset value is stale");
        RingCloudShadowProjector.ResetParameterWriteCount();
        RingCloudShadowProjector.UpdateCascades(replacement, Vector3.Zero, around, across, up, 100, Vector2.Zero,
            far, Vector3.Right, around, across, up, 200, Vector2.Zero, 1000, Vector3.Right, .5f);
        Require(first.GetShaderParameter("local_cloud_shadow_texture").AsGodotObject() == replacement, "replacement texture not rebound");
        RingCloudShadowProjector.Disable();
        Require(!(bool)first.GetShaderParameter("local_cloud_shadow_enabled"), "disable not applied");
        RingCloudShadowProjector.ResetParameterWriteCount();
        RingCloudShadowProjector.Disable();
        Require(RingCloudShadowProjector.ParameterWriteCount == 0, "repeated disable wrote parameters");
        RingCloudShadowProjector.ReleaseTextures();
        Require(first.GetShaderParameter("local_cloud_shadow_texture").AsGodotObject() != replacement, "fallback not rebound");
        RingCloudShadowProjector.Register(second);
        Require(second.GetShaderParameter("local_cloud_shadow_texture").AsGodotObject() != null, "new receiver missing fallback");
        first.SetShaderParameter("local_cloud_shadow_strength", 0.0f);
        RingCloudShadowProjector.Register(first);
        Require((float)first.GetShaderParameter("local_cloud_shadow_strength") != 0.0f, "reregister did not resync external edit");
        RingCloudShadowProjector.Update(near, Vector3.Zero, around, across, 100, .5f, .5f, 0, 1, 0, 1, up, 1000);
        RingCloudShadowProjector.UpdateTransform(Vector3.One, around, across);
        Require((Vector3)first.GetShaderParameter("local_cloud_shadow_center_world") == Vector3.One, "CPU transform update not applied");
        RingCloudShadowProjector.ResetParameterWriteCount();
        RingCloudShadowProjector.UpdateTransform(Vector3.One, around, across);
        Require(RingCloudShadowProjector.ParameterWriteCount == 0, "identical CPU transform wrote parameters");
        ShaderMaterial retired = new() { Shader = shader };
        RingCloudShadowProjector.Register(retired);
        retired.Dispose();
        RingCloudShadowProjector.ReleaseTextures();
        Require(RingCloudShadowProjector.ActiveReceiverCount == 2, "freed receiver was not pruned");
        Require(!RingCloudShadowProjector.HasExternalTextures, "released GPU texture references remain installed");
        GD.Print("SHADOW_RECEIVER_CACHE_BEHAVIOR=PASS full=27 unchanged=0 offsets=2 lifecycle=pass freed_receiver=pruned");
        return "PASS";
    }

    private static ImageTexture MakeTexture(byte value)
    {
        using Image image = Image.CreateFromData(1, 1, false, Image.Format.Rgba8, new byte[] { value, 0, 0, 255 });
        return ImageTexture.CreateFromImage(image);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new System.InvalidOperationException(message); }
}
