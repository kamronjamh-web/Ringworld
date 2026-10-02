using System;
using System.IO;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.LOD;

/// <summary>Rendered Forward+ regression for the production grass modifier tint atlas.</summary>
public partial class GrassModifierTintFixture : Node3D
{
    public string Result { get; private set; } = "RUNNING";
    private Camera3D? _camera; private Node3D? _fieldParent; private MultiMeshInstance3D? _grass; private ShaderMaterial? _material;
    public async void Start() { try { await Run(); Result = "PASS rendered RGBA8 modifier tint, covectors, and transformed-parent coverage"; } catch (Exception error) { Result = "FAIL " + error.Message; } _fieldParent?.QueueFree(); _camera?.QueueFree(); await Frames(2); }
    private async Task Run()
    {
        BuildWorld(); _material = CreateProductionMaterial(CreateRedAtlas());
        _fieldParent = new Node3D { Name = "ModifierTintField" }; AddChild(_fieldParent);
        _grass = new MultiMeshInstance3D { Multimesh = CreateFieldMesh(), MaterialOverride = _material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _grass.SetInstanceShaderParameter("grass_lod_pass", 0); _grass.SetInstanceShaderParameter("grass_instance_count", 9f); _fieldParent.AddChild(_grass);
        RenderingServer.GlobalShaderParameterSet("ring_local_wind_enabled", 0f);
        _camera!.GlobalPosition = new Vector3(15, 10, 19); _camera.LookAt(Vector3.Zero, Vector3.Up);
        ConfigureCovectors(); _material.SetShaderParameter("grass_modifier_enabled", false); Image baseline = await Capture("identity_off");
        _material.SetShaderParameter("grass_modifier_enabled", true); Image tinted = await Capture("identity_red"); AssertVisibleRedTint(baseline, tinted, "identity");
        _fieldParent.Transform = new Transform3D(new Basis(Vector3.Up, .72f).Scaled(new Vector3(1.55f, .85f, .62f)), new Vector3(4, .4f, -3));
        ConfigureCovectors(); _material.SetShaderParameter("grass_modifier_enabled", false); Image transformedOff = await Capture("transformed_off");
        _material.SetShaderParameter("grass_modifier_enabled", true); Image transformedRed = await Capture("transformed_red"); AssertVisibleRedTint(transformedOff, transformedRed, "rotated translated nonuniform parent");
    }
    private void BuildWorld()
    {
        _camera = new Camera3D { Current = true, Near = .05f, Far = 100, Fov = 55 }; AddChild(_camera);
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-48, 28, 0), LightEnergy = 1.25f });
        AddChild(new WorldEnvironment { Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(.16f, .28f, .42f), AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = Colors.White, AmbientLightEnergy = .55f } });
        AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(80, 80), Material = new StandardMaterial3D { AlbedoColor = new Color(.18f, .13f, .07f) } } });
    }
    private static MultiMesh CreateFieldMesh()
    {
        var multi = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, UseCustomData = true, Mesh = RingGrassMeshFactory.CreateIntermediateClump(), InstanceCount = 9 };
        int index = 0; for (int z = -1; z <= 1; z++) for (int x = -1; x <= 1; x++, index++)
        { multi.SetInstanceTransform(index, new Transform3D(Basis.Identity.Scaled(new Vector3(1.25f, 1.8f, 1.25f)), new Vector3(x * 5, 0, z * 5))); multi.SetInstanceColor(index, Colors.White); multi.SetInstanceCustomData(index, new Color(.55f, .45f, .2f + index * .03f, .7f)); }
        return multi;
    }
    private static ShaderMaterial CreateProductionMaterial(Texture2DArray atlas)
    {
        Shader? shader = GD.Load<Shader>("res://materials/ring_standing_grass.gdshader"); Require(shader != null, "production grass shader unavailable");
        var material = new ShaderMaterial { Shader = shader }; material.SetShaderParameter("grass_lod_enabled", false); material.SetShaderParameter("grass_gust_enabled", false); material.SetShaderParameter("grass_modifier_atlas", atlas); material.SetShaderParameter("grass_modifier_tint_strength", 1f); return material;
    }
    private static Texture2DArray CreateRedAtlas()
    {
        // A visible checker prevents a passing test from proving only that a
        // uniform texture was sampled somewhere. Transparent squares expose
        // a wrong rotated/nonuniform parent projection in the capture.
        byte[] pixels = new byte[32 * 32 * 4];
        for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
        {
            int i = (y * 32 + x) * 4;
            if (((x / 4) + (y / 4)) % 2 == 0) { pixels[i] = 255; pixels[i + 1] = 8; pixels[i + 2] = 8; pixels[i + 3] = 255; }
        }
        using Image image = Image.CreateFromData(32, 32, false, Image.Format.Rgba8, pixels); var images = new Godot.Collections.Array<Image> { image }; var atlas = new Texture2DArray(); Require(atlas.CreateFromImages(images) == Error.Ok, "RGBA8 modifier atlas creation"); return atlas;
    }
    private void ConfigureCovectors()
    {
        Transform3D transform = _grass!.GlobalTransform;
        // The rows of inverse(B) are the horizontal dual basis. This remains
        // correct for the transformed-parent fixture even when B is sheared.
        Vector3 aroundCovector = transform.Basis.Inverse().Transposed() * Vector3.Right;
        Vector3 acrossCovector = transform.Basis.Inverse().Transposed() * Vector3.Forward;
        _grass.SetInstanceShaderParameter("grass_modifier_layer", 0); _grass.SetInstanceShaderParameter("grass_modifier_origin_world", transform.Origin);
        _grass.SetInstanceShaderParameter("grass_modifier_around_world", new Vector4(aroundCovector.X, aroundCovector.Y, aroundCovector.Z, 1f / 18f));
        _grass.SetInstanceShaderParameter("grass_modifier_across_world", new Vector4(acrossCovector.X, acrossCovector.Y, acrossCovector.Z, 1f / 18f));
    }
    private async Task<Image> Capture(string label)
    {
        await Frames(4); await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw); Image image = GetViewport().GetTexture().GetImage(); Require(!image.IsEmpty() && image.GetWidth() > 0, label + " empty capture"); image.Convert(Image.Format.Rgba8);
        string directory = ProjectSettings.GlobalizePath("res://artifacts/grass-modifiers"); Directory.CreateDirectory(directory); Require(image.SavePng(Path.Combine(directory, label + ".png")) == Error.Ok, label + " capture save failed"); return image;
    }
    private static void AssertVisibleRedTint(Image off, Image on, string label)
    {
        byte[] before = off.GetData(), after = on.GetData(); Require(before.Length == after.Length && before.Length > 0, label + " incompatible captures"); int changedPixels = 0, redPixels = 0; long redGain = 0;
        for (int i = 0; i < before.Length; i += 4) { int redDelta = after[i] - before[i]; if (before[i] != after[i] || before[i + 1] != after[i + 1] || before[i + 2] != after[i + 2]) changedPixels++; if (redDelta > 20 && after[i] > after[i + 1] + 20 && after[i] > after[i + 2] + 20) redPixels++; if (redDelta > 0) redGain += redDelta; }
        Require(changedPixels > 128, label + " modifier did not affect a rendered region"); Require(redPixels > 64 && redGain > 8_000, label + " modifier region did not produce a substantial red tint");
    }
    private async Task Frames(int count) { for (int i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
