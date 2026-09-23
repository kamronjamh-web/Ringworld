using Godot;
using System;

namespace RingworldTechDemo.UI;

/// <summary>Controller-driven, deterministic menu flyover cloud renderer.</summary>
public partial class MenuFlyoverCloudLayer : Control
{
    [Export]
    public int Seed { get; set; } = 1337;

    public Texture2D? ShadowTexture => _shadowViewport?.GetTexture();
    public Vector2 ShadowOriginKm { get; private set; }
    public float ShadowExtentKm => ShadowSpanKm;
    public Vector2 ShadowWindKm { get; private set; }

    private const float ShadowSpanKm = 256f;
    private const float ShadowInterval = .2f;

    private SubViewport? _cloudViewport;
    private SubViewport? _shadowViewport;
    private ShaderMaterial? _cloudMaterial;
    private ShaderMaterial? _shadowMaterial;
    private ImageTexture3D? _noiseTexture;
    private TextureRect? _composite;
    private int _renderWidth;
    private int _renderHeight;
    private int _updateCount;
    private int _shadowUpdateCount;
    private double _lastShadowAnimationTime = double.NegativeInfinity;
    private ulong _lastShadowRealMsec;
    private float _radiusKm;
    private float _ringWidthKm;
    private Vector3 _sunDirection = new(-.4f, .75f, -.5f);
    private bool _configured;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        BuildChildren();
    }

    public void Configure(int seed, float radiusKm, float ringWidthKm, Vector3 sunDirection)
    {
        Seed = seed;
        _radiusKm = Mathf.Max(1f, radiusKm);
        _ringWidthKm = Mathf.Max(1f, ringWidthKm);
        _sunDirection = sunDirection.LengthSquared() > .00001f
            ? sunDirection.Normalized()
            : new Vector3(-.4f, .75f, -.5f).Normalized();
        _configured = true;

        BuildChildren();
        RebuildNoiseTexture();
    }

    public void UpdateFrame(
        Camera3D camera,
        Vector2I outputSize,
        double canonicalAroundMeters,
        float canonicalAcrossMeters,
        double animationSeconds,
        Texture2D? heightTexture,
        Vector4 terrainInfo,
        int rowOffset,
        int rowCount)
    {
        if (!IsInstanceValid(camera))
        {
            return;
        }

        BuildChildren();
        if (_cloudViewport is null || _cloudMaterial is null || _shadowMaterial is null)
        {
            return;
        }

        int width = Mathf.Max(1, outputSize.X / 2);
        int height = Mathf.Max(1, outputSize.Y / 2);
        if (width != _renderWidth || height != _renderHeight)
        {
            _renderWidth = width;
            _renderHeight = height;
            _cloudViewport.Size = new Vector2I(width, height);
        }

        Transform3D cameraTransform = camera.GlobalTransform;
        _cloudMaterial.SetShaderParameter("camera_origin_km", camera.GlobalPosition);
        _cloudMaterial.SetShaderParameter("camera_basis_x", cameraTransform.Basis.X);
        _cloudMaterial.SetShaderParameter("camera_basis_y", cameraTransform.Basis.Y);
        _cloudMaterial.SetShaderParameter("camera_basis_z", cameraTransform.Basis.Z);
        _cloudMaterial.SetShaderParameter("camera_fov_y", Mathf.DegToRad(camera.Fov));
        _cloudMaterial.SetShaderParameter("camera_aspect", outputSize.X / Mathf.Max(1f, outputSize.Y));
        _cloudMaterial.SetShaderParameter("ring_radius_km", _radiusKm);
        _cloudMaterial.SetShaderParameter("ring_width_km", _ringWidthKm);
        _cloudMaterial.SetShaderParameter("sun_direction", _sunDirection);
        _cloudMaterial.SetShaderParameter("seed", (float)Seed);
        _cloudMaterial.SetShaderParameter("time_seconds", (float)animationSeconds);
        _cloudMaterial.SetShaderParameter(
            "canonical_anchor_km",
            new Vector2((float)(canonicalAroundMeters / 1000.0), canonicalAcrossMeters / 1000f));

        Variant heightVariant = default;
        if (heightTexture is not null)
        {
            heightVariant = heightTexture;
        }
        _cloudMaterial.SetShaderParameter("height_texture", heightVariant);
        _cloudMaterial.SetShaderParameter("terrain_info", terrainInfo);
        _cloudMaterial.SetShaderParameter("terrain_row_offset", rowOffset);
        _cloudMaterial.SetShaderParameter("terrain_row_count", Mathf.Max(1, rowCount));
        _cloudMaterial.SetShaderParameter("terrain_columns", heightTexture?.GetWidth() ?? 1);
        _updateCount++;

        float around = (float)(canonicalAroundMeters / 1000.0);
        float across = canonicalAcrossMeters * .001f;
        Vector2 requestedOrigin = new(
            Mathf.Floor((around - 64f) / 8f) * 8f,
            Mathf.Floor((across - 128f) / 8f) * 8f);
        ShadowWindKm = double.IsNegativeInfinity(_lastShadowAnimationTime)
            ? Vector2.Zero
            : new Vector2(
                (float)(animationSeconds - _lastShadowAnimationTime) * .09f,
                (float)(animationSeconds - _lastShadowAnimationTime) * .035f);

        ulong nowMsec = Time.GetTicksMsec();
        bool originChanged = requestedOrigin != ShadowOriginKm;
        bool hasNewShadowState = originChanged || animationSeconds != _lastShadowAnimationTime;
        bool shadowIntervalElapsed = nowMsec - _lastShadowRealMsec >= (ulong)(ShadowInterval * 1000f);
        if (_shadowUpdateCount == 0 || (hasNewShadowState && shadowIntervalElapsed))
        {
            ShadowOriginKm = requestedOrigin;
            _shadowMaterial.SetShaderParameter("seed", (float)Seed);
            _shadowMaterial.SetShaderParameter("time_seconds", (float)animationSeconds);
            _shadowMaterial.SetShaderParameter("origin_km", ShadowOriginKm);
            _shadowMaterial.SetShaderParameter("span_km", ShadowSpanKm);
            _shadowMaterial.SetShaderParameter("ring_radius_km", _radiusKm);
            _shadowViewport?.SetUpdateMode(SubViewport.UpdateMode.Once);
            _lastShadowAnimationTime = animationSeconds;
            _lastShadowRealMsec = nowMsec;
            _shadowUpdateCount++;
            ShadowWindKm = Vector2.Zero;
        }

        _cloudViewport.SetUpdateMode(SubViewport.UpdateMode.Once);
        _composite?.Show();
    }

    public Godot.Collections.Dictionary GetCloudDiagnostics()
    {
        return new Godot.Collections.Dictionary {
            ["size"] = new Vector2I(_renderWidth, _renderHeight),
            ["shadow_size"] = new Vector2I(512, 512),
            ["noise_size"] = new Vector3I(64, 64, 64),
            ["view_steps"] = 32,
            ["update_count"] = _updateCount,
            ["shadow_update_count"] = _shadowUpdateCount,
            ["shadow_origin_km"] = ShadowOriginKm,
            ["shadow_extent_km"] = ShadowSpanKm,
            ["configured"] = _configured
        };
    }

    private void BuildChildren()
    {
        if (_cloudViewport is not null)
        {
            return;
        }

        _cloudViewport = NewViewport(new Vector2I(1, 1));
        _cloudViewport.Name = "MenuFlyoverCloudViewport";
        _shadowViewport = NewViewport(new Vector2I(512, 512));
        _shadowViewport.Name = "MenuFlyoverShadowViewport";
        _shadowViewport.SetUpdateMode(SubViewport.UpdateMode.Disabled);
        AddChild(_cloudViewport);
        AddChild(_shadowViewport);

        _cloudMaterial = new ShaderMaterial {
            Shader = GD.Load<Shader>("res://materials/menu_flyover_clouds.gdshader")
        };
        _shadowMaterial = new ShaderMaterial {
            Shader = GD.Load<Shader>("res://materials/menu_flyover_cloud_shadow.gdshader")
        };
        AddFullRect(_cloudViewport, _cloudMaterial);
        AddFullRect(_shadowViewport, _shadowMaterial);

        _composite = new TextureRect {
            Texture = _cloudViewport.GetTexture(),
            Visible = false,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
            Material = new ShaderMaterial {
                Shader = GD.Load<Shader>("res://materials/menu_flyover_cloud_composite.gdshader")
            }
        };
        _composite.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_composite);
    }

    private static SubViewport NewViewport(Vector2I size)
    {
        return new SubViewport {
            Size = size,
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
            Disable3D = true,
            GuiDisableInput = true
        };
    }

    private static void AddFullRect(SubViewport viewport, Material material)
    {
        var rect = new ColorRect {
            Color = Colors.White,
            Material = material,
            MouseFilter = MouseFilterEnum.Ignore
        };
        rect.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        viewport.AddChild(rect);
    }

    private ImageTexture3D CreateNoiseTexture()
    {
        const int side = 64;
        using var random = new RandomNumberGenerator { Seed = (ulong)(uint)Seed };
        var slices = new Godot.Collections.Array<Image>();
        for (int z = 0; z < side; z++)
        {
            Image slice = Image.CreateEmpty(side, side, false, Image.Format.R8);
            for (int y = 0; y < side; y++)
            {
                for (int x = 0; x < side; x++)
                {
                    slice.SetPixel(x, y, new Color(random.Randf(), 0, 0));
                }
            }
            slices.Add(slice);
        }

        var texture = new ImageTexture3D();
        Error result = texture.Create(Image.Format.R8, side, side, side, false, slices);
        foreach (Image slice in slices)
        {
            slice.Dispose();
        }
        if (result != Error.Ok)
        {
            throw new InvalidOperationException($"Menu cloud noise creation failed: {result}");
        }
        return texture;
    }

    private void RebuildNoiseTexture()
    {
        _noiseTexture?.Dispose();
        _noiseTexture = CreateNoiseTexture();
        _cloudMaterial?.SetShaderParameter("menu_noise_texture", _noiseTexture);
        _shadowMaterial?.SetShaderParameter("menu_noise_texture", _noiseTexture);
    }

    public override void _ExitTree()
    {
        _cloudViewport?.QueueFree();
        _shadowViewport?.QueueFree();
        _composite?.QueueFree();
        _noiseTexture?.Dispose();
        _cloudViewport = null;
        _shadowViewport = null;
        _composite = null;
        _cloudMaterial = null;
        _shadowMaterial = null;
        _noiseTexture = null;
    }
}
