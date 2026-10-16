using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Player-tunable Cards settings (grass-cards plan step S8). Names equal the live-tuning keys. Rebuild classes:
/// placement (lattice and low-layer density, tile size, heights, lean, atlas blades) rebuilds every tile through the placement
/// fingerprint; the density curve goes through the coverage fingerprint (tier filter, never a reset); everything else is a
/// material uniform write. None of these is read in Blades style. Defaults equal grass-cards variant D.</summary>
public partial class RingStandingGrassLayer
{
    // ---- Coverage ----
    [ExportGroup("Grass cards coverage")]
    /// <summary>Main lattice density per square metre (rebuilds).</summary>
    [Export(PropertyHint.Range, "2,24,0.5")] public float GrassCardNearDensity { get; set; } = 14f;
    /// <summary>Density at the start of the far falloff (curve).</summary>
    [Export(PropertyHint.Range, "0.5,6,0.1")] public float GrassCardBaseDensity { get; set; } = 2.5f;
    [Export(PropertyHint.Range, "0.02,1,0.01")] public float GrassCardFloorDensity { get; set; } = .15f;
    [Export(PropertyHint.Range, "0.5,2.5,0.05")] public float GrassCardDensityFalloff { get; set; } = 1.3f;
    [Export(PropertyHint.Range, "0,100,1")] public float GrassCardNearBoostStartMeters { get; set; } = 8f;
    [Export(PropertyHint.Range, "1,200,1")] public float GrassCardNearBoostEndMeters { get; set; } = 45f;
    /// <summary>Low upright layer lattice density (rebuilds); 0 removes the layer.</summary>
    [Export(PropertyHint.Range, "0,30,0.5")] public float GrassCardLowLayerDensity { get; set; } = 18f;
    [Export(PropertyHint.Range, "10,60,1")] public float GrassCardLowLayerEndMeters { get; set; } = 22f;
    /// <summary>Largest scale the survivors of thinning grow to; capped at 2.5 by the instance bounds.</summary>
    [Export(PropertyHint.Range, "1,2.5,0.05")] public float GrassCardScaleCompensationMax { get; set; } = 2.5f;
    [Export(PropertyHint.Range, "20,600,1")] public float GrassCardSingleCardStartMeters { get; set; } = 90f;
    [Export(PropertyHint.Range, "30,700,1")] public float GrassCardSingleCardEndMeters { get; set; } = 160f;
    [Export(PropertyHint.Range, "1,50,1")] public float GrassCardFadeBandPercent { get; set; } = 15f;

    // ---- Form ----
    [ExportGroup("Grass cards form")]
    [Export(PropertyHint.Range, "0.2,1.5,0.01")] public float GrassCardTallHeight { get; set; } = .6f;
    [Export(PropertyHint.Range, "0.1,1,0.01")] public float GrassCardShortHeight { get; set; } = .3f;
    [Export(PropertyHint.Range, "0.3,1,0.01")] public float GrassCardNearWidthScale { get; set; } = .7f;
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float GrassCardLean { get; set; } = .12f;
    [Export(PropertyHint.Range, "0,0.4,0.01")] public float GrassCardTilt { get; set; } = .08f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float GrassCardNormalOut { get; set; } = .45f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float GrassCardNormalFace { get; set; } = .25f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float GrassCardBacklight { get; set; } = .28f;
    [Export(PropertyHint.Range, "0.1,0.9,0.01")] public float GrassCardAlphaCutoff { get; set; } = .5f;
    /// <summary>Blades per atlas tuft variant; regenerates the atlas (rebuilds).</summary>
    [Export(PropertyHint.Range, "4,96,1")] public int GrassCardAtlasBlades { get; set; } = 56;

    // ---- Colour ----
    [ExportGroup("Grass cards colour")]
    [Export(PropertyHint.Range, "0,1,0.01")] public float GrassCardRootAo { get; set; } = .5f;
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float GrassCardTipLight { get; set; } = .16f;
    [Export(PropertyHint.Range, "0,0.6,0.01")] public float GrassCardBladeValueJitter { get; set; } = .22f;
    [Export(PropertyHint.Range, "0,0.3,0.01")] public float GrassCardBladeHueJitter { get; set; } = .10f;
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float GrassCardDryFraction { get; set; } = .07f;
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float GrassCardTuftValueJitter { get; set; } = .14f;
    [Export(PropertyHint.Range, "10,400,1")] public float GrassCardFragmentColorMeters { get; set; } = 70f;
    [Export(PropertyHint.Range, "0,99,1")] public float GrassCardConvergeStartPercent { get; set; } = 60f;
    [Export(PropertyHint.Range, "1,100,1")] public float GrassCardConvergeEndPercent { get; set; } = 90f;

    /// <summary>Placement and curve settings captured for the style profile (allocation free).</summary>
    private RingGrassCardTuning CaptureCardTuning() => new(
        GrassCardNearDensity, GrassCardLowLayerDensity, GrassCardTallHeight, GrassCardShortHeight, GrassCardLean, GrassCardAtlasBlades,
        GrassCardBaseDensity, GrassCardFloorDensity, GrassCardDensityFalloff, GrassCardNearBoostStartMeters, GrassCardNearBoostEndMeters,
        GrassCardLowLayerEndMeters, GrassCardScaleCompensationMax, GrassCardNearWidthScale, GrassCardFadeBandPercent);

    /// <summary>Material-only settings (normalized) the card material is written from.</summary>
    private RingGrassCardMaterialSettings CaptureCardMaterialSettings() => new RingGrassCardMaterialSettings(
        GrassCardGreenPull, GrassCardAlphaCutoff, GrassCardSingleCardStartMeters, GrassCardSingleCardEndMeters, GrassCardTilt,
        GrassCardNormalOut, GrassCardNormalFace, GrassCardBacklight, GrassCardRootAo, GrassCardTipLight, GrassCardBladeValueJitter,
        GrassCardBladeHueJitter, GrassCardDryFraction, GrassCardTuftValueJitter, GrassCardFragmentColorMeters,
        GrassCardConvergeStartPercent, GrassCardConvergeEndPercent).Normalized();

    internal RingGrassCardMaterialSettings CardMaterialSettingsForTests => CaptureCardMaterialSettings();
    internal RingGrassCardTuning CardTuningForTests => CaptureCardTuning();
    internal RingGrassCardAssetSet? CardAssetSetForTests => _cardAssets;

    /// <summary>Restores every Cards setting except the quality-profile budgets (reach and near density) to its variant D default.
    /// Called by the graphics controller when it resets grass tuning to the current profile.</summary>
    public void ResetGrassCardSettings()
    {
        GrassCardTileMeters = 32f;
        GrassCardBaseDensity = 2.5f; GrassCardFloorDensity = .15f; GrassCardDensityFalloff = 1.3f;
        GrassCardNearBoostStartMeters = 8f; GrassCardNearBoostEndMeters = 45f;
        GrassCardLowLayerDensity = 18f; GrassCardLowLayerEndMeters = 22f; GrassCardScaleCompensationMax = 2.5f;
        GrassCardSingleCardStartMeters = 90f; GrassCardSingleCardEndMeters = 160f; GrassCardFadeBandPercent = 15f;
        GrassCardTallHeight = .6f; GrassCardShortHeight = .3f; GrassCardNearWidthScale = .7f; GrassCardLean = .12f; GrassCardTilt = .08f;
        GrassCardNormalOut = .45f; GrassCardNormalFace = .25f; GrassCardBacklight = .28f; GrassCardAlphaCutoff = .5f; GrassCardAtlasBlades = 56;
        GrassCardGreenPull = .15f; GrassCardRootAo = .5f; GrassCardTipLight = .16f; GrassCardBladeValueJitter = .22f;
        GrassCardBladeHueJitter = .10f; GrassCardDryFraction = .07f; GrassCardTuftValueJitter = .14f; GrassCardFragmentColorMeters = 70f;
        GrassCardConvergeStartPercent = 60f; GrassCardConvergeEndPercent = 90f;
    }
}
