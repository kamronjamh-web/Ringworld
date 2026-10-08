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
            ValidateAtmosphereFrameWrites(layer, material.Shader!, RingAtmosphereRayFrame.FromMainCamera(grid, camera, sun.GlobalBasis.Z.Normalized()));
            using (var proxy = new RingTerrainProxySector())
                ValidateAtmosphereFrameWrites(proxy, material.Shader!, RingAtmosphereRayFrame.FromMainCamera(grid, camera, sun.GlobalBasis.Z.Normalized()),
                    "ApplyProxyAtmosphereMaterialFrame", "_proxyAtmosphereParameterWrites", "PROXY");
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
    private static void ValidateAtmosphereFrameWrites(object layer, Shader shader, RingAtmosphereRayFrame initial,
        string applyMethod = "ApplyTreeAtmosphereRayFrame", string counterField = "_treeAtmosphereParameterWrites", string label = "TREE")
    {
        using var material = new ShaderMaterial { Shader = shader };
        long Writes() => (long)layer.GetType().GetField(counterField, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(layer)!;
        void Apply(RingAtmosphereRayFrame frame, int expected, string observation)
        {
            long before = Writes();
            Invoke(layer, applyMethod, material, frame);
            long delta = Writes() - before;
            Require(delta == expected, $"{observation}: expected {expected} atmosphere writes, got {delta}");
            GD.Print($"{label}_ATMOSPHERE_WRITES observation={observation} writes={delta}");
        }
        Apply(initial, 9, "first_material");
        Apply(initial, 0, "stationary");
        var sunOnly = initial with { ToSun = new Vector3(.2f, .8f, .5f).Normalized() };
        Apply(sunOnly, 1, "sun_only");
        Require(((Vector3)material.GetShaderParameter("ring_atmosphere_to_sun")).Equals(sunOnly.ToSun), "sun-only ray update was lost");
        var moved = sunOnly with { CameraWorld = sunOnly.CameraWorld + Vector3.Right };
        Apply(moved, 1, "camera_movement");
        var rebased = moved with { CameraWorld = moved.CameraWorld + new Vector3(4_000_000, 0, 0), OutwardWorld = Vector3.Right };
        Apply(rebased, moved.OutwardWorld.Equals(Vector3.Right) ? 1 : 2, "rebase");
        var smallRebase = rebased with { CameraWorld = rebased.CameraWorld + Vector3.Right };
        Apply(smallRebase, 1, "small_rebase_large_coordinates");
        Require(((Vector3)material.GetShaderParameter("ring_atmosphere_camera_world_position")).Equals(smallRebase.CameraWorld), "small rebase ray update was lost");
        var geometry = smallRebase with { SidewallPositiveMeters = smallRebase.SidewallPositiveMeters + 32, AxisWorld = Vector3.Right };
        Apply(geometry, smallRebase.AxisWorld.Equals(Vector3.Right) ? 1 : 2, "geometry");
        Require(((Vector3)material.GetShaderParameter("ring_atmosphere_outward_world")).Equals(geometry.OutwardWorld)
            && ((Vector3)material.GetShaderParameter("ring_atmosphere_axis_world")).Equals(geometry.AxisWorld)
            && ((float)material.GetShaderParameter("ring_atmosphere_sidewall_positive_meters")).Equals(geometry.SidewallPositiveMeters),
            "geometry/rebase atmosphere shader values were stale");
        var dimensions = geometry with { CameraHeightMeters = geometry.CameraHeightMeters + 8,
            SidewallNegativeMeters = geometry.SidewallNegativeMeters + 16, CoordinateScale = .5f };
        Apply(dimensions, 3, "height_sidewall_scale");
        Require(((float)material.GetShaderParameter("ring_atmosphere_camera_height_meters")).Equals(dimensions.CameraHeightMeters)
            && ((float)material.GetShaderParameter("ring_atmosphere_sidewall_negative_meters")).Equals(dimensions.SidewallNegativeMeters)
            && ((float)material.GetShaderParameter("ring_atmosphere_coordinate_scale")).Equals(dimensions.CoordinateScale),
            "height/sidewall/scale atmosphere shader values were stale");
        Apply(RingAtmosphereRayFrame.Invalid, 1, "invalid");
        Require(!(bool)material.GetShaderParameter("ring_atmosphere_ray_frame_valid"), "invalid atmosphere frame remained active");
        Apply(RingAtmosphereRayFrame.Invalid, 0, "invalid_stationary");
        Apply(dimensions, 9, "valid_again");
        Require((bool)material.GetShaderParameter("ring_atmosphere_ray_frame_valid"), "recovered atmosphere frame remained inactive");
        using var replacement = new ShaderMaterial { Shader = shader };
        long beforeReplacement = Writes();
        Invoke(layer, applyMethod, replacement, dimensions);
        Require(Writes() - beforeReplacement == 9, "replacement material missed full atmosphere publication");
        Require((bool)replacement.GetShaderParameter("ring_atmosphere_ray_frame_valid")
            && ((Vector3)replacement.GetShaderParameter("ring_atmosphere_camera_world_position")).Equals(dimensions.CameraWorld)
            && ((Vector3)replacement.GetShaderParameter("ring_atmosphere_to_sun")).Equals(dimensions.ToSun),
            "replacement atmosphere shader values were stale");
        GD.Print($"{label}_ATMOSPHERE_WRITES observation=replacement_material writes=9");
    }
    private static void SetField(object instance, string name, object value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private static bool Approximately(float a, float b) => Mathf.IsEqualApprox(a, b);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
