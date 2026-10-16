using Godot;

namespace RingworldTechDemo.UI;

public partial class RingLiveTuningDrawer
{
    /// <summary>Cards-style groups of the Grass tab (grass-cards plan step S8). Every slider is debounced like the rest of the tab;
    /// "(rebuilds)" marks the placement class (a change rebuilds all grass tiles). Cards settings are ignored in Blades style.</summary>
    private void BuildGrassCardControls(VBoxContainer tab)
    {
        var coverage = AddGrassGroup(tab, "Grass cards — coverage", "Cards only. Reach and the density curve decide how many cards are drawn and how far. Settings marked (rebuilds) re-place every tile; the others only re-filter the tiers. The streaming hysteresis in Coverage and distance applies to Cards as well.");
        AddGrassSlider(coverage, "GrassCardReachMeters", "Cards reach (m, rebuilds)", 100, 600, 5);
        AddGrassSlider(coverage, "GrassCardTileMeters", "Card tile size (m, rebuilds)", 16, 64, 1);
        AddGrassSlider(coverage, "GrassCardNearDensity", "Near card density (per m², rebuilds)", 2, 24, .5);
        AddGrassSlider(coverage, "GrassCardLowLayerDensity", "Low layer density (per m², rebuilds)", 0, 30, .5);
        AddGrassSlider(coverage, "GrassCardLowLayerEndMeters", "Low layer end distance (m)", 10, 60, 1);
        AddGrassSlider(coverage, "GrassCardBaseDensity", "Far base density (per m²)", .5, 6, .1);
        AddGrassSlider(coverage, "GrassCardFloorDensity", "Far floor density (per m²)", .02, 1, .01);
        AddGrassSlider(coverage, "GrassCardDensityFalloff", "Density falloff exponent", .5, 2.5, .05);
        AddGrassSlider(coverage, "GrassCardNearBoostStartMeters", "Near boost start (m)", 0, 100, 1);
        AddGrassSlider(coverage, "GrassCardNearBoostEndMeters", "Near boost end (m)", 1, 200, 1);
        AddGrassSlider(coverage, "GrassCardScaleCompensationMax", "Thinned card scale limit", 1, 2.5, .05);
        AddGrassSlider(coverage, "GrassCardFadeBandPercent", "Reach fade band (% of reach)", 1, 50, 1);
        AddGrassSlider(coverage, "GrassCardSingleCardStartMeters", "Single card start (m)", 20, 600, 1);
        AddGrassSlider(coverage, "GrassCardSingleCardEndMeters", "Single card end (m)", 30, 700, 1);

        var form = AddGrassGroup(tab, "Grass cards — form", "Cards only. Heights, lean and the atlas are placement settings (rebuilds); the rest is shading and shape.");
        AddGrassSlider(form, "GrassCardTallHeight", "Tall tuft height (m, rebuilds)", .2, 1.5, .01);
        AddGrassSlider(form, "GrassCardShortHeight", "Short tuft height (m, rebuilds)", .1, 1, .01);
        AddGrassSlider(form, "GrassCardLean", "Lean (rebuilds)", 0, .5, .01);
        AddGrassSlider(form, "GrassCardAtlasBlades", "Blades per atlas tuft (rebuilds)", 4, 96, 1);
        AddGrassSlider(form, "GrassCardNearWidthScale", "Near width scale", .3, 1, .01);
        AddGrassSlider(form, "GrassCardTilt", "Card tilt", 0, .4, .01);
        AddGrassSlider(form, "GrassCardNormalOut", "Normal outward", 0, 1, .01);
        AddGrassSlider(form, "GrassCardNormalFace", "Normal facing", 0, 1, .01);
        AddGrassSlider(form, "GrassCardBacklight", "Backlight", 0, 1, .01);
        AddGrassSlider(form, "GrassCardAlphaCutoff", "Alpha cutoff", .1, .9, .01);

        var colour = AddGrassGroup(tab, "Grass cards — colour", "Cards only. Cards take their colour from the ground; green pull 0 is the raw ground colour.");
        AddGrassSlider(colour, "GrassCardGreenPull", "Green pull (0 = raw ground colour)", 0, 1, .01);
        AddGrassSlider(colour, "GrassCardRootAo", "Root darkening", 0, 1, .01);
        AddGrassSlider(colour, "GrassCardTipLight", "Tip lightening", 0, .5, .01);
        AddGrassSlider(colour, "GrassCardBladeValueJitter", "Blade brightness variation", 0, .6, .01);
        AddGrassSlider(colour, "GrassCardBladeHueJitter", "Blade hue variation", 0, .3, .01);
        AddGrassSlider(colour, "GrassCardDryFraction", "Dry blade fraction", 0, .5, .01);
        AddGrassSlider(colour, "GrassCardTuftValueJitter", "Tuft brightness variation", 0, .5, .01);
        AddGrassSlider(colour, "GrassCardFragmentColorMeters", "Per-pixel colour distance (m)", 10, 400, 1);
        AddGrassSlider(colour, "GrassCardConvergeStartPercent", "Colour convergence start (% of reach)", 0, 99, 1);
        AddGrassSlider(colour, "GrassCardConvergeEndPercent", "Colour convergence end (% of reach)", 1, 100, 1);
    }
}
