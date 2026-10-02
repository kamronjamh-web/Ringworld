using System;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Rendered regression fixture for the optional distant grass camera-facing
/// comparison. It uses the production shader and clump prototype, while the
/// saved images are intentionally retained for human visual review.
/// </summary>
public partial class GrassCameraFacingTest : Node3D
{
    public string Result { get; private set; } = "RUNNING";
    private Camera3D? _camera;
    private ShaderMaterial? _material;
    private MultiMeshInstance3D? _grass;

    public async void Start()
    {
        try { await Run(); Result = "PASS production camera-facing captures, source metadata, finite mesh attributes, and near-pass invariance"; }
        catch (Exception error) { Result = "FAIL " + error.Message; }
        if (GodotObject.IsInstanceValid(_grass)) _grass?.QueueFree();
        if (GodotObject.IsInstanceValid(_camera)) _camera?.QueueFree();
        await Frames(2);
    }

    private async Task Run()
    {
        ValidateCameraFacingMaterialCache();
        ValidateClumpSourceMetadata();
        _camera = new Camera3D { Current = true, Near = .05f, Far = 100f, Fov = 55f };
        AddChild(_camera);
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-52, 31, 0), LightEnergy = 1.2f });
        AddChild(new WorldEnvironment { Environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(.22f, .35f, .52f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = Colors.White, AmbientLightEnergy = .55f
        }});
        AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(60, 60), Material = new StandardMaterial3D { AlbedoColor = new Color(.16f, .12f, .06f) } } });
        _material = CreateMaterial();
        var multi = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, UseCustomData = true, Mesh = RingGrassMeshFactory.CreateIntermediateClump(), InstanceCount = 1 };
        multi.SetInstanceTransform(0, new Transform3D(Basis.Identity.Scaled(new Vector3(2.2f, 2.2f, 2.2f)), Vector3.Zero));
        multi.SetInstanceColor(0, Colors.White); multi.SetInstanceCustomData(0, new Color(.55f, .45f, .21f, .73f));
        _grass = new MultiMeshInstance3D { Multimesh = multi, MaterialOverride = _material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _grass.SetInstanceShaderParameter("grass_instance_count", 1f);
        AddChild(_grass);
        RenderingServer.GlobalShaderParameterSet("ring_local_wind_enabled", 0f);

        SetCamera(new Vector3(14, 6, 16), Vector3.Zero);
        _grass.SetInstanceShaderParameter("grass_lod_pass", 0);
        _material.SetShaderParameter("grass_camera_facing_enabled", false);
        Image nearOff = await Capture("near_pass_off");
        _grass.Visible = false;
        Image nearWithoutGrass = await Capture("near_without_grass");
        _grass.Visible = true;
        Require(CountDifferentPixels(nearOff, nearWithoutGrass) > 128, "near capture contains no visible grass");
        _material.SetShaderParameter("grass_camera_facing_enabled", true);
        Image nearOn = await Capture("near_pass_on");
        Require(SamePixels(nearOff, nearOn), "camera-facing changed the authored near pass");

        _grass.SetInstanceShaderParameter("grass_lod_pass", 1);
        _material.SetShaderParameter("grass_camera_facing_enabled", false);
        Image midOff = await Capture("mode_off_mid");
        _material.SetShaderParameter("grass_camera_facing_enabled", true);
        SetCamera(new Vector3(14, 6, 16), Vector3.Zero);
        Image midAzimuthA = await Capture("mode_on_azimuth_a");
        Require(CountDifferentPixels(midOff, midAzimuthA) > 128, "distant camera-facing mode did not change rendered grass");
        SetCamera(new Vector3(-16, 5, 11), Vector3.Zero); await Capture("mode_on_azimuth_b");
        SetCamera(new Vector3(0, 24, .001f), Vector3.Zero); await Capture("mode_on_top_down");
    }

    private static void ValidateCameraFacingMaterialCache()
    {
        var layer = new RingStandingGrassLayer { EnableGrassCameraFacing = true, GrassCameraFacingStrength = .75f,
            GrassCameraFacingStartMeters = 12f, GrassCameraFacingEndMeters = 48f };
        var near = new ShaderMaterial(); var intermediate = new ShaderMaterial();
        try
        {
            MethodInfo? configure = typeof(RingStandingGrassLayer).GetMethod("ConfigureGrassCameraFacingMaterial", BindingFlags.Instance | BindingFlags.NonPublic);
            Require(configure != null, "camera-facing material configuration unavailable");
            Configure(layer, configure!, near); Configure(layer, configure!, intermediate);
            AssertCameraFacingSettings(near, .75f, 12f, 48f, "initial near");
            AssertCameraFacingSettings(intermediate, .75f, 12f, 48f, "initial intermediate");
            // The old shifted-XOR fingerprint collides for these two range pairs.
            layer.GrassCameraFacingStartMeters = 14f; layer.GrassCameraFacingEndMeters = 52f;
            Configure(layer, configure!, near); Configure(layer, configure!, intermediate);
            AssertCameraFacingSettings(near, .75f, 14f, 52f, "collision near");
            AssertCameraFacingSettings(intermediate, .75f, 14f, 52f, "collision intermediate");
            layer.GrassCameraFacingStartMeters = 12f; layer.GrassCameraFacingEndMeters = 48f;
            Configure(layer, configure!, near); Configure(layer, configure!, intermediate);
            AssertCameraFacingSettings(near, .75f, 12f, 48f, "reset near");
            AssertCameraFacingSettings(intermediate, .75f, 12f, 48f, "reset intermediate");
        }
        finally
        {
            near.Dispose(); intermediate.Dispose(); layer.Free();
        }
    }

    private static void Configure(RingStandingGrassLayer layer, MethodInfo method, ShaderMaterial material) => method.Invoke(layer, new object?[] { material });
    private static void AssertCameraFacingSettings(ShaderMaterial material, float strength, float start, float end, string label)
    {
        Require(Mathf.IsEqualApprox(material.GetShaderParameter("grass_camera_facing_strength").AsSingle(), strength), label + " strength stale");
        Require(Mathf.IsEqualApprox(material.GetShaderParameter("grass_camera_facing_start").AsSingle(), start), label + " start stale");
        Require(Mathf.IsEqualApprox(material.GetShaderParameter("grass_camera_facing_end").AsSingle(), end), label + " end stale");
    }

    private static ShaderMaterial CreateMaterial()
    {
        Shader? shader = GD.Load<Shader>("res://materials/ring_standing_grass.gdshader");
        Require(shader != null, "production grass shader unavailable");
        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("grass_lod_enabled", false);
        material.SetShaderParameter("grass_camera_facing_strength", 1f);
        material.SetShaderParameter("grass_camera_facing_start", 0f);
        material.SetShaderParameter("grass_camera_facing_end", 1f);
        material.SetShaderParameter("grass_gust_enabled", false);
        return material;
    }

    private void SetCamera(Vector3 position, Vector3 target)
    {
        _camera!.GlobalPosition = position;
        _camera.LookAt(target, Vector3.Up);
    }

    private async Task<Image> Capture(string label)
    {
        await Frames(4);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image image = GetViewport().GetTexture().GetImage();
        Require(!image.IsEmpty() && image.GetWidth() > 0 && image.GetHeight() > 0, label + " produced no image");
        string directory = ProjectSettings.GlobalizePath("res://artifacts/grass-camera-facing");
        Directory.CreateDirectory(directory);
        Require(image.SavePng(Path.Combine(directory, label + ".png")) == Error.Ok, label + " capture save failed");
        return image;
    }

    private static void ValidateClumpSourceMetadata()
    {
        Mesh mesh = RingGrassMeshFactory.CreateIntermediateClump();
        ArrayMesh arrayMesh = mesh as ArrayMesh ?? throw new InvalidOperationException("grass prototype did not produce an ArrayMesh");
        Godot.Collections.Array arrays = mesh.SurfaceGetArrays(0);
        Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        Vector3[] normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
        float[] source = arrays[(int)Mesh.ArrayType.Custom0].AsFloat32Array();
        ulong customFormat = ((ulong)arrayMesh.SurfaceGetFormat(0) >> (int)Mesh.ArrayFormat.FormatCustom0Shift)
            & (ulong)Mesh.ArrayFormat.FormatCustomMask;
        Require(customFormat == (ulong)Mesh.ArrayCustomFormat.RgbaFloat, "CUSTOM0 is not RGBA_FLOAT");
        Require(source.Length == vertices.Length * 4 && normals.Length == vertices.Length, "CUSTOM0 was not retained as four floats per vertex");
        int nonZeroRoots = 0;
        var sourceFrames = new Dictionary<(int X, int Z), (int SideX, int SideZ)>();
        for (int i = 0; i < vertices.Length; i++)
        {
            int at = i * 4;
            float rootX = source[at], rootZ = source[at + 1], sideX = source[at + 2], sideZ = source[at + 3];
            Require(IsFinite(vertices[i]) && IsFinite(normals[i]) && float.IsFinite(rootX) && float.IsFinite(rootZ) && float.IsFinite(sideX) && float.IsFinite(sideZ), "non-finite clump mesh attribute");
            Require(Mathf.Abs(rootX) <= 2.51f && Mathf.Abs(rootZ) <= 2.51f, "source root precision/range changed");
            Require(Mathf.Abs(sideX) > .1f || Mathf.Abs(sideZ) > .1f, "source side missing");
            if (rootX * rootX + rootZ * rootZ > 1f) nonZeroRoots++;
            var root = ((int)MathF.Round(rootX * 1000f), (int)MathF.Round(rootZ * 1000f));
            var side = ((int)MathF.Round(sideX * 1000f), (int)MathF.Round(sideZ * 1000f));
            if (sourceFrames.TryGetValue(root, out var prior)) Require(prior == side, "crossed cards do not share a source axis");
            else sourceFrames.Add(root, side);
        }
        Require(nonZeroRoots == vertices.Length, "clump source offsets were normalized or reset");
        Require(sourceFrames.Count == 4, "production clump did not retain four distinct source anchors");
    }

    private static bool SamePixels(Image a, Image b)
    {
        if (a.GetWidth() != b.GetWidth() || a.GetHeight() != b.GetHeight() || a.GetFormat() != b.GetFormat()) return false;
        byte[] left = a.GetData(), right = b.GetData();
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
        return true;
    }
    private static int CountDifferentPixels(Image a, Image b)
    {
        if (a.GetWidth() != b.GetWidth() || a.GetHeight() != b.GetHeight() || a.GetFormat() != b.GetFormat()) return int.MaxValue;
        byte[] left = a.GetData(), right = b.GetData();
        int changed = 0;
        for (int i = 0; i < left.Length; i += 4)
        {
            if (left[i] != right[i] || left[i + 1] != right[i + 1] || left[i + 2] != right[i + 2] || left[i + 3] != right[i + 3]) changed++;
        }
        return changed;
    }
    private async Task Frames(int count) { for (int i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
