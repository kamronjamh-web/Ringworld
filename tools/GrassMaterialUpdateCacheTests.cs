using System;
using System.Reflection;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tests;

/// <summary>Exact publication contracts, including reused projection arrays and replacement resources.</summary>
public partial class GrassMaterialUpdateCacheTests : RefCounted
{
    public bool Run()
    {
        try { Verify(); GD.Print("GRASS_MATERIAL_UPDATE_CACHE=PASS"); return true; }
        catch (Exception error) { GD.PushError("GRASS_MATERIAL_UPDATE_CACHE=FAIL " + error); return false; }
    }

    private static void Verify()
    {
        var layer = new RingStandingGrassLayer();
        FieldInfo atlasField = typeof(RingStandingGrassLayer).GetField("_grassModifierAtlas", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            using var near = new ShaderMaterial();
            using var intermediate = new ShaderMaterial();
            using var replacement = new ShaderMaterial();
            MethodInfo interaction = typeof(RingStandingGrassLayer).GetMethod("ConfigureInteractionMaterial", BindingFlags.Instance | BindingFlags.NonPublic)!;
            PropertyInfo count = typeof(RingStandingGrassLayer).GetProperty(nameof(RingStandingGrassLayer.ActiveGrassInteractionSampleCount))!;
            Vector4[] points = new Vector4[RingGrassInteractionField.Capacity];
            void Publish(ShaderMaterial material) => interaction.Invoke(layer, new object[] { material, points });
            Publish(near); Publish(intermediate);
            Require(layer.InteractionParameterWrites == 16, "both materials must receive complete initial interaction state");
            Publish(near); Publish(intermediate);
            Require(layer.InteractionParameterWrites == 16, "unchanged empty state repeated writes");
            count.SetValue(layer, 1); points[0] = new Vector4(1, 2, 3, 1);
            Publish(near); Publish(intermediate);
            Require(layer.InteractionParameterWrites == 22, "active trail should change only enabled/count/points");
            points[0].W = .5f;
            Publish(near); Publish(intermediate);
            Require(layer.InteractionParameterWrites == 24, "reused projection array decay was missed or static controls repeated");
            Require(near.GetShaderParameter("grass_interaction_points").AsVector4Array()[0].W == .5f,
                "decayed points did not reach shader");
            points[0].X = 1000;
            Publish(near); Publish(intermediate);
            Require(layer.InteractionParameterWrites == 26, "reprojection should publish only points");
            layer.GrassInteractionRadiusMeters = 2;
            Publish(near); Publish(intermediate);
            Require(layer.InteractionParameterWrites == 36 && near.GetShaderParameter("grass_interaction_radius").AsSingle() == 2,
                "changed controls were missed");
            layer.EnableGrassInteraction = false; Publish(near);
            Require(!near.GetShaderParameter("grass_interaction_enabled").AsBool(), "disable was missed");
            layer.EnableGrassInteraction = true; Publish(near);
            Require(near.GetShaderParameter("grass_interaction_enabled").AsBool(), "restore was missed");
            long writes = layer.InteractionParameterWrites;
            Publish(replacement);
            Require(layer.InteractionParameterWrites == writes + 8, "replacement interaction material inherited old cache");
            count.SetValue(layer, 0); Array.Clear(points); Publish(near);
            Require(!near.GetShaderParameter("grass_interaction_enabled").AsBool()
                && near.GetShaderParameter("grass_interaction_count").AsInt32() == 0,
                "expired trail did not clear shader state");

            using var atlas = CreateAtlas(); using var nextAtlas = CreateAtlas();
            layer.ConfigureGrassModifierMaterial(near); layer.ConfigureGrassModifierMaterial(intermediate);
            Require(layer.ModifierParameterWrites == 4, "missing-atlas initial states were not published");
            layer.ConfigureGrassModifierMaterial(near); layer.ConfigureGrassModifierMaterial(intermediate);
            Require(layer.ModifierParameterWrites == 4, "unchanged modifier state repeated writes");
            atlasField.SetValue(layer, atlas);
            layer.ConfigureGrassModifierMaterial(near); layer.ConfigureGrassModifierMaterial(intermediate);
            Require(layer.ModifierParameterWrites == 10 && near.GetShaderParameter("grass_modifier_enabled").AsBool(),
                "atlas creation did not enable both materials");
            layer.GrassModifierTintStrength = .4f; layer.ConfigureGrassModifierMaterial(near);
            Require(near.GetShaderParameter("grass_modifier_tint_strength").AsSingle() == .4f, "tint change was missed");
            layer.EnableGrassModifiers = false; layer.ConfigureGrassModifierMaterial(near);
            Require(!near.GetShaderParameter("grass_modifier_enabled").AsBool(), "modifier disable was missed");
            layer.EnableGrassModifiers = true; layer.ConfigureGrassModifierMaterial(near);
            Require(near.GetShaderParameter("grass_modifier_enabled").AsBool(), "modifier restore was missed");
            atlasField.SetValue(layer, nextAtlas); layer.ConfigureGrassModifierMaterial(near);
            Require(near.GetShaderParameter("grass_modifier_atlas").AsGodotObject() == nextAtlas, "atlas replacement retained old binding");
            writes = layer.ModifierParameterWrites; layer.ConfigureGrassModifierMaterial(replacement);
            Require(layer.ModifierParameterWrites == writes + 3, "replacement modifier material inherited old cache");
            // Exercise the actual disposal invalidation; detach externally owned test textures first.
            atlasField.SetValue(layer, null);
            typeof(RingStandingGrassLayer).GetMethod("DisposeGrassModifierTint", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(layer, null);
            layer.ConfigureGrassModifierMaterial(near);
            Require(!near.GetShaderParameter("grass_modifier_enabled").AsBool(), "atlas disposal did not disable modifiers");
            atlasField.SetValue(layer, atlas); layer.ConfigureGrassModifierMaterial(near);
            Require(near.GetShaderParameter("grass_modifier_atlas").AsGodotObject() == atlas
                && near.GetShaderParameter("grass_modifier_enabled").AsBool(), "atlas recreation did not restore binding");
            atlasField.SetValue(layer, null);
        }
        finally
        {
            // Test atlases belong to their using scopes, even when an assertion throws.
            atlasField.SetValue(layer, null);
            layer.Free();
        }
    }

    private static Texture2DArray CreateAtlas()
    {
        using Image image = Image.CreateFromData(1, 1, false, Image.Format.Rgba8, new byte[4]);
        var atlas = new Texture2DArray();
        Require(atlas.CreateFromImages(new Godot.Collections.Array<Image> { image }) == Error.Ok, "test atlas creation failed");
        return atlas;
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
