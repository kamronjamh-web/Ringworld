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
        Require(RingCloudShadowProjector.ParameterWriteCount == 18, $"new receiver did not full-sync: {RingCloudShadowProjector.ParameterWriteCount} writes");
        RingCloudShadowProjector.ResetParameterWriteCount();
        RingCloudShadowProjector.UpdateCascades(near, Vector3.Zero, around, across, up, 100, Vector2.Zero,
            far, Vector3.Right, around, across, up, 200, Vector2.Zero, 1000, Vector3.Right, .5f);
        RingCloudShadowProjector.ResetParameterWriteCount();
        RingCloudShadowProjector.UpdateCascades(near, Vector3.Zero, around, across, up, 100, Vector2.Zero,
            far, Vector3.Right, around, across, up, 200, Vector2.Zero, 1000, Vector3.Right, .5f);
        Require(RingCloudShadowProjector.ParameterWriteCount == 0, "identical cascade update wrote parameters");
        Require(RingCloudShadowProjector.GlobalWriteCount == 0, "identical cascade update republished globals");
        Require((bool)first.GetShaderParameter("local_cloud_shadow_use_globals"), "receiver did not opt into shared transforms");
        RingCloudShadowProjector.UpdateCascadeTransforms(Vector3.Zero, around, across, up, new Vector2(.1f, 0),
            Vector3.Right, around, across, up, new Vector2(0, .1f));
        Require(RingCloudShadowProjector.ParameterWriteCount == 0, "offset-only update wrote receiver materials");
        Require(RingCloudShadowProjector.GlobalWriteCount == 2, "offset-only update was not two global writes");
        Require(GlobalIs("ring_cloud_shadow_near_uv_offset", new Vector2(.1f, 0)), "near offset value is stale");
        Require(GlobalIs("ring_cloud_shadow_far_uv_offset", new Vector2(0, .1f)), "far offset value is stale");
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
        Require(GlobalIs("ring_cloud_shadow_near_center_world", Vector3.One), "CPU transform update not applied");
        RingCloudShadowProjector.ResetParameterWriteCount();
        RingCloudShadowProjector.UpdateTransform(Vector3.One, around, across);
        Require(RingCloudShadowProjector.ParameterWriteCount == 0, "identical CPU transform wrote parameters");
        Require(RingCloudShadowProjector.GlobalWriteCount == 0, "identical CPU transform republished globals");
        ShaderMaterial retired = new() { Shader = shader };
        RingCloudShadowProjector.Register(retired);
        retired.Dispose();
        RingCloudShadowProjector.ReleaseTextures();
        Require(RingCloudShadowProjector.ActiveReceiverCount == 2, "freed receiver was not pruned");
        Require(!RingCloudShadowProjector.HasExternalTextures, "released GPU texture references remain installed");
        GD.Print("SHADOW_RECEIVER_CACHE_BEHAVIOR=PASS full=18 unchanged=0 offsets=0material+2global lifecycle=pass freed_receiver=pruned");
        return "PASS";
    }

    /// <summary>Exercises the native Terrain3D material path after its owning node has entered the scene tree.</summary>
    public string RunNative(GodotObject material)
    {
        if (!GodotObject.IsInstanceValid(material) || !material.HasMethod("set_shader_param"))
            throw new System.InvalidOperationException("active Terrain3D material does not expose set_shader_param");

        using ImageTexture near = MakeTexture(73);
        using ImageTexture far = MakeTexture(149);
        Vector3 nearCenter = new(17.0f, 23.0f, 31.0f);
        Vector3 nearAround = new(0.0f, 0.0f, -1.0f);
        Vector3 nearAcross = Vector3.Up;
        Vector3 nearUp = Vector3.Right;
        RingCloudShadowProjector.Register(material);
        RingCloudShadowProjector.UpdateCascades(
            near, nearCenter, nearAround, nearAcross, nearUp, 4321.0f, new Vector2(.125f, .25f),
            far, new Vector3(-41.0f, 5.0f, 9.0f), nearAround, nearAcross, nearUp, 8765.0f, new Vector2(.5f, .75f),
            5_000_000.0f, new Vector3(.2f, .3f, .4f), .37f);
        Require(material.Call("get_shader_param", "local_cloud_shadow_texture").AsGodotObject() == near,
            "native Terrain3D receiver did not receive the near texture");
        Require(material.Call("get_shader_param", "local_cloud_shadow_use_globals").AsBool(),
            "native Terrain3D receiver did not opt into shared transforms");
        Require(GlobalIs("ring_cloud_shadow_near_center_world", nearCenter),
            "native Terrain3D receiver did not receive the cascade center");
        Require(GlobalIs("ring_cloud_shadow_near_axis_around", nearAround),
            "native Terrain3D receiver did not receive the cascade basis");
        Require(Mathf.IsEqualApprox(material.Call("get_shader_param", "local_cloud_shadow_strength").AsSingle(), .37f),
            "native Terrain3D receiver did not receive shadow strength");
        RingCloudShadowProjector.ResetParameterWriteCount();
        RingCloudShadowProjector.UpdateCascades(
            near, nearCenter, nearAround, nearAcross, nearUp, 4321.0f, new Vector2(.125f, .25f),
            far, new Vector3(-41.0f, 5.0f, 9.0f), nearAround, nearAcross, nearUp, 8765.0f, new Vector2(.5f, .75f),
            5_000_000.0f, new Vector3(.2f, .3f, .4f), .37f);
        Require(RingCloudShadowProjector.ParameterWriteCount == 0,
            "identical native Terrain3D cascade update wrote parameters");
        RingCloudShadowProjector.Disable();
        Require(!material.Call("get_shader_param", "local_cloud_shadow_enabled").AsBool(),
            "native Terrain3D receiver did not receive disable");
        RingCloudShadowProjector.ReleaseTextures();
        Require(material.Call("get_shader_param", "local_cloud_shadow_texture").AsGodotObject() != near,
            "native Terrain3D receiver did not receive fallback after release");
        GD.Print("NATIVE_TERRAIN3D_SHADOW_RECEIVER=PASS");
        return "PASS";
    }

    /// <summary>Checks the projector's published copy and, when the rendering server reports one, the live global.</summary>
    private static bool GlobalIs(string name, Variant expected)
    {
        if (!RingCloudShadowProjector.GetPublishedGlobal(name).Equals(expected)) return false;
        Variant live = RenderingServer.GlobalShaderParameterGet(name);
        return live.VariantType == Variant.Type.Nil || live.Equals(expected);
    }

    private static ImageTexture MakeTexture(byte value)
    {
        using Image image = Image.CreateFromData(1, 1, false, Image.Format.Rgba8, new byte[] { value, 0, 0, 255 });
        return ImageTexture.CreateFromImage(image);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new System.InvalidOperationException(message); }
}
