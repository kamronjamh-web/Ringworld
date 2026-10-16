using System;
using Godot;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Cards render style of the standing-grass layer (grass-cards plan step S5): the style switch, the per-step
/// <see cref="RingGrassStyleProfile"/>, the card asset/material lifecycle, the Terrain3D surface-follower registration and
/// the per-tile terrain-uv instance uniform. The scheduler, invalidation and presentation code is shared with Blades.</summary>
public partial class RingStandingGrassLayer
{
    public enum GrassRenderStyleKind { Blades = 0, Cards = 1 }

    /// <summary>Blades is the established renderer. Cards draws alpha-scissor clump cards coloured from the Terrain3D surface.
    /// Changing it resets the layer and rebuilds every tile.</summary>
    [Export] public GrassRenderStyleKind GrassRenderStyle { get; set; } = GrassRenderStyleKind.Blades;

    [ExportGroup("Grass cards")]
    [Export(PropertyHint.Range, "100,600,5")] public float GrassCardReachMeters { get; set; } = 400f;
    /// <summary>Cards GPU culling tests distance minus this margin, so deferred batches and frame latency never drop a visible card.</summary>
    [Export(PropertyHint.Range, "0,8,0.25")] public float GrassCardComputeMarginMeters { get; set; } = 2f;
    [Export(PropertyHint.Range, "16,64,1")] public float GrassCardTileMeters { get; set; } = 32f;
    /// <summary>Distance by which tier upgrades are requested ahead of the camera. Zero follows the streaming hysteresis.</summary>
    [Export(PropertyHint.Range, "0,64,0.5")] public float GrassCardLookaheadMeters { get; set; }
    /// <summary>Lower bound of the Cards resident-tile cap; the effective cap never drops below the planned window plus two perimeters.</summary>
    [Export(PropertyHint.Range, "16,2048,1")] public int GrassCardResidentTiles { get; set; } = 256;
    /// <summary>0 = raw ground colour, 1 = ground luminance in a grass hue (variant D default 0.15).</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float GrassCardGreenPull { get; set; } = .15f;
    /// <summary>Gameplay node that owns the Terrain3D surface the card material follows for lighting and biome textures.</summary>
    [Export] public NodePath Terrain3DGameplayPath { get; set; } = new("../Terrain3DGameplay");

    private RingGrassStyleProfile _style = RingGrassStyleProfile.Build(new RingGrassStyleProfile.Inputs(GrassRenderStyleKind.Blades, 48f, 0, 0, 0, 0, 0));
    private RingGrassStyleProfile.Inputs _styleInputs = new(GrassRenderStyleKind.Blades, 48f, 0, 0, 0, 0, 0);
    private double _ringAcrossWidthMeters = 1;
    private double _cardFocusAroundMeters; // unwrap reference (terrain frame anchor, else the focus)
    /// <summary>Full layer resets (style switches, fingerprint changes, suspend). Test and diagnostic counter.</summary>
    public int GrassResetCount { get; private set; }
    internal RingGrassStyleProfile StyleProfile => _style;
    public bool IsCardStyle => _style.IsCards;

    private RingGrassStyleProfile? _cardComputeProfile;
    private float _cardComputeMargin = float.NaN;
    private RingGrassCardComputeParameters _cardCompute;

    /// <summary>GPU card-culling parameters for the active curve and margin; cached so unchanged inputs give an equal value.</summary>
    internal RingGrassCardComputeParameters CardComputeFrame()
    {
        float margin = float.IsFinite(GrassCardComputeMarginMeters) ? Mathf.Clamp(GrassCardComputeMarginMeters, 0f, 8f) : 2f;
        if (!ReferenceEquals(_cardComputeProfile, _style) || _cardComputeMargin != margin)
        {
            _cardCompute = RingGrassCardComputeParameters.From(_style.Curve, margin);
            _cardComputeProfile = _style;
            _cardComputeMargin = margin;
        }
        return _cardCompute;
    }

    private int ResidentTileCap => _style.IsCards ? _style.ResidentTileCap : Math.Max(1, MaximumResidentTiles);

    /// <summary>Captures the style profile from the current properties; rebuilt only when an input changed.</summary>
    private void RefreshStyleProfile()
    {
        bool cards = GrassRenderStyle == GrassRenderStyleKind.Cards;
        // Blades ignores every card input, so editing one while Blades is active must not rebuild the profile.
        var inputs = cards
            ? new RingGrassStyleProfile.Inputs(GrassRenderStyleKind.Cards, 0, GrassCardTileMeters, GrassCardReachMeters, GrassCardLookaheadMeters,
                GrassCardResidentTiles, GrassStreamingHysteresisMeters, CaptureCardTuning())
            : new RingGrassStyleProfile.Inputs(GrassRenderStyleKind.Blades, SpatialTileMeters, 0, 0, 0, 0, 0);
        if (inputs == _styleInputs) return;
        _styleInputs = inputs;
        _style = RingGrassStyleProfile.Build(inputs);
    }

    // ---- Card resources ----
    private RingGrassCardAssetSet? _cardAssets;
    private Mesh? _cardMesh;
    private ShaderMaterial? _cardMaterial;
    private readonly RingGrassCardMaterialConfig _cardMaterialConfig = new();
    private RingGrassStyleProfile? _cardMaterialProfile;
    private RingGrassCardMaterialSettings? _cardMaterialSettings;
    private RingTerrain3DSurface? _cardSurface;
    private ulong _nextCardSurfaceSearchMsec;
    private bool? _cardWorldUv;
    private (Vector3 Sun, float Start, float Full, float NightDirect, float DayDirect)? _cardFallbackLighting;

    /// <summary>True once the atlas and meshes of the active card parameters exist and the card material is created.</summary>
    public bool CardAssetsReady => _cardAssets != null && _cardMaterial != null;

    /// <summary>Main thread. Starts the off-thread asset build and creates the mesh and material once it finishes.</summary>
    private bool EnsureCardResources()
    {
        RingGrassCardAtlasParameters atlas = _style.Atlas.Normalized();
        if (_cardAssets == null || _cardAssets.Parameters != atlas)
        {
            if (!RingGrassCardAssets.TryGet(atlas, out RingGrassCardAssetSet? set) || set == null)
            {
                _ = RingGrassCardAssets.RequestAsync(atlas);
                return false;
            }
            _cardAssets = set;
            _cardMesh = set.CardArrayMesh;
            _cardMaterial?.SetShaderParameter("grass_card_atlas", set.AtlasTexture);
        }
        if (_cardMaterial == null)
        {
            _cardMaterial = CreateCardMaterial(_cardAssets!.AtlasTexture);
            _grassLightingStateValid = false;
        }
        return true;
    }

    /// <summary>Leaving Cards: stop every write to the card material and let it die (the projectors hold it weakly, so it
    /// stops receiving shadow writes once its renderers are freed). Blades then does no card work.</summary>
    private void DropCardMaterial()
    {
        ShaderMaterial? material = _cardMaterial;
        if (material == null) return;
        ulong id = material.GetInstanceId();
        _grassGustMaterialStates.Remove(material);
        _interactionMaterialCaches.Remove(id);
        _grassModifierMaterialStates.Remove(id);
        _cardMaterial = null;
        _cardMaterialProfile = null;
        _cardWorldUv = null;
        _cardFallbackLighting = null;
    }

    private ShaderMaterial CreateCardMaterial(Texture2D atlas)
    {
        ShaderMaterial material = _cardMaterialConfig.CreateCardMaterial(atlas);
        // The biome albedo samplers are constants of the terrain; setting them here keeps the legacy (no-follower) path
        // coloured. The follower registration replays the same values later.
        material.SetShaderParameter("biome_texture_scale", .20f);
        material.SetShaderParameter("biome_grass_albedo", GD.Load<Texture2D>("res://materials/third_party/voxelgame/grass_albedo.png"));
        material.SetShaderParameter("biome_dirt_albedo", GD.Load<Texture2D>("res://materials/third_party/voxelgame/dirt_albedo.png"));
        material.SetShaderParameter("biome_desert_albedo", GD.Load<Texture2D>("res://materials/biomes/desert_sand_albedo.png"));
        material.SetShaderParameter("biome_forest_albedo", GD.Load<Texture2D>("res://materials/biomes/forest_floor_albedo.png"));
        material.SetShaderParameter("biome_tundra_albedo", GD.Load<Texture2D>("res://materials/biomes/tundra_ground_albedo.png"));
        material.SetShaderParameter("biome_alpine_albedo", GD.Load<Texture2D>("res://materials/biomes/alpine_rock_albedo.png"));
        // Cloud and macro/planet shadow textures reach the card material through the same projectors the terrain registers with.
        global::RingworldTechDemo.RingCloudShadowProjector.Register(material);
        global::RingworldTechDemo.RingTerrainMacroShadowProjector.Register(material);
        _cardMaterialProfile = null;
        _cardSurface = null;
        _cardWorldUv = null;
        _cardFallbackLighting = null;
        return material;
    }

    /// <summary>Material-only card settings and the surface-follower link; cheap and idempotent every presentation pass.</summary>
    private void ConfigureCardMaterial()
    {
        ShaderMaterial? material = _cardMaterial;
        if (material == null) return;
        ConfigureGrassGustMaterial(material);
        ConfigureGrassModifierMaterial(material);
        RingGrassCardMaterialSettings settings = CaptureCardMaterialSettings();
        if (!ReferenceEquals(_cardMaterialProfile, _style) || _cardMaterialSettings != settings)
        {
            settings.ApplyTo(_cardMaterialConfig);
            _cardMaterialConfig.Curve = _style.Curve;
            _cardMaterialConfig.ApplyCard(material);
            _cardMaterialProfile = _style;
            _cardMaterialSettings = settings;
        }
        UpdateCardSurfaceFollower();
    }

    private void UpdateCardSurfaceFollower()
    {
        ShaderMaterial? material = _cardMaterial;
        if (material == null) return;
        if (_cardSurface != null && GodotObject.IsInstanceValid(_cardSurface)) return;
        _cardSurface = null;
        ulong now = Time.GetTicksMsec();
        if (now < _nextCardSurfaceSearchMsec) return;
        _nextCardSurfaceSearchMsec = now + 500;
        RingTerrain3DSurface? surface = CardSurfaceOverrideForTests ?? GetNodeOrNull<RingTerrain3DGameplay>(Terrain3DGameplayPath)?.SurfaceForFollowers;
        if (surface == null || !GodotObject.IsInstanceValid(surface))
        {
            SetCardWorldUv(true);
            return;
        }
        surface.RegisterSurfaceFollower(material, RingGrassCardMaterialConfig.SurfaceFollowerNames);
        _cardSurface = surface;
        _cardFallbackLighting = null;
        SetCardWorldUv(false);
    }

    private void SetCardWorldUv(bool worldUv)
    {
        if (_cardMaterial == null || _cardWorldUv == worldUv) return;
        _cardMaterial.SetShaderParameter("grass_card_world_uv", worldUv);
        _cardWorldUv = worldUv;
    }

    private void ReleaseCardFollower()
    {
        if (_cardSurface != null && GodotObject.IsInstanceValid(_cardSurface) && _cardMaterial != null)
            _cardSurface.UnregisterSurfaceFollower(_cardMaterial);
        _cardSurface = null;
    }

    /// <summary>True when the card material is currently fed by a Terrain3D surface follower registration.</summary>
    internal bool CardMaterialFollowsSurface => _cardSurface != null && GodotObject.IsInstanceValid(_cardSurface);
    internal ShaderMaterial? CardMaterialForTests => _cardMaterial;
    internal RingTerrain3DSurface? CardSurfaceOverrideForTests { get; set; }

    /// <summary>Legacy voxel terrain has no Terrain3D follower: feed the card material the grass layer's own lighting state.</summary>
    private void ApplyCardFallbackLighting(GrassLightingState state)
    {
        ShaderMaterial? material = _cardMaterial;
        if (!_style.IsCards || material == null || CardMaterialFollowsSurface) return;
        float start = _grid?.RingDayStartDot ?? -.2f, full = Mathf.Max(start + .001f, _grid?.RingDayFullDot ?? .35f);
        var next = (state.SunDirection, start, full, state.NightDirect, state.DayDirect);
        if (_cardFallbackLighting == next) return;
        material.SetShaderParameter("sun_direction", state.SunDirection);
        material.SetShaderParameter("day_start_dot", start);
        material.SetShaderParameter("day_full_dot", full);
        material.SetShaderParameter("night_direct_light_factor", state.NightDirect);
        material.SetShaderParameter("day_direct_light_factor", state.DayDirect);
        _cardFallbackLighting = next;
    }

    private bool _cardUnwrapFromAnchor;

    /// <summary>Around coordinate the per-tile uv is unwrapped around. The Terrain3D shader computes
    /// uv = (anchor mod 1000 + SignedAroundDeltaMeters(p)) * 0.2, i.e. it unwraps around its frame anchor (which moves in 256 m
    /// steps), so with a surface present the reference is the active frame's anchor; without one it is the focus.
    /// Tiles are refreshed only when the anchor changes (and the cached uniform value is rewritten only if it actually changed).</summary>
    private void UpdateCardUnwrapReference(double focusAroundMeters)
    {
        bool anchor = false;
        double reference = focusAroundMeters;
        if (_cardSurface != null && GodotObject.IsInstanceValid(_cardSurface))
        {
            RingSurfaceFrame frame = _cardSurface.Frame;
            if (frame.CircumferenceMeters > 0) { reference = frame.AnchorAroundMeters; anchor = true; }
        }
        if (anchor ? (!_cardUnwrapFromAnchor || reference != _cardFocusAroundMeters) : _cardUnwrapFromAnchor) InvalidateGrassWork();
        _cardUnwrapFromAnchor = anchor;
        _cardFocusAroundMeters = reference;
    }
    internal double CardUnwrapReferenceForTests => _cardFocusAroundMeters;

    /// <summary>Instance uniform <c>grass_terrain_origin_uv</c> of a tile: xy = Terrain3D texture uv of the tile's origin
    /// (metres mod the 5 m texture period, times 0.2), zw = macro-shadow uv origin (around / circumference, across / ring
    /// width). The around coordinate is unwrapped to the copy nearest <paramref name="focusAroundMeters"/> (the terrain frame anchor, else the focus), exactly like the
    /// terrain's anchor-relative frame, so the texture phase is continuous across the ring wrap (a circumference that is not
    /// a multiple of 5 m would otherwise leave a phase jump there). Away from the wrap it is canonical, so floating-origin
    /// rebases never change it.</summary>
    internal static Vector4 CardTerrainOriginUv(in RingGrassTileBounds bounds, double ringRadiusMeters, double ringWidthMeters,
        double circumferenceMeters, double focusAroundMeters)
    {
        double around = bounds.AroundCenter;
        if (circumferenceMeters > 0) around += circumferenceMeters * Math.Round((focusAroundMeters - around) / circumferenceMeters);
        return new((float)(Modulo(around, 5.0) * .2), (float)(Modulo(bounds.AcrossCenter, 5.0) * .2),
            (float)(bounds.AroundCenter / (Math.Tau * Math.Max(1.0, ringRadiusMeters))), (float)(bounds.AcrossCenter / Math.Max(1.0, ringWidthMeters)));
    }

    /// <summary>Estimated bytes of resident instance data (the CPU buffer is mirrored once on the GPU by the MultiMesh).</summary>
    public long ResidentGrassInstanceBytes => ResidentGrassInstanceCount * RingGrassStyleProfile.BytesPerInstance;
}
