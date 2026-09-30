using Godot;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using System;
using System.Reflection;

namespace RingworldTechDemo;

/// <summary>Small test-only bridge so the GPU contract uses the production KayKit loader.</summary>
public partial class TreeLightingShadowBehaviorTest : RefCounted
{
    public Mesh? LoadNormalizedKayKitTree() => KayKitTreeMeshLoader.LoadNormalizedMesh(
        "res://resources/trees/kaykit_forest/Tree_2_A_Color1.gltf");

    /// <summary>Exercises private decoration wiring without generating a world.</summary>
    public bool Run(Node parent)
    {
        Node3D harness = new() { Name = "TreeLightingBehaviorHarness" };
        bool originalAtmosphereEnabled = RingAtmosphereModel.Enabled;
        try
        {
            parent.AddChild(harness);
            var camera = new Camera3D { Name = "TestCamera", Position = new Vector3(0, 3, 12) };
            harness.AddChild(camera);
            camera.MakeCurrent();
            var sun = new DirectionalLight3D { Name = "TestSun" };
            harness.AddChild(sun);
            sun.LookAt(Vector3.Right, Vector3.Up);
            var grid = new VoxelChunkGridManager {
                Name = "ChunkGridManager", BuildOnReady = false, DormantEditableBackend = true,
                EnableChunkStreaming = false, RingSunLightPath = new NodePath("../TestSun")
            };
            harness.AddChild(grid);
            var layer = new RingBiomeDecorationLayer { Name = "Decoration", Enabled = false };
            harness.AddChild(layer);

            SetField(layer, "_grid", grid);
            ShaderMaterial material = (ShaderMaterial)Invoke(layer, "CreateTreeMaterial", new Color(.2f, .5f, .1f, 1), false)!;
            Require(((Vector3)material.GetShaderParameter("sun_direction")).IsEqualApprox(sun.GlobalBasis.Z.Normalized()),
                "new material did not receive the actual sun direction");
            Require((Vector3)material.GetShaderParameter("ring_center") == grid.GetCanonicalRingCenterWorld(),
                "new material did not receive the canonical ring center");
            // Load and register every production tree shader. This verifies the
            // same imported and shared-impostor material paths used by renderers.
            ShaderMaterial imported = new() { Shader = ResourceLoader.Load<Shader>("res://materials/imported_tree.gdshader") };
            ShaderMaterial impostor = new() { Shader = ResourceLoader.Load<Shader>("res://materials/imported_tree_impostor.gdshader") };
            Invoke(layer, "RegisterTreeLightingMaterial", imported);
            Invoke(layer, "RegisterTreeLightingMaterial", impostor);
            Require((float)material.GetShaderParameter("ring_radius_meters") > 0.0f
                && (float)imported.GetShaderParameter("ring_radius_meters") > 0.0f
                && (float)impostor.GetShaderParameter("ring_radius_meters") > 0.0f,
                "tree renderer material registration missed the atmosphere profile");
            // First synchronization must replace stale fields even while nothing streams.
            material.SetShaderParameter("day_start_dot", 99.0f);
            layer._Process(0.0);
            Require(Approximately((float)material.GetShaderParameter("day_start_dot"), grid.RingDayStartDot),
                "first process did not synchronize configuration");
            Require((bool)material.GetShaderParameter("ring_atmosphere_ray_frame_valid")
                && (Vector3)material.GetShaderParameter("ring_atmosphere_camera_world_position") == camera.GlobalPosition,
                "tree atmosphere ray frame did not use the active camera");
            RingAtmosphereModel.Enabled = !originalAtmosphereEnabled;
            Require((bool)imported.GetShaderParameter("ring_atmosphere_enabled") == !originalAtmosphereEnabled
                && (bool)impostor.GetShaderParameter("ring_atmosphere_enabled") == !originalAtmosphereEnabled,
                "atmosphere profile change did not reach imported tree materials");
            RingAtmosphereModel.Enabled = originalAtmosphereEnabled;
            Vector3 initialAtmosphereCamera = (Vector3)material.GetShaderParameter("ring_atmosphere_camera_world_position");
            camera.GlobalPosition += new Vector3(32, 0, 0);
            layer._Process(0.0);
            Require((Vector3)material.GetShaderParameter("ring_atmosphere_camera_world_position") == camera.GlobalPosition
                && (Vector3)material.GetShaderParameter("ring_atmosphere_camera_world_position") != initialAtmosphereCamera,
                "tree atmosphere ray frame was not refreshed after camera rebase");
            Vector3 initialSun = (Vector3)material.GetShaderParameter("sun_direction");

            // A sentinel on a synchronized field detects unwanted configuration writes.
            material.SetShaderParameter("day_start_dot", 99.0f);
            layer._Process(0.0);
            Require(Approximately((float)material.GetShaderParameter("day_start_dot"), 99.0f),
                "unchanged frame resent configuration");
            sun.RotateObjectLocal(Vector3.Up, .4f);
            layer._Process(0.0);
            Vector3 changedSun = (Vector3)material.GetShaderParameter("sun_direction");
            Require(!changedSun.IsEqualApprox(initialSun)
                && changedSun.IsEqualApprox(sun.GlobalBasis.Z.Normalized()),
                "stationary process did not follow sun rotation");
            Require(Approximately((float)material.GetShaderParameter("day_start_dot"), 99.0f),
                "sun-only update resent configuration");
            grid.RingDayStartDot = -.3f;
            layer._Process(0.0);
            Require(Approximately((float)material.GetShaderParameter("day_start_dot"), -.3f),
                "live configuration change did not synchronize");

            grid.Position = new Vector3(4_000_000, 0, 0);
            layer._Process(0.0);
            grid.Position += Vector3.Right;
            layer._Process(0.0);
            Require((Vector3)material.GetShaderParameter("ring_center") == grid.GetCanonicalRingCenterWorld(),
                "small center shift was lost at large world coordinates");
            ShaderMaterial lateMaterial = (ShaderMaterial)Invoke(layer, "CreateTreeMaterial", Colors.White, false)!;
            Require((Vector3)lateMaterial.GetShaderParameter("ring_center") == grid.GetCanonicalRingCenterWorld()
                && ((Vector3)lateMaterial.GetShaderParameter("sun_direction")).IsEqualApprox(changedSun)
                && Approximately((float)lateMaterial.GetShaderParameter("day_start_dot"), -.3f),
                "material created after cache warm-up missed lighting state");

            MultiMeshInstance3D close = (MultiMeshInstance3D)Invoke(layer, "CreateInstances", "Close", new BoxMesh(), material, true, 0)!;
            MultiMeshInstance3D billboard = (MultiMeshInstance3D)Invoke(layer, "CreateInstances", "Billboard", new QuadMesh(), material, false, -1)!;
            Require(close.CastShadow == GeometryInstance3D.ShadowCastingSetting.On
                && billboard.CastShadow == GeometryInstance3D.ShadowCastingSetting.Off,
                "3D/billboard shadow policy was not applied");
            return true;
        }
        catch (Exception exception)
        {
            GD.PushError("TREE_LIGHTING_SHADOW_BEHAVIOR=FAIL " + exception.Message);
            return false;
        }
        finally
        {
            RingAtmosphereModel.Enabled = originalAtmosphereEnabled;
            if (GodotObject.IsInstanceValid(harness)) harness.QueueFree();
        }
    }

    private static object? Invoke(object instance, string name, params object?[] arguments) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, arguments);
    private static void SetField(object instance, string name, object value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private static bool Approximately(float a, float b) => Mathf.IsEqualApprox(a, b);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
