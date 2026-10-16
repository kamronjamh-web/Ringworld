using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tests;

/// <summary>Headless test of the grass render style switch and the Cards style (grass-cards plan step S5): Blades golden
/// fingerprints and layer plumbing, the sqrt(2) ladder and budgets, idle / oscillation streaming counters, the style
/// switch (exactly one reset, full rebuild) and the origin-uv continuity gate. Run via tools/grass_card_style_test.gd.</summary>
public partial class GrassCardStyleTest : Node
{
    [Signal] public delegate void CompletedEventHandler(bool passed);

    private const long Circumference = 981_748L * 32L;
    private const int Width = 318_000;
    // Pre-S5 output of Fingerprint(): default layer and the user's maxed blade configuration (committed HEAD behaviour).
    private const ulong BladesDefaultFingerprint = 0x0ADB9010E9EDA1C1UL;
    private const ulong BladesMaxedFingerprint = 0x51C5472B271BB04AUL;
    private readonly List<string> _failures = new();
    private readonly List<string> _report = new();

    public override async void _Ready()
    {
        try { EmitSignal(SignalName.Completed, await RunAsync()); }
        catch (Exception exception) { GD.PushError("GRASS_CARD_STYLE=FAIL " + exception); EmitSignal(SignalName.Completed, false); }
    }

    private void Check(bool condition, string message) { if (!condition) _failures.Add(message); }

    private async Task<bool> RunAsync()
    {
        int previousMaxFps = Engine.MaxFps; Engine.MaxFps = 0;
        var settings = (VoxelTerrainSettings)(GD.Load<VoxelTerrainSettings>("res://resources/voxel/voxel_terrain_settings_ring_integration.tres")?.Duplicate(true) ?? throw new InvalidOperationException("terrain settings missing"));
        using var sampler = new RingTerrainSampler(settings, true, Circumference, 5_000_000.0, Width, 8000);
        try
        {
            await Task.Run(() => RingGrassCardAssets.GetBlocking(RingGrassCardAtlasParameters.VariantD));
            TestLadder();
            TestFingerprints(sampler);
            TestCardSettingClasses(sampler);
            TestOldSaveLoads();
            TestOriginUv();
            TestGroupUv();
            await TestSwitchAndBladesPlumbing(sampler);
            await TestCardsStreaming(sampler);
            await TestBudgetAndAssets(sampler);
        }
        catch (Exception exception) { _failures.Add("exception: " + exception); }
        finally { Engine.MaxFps = previousMaxFps; }
        foreach (string line in _report) GD.Print(line);
        if (_failures.Count > 0)
        {
            foreach (string failure in _failures) GD.PushError("GRASS_CARD_STYLE fail: " + failure);
            GD.Print("GRASS_CARD_STYLE=FAIL " + _failures.Count);
            return false;
        }
        GD.Print("GRASS_CARD_STYLE=PASS");
        return true;
    }

    private static RingStandingGrassLayer.GrassRenderStyleKind Cards => RingStandingGrassLayer.GrassRenderStyleKind.Cards;
    private static RingStandingGrassLayer.GrassRenderStyleKind Blades => RingStandingGrassLayer.GrassRenderStyleKind.Blades;

    // ------------------------------------------------------------------ (c) ladder, budgets, cap

    private void TestLadder()
    {
        RingGrassStyleProfile p = RingGrassStyleProfile.Build(new RingGrassStyleProfile.Inputs(Cards, 0, 32, 400, 0, 256, 0));
        Check(p.IsCards && p.TileMeters == 32 && p.ReachMeters == 400 && p.CoverageRadiusMeters == 400, "cards profile tile/reach");
        Check(p.HysteresisMeters == 8 && p.LookaheadMeters == 8, $"hysteresis/lookahead default to H (got {p.HysteresisMeters}/{p.LookaheadMeters})");
        // Monotone: the tier never decreases with distance and each tier keeps at least the curve's keep fraction at the
        // lookahead-shifted distance (a tile always holds a superset of what its nearest card needs).
        byte previous = 0;
        for (double d = 0; d <= 900; d += .1)
        {
            byte tier = p.TierForDistance(d);
            Check(tier >= previous, $"ladder tier decreased at {d:F1} m ({previous} -> {tier})");
            previous = tier;
            float keep = RingGrassCardCurve.Keep(p.Curve, (float)Math.Max(0, d - p.LookaheadMeters));
            Check(p.FractionForTier(tier) + 1e-6f >= keep, $"tier {tier} fraction {p.FractionForTier(tier)} < keep {keep} at {d:F1} m");
        }
        Check(previous == p.TierCount - 1, $"far tier is the last ladder tier ({previous} vs {p.TierCount - 1})");
        for (int t = 0; t < p.TierCount; t++)
            Check(Math.Abs(p.FractionForTier(t) - MathF.Pow(2f, -t * .5f)) < 1e-6f, $"fraction of tier {t} is 2^(-t/2)");
        Check(p.FractionForTier(p.TierCount - 1) >= p.Curve.FloorDensity / p.Curve.NearDensity - 1e-6f, "last tier >= floor density / lattice density");
        // Tier starts are increasing in nearest-point distance.
        for (int t = 2; t < p.TierCount; t++) Check(p.TierStartMeters[t] >= p.TierStartMeters[t - 1], $"tier starts not monotone at {t}");
        // Budgets come from the reach only.
        RingGrassStyleProfile other = RingGrassStyleProfile.Build(new RingGrassStyleProfile.Inputs(Cards, 96, 32, 400, 0, 256, 0));
        Check(other.InstanceBudget == p.InstanceBudget && other.ResidentTileCap == p.ResidentTileCap, "cards budgets depend on blade tile size");
        Check(p.ResidentTileCap >= p.DesiredTiles + 2 * p.PerimeterTiles, "cap >= desired + 2 x perimeter");
        RingGrassStyleProfile lowSetting = RingGrassStyleProfile.Build(new RingGrassStyleProfile.Inputs(Cards, 0, 32, 400, 0, 5000, 0));
        Check(lowSetting.ResidentTileCap == 5000, "a larger setting raises the cap (max(setting, desired + 2 x perimeter))");
        RingGrassStyleProfile shortReach = RingGrassStyleProfile.Build(new RingGrassStyleProfile.Inputs(Cards, 0, 32, 150, 0, 256, 0));
        Check(shortReach.InstanceBudget < p.InstanceBudget, "shorter reach lowers the budget");
        _report.Add($"GRASS_CARD_STYLE[ladder] tiers={p.TierCount} lowLayerLastTier={p.LowLayerLastTier} cap={p.ResidentTileCap} desired={p.DesiredTiles} perimeter={p.PerimeterTiles} "
            + $"budget={p.InstanceBudget:N0} mainCap/tile={p.MainCapPerTile} lowCap/tile={p.LowCapPerTile} starts(m)=[{string.Join(", ", p.TierStartMeters.Select(s => float.IsPositiveInfinity(s) ? "inf" : s.ToString("F0")))}]");
    }

    // ------------------------------------------------------------------ (a) blades unchanged

    private void TestFingerprints(RingTerrainSampler sampler)
    {
        RingLodGeometrySnapshot geometry = Geometry(Vector3.Zero, new RingCanonicalPosition(0, 0, 100));
        var defaults = new RingStandingGrassLayer();
        var maxed = new RingStandingGrassLayer
        {
            VisibilityRadiusMeters = 230, BladesPerSquareMeter = 96, MaximumInstancesPerTile = 1700, MaximumInstances = 5_000_000, MaximumResidentTiles = 40,
        };
        ulong d = defaults.SettingsFingerprintForTests(sampler, geometry), m = maxed.SettingsFingerprintForTests(sampler, geometry);
        Check(d == BladesDefaultFingerprint, $"blades default fingerprint 0x{d:X16} != golden 0x{BladesDefaultFingerprint:X16}");
        Check(m == BladesMaxedFingerprint, $"blades maxed fingerprint 0x{m:X16} != golden 0x{BladesMaxedFingerprint:X16}");
        // Editing a card setting while Blades is active must not touch the Blades fingerprint.
        defaults.GrassCardReachMeters = 250; defaults.GrassCardGreenPull = .5f;
        Check(defaults.SettingsFingerprintForTests(sampler, geometry) == BladesDefaultFingerprint, "card settings changed the blades fingerprint");
        _report.Add($"GRASS_CARD_STYLE[fingerprint] blades default=0x{d:X16} maxed=0x{m:X16}");

        // Cards: style is part of the fingerprint, reach/tile are placement keys, blade keys are not.
        RingStandingGrassLayer Cards1(Action<RingStandingGrassLayer>? tweak = null)
        {
            var layer = new RingStandingGrassLayer { GrassRenderStyle = Cards };
            tweak?.Invoke(layer);
            AddChild(layer);
            layer.InitializeForTests();
            return layer;
        }
        RingStandingGrassLayer a = Cards1(), b = Cards1(l => { l.BladesPerSquareMeter = 96; l.MaximumInstancesPerTile = 1700; l.MaximumResidentTiles = 40; l.VisibilityRadiusMeters = 99; l.SpatialTileMeters = 64; });
        RingStandingGrassLayer hy = Cards1(l => { l.GrassStreamingHysteresisMeters = 3; l.GrassCardLookaheadMeters = 20; });
        RingStandingGrassLayer c = Cards1(l => l.GrassCardReachMeters = 300), e = Cards1(l => l.GrassCardTileMeters = 24);
        ulong fa = a.SettingsFingerprintForTests(sampler, geometry);
        Check(fa != BladesDefaultFingerprint && fa != 0, "cards fingerprint differs from blades");
        Check(fa == b.SettingsFingerprintForTests(sampler, geometry), "blade-key edits changed the Cards fingerprint");
        Check(fa == c.SettingsFingerprintForTests(sampler, geometry) && fa == hy.SettingsFingerprintForTests(sampler, geometry), "reach / hysteresis / lookahead are placement keys: they must go through the coverage path, not a reset");
        Check(a.StyleProfile.CoverageFingerprint != c.StyleProfile.CoverageFingerprint && a.StyleProfile.CoverageFingerprint != hy.StyleProfile.CoverageFingerprint, "reach / hysteresis did not change the coverage fingerprint");
        Check(fa != e.SettingsFingerprintForTests(sampler, geometry), "tile change did not change the Cards fingerprint");
        Check(a.StyleProfile.ResidentTileCap == b.StyleProfile.ResidentTileCap && a.MaximumGrassSourceInstances == b.MaximumGrassSourceInstances,
            "blade keys reached the Cards budgets");
        foreach (var layer in new[] { a, b, c, e, hy }) layer.QueueFree();
        defaults.Free(); maxed.Free();
    }

    // ------------------------------------------------------------------ (g) S8 card setting classes

    private static readonly (string Key, Variant Value)[] PlacementClassSettings =
    {
        ("GrassCardNearDensity", 10f), ("GrassCardLowLayerDensity", 10f), ("GrassCardTileMeters", 24f), ("GrassCardTallHeight", .8f),
        ("GrassCardShortHeight", .2f), ("GrassCardLean", .2f), ("GrassCardAtlasBlades", 40),
    };
    private static readonly (string Key, Variant Value)[] CoverageClassSettings =
    {
        ("GrassCardReachMeters", 300f), ("GrassCardBaseDensity", 3f), ("GrassCardFloorDensity", .3f), ("GrassCardDensityFalloff", 1.6f),
        ("GrassCardNearBoostStartMeters", 12f), ("GrassCardNearBoostEndMeters", 60f), ("GrassCardLowLayerEndMeters", 30f),
        ("GrassCardScaleCompensationMax", 2f), ("GrassCardNearWidthScale", .8f), ("GrassCardFadeBandPercent", 25f),
    };
    private static readonly (string Key, Variant Value)[] MaterialClassSettings =
    {
        ("GrassCardGreenPull", .4f), ("GrassCardAlphaCutoff", .7f), ("GrassCardSingleCardStartMeters", 100f), ("GrassCardSingleCardEndMeters", 200f),
        ("GrassCardTilt", .2f), ("GrassCardNormalOut", .6f), ("GrassCardNormalFace", .35f), ("GrassCardBacklight", .4f), ("GrassCardRootAo", .3f),
        ("GrassCardTipLight", .25f), ("GrassCardBladeValueJitter", .3f), ("GrassCardBladeHueJitter", .15f), ("GrassCardDryFraction", .2f),
        ("GrassCardTuftValueJitter", .25f), ("GrassCardFragmentColorMeters", 120f), ("GrassCardConvergeStartPercent", 50f), ("GrassCardConvergeEndPercent", 80f),
    };
    private static readonly (string Key, Variant Value)[] BladeOnlySettings =
    {
        ("BladesPerSquareMeter", 96f), ("MaximumInstancesPerTile", 1700), ("MaximumInstances", 5_000_000), ("MaximumResidentTiles", 40), ("VisibilityRadiusMeters", 99f),
        ("SpatialTileMeters", 64f), ("GrassMinimumHeightMeters", .05f), ("GrassMaximumHeightMeters", .9f), ("EnableGrassCameraFacing", false),
        ("GrassCameraFacingStrength", .3f), ("UseSingleRibbonIntermediateGrass", false), ("UseReducedNearGrassGeometry", false),
        ("LodNearDistanceMeters", 30f), ("LodMidDistanceMeters", 120f), ("EnableMeadowTufts", false), ("MeadowTuftSpread", 1.7f),
    };

    private void TestCardSettingClasses(RingTerrainSampler sampler)
    {
        RingLodGeometrySnapshot geometry = Geometry(Vector3.Zero, new RingCanonicalPosition(0, 0, 100));
        var owned = new List<RingStandingGrassLayer>();
        RingStandingGrassLayer Make(RingStandingGrassLayer.GrassRenderStyleKind style, params (string Key, Variant Value)[] settings)
        {
            var layer = new RingStandingGrassLayer { GrassRenderStyle = style };
            foreach (var (key, value) in settings) layer.Set(key, value);
            AddChild(layer);
            layer.InitializeForTests();
            owned.Add(layer);
            return layer;
        }
        RingStandingGrassLayer a = Make(Cards);
        ulong fa = a.SettingsFingerprintForTests(sampler, geometry);
        RingGrassStyleProfile pa = a.StyleProfile;

        // Defaults reproduce the pre-S8 look and placement: the layer's export literals equal the curve / placement / atlas / material sources.
        Check(a.CardTuningForTests == RingGrassCardTuning.Default && RingGrassCardTuning.Default.Normalized() == RingGrassCardTuning.Default, "layer export defaults differ from RingGrassCardTuning.Default or it is not range-stable");
        Check(pa.Curve == RingGrassCardCurveParameters.Default, "default Cards curve differs from RingGrassCardCurveParameters.Default");
        Check(pa.Placement == new RingGrassCardPlacement { LowLayerMaxInstances = pa.Placement.LowLayerMaxInstances }, "default Cards placement differs from RingGrassCardPlacement defaults");
        Check(pa.Atlas == RingGrassCardAtlasParameters.VariantD, "default Cards atlas differs from variant D");
        Check(a.CardMaterialSettingsForTests == RingGrassCardMaterialSettings.Default, "layer material defaults differ from RingGrassCardMaterialConfig defaults");
        RingGrassStyleProfile bare = RingGrassStyleProfile.Build(new RingGrassStyleProfile.Inputs(Cards, 0, 32, 400, 0, 256, 0));
        Check(bare.PlacementFingerprint == pa.PlacementFingerprint && bare.CoverageFingerprint == pa.CoverageFingerprint, "layer defaults differ from a profile built without tuning");

        foreach (var (key, value) in PlacementClassSettings)
        {
            RingStandingGrassLayer l = Make(Cards, (key, value));
            Check(l.SettingsFingerprintForTests(sampler, geometry) != fa, $"{key}: rebuild-class key did not change the layer fingerprint");
            Check(l.StyleProfile.PlacementFingerprint != pa.PlacementFingerprint, $"{key}: rebuild-class key did not change the placement fingerprint");
        }
        foreach (var (key, value) in CoverageClassSettings)
        {
            RingStandingGrassLayer l = Make(Cards, (key, value));
            Check(l.SettingsFingerprintForTests(sampler, geometry) == fa && l.StyleProfile.PlacementFingerprint == pa.PlacementFingerprint, $"{key}: coverage key changed the placement fingerprint (it would reset the layer)");
            Check(l.StyleProfile.CoverageFingerprint != pa.CoverageFingerprint, $"{key}: coverage key did not change the coverage fingerprint");
        }
        foreach (var (key, value) in MaterialClassSettings)
        {
            RingStandingGrassLayer l = Make(Cards, (key, value));
            Check(l.SettingsFingerprintForTests(sampler, geometry) == fa && l.StyleProfile.PlacementFingerprint == pa.PlacementFingerprint
                && l.StyleProfile.CoverageFingerprint == pa.CoverageFingerprint, $"{key}: material key changed a fingerprint");
            Check(l.StyleProfile.Curve == pa.Curve && l.StyleProfile.Placement == pa.Placement && l.StyleProfile.Atlas == pa.Atlas
                && l.StyleProfile.ResidentTileCap == pa.ResidentTileCap && l.StyleProfile.InstanceBudget == pa.InstanceBudget, $"{key}: material key changed the style profile");
            Check(l.CardMaterialSettingsForTests != RingGrassCardMaterialSettings.Default, $"{key}: material key did not reach the material settings");
        }
        {
            RingStandingGrassLayer l = Make(Cards, BladeOnlySettings);
            Check(l.SettingsFingerprintForTests(sampler, geometry) == fa && l.StyleProfile.PlacementFingerprint == pa.PlacementFingerprint
                && l.StyleProfile.CoverageFingerprint == pa.CoverageFingerprint, "blade-only keys changed the Cards fingerprints");
            Check(l.StyleProfile.ResidentTileCap == pa.ResidentTileCap && l.StyleProfile.InstanceBudget == pa.InstanceBudget && l.MaximumGrassSourceInstances == a.MaximumGrassSourceInstances, "blade-only keys changed the Cards budgets");
        }
        {
            // Blades ignores every Cards key and keeps its golden fingerprint.
            RingStandingGrassLayer l = Make(Blades, PlacementClassSettings.Concat(CoverageClassSettings).Concat(MaterialClassSettings).ToArray());
            Check(l.SettingsFingerprintForTests(sampler, geometry) == BladesDefaultFingerprint, "Cards keys changed the Blades golden fingerprint");
        }
        // Companion orderings hold even when a value is set directly (a saved or scripted pair that crosses).
        {
            RingStandingGrassLayer l = Make(Cards, ("GrassCardTallHeight", .2f), ("GrassCardShortHeight", .9f), ("GrassCardNearBoostStartMeters", 90f), ("GrassCardNearBoostEndMeters", 5f),
                ("GrassCardBaseDensity", .5f), ("GrassCardFloorDensity", 1f), ("GrassCardScaleCompensationMax", 9f), ("GrassCardLean", float.NaN),
                ("GrassCardSingleCardStartMeters", 600f), ("GrassCardSingleCardEndMeters", 30f), ("GrassCardConvergeStartPercent", 99f), ("GrassCardConvergeEndPercent", 1f));
            RingGrassStyleProfile p = l.StyleProfile;
            Check(p.Placement.ShortHeight < p.Placement.TallHeight, "short height not kept below tall height");
            Check(p.Curve.NearBoostEndMeters > p.Curve.NearBoostStartMeters, "near boost end not kept above start");
            Check(p.Curve.FloorDensity <= p.Curve.BaseDensity, "floor density above base density");
            Check(p.Curve.CompensationMax <= 2.5f, "compensation exceeds the instance bounds cap");
            Check(Math.Abs(p.Placement.Lean - .12f) < 1e-6f, "a non-finite lean was not replaced by the default");
            RingGrassCardMaterialSettings m = l.CardMaterialSettingsForTests;
            Check(m.SingleCardEndMeters > m.SingleCardStartMeters && m.ConvergeEndPercent > m.ConvergeStartPercent, "material start/end pairs not ordered");
        }
        {
            RingStandingGrassLayer none = Make(Cards, ("GrassCardLowLayerDensity", 0f));
            Check(none.StyleProfile.LowCapPerTile == 0 && !none.StyleProfile.IncludesLowLayer(0), "low layer density 0 still reserves or includes the low layer");
        }
        _report.Add($"GRASS_CARD_STYLE[settings] placement={PlacementClassSettings.Length} coverage={CoverageClassSettings.Length} material={MaterialClassSettings.Length} blade_only={BladeOnlySettings.Length} classes ok");
        foreach (var layer in owned) layer.QueueFree();
    }

    /// <summary>Old saves carry none of the S8 keys: they load, leave the Cards values to the profile defaults, and out-of-range new values clamp.</summary>
    private void TestOldSaveLoads()
    {
        const string path = "res://.godot/grass_card_style_old_save.tres";
        var legacy = new GameplayGrassSettings();
        legacy.Overrides["BladesPerSquareMeter"] = 21f; legacy.Overrides["Enabled"] = true; legacy.Overrides["GrassWindStrengthMultiplier"] = 1.2f;
        legacy.Overrides["GrassRenderStyle"] = 1; legacy.Overrides["GrassCardReachMeters"] = 350f; legacy.Overrides["GrassCardGreenPull"] = .3f;
        Check(ResourceSaver.Save(legacy, path) == Error.Ok, "could not write the legacy grass save");
        var defaults = new GameplayGrassDefaults(path);
        defaults.Initialize(null);
        Check(defaults.LastError == "" && defaults.Settings.Overrides.Count == 6, $"legacy save did not load cleanly ({defaults.LastError}, {defaults.Settings.Overrides.Count} keys)");
        Check(defaults.GetValue("GrassCardLean", .12f).AsSingle() == .12f && !defaults.Settings.Overrides.ContainsKey("GrassCardNearDensity"), "legacy save invented S8 keys");
        Check(defaults.GetValue("GrassCardReachMeters", 400f).AsSingle() == 350f, "legacy reach was not kept");
        var modern = new GameplayGrassSettings();
        modern.Overrides["GrassCardLean"] = 99f; modern.Overrides["GrassCardAtlasBlades"] = 500; modern.Overrides["GrassCardTallHeight"] = float.NaN; modern.Overrides["GrassCardLeanX"] = 1f;
        Check(ResourceSaver.Save(modern, path) == Error.Ok, "could not write the modern grass save");
        var clamped = new GameplayGrassDefaults(path);
        clamped.Initialize(null);
        Check(clamped.GetValue("GrassCardLean", 0f).AsSingle() == .5f && clamped.GetValue("GrassCardAtlasBlades", 0).AsInt32() == 96, "out-of-range S8 values were not clamped on load");
        Check(!clamped.Settings.Overrides.ContainsKey("GrassCardTallHeight") && !clamped.Settings.Overrides.ContainsKey("GrassCardLeanX"), "invalid or unknown S8 keys were kept");
        foreach (string key in GameplayGrassDefaults.Keys.Where(k => k.StartsWith("GrassCard"))) Check(GameplayGrassDefaults.Keys.Count(k => k == key) == 1, $"duplicate key {key}");
        try { System.IO.File.Delete(ProjectSettings.GlobalizePath(path)); } catch { }
    }

    /// <summary>Live classes on a running Cards layer: material keys write uniforms only, coverage keys refilter without a reset,
    /// placement keys reset and rebuild once, and atlas blades regenerate the atlas.</summary>
    private async Task TestLiveSettingClasses(RingStandingGrassLayer layer, RingTerrainSampler sampler, RingLodSurfaceEditSnapshot clean, RingCanonicalPosition home, RingLodGeometrySnapshot geometry)
    {
        int resets = layer.GrassResetCount, builds = layer.TotalGrassBuildCount, tiles = layer.ResidentGrassTileCount;
        ulong fp = layer.SettingsFingerprintForTests(sampler, geometry);
        ShaderMaterial material = layer.CardMaterialForTests!;
        // Material class.
        layer.GrassCardAlphaCutoff = .7f; layer.GrassCardTilt = .2f; layer.GrassCardRootAo = .3f; layer.GrassCardSingleCardStartMeters = 100f;
        layer.GrassCardSingleCardEndMeters = 200f; layer.GrassCardFragmentColorMeters = 120f; layer.GrassCardConvergeEndPercent = 80f; layer.GrassCardGreenPull = .4f;
        await Drive(layer, sampler, clean, home, geometry, 4, untilIdle: false);
        Check(layer.GrassResetCount == resets && layer.TotalGrassBuildCount == builds && layer.ResidentGrassTileCount == tiles, "material settings rebuilt or reset the layer");
        Check(layer.SettingsFingerprintForTests(sampler, geometry) == fp, "material settings changed the layer fingerprint");
        Check(Mathf.IsEqualApprox(material.GetShaderParameter("grass_card_alpha_cutoff").AsSingle(), .7f) && Mathf.IsEqualApprox(material.GetShaderParameter("grass_card_tilt").AsSingle(), .2f)
            && Mathf.IsEqualApprox(material.GetShaderParameter("grass_card_root_ao").AsSingle(), .3f) && Mathf.IsEqualApprox(material.GetShaderParameter("grass_card_single_start").AsSingle(), 100f)
            && Mathf.IsEqualApprox(material.GetShaderParameter("grass_card_single_end").AsSingle(), 200f) && Mathf.IsEqualApprox(material.GetShaderParameter("grass_card_fragment_color_meters").AsSingle(), 120f)
            && Mathf.IsEqualApprox(material.GetShaderParameter("grass_card_converge_end_percent").AsSingle(), 80f) && Mathf.IsEqualApprox(material.GetShaderParameter("grass_card_green_pull").AsSingle(), .4f),
            "material settings did not reach the card material uniforms");
        // Coverage class: curve values reach the material and the GPU frame, with no reset.
        layer.GrassCardBaseDensity = 3f; layer.GrassCardFadeBandPercent = 25f; layer.GrassCardNearWidthScale = .8f; layer.GrassCardScaleCompensationMax = 2f;
        await Drive(layer, sampler, clean, home, geometry, 300, untilIdle: true);
        Check(layer.GrassResetCount == resets, "curve settings reset the layer");
        Check(Mathf.IsEqualApprox(material.GetShaderParameter("grass_card_base_density").AsSingle(), 3f) && Mathf.IsEqualApprox(material.GetShaderParameter("grass_card_fade_band_percent").AsSingle(), 25f)
            && Mathf.IsEqualApprox(material.GetShaderParameter("grass_card_near_width").AsSingle(), .8f) && Mathf.IsEqualApprox(material.GetShaderParameter("grass_card_comp_max").AsSingle(), 2f),
            "curve settings did not reach the card material uniforms");
        Check(layer.CardComputeFrame().BaseDensity == 3f, "curve settings did not reach the GPU card frame");
        layer.GrassCardBaseDensity = 2.5f; layer.GrassCardFadeBandPercent = 15f; layer.GrassCardNearWidthScale = .7f; layer.GrassCardScaleCompensationMax = 2.5f;
        await Drive(layer, sampler, clean, home, geometry, 300, untilIdle: true);
        Check(layer.GrassResetCount == resets, "restoring the curve reset the layer");
        // Placement class: one reset, a full rebuild.
        int builds1 = layer.TotalGrassBuildCount;
        layer.GrassCardLean = .2f;
        await Drive(layer, sampler, clean, home, geometry, 300, untilIdle: true);
        Check(layer.GrassResetCount == resets + 1, $"lean change: resets {layer.GrassResetCount - resets} != 1");
        Check(layer.TotalGrassBuildCount - builds1 >= layer.ResidentGrassTileCount && layer.ResidentGrassTileCount > 0, "lean change did not rebuild the tiles");
        // Atlas blades regenerate the atlas.
        RingGrassCardAssetSet before = layer.CardAssetSetForTests!;
        layer.GrassCardAtlasBlades = 40;
        ulong deadline = Time.GetTicksMsec() + 120_000;
        while (layer.CardAssetSetForTests?.Parameters.BladesPerVariant != 40 && Time.GetTicksMsec() < deadline)
        {
            layer.StepForTests(sampler, clean, home, geometry);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        await Drive(layer, sampler, clean, home, geometry, 300, untilIdle: true);
        RingGrassCardAssetSet after = layer.CardAssetSetForTests!;
        Check(after.Parameters.BladesPerVariant == 40 && after.CacheKey != before.CacheKey && after.Atlas.FullHash != before.Atlas.FullHash, "atlas blades did not regenerate the atlas");
        Check(layer.GrassResetCount == resets + 2, $"atlas change: resets {layer.GrassResetCount - resets} != 2");
        Check(ReferenceEquals(layer.CardMaterialForTests!.GetShaderParameter("grass_card_atlas").AsGodotObject(), after.AtlasTexture), "card material kept the old atlas");
        layer.GrassCardAtlasBlades = 56; layer.GrassCardLean = .12f;
        await Drive(layer, sampler, clean, home, geometry, 300, untilIdle: true);
        Check(layer.CardAssetSetForTests!.CacheKey == before.CacheKey && layer.ResidentGrassTileCount > 0, "restoring the defaults did not restore the default atlas");
        _report.Add($"GRASS_CARD_STYLE[live_settings] material=no rebuild, curve=no reset, lean=+1 reset, atlas 56->40 regenerated (hash {before.Atlas.FullHash:X16} -> {after.Atlas.FullHash:X16})");
    }

    // ------------------------------------------------------------------ (e) origin uv

    private static double Frac(double v) => v - Math.Floor(v);
    private static bool SameModOne(double a, double b, double tol = 2e-4) { double d = Frac(a - b); return d < tol || d > 1 - tol; }

    /// <summary>Cards grouping (S6 C): a group renderer's own uniforms (origin uv, gust origin, covectors from the group bounds) give
    /// the same terrain uv as the leaf's for every grouped instance (mod one texture period), including a group beside the ring wrap
    /// with the unwrap reference on either side of it.</summary>
    private void TestGroupUv()
    {
        double radius = Circumference / Math.Tau;
        long tiles = RingGrassCoordinates.AroundTileCount(Circumference, 32);
        int groups = 0, instances = 0;
        double worstUv = 0, worstShadow = 0, worstWorld = 0;
        var cases = new List<(long around, long across, int level, double home, double focus)>
        {
            (12_000, 400, 1, 12_000 * 32.0, 12_000 * 32.0), (12_000, 400, 2, 12_000 * 32.0, 12_000 * 32.0),
            ((tiles - 2), 400, 1, Circumference - 40.0, Circumference - 40.0), ((tiles - 2), 400, 1, Circumference - 40.0, 10.0),
            ((tiles - 4), 400, 2, Circumference - 40.0, Circumference - 40.0), ((tiles - 4), 400, 2, Circumference - 40.0, 10.0),
            (0, 400, 1, 40.0, 40.0), (0, 400, 1, 40.0, Circumference - 10.0),
        };
        foreach (var (aroundTile, acrossTile, level, home, focus) in cases)
        {
            var geometry = new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero, radius, 0, 0, Circumference, -1000, 1000,
                new RingCanonicalPosition(home, 0, 0));
            int factor = level == 2 ? 4 : 2;
            RingGrassGroupKey key = RingGrassGroupKey.FromLeaf(new RingGrassTileKey(aroundTile, acrossTile), level);
            var leaves = new List<RingGrassTileData>();
            for (int across = 0; across < factor; across++) for (int around = 0; around < factor; around++)
            {
                RingGrassTileKey leafKey = key.Leaf(around, across);
                RingGrassTileBounds bounds = RingGrassCoordinates.Bounds(leafKey.Around, leafKey.Across, 32, Circumference, Width);
                const int count = 4;
                float[] rows = new float[count * 20];
                for (int id = 0; id < count; id++)
                {
                    Transform3D transform = RingGrassCoordinates.CreateBladeLocalTransform(bounds, 2, bounds.AroundCenter + id * 5 - 7, 2,
                        bounds.AcrossCenter - id * 4 + 6, .08f, .8f, .4f, geometry);
                    RingGrassGrouping.WriteTransform(rows, id * 20, transform);
                    for (int i = 12; i < 20; i++) rows[id * 20 + i] = (leafKey.Around % 7 + id + i) * .013f;
                }
                leaves.Add(new RingGrassTileData(leafKey, bounds, 2, rows, new Aabb(new(-16, -3, -16), new(32, 12, 32)), 1, .5f, 13));
            }
            RingGrassTileData merged = RingGrassGrouping.Build(leaves, geometry, out _);
            Transform3D groupWorld = RingGrassCoordinates.CreateTileWorldTransform(merged.Bounds, merged.AnchorHeight, geometry);
            Vector4 groupOrigin = RingStandingGrassLayer.CardTerrainOriginUv(merged.Bounds, radius, Width, Circumference, focus);
            bool groupFrame = RingStandingGrassLayer.CalculateGrassGroundCovectors(geometry, merged.AnchorHeight, merged.Bounds.AroundCenter, out Vector3 groupAround, out Vector3 groupAcross);
            Check(groupFrame, "group covectors invalid");
            int destination = 0;
            foreach (RingGrassTileData leaf in leaves)
            {
                Transform3D leafWorld = RingGrassCoordinates.CreateTileWorldTransform(leaf.Bounds, leaf.AnchorHeight, geometry);
                Vector4 leafOrigin = RingStandingGrassLayer.CardTerrainOriginUv(leaf.Bounds, radius, Width, Circumference, focus);
                bool leafFrame = RingStandingGrassLayer.CalculateGrassGroundCovectors(geometry, leaf.AnchorHeight, leaf.Bounds.AroundCenter, out Vector3 leafAround, out Vector3 leafAcross);
                Check(leafFrame, "leaf covectors invalid");
                for (int id = 0; id < leaf.InstanceCount; id++, destination++)
                {
                    Vector3 leafPoint = leafWorld * RingGrassGrouping.ReadTransform(leaf.Buffer, id * 20).Origin;
                    Vector3 groupPoint = groupWorld * RingGrassGrouping.ReadTransform(merged.Buffer, destination * 20).Origin;
                    worstWorld = Math.Max(worstWorld, leafPoint.DistanceTo(groupPoint));
                    // Shader: uv = origin_uv.xy + canonical_offset * 0.2 with canonical_offset = (p - origin_world) . covector.
                    Vector3 ld = leafPoint - leafWorld.Origin, gd = groupPoint - groupWorld.Origin;
                    double lu = leafOrigin.X + ld.Dot(leafAround) * .2, lv = leafOrigin.Y + ld.Dot(leafAcross) * .2;
                    double gu = groupOrigin.X + gd.Dot(groupAround) * .2, gv = groupOrigin.Y + gd.Dot(groupAcross) * .2;
                    double du = gu - lu, dv = gv - lv;
                    worstUv = Math.Max(worstUv, Math.Max(Math.Abs(du - Math.Round(du)), Math.Abs(dv - Math.Round(dv))));
                    double ls = leafOrigin.Z + ld.Dot(leafAround) / (Math.Tau * radius), gs = groupOrigin.Z + gd.Dot(groupAround) / (Math.Tau * radius);
                    double lc = leafOrigin.W + ld.Dot(leafAcross) / Width, gc = groupOrigin.W + gd.Dot(groupAcross) / Width;
                    worstShadow = Math.Max(worstShadow, Math.Max(Math.Abs(gs - ls), Math.Abs(gc - lc)));
                    for (int i = 12; i < 20; i++) Check(leaf.Buffer[id * 20 + i] == merged.Buffer[destination * 20 + i], "group changed COLOR/CUSTOM");
                    instances++;
                }
            }
            groups++;
        }
        Check(worstUv <= 1e-3, $"group vs leaf terrain uv differs by {worstUv:E2}");
        Check(worstShadow <= 1e-6, $"group vs leaf macro-shadow uv differs by {worstShadow:E2}");
        _report.Add($"GRASS_CARD_STYLE[group_uv] groups={groups} instances={instances} worst_uv={worstUv:E2} worst_shadow_uv={worstShadow:E2} worst_world_m={worstWorld:E2}");
    }

    private void TestOriginUv()
    {
        foreach (long circumference in new[] { Circumference, 31_415_935L })
        {
            double radius = circumference / Math.Tau;
            long tiles = RingGrassCoordinates.AroundTileCount(circumference, 32);
            int checks = 0;
            // The uv a point gets from tile `bounds`, with the around coordinate taken on the branch nearest the focus.
            Vector4 Origin(RingGrassTileBounds b, double focus) => RingStandingGrassLayer.CardTerrainOriginUv(b, radius, Width, circumference, focus);
            (double u, double v, double sa, double sc) UvAt(RingGrassTileBounds b, double focus, double aroundOnBranch, double across)
            {
                Vector4 o = Origin(b, focus);
                double center = b.AroundCenter + circumference * Math.Round((focus - b.AroundCenter) / circumference);
                return (o.X + (aroundOnBranch - center) * .2, o.Y + (across - b.AcrossCenter) * .2,
                    o.Z + (aroundOnBranch - center) / (Math.Tau * radius), o.W + (across - b.AcrossCenter) / Width);
            }
            // Adjacent tiles around, in the interior, at the wrap (both focus sides), and across.
            var around = new List<(long a, long b, double edge, double focus)>();
            foreach (long i in new[] { 1000L, 17_000L, tiles / 2 }) around.Add((i, i + 1, (i + 1) * 32.0, (i + 1) * 32.0));
            // Wrap: last tile -> tile 0. The shared edge is around = circumference on the end side, 0 on the other.
            foreach (double focus in new[] { 5.0, circumference - 5.0, 0.0, circumference * .999999 })
                around.Add((tiles - 1, 0, focus < circumference * .5 ? 0.0 : (double)circumference, focus));
            foreach (var (ia, ib, edge, focus) in around)
            {
                RingGrassTileBounds ta = RingGrassCoordinates.Bounds(ia, 100, 32, circumference, Width), tb = RingGrassCoordinates.Bounds(ib, 100, 32, circumference, Width);
                double across = ta.AcrossCenter + 3.3;
                // For the wrap pair, evaluate the shared edge on the branch of the focus: end tile at `edge`, start tile at `edge` as well.
                var ua = UvAt(ta, focus, edge, across);
                var ub = UvAt(tb, focus, edge, across);
                Check(SameModOne(ua.u, ub.u) && SameModOne(ua.v, ub.v), $"circ={circumference} uv seam between tiles {ia}->{ib} focus={focus:F0}: {ua.u:F5} vs {ub.u:F5} (v {ua.v:F5} vs {ub.v:F5})");
                Check(SameModOne(ua.sa, ub.sa, 1e-6) && SameModOne(ua.sc, ub.sc, 1e-6), $"circ={circumference} shadow uv seam between tiles {ia}->{ib}");
                checks++;
            }
            // Across neighbours.
            foreach (long j in new[] { 0L, 500L, 9000L })
            {
                RingGrassTileBounds ta = RingGrassCoordinates.Bounds(2000, j, 32, circumference, Width), tb = RingGrassCoordinates.Bounds(2000, j + 1, 32, circumference, Width);
                double edge = ta.AcrossMax, aroundP = ta.AroundCenter + 5.5, focus = ta.AroundCenter;
                var ua = UvAt(ta, focus, aroundP, edge);
                var ub = UvAt(tb, focus, aroundP, edge);
                Check(SameModOne(ua.u, ub.u) && SameModOne(ua.v, ub.v), $"circ={circumference} uv seam across tiles {j}->{j + 1}");
                checks++;
            }
            // Canonical away from the wrap: independent of the focus and of floating-origin rebases.
            RingGrassTileBounds mid = RingGrassCoordinates.Bounds(40_000, 300, 32, circumference, Width);
            Vector4 canonical = Origin(mid, mid.AroundCenter);
            Check(canonical == Origin(mid, mid.AroundCenter + 250) && Math.Abs(canonical.X - (float)(Frac(mid.AroundCenter / 5.0) * 1.0)) < 1e-5,
                "origin uv is not canonical away from the wrap");
            // Terrain3D shader: uv.x = (anchor mod 1000 + SignedAroundDeltaMeters(p)) * 0.2. Per-tile origin uv under that anchor,
            // with the focus on either side of the seam, must equal it at the tile centre (card uv = origin + offset * 0.2).
            long wrapTiles = RingGrassCoordinates.AroundTileCount(circumference, 32);
            int anchorChecks = 0; bool focusWouldMismatch = false;
            foreach (long anchor in circumference % 32 != 0 ? Array.Empty<long>() : new[] { circumference - 96, circumference - 32, 32L, 96L, 0L }) // frames need a lattice-aligned circumference
            foreach (double focus in new[] { 20.0, circumference - 20.0, anchor + 0.0 })
            {
                var frame = new RingworldTechDemo.Terrain.Surface.RingSurfaceFrame(anchor, 1000, 0f, circumference, radius);
                for (long ti = -4; ti <= 4; ti++)
                {
                    long index = ((ti % wrapTiles) + wrapTiles) % wrapTiles;
                    RingGrassTileBounds tb = RingGrassCoordinates.Bounds(index, 100, 32, circumference, Width);
                    long centre = (long)Math.Round(tb.AroundCenter);
                    double terrainU = (((anchor % 1000) + 1000) % 1000 + frame.SignedAroundDeltaMeters(centre)) * .2;
                    Vector4 byAnchor = RingStandingGrassLayer.CardTerrainOriginUv(tb, radius, Width, circumference, anchor);
                    Check(SameModOne(byAnchor.X, terrainU, 1e-4), $"circ={circumference} anchor={anchor} tile {index}: origin uv {byAnchor.X:F5} != terrain {Frac(terrainU):F5}");
                    Vector4 byFocus = RingStandingGrassLayer.CardTerrainOriginUv(tb, radius, Width, circumference, focus);
                    if (!SameModOne(byFocus.X, terrainU, 1e-4)) focusWouldMismatch = true;
                    anchorChecks++;
                }
            }
            if (circumference % 5 != 0) Check(focusWouldMismatch, "focus-relative unwrap never disagreed with the terrain anchor (the test is not sensitive)");
            _report.Add($"GRASS_CARD_STYLE[origin_uv_anchor] circumference={circumference} anchor-vs-terrain-shader checks={anchorChecks} ok (focus-relative would mismatch: {focusWouldMismatch})");
            _report.Add($"GRASS_CARD_STYLE[origin_uv] circumference={circumference} (mod 5 = {circumference % 5}) seam checks={checks} ok");
        }
    }

    // ------------------------------------------------------------------ (d) style switch + blades plumbing

    private async Task TestSwitchAndBladesPlumbing(RingTerrainSampler sampler)
    {
        var clean = new RingLodSurfaceEditSnapshot(1, Circumference, new Dictionary<RingTerrainColumnCoordinate, int>());
        RingCanonicalPosition home = new(186_000.0 + 24, 0, 141_000.0 + 24);
        RingLodGeometrySnapshot geometry = Geometry(Vector3.Zero, home);
        var layer = new RingStandingGrassLayer { MaximumTileBuildsInFlight = 2, MaximumTileUploadsPerFrame = 4, MaximumTileUploadMilliseconds = 20, GrassCardReachMeters = 150 };
        AddChild(layer); layer.InitializeForTests();
        layer.ApplyGrassQuality(true, 48, 115, 12, 1, .45f);
        try
        {
            await Drive(layer, sampler, clean, home, geometry, 300, untilIdle: true);
            Check(layer.ResidentGrassTileCount > 0, "blades baseline installed nothing");
            VerifyBladeTiles(layer, sampler, clean, geometry, "initial");
            int resets0 = layer.GrassResetCount;
            int blades0 = layer.ResidentGrassTileCount;

            int builds0 = layer.TotalGrassBuildCount;
            layer.GrassRenderStyle = Cards;
            layer.StepForTests(sampler, clean, home, geometry);
            Check(layer.GrassResetCount == resets0 + 1, $"switch to Cards: resets {layer.GrassResetCount - resets0} != 1");
            Check(layer.ResidentGrassTileCount == 0, "switch to Cards left blade tiles resident");
            Check(layer.PendingGrassTileBuildCount > 0, "switch to Cards queued no rebuild");
            await Drive(layer, sampler, clean, home, geometry, 300, untilIdle: true);
            var cardTiles = layer.CaptureTileDataForTests();
            Check(cardTiles.Length > 0 && cardTiles.All(t => t.IsCardPlacement), "cards style produced non-card tiles");
            Check(layer.TotalGrassBuildCount - builds0 >= cardTiles.Length, "cards rebuild count below resident tile count");
            Check(layer.GrassResetCount == resets0 + 1, "extra reset while building Cards");
            Check(layer.CardMaterialForTests != null && layer.CardMaterialForTests.Shader.ResourcePath.EndsWith("ring_grass_cards.gdshader"), "card material/shader missing");
Check(layer.CardMaterialForTests!.GetShaderParameter("terrain_macro_shadow_strength").VariantType == Variant.Type.Float, "macro/planet shadow projector did not register the card material");            Check(layer.CardMaterialForTests!.GetShaderParameter("biome_texture_scale").AsSingle() == .2f && layer.CardMaterialForTests!.GetShaderParameter("grass_card_world_uv").AsBool(), "legacy no-follower fallback (biome textures, world uv) not active");
            _report.Add($"GRASS_CARD_STYLE[switch] blades_tiles={blades0} -> cards_tiles={cardTiles.Length} instances={layer.ResidentGrassInstanceCount} resets+{layer.GrassResetCount - resets0} builds+{layer.TotalGrassBuildCount - builds0}");
            // Wiring check: the per-tile instance uniform equals the helper's value.
            var audits = layer.CaptureTileAuditsForTests().Where(a => a.RenderInstanceId != 0).Take(3).ToArray();
            Check(audits.Length > 0, "no cards renderer to inspect");
            foreach (var audit in audits)
            {
                var renderer = GodotObject.InstanceFromId(audit.RenderInstanceId) as MultiMeshInstance3D;
                RingGrassTileBounds b = RingGrassCoordinates.Bounds(audit.Around, audit.Across, 32, Circumference, Width);
                Vector4 expected = RingStandingGrassLayer.CardTerrainOriginUv(b, Circumference / Math.Tau, Width, Circumference, home.AroundMeters);
                Variant actual = renderer?.GetInstanceShaderParameter("grass_terrain_origin_uv") ?? default;
                Check(actual.VariantType == Variant.Type.Vector4 && actual.AsVector4().IsEqualApprox(expected), $"tile {audit.Around},{audit.Across}: origin uv uniform {actual} != {expected}");
            }

            // GPU card compute frame: cached, follows the margin property (clamped) and the style curve.
            RingGrassCardComputeParameters frame0 = layer.CardComputeFrame();
            Check(frame0.DistanceMarginMeters == layer.GrassCardComputeMarginMeters && frame0.ReachMeters == layer.GrassCardReachMeters, "card compute frame ignores the margin/reach");
            Check(layer.CardComputeFrame().Equals(frame0), "card compute frame not stable for unchanged inputs");
            layer.GrassCardComputeMarginMeters = 99f;
            Check(layer.CardComputeFrame().DistanceMarginMeters == 8f, "card compute margin not clamped to 8 m");
            layer.GrassCardComputeMarginMeters = 2f;
            Check(layer.CardComputeFrame().Equals(frame0), "card compute frame did not return to the default");

            // Follower registration: with a Terrain3D surface present the card material follows it (and leaves the world-uv fallback).
            var surface = new RingworldTechDemo.Terrain.Surface.RingTerrain3DSurface();
            AddChild(surface);
            layer.CardSurfaceOverrideForTests = surface;
            ulong followDeadline = Time.GetTicksMsec() + 3000;
            while (!layer.CardMaterialFollowsSurface && Time.GetTicksMsec() < followDeadline)
            {
                layer.StepForTests(sampler, clean, home, geometry);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            Check(layer.CardMaterialFollowsSurface && surface.FollowerCount == 1, $"card material was not registered as a surface follower (followers={surface.FollowerCount})");
            Check(!layer.CardMaterialForTests!.GetShaderParameter("grass_card_world_uv").AsBool(), "follower mode kept the world-uv fallback");
            layer.CardSurfaceOverrideForTests = null;
            surface.QueueFree();
            Check(layer.CardUnwrapReferenceForTests == home.AroundMeters, "without a terrain frame the uv unwrap reference must be the focus");
            // Reach / hysteresis are coverage settings: a live change goes through replan, eviction and tier filtering, never a reset.
            int resetsR = layer.GrassResetCount, tilesBefore = layer.ResidentGrassTileCount;
            layer.GrassCardReachMeters = 110;
            await Drive(layer, sampler, clean, home, geometry, 300, untilIdle: true);
            Check(layer.GrassResetCount == resetsR, "a reach change reset the layer");
            Check(layer.ResidentGrassTileCount < tilesBefore, $"shorter reach kept {layer.ResidentGrassTileCount} of {tilesBefore} tiles");
            layer.GrassStreamingHysteresisMeters = 4;
            await Drive(layer, sampler, clean, home, geometry, 300, untilIdle: true);
            Check(layer.GrassResetCount == resetsR, "a hysteresis change reset the layer");
            _report.Add($"GRASS_CARD_STYLE[coverage_change] reach 150->110 and H 8->4 without reset: tiles {tilesBefore} -> {layer.ResidentGrassTileCount}");
            layer.GrassCardReachMeters = 150; layer.GrassStreamingHysteresisMeters = 0;
            await Drive(layer, sampler, clean, home, geometry, 300, untilIdle: true);
            await TestLiveSettingClasses(layer, sampler, clean, home, geometry);
            // Second switch back: exactly one reset and a full blades rebuild equal to a direct build.
            int resets1 = layer.GrassResetCount;
            layer.GrassRenderStyle = Blades;
            layer.StepForTests(sampler, clean, home, geometry);
            Check(layer.GrassResetCount == resets1 + 1, $"switch to Blades: resets {layer.GrassResetCount - resets1} != 1");
Check(layer.CardMaterialForTests == null, "leaving Cards kept the card material (it would keep receiving interaction/modifier/projector writes)");
            await Drive(layer, sampler, clean, home, geometry, 300, untilIdle: true);
            Check(layer.ResidentGrassTileCount == blades0, $"blades tiles after round trip {layer.ResidentGrassTileCount} != {blades0}");
            Check(layer.CaptureTileDataForTests().All(t => !t.IsCardPlacement), "blades round trip kept card tiles");
            VerifyBladeTiles(layer, sampler, clean, geometry, "round trip");
            int settled = layer.GrassResetCount;
            await Drive(layer, sampler, clean, home, geometry, 300, untilIdle: false);
            Check(layer.GrassResetCount == settled, "reset while idle");
        }
        finally { layer.QueueFree(); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    }

    /// <summary>Layer-built Blades tiles must equal a direct build with the pre-S5 argument set.</summary>
    private void VerifyBladeTiles(RingStandingGrassLayer layer, RingTerrainSampler sampler, RingLodSurfaceEditSnapshot clean, RingLodGeometrySnapshot geometry, string label)
    {
        int compared = 0;
        foreach (RingGrassTileData tile in layer.CaptureTileDataForTests().OrderBy(t => t.Key.Around).ThenBy(t => t.Key.Across).Take(6))
        {
            RingGrassTileData fresh = RingGrassTileBuilder.Build(tile.Key, tile.Bounds, 8, 9, 0, 6000, 25, sampler, clean, geometry, CancellationToken.None,
                appearance: RingGrassAppearanceSnapshot.Default, sourceFraction: tile.SourceFraction, sourceTier: tile.SourceTier, useGrassClumps: false,
                meadowTufts: false, meadowSpread: 1, minimumHeight: .10f, maximumHeight: .20f, useConfiguredHeightRange: true);
            bool same = fresh.PlacementHash == tile.PlacementHash && fresh.InstanceCount == tile.InstanceCount && fresh.Buffer.AsSpan().SequenceEqual(tile.Buffer);
            Check(same, $"blades {label}: tile {tile.Key} differs from a direct build");
            compared++;
        }
        Check(compared > 0, $"blades {label}: nothing compared");
        _report.Add($"GRASS_CARD_STYLE[blades_plumbing] {label}: {compared} tiles byte-identical to a direct builder call");
    }

    // ------------------------------------------------------------------ budget saturation / asset failure

    private async Task TestBudgetAndAssets(RingTerrainSampler sampler)
    {
        var clean = new RingLodSurfaceEditSnapshot(1, Circumference, new Dictionary<RingTerrainColumnCoordinate, int>());
        RingCanonicalPosition home = new(186_000.0 + 24, 0, 141_000.0 + 24);
        RingLodGeometrySnapshot geometry = Geometry(Vector3.Zero, home);
        // Analytic bound: every planned tile at placement chance 1 in the highest-density tier it can be held at (d - H).
        foreach (float reach in new[] { 200f, 400f })
        {
            RingGrassStyleProfile p = RingGrassStyleProfile.Build(new RingGrassStyleProfile.Inputs(Cards, 0, 32, reach, 0, 256, 0));
            double planning = p.ReachMeters + p.TileMeters * .125 + p.HysteresisMeters;
            long worst = 0; int tiles = 0;
            for (long z = -30; z <= 30; z++) for (long x = -30; x <= 30; x++)
            {
                RingGrassTileBounds b = RingGrassCoordinates.Bounds(186_000 / 32 + x, 141_000 / 32 + z, 32, Circumference, Width);
                double d = Math.Sqrt(RingGrassCoordinates.TileDistanceSquared(b, home, Circumference));
                if (d > planning + p.HysteresisMeters) continue;
                worst += p.ReserveForTier(p.TierForDistance(Math.Max(0, d - p.HysteresisMeters)));
                tiles++;
            }
            Check(worst <= p.InstanceBudget, $"reach {reach}: full-density worst case {worst:N0} ({tiles} tiles) exceeds the budget {p.InstanceBudget:N0}");
            _report.Add($"GRASS_CARD_STYLE[budget] reach={reach} worst-case(chance 1, held tiers)={worst:N0} budget={p.InstanceBudget:N0} ratio={(double)worst / p.InstanceBudget:F2}");
        }
        // Forced chance 1, natural budget, hysteresis-held tiers: nothing may be built, rejected and re-queued while idle.
        long forcedInstances = 0;
        RingGrassTileBuilder.ForceCardChanceForTests = 1f;
        try
        {
            var layer = new RingStandingGrassLayer { GrassRenderStyle = Cards, GrassCardReachMeters = 200, MaximumTileBuildsInFlight = 4, MaximumTileUploadsPerFrame = 8, MaximumTileUploadMilliseconds = 20 };
            AddChild(layer); layer.InitializeForTests();
            try
            {
                await Drive(layer, sampler, clean, home, geometry, 300, untilIdle: true);
                forcedInstances = layer.ResidentGrassInstanceCount;
                Check(forcedInstances < layer.MaximumGrassSourceInstances, $"full-density field saturated the budget ({forcedInstances:N0} / {layer.MaximumGrassSourceInstances:N0})");
                int builds = layer.TotalGrassBuildCount;
                await Drive(layer, sampler, clean, home, geometry, 900, untilIdle: false);
                for (int i = 0; i < 4; i++) await Drive(layer, sampler, clean, home with { AroundMeters = home.AroundMeters + (i % 2 == 0 ? 3 : -3) }, geometry, 20, untilIdle: true);
                int afterFirst = layer.TotalGrassBuildCount;
                for (int i = 4; i < 30; i++) await Drive(layer, sampler, clean, home with { AroundMeters = home.AroundMeters + (i % 2 == 0 ? 3 : -3) }, geometry, 20, untilIdle: true);
                Check(layer.TotalGrassBuildCount == afterFirst, $"full-density oscillation kept rebuilding ({layer.TotalGrassBuildCount - afterFirst} builds)");
                Check(afterFirst - builds <= 40, $"full-density idle+first oscillation rebuilt {afterFirst - builds} tiles");
                _report.Add($"GRASS_CARD_STYLE[budget_forced_full_density] reach=200 tiles={layer.ResidentGrassTileCount} instances={forcedInstances:N0} budget={layer.MaximumGrassSourceInstances:N0} idle_rebuilds=0 oscillation_rebuilds_after_first=0");
            }
            finally { layer.QueueFree(); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }

            // A genuinely saturated budget must idle too: refused tiles are budget_reached, never built-and-rejected in a loop.
            var small = new RingStandingGrassLayer { GrassRenderStyle = Cards, GrassCardReachMeters = 200, MaximumTileBuildsInFlight = 4, MaximumTileUploadsPerFrame = 8, MaximumTileUploadMilliseconds = 20 };
            small.CardBudgetOverrideForTests = (int)(forcedInstances * .5);
            AddChild(small); small.InitializeForTests();
            try
            {
                await Drive(small, sampler, clean, home, geometry, 600, untilIdle: true);
                Check(small.ResidentGrassInstanceCount <= small.MaximumGrassSourceInstances, "saturated budget exceeded");
                int builds = small.TotalGrassBuildCount;
                await Drive(small, sampler, clean, home, geometry, 1200, untilIdle: false);
                Check(small.TotalGrassBuildCount == builds, $"saturated budget kept building ({small.TotalGrassBuildCount - builds} builds while idle)");
                _report.Add($"GRASS_CARD_STYLE[budget_saturated] budget={small.MaximumGrassSourceInstances:N0} resident={small.ResidentGrassInstanceCount:N0} tiles={small.ResidentGrassTileCount} idle_builds=0");
            }
            finally { small.QueueFree(); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        }
        finally { RingGrassTileBuilder.ForceCardChanceForTests = -1f; }

        // A faulted asset build is reported and evicted, so the next request retries.
        var other = RingGrassCardAtlasParameters.VariantD with { BladesPerVariant = 12 };
        RingGrassCardAssets.FailNextBuildForTests = true;
        Task<RingGrassCardAssetSet> failed = RingGrassCardAssets.RequestAsync(other);
        try { await failed; } catch { }
        Check(failed.IsFaulted && !RingGrassCardAssets.TryGet(other, out _), "forced asset failure did not fault");
        Task<RingGrassCardAssetSet> retry = RingGrassCardAssets.RequestAsync(other);
        Check(!ReferenceEquals(retry, failed), "faulted asset task stayed cached");
        await retry;
        Check(RingGrassCardAssets.TryGet(other, out RingGrassCardAssetSet? ok) && ok != null, "asset retry did not produce a set");
        _report.Add("GRASS_CARD_STYLE[asset_failure] faulted build reported, evicted and retried");
    }

    // ------------------------------------------------------------------ (b) cards streaming, resident counts

    private sealed class Tracker
    {
        public Dictionary<(long, long), ulong> Resident = new();
        public Dictionary<(long, long), ulong> Evicted = new();
        public long Evictions, Readds, ReaddsIdentical;
        public void Observe(RingStandingGrassLayer.TileAudit[] audits)
        {
            var now = new Dictionary<(long, long), ulong>();
            foreach (var a in audits) now[(a.Around, a.Across)] = a.PlacementHash;
            foreach (var kv in Resident) if (!now.ContainsKey(kv.Key)) { Evictions++; Evicted[kv.Key] = kv.Value; }
            foreach (var kv in now)
            {
                if (Resident.ContainsKey(kv.Key)) continue;
                if (Evicted.TryGetValue(kv.Key, out ulong old)) { Readds++; if (old == kv.Value) ReaddsIdentical++; Evicted.Remove(kv.Key); }
            }
            Resident = now;
        }
    }

    private readonly record struct Snapshot(long Builds, long Uploads, long Evictions, long IdenticalReadds, long Filters, long ParameterWrites, int Resets, int NewTile, int Upgrade, int Other);

    private static Snapshot Capture(RingStandingGrassLayer layer, Tracker tracker)
        => new(layer.TotalGrassBuildCount, layer.TotalGrassUploadCount, tracker.Evictions, tracker.ReaddsIdentical, layer.GrassTierFilterCount,
            layer.PresentationParameterWrites, layer.GrassResetCount, layer.GrassBuildNewTileCount, layer.GrassBuildTierUpgradeCount, layer.GrassBuildTierOtherCount);

    private void Report(string scenario, Snapshot a, Snapshot b, long maxBuilds, long maxEvictions, long maxUploads = long.MaxValue, long maxWrites = long.MaxValue)
    {
        long builds = b.Builds - a.Builds, uploads = b.Uploads - a.Uploads, evictions = b.Evictions - a.Evictions, writes = b.ParameterWrites - a.ParameterWrites;
        _report.Add($"GRASS_CARD_STYLE[cards] {scenario}: builds={builds} reasons[new={b.NewTile - a.NewTile} upgrade={b.Upgrade - a.Upgrade} other={b.Other - a.Other} filtered={b.Filters - a.Filters}] uploads={uploads} evictions={evictions} identical_readds={b.IdenticalReadds - a.IdenticalReadds} param_writes={writes} resets={b.Resets - a.Resets}");
        Check(builds <= maxBuilds, $"cards/{scenario}: builds {builds} > {maxBuilds}");
        Check(evictions <= maxEvictions, $"cards/{scenario}: evictions {evictions} > {maxEvictions}");
        Check(uploads <= maxUploads, $"cards/{scenario}: uploads {uploads} > {maxUploads}");
        Check(writes <= maxWrites, $"cards/{scenario}: presentation parameter writes {writes} > {maxWrites}");
        Check(b.Resets == a.Resets, $"cards/{scenario}: layer reset");
        if (maxBuilds == 0) Check(b.IdenticalReadds - a.IdenticalReadds == 0, $"cards/{scenario}: identical re-adds");
    }

    private async Task TestCardsStreaming(RingTerrainSampler sampler)
    {
        var clean = new RingLodSurfaceEditSnapshot(1, Circumference, new Dictionary<RingTerrainColumnCoordinate, int>());
        RingCanonicalPosition home = new(186_000.0 + 24, 0, 141_000.0 + 24);
        RingLodGeometrySnapshot geometry = Geometry(Vector3.Zero, home);
        // The user's maxed blade preferences are set on purpose: they must not reach Cards.
        var layer = new RingStandingGrassLayer
        {
            GrassRenderStyle = Cards, MaximumTileBuildsInFlight = 4, MaximumTileUploadsPerFrame = 8, MaximumTileUploadMilliseconds = 20,
            BladesPerSquareMeter = 96, MaximumInstancesPerTile = 1700, MaximumInstances = 5_000_000, MaximumResidentTiles = 40,
        };
        AddChild(layer); layer.InitializeForTests();
        var tracker = new Tracker();
        RingGrassStyleProfile profile = layer.StyleProfile;
        try
        {
            await Drive(layer, sampler, clean, home, geometry, 300, untilIdle: true, tracker);
            int tiles = layer.ResidentGrassTileCount;
            Check(tiles > 0, "cards baseline installed nothing");
            Check(tiles <= layer.PlanningTilesInRadiusForTests, "resident tiles exceed the planned set");
            // (c) the resident cap never saturates: the planned set (incl. the hold ring) is below it, with room to spare.
            int planned = layer.PlanningTilesInRadiusForTests;
            Check(planned < profile.ResidentTileCap, $"cap saturated: planned {planned} >= cap {profile.ResidentTileCap}");
            Check(planned + profile.PerimeterTiles <= profile.ResidentTileCap, $"cap headroom below one perimeter: planned {planned}, perimeter {profile.PerimeterTiles}, cap {profile.ResidentTileCap}");
            Check(layer.ResidentGrassInstanceCount < layer.MaximumGrassSourceInstances, $"instance budget saturated ({layer.ResidentGrassInstanceCount} vs {layer.MaximumGrassSourceInstances})");
            Check(layer.PendingGrassTileBuildCount == 0, "builds still pending at idle");
            var data = layer.CaptureTileDataForTests();
            Check(data.All(t => t.IsCardPlacement && t.TierKeys != null), "non-card tile in Cards style");
            var byTier = data.GroupBy(t => (int)t.SourceTier).OrderBy(g => g.Key).Select(g => $"t{g.Key}:{g.Count()}tiles/{g.Sum(t => (long)t.InstanceCount):N0}inst").ToArray();
            long instances = layer.ResidentGrassInstanceCount, bytes = layer.ResidentGrassInstanceBytes;
            var atlasData = RingGrassCardAssets.GetBlocking(RingGrassCardAtlasParameters.VariantD).Atlas;
            double atlasMb = atlasData.Width * (double)atlasData.Height * 4 * 1.34 / 1048576.0;
            _report.Add($"GRASS_CARD_STYLE[resident] reach=400 tiles={tiles} planned={planned} cap={profile.ResidentTileCap} instances={instances:N0} budget={layer.MaximumGrassSourceInstances:N0} "
                + $"instance_buffer_mib={bytes / 1048576.0:F1} atlas_mib={atlasMb:F1} est_vram_mib={(bytes + 0) / 1048576.0 + atlasMb:F1} per_tier=[{string.Join(" ", byTier)}] low_layer_instances={data.Sum(t => (long)t.LowLayerInstanceCount):N0}");
            // Worst case: every resident tile as dense as the densest meadow tile found nearby (Risk 1: resident instances / VRAM).
            int denseMain = 0, denseLow = 0;
            for (int dz = 0; dz < 12; dz++) for (int da = 0; da < 12; da++)
            {
                var key = new RingGrassTileKey(186_000 / 32 + da * 15, 141_000 / 32 + dz * 15);
                RingGrassTileBounds b = RingGrassCoordinates.Bounds(key.Around, key.Across, 32, Circumference, Width);
                RingGrassTileData full = RingGrassTileBuilder.Build(key, b, 8, 4, 0, profile.MainCapPerTile, 25, sampler, clean, Geometry(Vector3.Zero, new RingCanonicalPosition(b.AroundCenter, 0, b.AcrossCenter)),
                    CancellationToken.None, sourceFraction: 1, sourceTier: 0, cards: profile.PlacementForTier(0));
                if (full.InstanceCount > denseMain + denseLow) { denseMain = full.InstanceCount - full.LowLayerInstanceCount; denseLow = full.LowLayerInstanceCount; }
            }
            long worst = 0;
            foreach (var group in data.GroupBy(t => (int)t.SourceTier))
                worst += (long)group.Count() * ((long)Math.Ceiling(denseMain * (double)profile.FractionForTier(group.Key)) + (profile.IncludesLowLayer(group.Key) ? denseLow : 0));
            _report.Add($"GRASS_CARD_STYLE[resident_worst_case] densest nearby tile = {denseMain:N0} cards + {denseLow:N0} low; same tier mix at that density = {worst:N0} instances = {worst * RingGrassStyleProfile.BytesPerInstance / 1048576.0:F1} MiB instance buffer "
                + $"(+{atlasMb:F1} MiB atlas) = {worst * RingGrassStyleProfile.BytesPerInstance / 1048576.0 + atlasMb:F1} MiB; analytic full-occupancy budget {layer.MaximumGrassSourceInstances:N0} = {layer.MaximumGrassSourceInstances * RingGrassStyleProfile.BytesPerInstance / 1048576.0:F1} MiB");
            Check(worst < layer.MaximumGrassSourceInstances, $"dense-meadow worst case {worst:N0} exceeds the instance budget {layer.MaximumGrassSourceInstances:N0}");
            tracker.Evictions = tracker.Readds = tracker.ReaddsIdentical = 0;

            // (b) 1. Stationary: 3600 frames of nothing.
            Snapshot a = Capture(layer, tracker);
            await Drive(layer, sampler, clean, home, geometry, 3600, untilIdle: false, tracker);
            Report("stationary_3600_frames", a, Capture(layer, tracker), 0, 0, 0, 0);

            // 2. +/-0.75 m oscillation (around only, then around+across corners): the first excursions may upgrade a tile whose
            // lookahead boundary they newly reach; once every extreme has been visited later cycles do nothing.
            RingCanonicalPosition Osc(int i, bool corners) => home with
            {
                AroundMeters = home.AroundMeters + (i % 2 == 0 ? .75 : -.75),
                AcrossMeters = home.AcrossMeters + (corners ? (i % 4 < 2 ? .4 : -.4) : 0),
            };
            foreach (bool corners in new[] { false, true })
            {
                string name = corners ? "oscillate_corners" : "oscillate_around";
                a = Capture(layer, tracker);
                for (int i = 0; i < 4; i++) await Drive(layer, sampler, clean, Osc(i, corners), geometry, 20, untilIdle: true, tracker);
                Snapshot first = Capture(layer, tracker);
                Report(name + "_first_cycle", a, first, 8, 8);
                for (int i = 4; i < 44; i++) await Drive(layer, sampler, clean, Osc(i, corners), geometry, 20, untilIdle: true, tracker);
                Report(name + "_later_cycles", first, Capture(layer, tracker), 0, 0);
            }
            await Drive(layer, sampler, clean, home, geometry, 20, untilIdle: true, tracker);

            // 3. Floating-origin rebase: a transform only.
            Snapshot beforeRebase = Capture(layer, tracker);
            RingLodGeometrySnapshot rebased = Geometry(new Vector3(37, 0, -19), home);
            await Drive(layer, sampler, clean, home, rebased, 120, untilIdle: false, tracker);
            Snapshot afterRebase = Capture(layer, tracker);
            Report("rebase", beforeRebase, afterRebase, 0, 0, 0);
            Check(afterRebase.ParameterWrites > beforeRebase.ParameterWrites, "a floating-origin rebase wrote no per-tile transform uniforms (the rebase did not take effect)");
            geometry = rebased;

            // 4. Straight crossing of 60 m: bounded by the swept band of the outer ring plus each tier boundary ring.
            RingCanonicalPosition crossed = home with { AroundMeters = home.AroundMeters + 60 };
            Snapshot beforeCross = Capture(layer, tracker);
            await Drive(layer, sampler, clean, crossed, geometry, 60, untilIdle: true, tracker);
            Snapshot cross = Capture(layer, tracker);
            double tile = profile.TileMeters, bound = Math.Ceiling(2.0 * profile.ReachMeters * 60.0 / (tile * tile));
            for (int t = 1; t < profile.TierCount; t++) if (!float.IsPositiveInfinity(profile.TierStartMeters[t])) bound += Math.Ceiling(2.0 * Math.Min(profile.TierStartMeters[t], profile.ReachMeters) * 60.0 / (tile * tile)) + 2;
            bound += 8;
            Report("cross_60m", beforeCross, cross, (long)bound, (long)bound);
            await Drive(layer, sampler, clean, crossed, geometry, 600, untilIdle: false, tracker);
            Report("cross_then_stationary", cross, Capture(layer, tracker), 0, 0, 0, 0);
            Check(layer.PlanningTilesInRadiusForTests < profile.ResidentTileCap, "cap saturated after crossing");
            Check(layer.ResidentGrassInstanceCount < layer.MaximumGrassSourceInstances, "budget saturated after crossing");
            _report.Add($"GRASS_CARD_STYLE[resident_after_cross] tiles={layer.ResidentGrassTileCount} instances={layer.ResidentGrassInstanceCount:N0} planned={layer.PlanningTilesInRadiusForTests}");
        }
        finally { layer.QueueFree(); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    }

    private async Task Drive(RingStandingGrassLayer layer, RingTerrainSampler sampler, RingLodSurfaceEditSnapshot edits,
        RingCanonicalPosition focus, RingLodGeometrySnapshot geometry, int frames, bool untilIdle, Tracker? tracker = null)
    {
        ulong deadline = Time.GetTicksMsec() + 540_000;
        for (int i = 0; ; i++)
        {
            layer.StepForTests(sampler, edits, focus, geometry);
            tracker?.Observe(layer.CaptureTileAuditsForTests());
            if (untilIdle ? i >= frames - 1 && layer.PendingGrassTileBuildCount == 0 && layer.PendingGrassUploadCount == 0 : i >= frames - 1) break;
            if (Time.GetTicksMsec() > deadline) throw new TimeoutException("grass streaming did not settle");
            if (!untilIdle && i % 64 != 0) continue;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static RingLodGeometrySnapshot Geometry(Vector3 origin, RingCanonicalPosition anchor)
        => new(Transform3D.Identity, origin, Vector3.Zero, Circumference / Math.Tau, 0, 0, Circumference, -1000, 1000, anchor); // origin = FloatingOriginWorldOffset (a real rebase)
}
