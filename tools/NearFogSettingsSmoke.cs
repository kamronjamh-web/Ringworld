using Godot;
using RingworldTechDemo.Atmosphere.NearFog;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

/// <summary>
/// NF-6: the "Ground fog" settings (NearFogAmount, NearFogLayerHeight) in AtmosphereVisualDefaults, and the preset gate RingNearFog.AllowsFog.
/// The drawer rows are checked in atmosphere_menu_smoke_test. Run: Godot --headless --path . --script res://tools/near_fog_settings_smoke_test.gd. Prints NEAR_FOG_SETTINGS_SMOKE=PASS.
/// </summary>
public partial class NearFogSettingsSmoke : RefCounted
{
    public Godot.Collections.Dictionary Run(string isolatedPath)
    {
        var failures = new Godot.Collections.Array<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        bool Near(float a, float b) => Mathf.IsEqualApprox(a, b);

        var defaults = new AtmosphereVisualDefaults(isolatedPath);
        defaults.Initialize();
        Check(Near(defaults.Settings.NearFogAmount, 1f) && Near(defaults.Settings.NearFogLayerHeight, 40f), "defaults are not 1 and 40");
        Check(Near(RingNearFog.Amount, 1f) && Near(RingNearFog.LayerHeight, 40f), "Apply did not push the defaults");

        // Apply reaches the owner's statics.
        defaults.Set("NearFogAmount", 0.5f);
        defaults.Set("NearFogLayerHeight", 90f);
        Check(Near(RingNearFog.Amount, 0.5f) && Near(RingNearFog.LayerHeight, 90f), "Apply did not reach RingNearFog.Amount / LayerHeight");

        // Ranges: 0..2 and 10..150; NaN falls back to the default.
        defaults.Set("NearFogAmount", 5f); Check(Near(defaults.Settings.NearFogAmount, 2f), "amount above range not clamped to 2");
        defaults.Set("NearFogAmount", -3f); Check(Near(defaults.Settings.NearFogAmount, 0f), "amount below range not clamped to 0");
        defaults.Set("NearFogAmount", float.NaN); Check(Near(defaults.Settings.NearFogAmount, 1f), "amount NaN not the default");
        defaults.Set("NearFogLayerHeight", 1000f); Check(Near(defaults.Settings.NearFogLayerHeight, 150f), "height above range not clamped to 150");
        defaults.Set("NearFogLayerHeight", 2f); Check(Near(defaults.Settings.NearFogLayerHeight, 10f), "height below range not clamped to 10");
        defaults.Set("NearFogLayerHeight", float.PositiveInfinity); Check(Near(defaults.Settings.NearFogLayerHeight, 40f), "height Inf not the default");

        // Save and reload (atomic save to the isolated path).
        defaults.Set("NearFogAmount", 1.5f);
        defaults.Set("NearFogLayerHeight", 75f);
        Check(defaults.Save(), "save failed: " + defaults.LastError);
        RingNearFog.Amount = 0f; RingNearFog.LayerHeight = 0f;
        var reload = new AtmosphereVisualDefaults(isolatedPath);
        reload.Initialize();
        Check(Near(reload.Settings.NearFogAmount, 1.5f) && Near(reload.Settings.NearFogLayerHeight, 75f), "round trip lost the values");
        Check(Near(RingNearFog.Amount, 1.5f) && Near(RingNearFog.LayerHeight, 75f), "Initialize did not apply the loaded values");

        // An older file without the keys loads the defaults.
        string absolute = ProjectSettings.GlobalizePath(isolatedPath);
        string text = System.IO.File.ReadAllText(absolute);
        string stripped = string.Join('\n', System.Array.FindAll(text.Split('\n'), line => !line.StartsWith("NearFog")));
        Check(text.Contains("NearFogAmount") && text.Contains("NearFogLayerHeight"), "saved file lacks the NearFog keys");
        System.IO.File.WriteAllText(absolute, stripped);
        var old = new AtmosphereVisualDefaults(isolatedPath);
        old.Initialize();
        Check(Near(old.Settings.NearFogAmount, 1f) && Near(old.Settings.NearFogLayerHeight, 40f), "older file did not load the defaults");

        // Reset.
        old.Set("NearFogAmount", 0.2f);
        old.Reset();
        Check(Near(old.Settings.NearFogAmount, 1f) && Near(old.Settings.NearFogLayerHeight, 40f) && Near(RingNearFog.Amount, 1f), "reset did not restore the defaults");

        // Preset gate.
        foreach (RingTerrainLodCoordinator.QualityPreset preset in System.Enum.GetValues<RingTerrainLodCoordinator.QualityPreset>())
        {
            bool expected = preset != RingTerrainLodCoordinator.QualityPreset.Low && preset != RingTerrainLodCoordinator.QualityPreset.Medium;
            Check(RingNearFog.AllowsFog(preset) == expected, "AllowsFog wrong for " + preset);
        }
        Check(!RingNearFog.AllowsFog(RingTerrainLodCoordinator.QualityPreset.Low) && !RingNearFog.AllowsFog(RingTerrainLodCoordinator.QualityPreset.Medium), "Low or Medium allows fog");
        Check(RingNearFog.PresetAllowsFog, "gate closed with no graphics controller (fixtures must behave as before)");
        RingNearFog.PresetOverride = false;
        Check(!RingNearFog.PresetAllowsFog, "PresetOverride false not honoured");
        RingNearFog.PresetOverride = null;
        Check(RingNearFog.FogLengthMeters == 220f && RingNearFog.FroxelVolumeSize == 96 && RingNearFog.FroxelVolumeDepth == 80, "fog length or froxel grid constants changed");

        RingNearFog.Amount = 1f; RingNearFog.LayerHeight = 40f;
        var result = new Godot.Collections.Dictionary { ["passed"] = failures.Count == 0, ["failures"] = string.Join(" | ", failures) };
        return result;
    }
}
