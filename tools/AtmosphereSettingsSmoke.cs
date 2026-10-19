using System;
using Godot;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

public partial class AtmosphereSettingsSmoke : RefCounted
{
    public Godot.Collections.Dictionary Run(string isolatedPath)
    {
        var defaults = new AtmosphereVisualDefaults(isolatedPath);
        defaults.Initialize();
        defaults.SetPreset(1);
        defaults.Set("Density", float.NaN);
        defaults.Set("Visibility", float.PositiveInfinity);
        bool finite = float.IsFinite(RingAtmosphereModel.Density)
            && float.IsFinite(RingAtmosphereModel.VisibilityFullKm)
            && RingAtmosphereModel.Density >= 0.0f && RingAtmosphereModel.Density <= 2.0f
            && RingAtmosphereModel.VisibilityFullKm >= 10.0f && RingAtmosphereModel.VisibilityFullKm <= 400.0f;
        bool saved = defaults.Save();
        var reload = new AtmosphereVisualDefaults(isolatedPath);
        reload.Initialize();
        bool persisted = reload.Settings.Preset == 1 && float.IsFinite(reload.Settings.Density);
        reload.Reset();
        bool reset = reload.Settings.Preset == 0 && Mathf.IsEqualApprox(reload.Settings.VisibilityFullKm, 120.0f);
        bool ContractAtBothRadii() => RingAtmosphereModel.RunContractSelfTest(1_000.0, out _)
            && RingAtmosphereModel.RunContractSelfTest(CertifiedRingScale.EffectiveRadiusMeters, out _)
            && RingAtmosphereModel.RunContractSelfTest(MegaRingScale.EffectiveRadiusMeters, out _);
        bool naturalContract = ContractAtBothRadii();
        defaults.SetPreset(1);
        bool cinematicContract = ContractAtBothRadii();
        defaults.Set("Density", 0.0f);
        bool zeroDensityContract = ContractAtBothRadii();
        RingAtmosphereModel.Enabled = false;
        bool disabledContract = ContractAtBothRadii();
        // Physical haze A/B toggle: applied to the model, contract holds, persisted, and a file written before the toggle existed
        // (no HazeModel line) loads as Legacy with every other value intact.
        RingAtmosphereModel.Enabled = true;
        defaults.Reset();
        defaults.SetPreset(1);
        defaults.SetHazeModel(1);
        bool hazePhysical = RingAtmosphereModel.HazeModel == RingAtmosphereModel.HazeModelKind.Physical && ContractAtBothRadii();
        bool hazeSaved = defaults.Save();
        var hazeReload = new AtmosphereVisualDefaults(isolatedPath);
        hazeReload.Initialize();
        bool hazePersisted = hazeReload.Settings.HazeModel == 1 && RingAtmosphereModel.HazeModel == RingAtmosphereModel.HazeModelKind.Physical;
        string absolute = ProjectSettings.GlobalizePath(isolatedPath);
        string text = System.IO.File.ReadAllText(absolute);
        string stripped = string.Join('\n', System.Array.FindAll(text.Split('\n'), line => !line.StartsWith("HazeModel")));
        bool hazeLineWritten = stripped.Length < text.Length;
        System.IO.File.WriteAllText(absolute, stripped);
        var legacyFile = new AtmosphereVisualDefaults(isolatedPath);
        legacyFile.Initialize();
        bool oldFileLoads = hazeLineWritten && legacyFile.Settings.HazeModel == 0 && legacyFile.Settings.Preset == 1
            && RingAtmosphereModel.HazeModel == RingAtmosphereModel.HazeModelKind.Legacy;
        bool hazeToggle = hazePhysical && hazeSaved && hazePersisted && oldFileLoads;
        // Haze brightness: applied to the model, persisted, and a file written before the control existed loads as 1.
        defaults.Set("HazeBrightness", 0.4f);
        bool brightApplied = Mathf.IsEqualApprox(RingAtmosphereModel.HazeBrightness, 0.4f);
        bool brightSaved = defaults.Save();
        var brightReload = new AtmosphereVisualDefaults(isolatedPath);
        brightReload.Initialize();
        bool brightPersisted = Mathf.IsEqualApprox(brightReload.Settings.HazeBrightness, 0.4f) && Mathf.IsEqualApprox(RingAtmosphereModel.HazeBrightness, 0.4f);
        string brightText = System.IO.File.ReadAllText(absolute);
        string brightStripped = string.Join('\n', System.Array.FindAll(brightText.Split('\n'), line => !line.StartsWith("HazeBrightness")));
        bool brightLineWritten = brightStripped.Length < brightText.Length;
        System.IO.File.WriteAllText(absolute, brightStripped);
        var brightOld = new AtmosphereVisualDefaults(isolatedPath);
        brightOld.Initialize();
        bool brightOldFile = brightLineWritten && Mathf.IsEqualApprox(brightOld.Settings.HazeBrightness, 1.0f) && Mathf.IsEqualApprox(RingAtmosphereModel.HazeBrightness, 1.0f);
        brightOld.Set("HazeBrightness", float.NaN);
        bool brightSane = Mathf.IsEqualApprox(brightOld.Settings.HazeBrightness, 1.0f);
        brightOld.Set("HazeBrightness", 99.0f);
        brightSane = brightSane && Mathf.IsEqualApprox(brightOld.Settings.HazeBrightness, 2.0f);
        bool hazeBrightness = brightApplied && brightSaved && brightPersisted && brightOldFile && brightSane;
        // P4 physical controls: defaults are the identity, applied to the model, persisted, clamped, old files load defaults, Reset restores.
        var p4 = new AtmosphereVisualDefaults(isolatedPath);
        p4.Reset();
        float nominalSigmaMist = RingAtmosphereModel.HazeSigmaMist, nominalSigmaR = RingAtmosphereModel.HazeSigmaRayleigh.X;
        float nominalHMist = RingAtmosphereModel.HazeMistHeightMeters, nominalHA = RingAtmosphereModel.HazeAerosolHeightMeters;
        bool p4Defaults = p4.Settings.MistDensity == 1.0f && p4.Settings.MistHeight == 400.0f && p4.Settings.AerosolHeight == 1500.0f
            && p4.Settings.RayleighStrength == 1.0f && Mathf.IsEqualApprox(p4.Settings.GroundAlbedo, 0.1f) && p4.Settings.MultiScatter == 1.0f
            && p4.Settings.SkyTintR == 1.0f && p4.Settings.SkyTintG == 1.0f && p4.Settings.SkyTintB == 1.0f && p4.Settings.HazeQuality == 8
            && RingAtmosphereModel.HazeQuality == 8 && Mathf.IsEqualApprox(nominalHMist, 400.0f) && Mathf.IsEqualApprox(nominalHA, 1500.0f)
            && RingAtmosphereModel.HazeSkyTint == Vector3.One && Mathf.IsEqualApprox(RingAtmosphereModel.HazeGroundAlbedo, 0.1f);
        ulong revisionBefore = RingAtmosphereModel.Revision;
        p4.Set("RayleighStrength", 1.5f); p4.Set("MistDensity", 2.0f); p4.Set("MistHeight", 800.0f); p4.Set("AerosolHeight", 3000.0f);
        p4.Set("GroundAlbedo", 0.3f); p4.Set("MultiScatter", 2.0f); p4.Set("SkyTintB", 0.5f); p4.SetHazeQuality(16);
        bool p4Applied = RingAtmosphereModel.Revision > revisionBefore
            && Mathf.IsEqualApprox(RingAtmosphereModel.HazeSigmaRayleigh.X, nominalSigmaR * 1.5f)
            && Mathf.IsEqualApprox(RingAtmosphereModel.HazeSigmaMist, nominalSigmaMist * 2.0f)
            && Mathf.IsEqualApprox(RingAtmosphereModel.HazeMistHeightMeters, 800.0f) && Mathf.IsEqualApprox(RingAtmosphereModel.HazeAerosolHeightMeters, 3000.0f)
            && Mathf.IsEqualApprox(RingAtmosphereModel.HazeGroundAlbedo, 0.3f) && Mathf.IsEqualApprox(RingAtmosphereModel.HazeMultiscatterGain, 2.0f)
            && Mathf.IsEqualApprox(RingAtmosphereModel.HazeSkyTint.Z, 0.5f) && RingAtmosphereModel.HazeQuality == 16;
        // The altitude-falloff slider still composes: falloff 1 halves both heights on top of the controls.
        p4.Set("Altitude", 1.0f);
        bool p4Composes = Mathf.IsEqualApprox(RingAtmosphereModel.HazeMistHeightMeters, 400.0f) && Mathf.IsEqualApprox(RingAtmosphereModel.HazeAerosolHeightMeters, 1500.0f);
        p4.Set("Altitude", 0.5f);
        bool p4Saved = p4.Save();
        var p4Reload = new AtmosphereVisualDefaults(isolatedPath);
        p4Reload.Initialize();
        bool p4Persisted = p4Saved && Mathf.IsEqualApprox(p4Reload.Settings.RayleighStrength, 1.5f) && Mathf.IsEqualApprox(p4Reload.Settings.MistHeight, 800.0f)
            && Mathf.IsEqualApprox(p4Reload.Settings.SkyTintB, 0.5f) && p4Reload.Settings.HazeQuality == 16 && RingAtmosphereModel.HazeQuality == 16
            && Mathf.IsEqualApprox(RingAtmosphereModel.HazeGroundAlbedo, 0.3f);
        p4Reload.Set("RayleighStrength", float.NaN); p4Reload.Set("MistDensity", 99.0f); p4Reload.Set("MistHeight", -5.0f); p4Reload.Set("AerosolHeight", 1.0e9f);
        p4Reload.Set("GroundAlbedo", 7.0f); p4Reload.Set("MultiScatter", float.PositiveInfinity); p4Reload.Set("SkyTintR", -2.0f); p4Reload.SetHazeQuality(11);
        bool p4Clamped = p4Reload.Settings.RayleighStrength == 1.0f && p4Reload.Settings.MistDensity == 5.0f && p4Reload.Settings.MistHeight == 50.0f
            && p4Reload.Settings.AerosolHeight == 5000.0f && p4Reload.Settings.GroundAlbedo == 0.5f && p4Reload.Settings.MultiScatter == 1.0f
            && p4Reload.Settings.SkyTintR == 0.0f && p4Reload.Settings.HazeQuality == 8 && RingAtmosphereModel.HazeQuality == 8;
        p4Reload.SetHazeQuality(100);
        p4Clamped = p4Clamped && p4Reload.Settings.HazeQuality == 16;
        p4Reload.Save();
        string p4Path = ProjectSettings.GlobalizePath(isolatedPath);
        string[] p4Keys = { "MistDensity", "MistHeight", "AerosolHeight", "RayleighStrength", "GroundAlbedo", "MultiScatter", "SkyTintR", "SkyTintG", "SkyTintB", "HazeQuality" };
        string p4Text = System.IO.File.ReadAllText(p4Path);
        string p4Stripped = string.Join('\n', System.Array.FindAll(p4Text.Split('\n'), line => System.Array.TrueForAll(p4Keys, key => !line.StartsWith(key))));
        bool p4LinesWritten = p4Stripped.Length < p4Text.Length;
        System.IO.File.WriteAllText(p4Path, p4Stripped);
        var p4Old = new AtmosphereVisualDefaults(isolatedPath);
        p4Old.Initialize();
        bool p4OldFile = p4LinesWritten && p4Old.Settings.MistDensity == 1.0f && p4Old.Settings.MistHeight == 400.0f && p4Old.Settings.AerosolHeight == 1500.0f
            && p4Old.Settings.RayleighStrength == 1.0f && Mathf.IsEqualApprox(p4Old.Settings.GroundAlbedo, 0.1f) && p4Old.Settings.HazeQuality == 8
            && RingAtmosphereModel.HazeQuality == 8 && Mathf.IsEqualApprox(RingAtmosphereModel.HazeMistHeightMeters, 400.0f)
            && Mathf.IsEqualApprox(RingAtmosphereModel.HazeSigmaRayleigh.X, nominalSigmaR);
        p4Old.Set("RayleighStrength", 2.0f); p4Old.SetHazeQuality(4);
        p4Old.Reset();
        bool p4Reset = p4Old.Settings.RayleighStrength == 1.0f && p4Old.Settings.HazeQuality == 8 && RingAtmosphereModel.HazeQuality == 8
            && Mathf.IsEqualApprox(RingAtmosphereModel.HazeSigmaRayleigh.X, nominalSigmaR);
        bool p4Contract = ContractAtBothRadii();
        // P9 cloud shadowing: default 1, applied to the model, persisted, clamped to 0-1, old files load 1, Reset restores.
        var cs = new AtmosphereVisualDefaults(isolatedPath);
        cs.Reset();
        bool csDefault = cs.Settings.CloudShadowing == 1.0f && RingAtmosphereModel.HazeCloudShadowing == 1.0f;
        cs.Set("CloudShadowing", 0.25f);
        bool csApplied = Mathf.IsEqualApprox(RingAtmosphereModel.HazeCloudShadowing, 0.25f);
        bool csSaved = cs.Save();
        var csReload = new AtmosphereVisualDefaults(isolatedPath);
        csReload.Initialize();
        bool csPersisted = csSaved && Mathf.IsEqualApprox(csReload.Settings.CloudShadowing, 0.25f) && Mathf.IsEqualApprox(RingAtmosphereModel.HazeCloudShadowing, 0.25f);
        csReload.Set("CloudShadowing", 9.0f);
        bool csClamped = csReload.Settings.CloudShadowing == 1.0f;
        csReload.Set("CloudShadowing", float.NaN);
        csClamped = csClamped && csReload.Settings.CloudShadowing == 1.0f;
        csReload.Set("CloudShadowing", 0.5f);
        csReload.Save();
        string csPath = ProjectSettings.GlobalizePath(isolatedPath);
        string csText = System.IO.File.ReadAllText(csPath);
        string csStripped = string.Join('\n', System.Array.FindAll(csText.Split('\n'), line => !line.StartsWith("CloudShadowing")));
        bool csLine = csStripped.Length < csText.Length;
        System.IO.File.WriteAllText(csPath, csStripped);
        var csOld = new AtmosphereVisualDefaults(isolatedPath);
        csOld.Initialize();
        bool csOldFile = csLine && csOld.Settings.CloudShadowing == 1.0f && RingAtmosphereModel.HazeCloudShadowing == 1.0f;
        csOld.Set("CloudShadowing", 0.0f);
        csOld.Reset();
        bool csReset = csOld.Settings.CloudShadowing == 1.0f && RingAtmosphereModel.HazeCloudShadowing == 1.0f;
        bool cloudShadowing = csDefault && csApplied && csPersisted && csClamped && csOldFile && csReset;
        // P9 storm haze strength: default 1, applied to the model, persisted, clamped to 0-1, old files load 1, Reset restores.
        var sh = new AtmosphereVisualDefaults(isolatedPath);
        sh.Reset();
        bool shDefault = sh.Settings.StormHaze == 1.0f && RingAtmosphereModel.HazeStormStrength == 1.0f;
        sh.Set("StormHaze", 0.25f);
        bool shApplied = Mathf.IsEqualApprox(RingAtmosphereModel.HazeStormStrength, 0.25f);
        bool shSaved = sh.Save();
        var shReload = new AtmosphereVisualDefaults(isolatedPath);
        shReload.Initialize();
        bool shPersisted = shSaved && Mathf.IsEqualApprox(shReload.Settings.StormHaze, 0.25f) && Mathf.IsEqualApprox(RingAtmosphereModel.HazeStormStrength, 0.25f);
        shReload.Set("StormHaze", 9.0f);
        bool shClamped = shReload.Settings.StormHaze == 1.0f;
        shReload.Set("StormHaze", float.NaN);
        shClamped = shClamped && shReload.Settings.StormHaze == 1.0f;
        shReload.Set("StormHaze", 0.5f);
        shReload.Save();
        string shPath = ProjectSettings.GlobalizePath(isolatedPath);
        string shText = System.IO.File.ReadAllText(shPath);
        string shStripped = string.Join('\n', System.Array.FindAll(shText.Split('\n'), line => !line.StartsWith("StormHaze")));
        bool shLine = shStripped.Length < shText.Length;
        System.IO.File.WriteAllText(shPath, shStripped);
        var shOld = new AtmosphereVisualDefaults(isolatedPath);
        shOld.Initialize();
        bool shOldFile = shLine && shOld.Settings.StormHaze == 1.0f && RingAtmosphereModel.HazeStormStrength == 1.0f;
        shOld.Set("StormHaze", 0.0f);
        shOld.Reset();
        bool shReset = shOld.Settings.StormHaze == 1.0f && RingAtmosphereModel.HazeStormStrength == 1.0f;
        bool stormHaze = shDefault && shApplied && shPersisted && shClamped && shOldFile && shReset;
        // P6 enclosed cloud fog gain: default 1, applied to the model, persisted, clamped, old files load 1.
        var ef = new AtmosphereVisualDefaults(isolatedPath);
        ef.Reset();
        bool efDefault = ef.Settings.EnclosedCloudFog == 1.0f && RingAtmosphereModel.EnclosedCloudFog == 1.0f;
        ef.Set("EnclosedCloudFog", 0.3f);
        bool efApplied = Mathf.IsEqualApprox(RingAtmosphereModel.EnclosedCloudFog, 0.3f);
        bool efSaved = ef.Save();
        var efReload = new AtmosphereVisualDefaults(isolatedPath);
        efReload.Initialize();
        bool efPersisted = efSaved && Mathf.IsEqualApprox(efReload.Settings.EnclosedCloudFog, 0.3f) && Mathf.IsEqualApprox(RingAtmosphereModel.EnclosedCloudFog, 0.3f);
        efReload.Set("EnclosedCloudFog", 9.0f);
        bool efClamped = efReload.Settings.EnclosedCloudFog == 1.0f;
        efReload.Set("EnclosedCloudFog", 0.5f);
        efReload.Save();
        string efPath = ProjectSettings.GlobalizePath(isolatedPath);
        string efText = System.IO.File.ReadAllText(efPath);
        string efStripped = string.Join('\n', System.Array.FindAll(efText.Split('\n'), line => !line.StartsWith("EnclosedCloudFog")));
        bool efLine = efStripped.Length < efText.Length;
        System.IO.File.WriteAllText(efPath, efStripped);
        var efOld = new AtmosphereVisualDefaults(isolatedPath);
        efOld.Initialize();
        bool efOldFile = efLine && efOld.Settings.EnclosedCloudFog == 1.0f && RingAtmosphereModel.EnclosedCloudFog == 1.0f;
        efOld.Set("EnclosedCloudFog", 0.0f);
        efOld.Reset();
        bool enclosedCloudFog = efDefault && efApplied && efPersisted && efClamped && efOldFile && efOld.Settings.EnclosedCloudFog == 1.0f && RingAtmosphereModel.EnclosedCloudFog == 1.0f;
        // P7/P8 sky haze LUT toggle: default on, applied to the host, persisted when off, old files load on.
        var sl = new AtmosphereVisualDefaults(isolatedPath);
        sl.Reset();
        bool slDefault = sl.Settings.SkyHazeLut && RingworldTechDemo.Atmosphere.RingAtmosphereLuts.SkyViewEnabled;
        sl.SetSkyHazeLut(false);
        bool slApplied = !RingworldTechDemo.Atmosphere.RingAtmosphereLuts.SkyViewEnabled;
        bool slSaved = sl.Save();
        var slReload = new AtmosphereVisualDefaults(isolatedPath);
        slReload.Initialize();
        bool slPersisted = slSaved && !slReload.Settings.SkyHazeLut && !RingworldTechDemo.Atmosphere.RingAtmosphereLuts.SkyViewEnabled;
        string slPath = ProjectSettings.GlobalizePath(isolatedPath);
        string slText = System.IO.File.ReadAllText(slPath);
        string slStripped = string.Join('\n', System.Array.FindAll(slText.Split('\n'), line => !line.StartsWith("SkyHazeLut")));
        bool slLine = slStripped.Length < slText.Length;
        System.IO.File.WriteAllText(slPath, slStripped);
        var slOld = new AtmosphereVisualDefaults(isolatedPath);
        slOld.Initialize();
        bool slOldFile = slLine && slOld.Settings.SkyHazeLut && RingworldTechDemo.Atmosphere.RingAtmosphereLuts.SkyViewEnabled;
        slOld.Reset();
        bool skyHazeLut = slDefault && slApplied && slPersisted && slOldFile;
        // Per-instance vegetation haze toggle: default on, applied to RingVegetationHaze, persisted when off, old files load on.
        var vh = new AtmosphereVisualDefaults(isolatedPath);
        vh.Reset();
        bool vhDefault = vh.Settings.VegetationInstanceHaze && RingworldTechDemo.Atmosphere.RingVegetationHaze.Enabled;
        vh.SetVegetationInstanceHaze(false);
        bool vhApplied = !RingworldTechDemo.Atmosphere.RingVegetationHaze.Enabled;
        bool vhSaved = vh.Save();
        var vhReload = new AtmosphereVisualDefaults(isolatedPath);
        vhReload.Initialize();
        bool vhPersisted = vhSaved && !vhReload.Settings.VegetationInstanceHaze && !RingworldTechDemo.Atmosphere.RingVegetationHaze.Enabled;
        string vhPath = ProjectSettings.GlobalizePath(isolatedPath);
        string vhText = System.IO.File.ReadAllText(vhPath);
        string vhStripped = string.Join('\n', System.Array.FindAll(vhText.Split('\n'), line => !line.StartsWith("VegetationInstanceHaze")));
        bool vhLine = vhStripped.Length < vhText.Length;
        System.IO.File.WriteAllText(vhPath, vhStripped);
        var vhOld = new AtmosphereVisualDefaults(isolatedPath);
        vhOld.Initialize();
        bool vhOldFile = vhLine && vhOld.Settings.VegetationInstanceHaze && RingworldTechDemo.Atmosphere.RingVegetationHaze.Enabled;
        vhOld.Reset();
        bool vegetationInstanceHaze = vhDefault && vhApplied && vhPersisted && vhOldFile;
        bool physicalControls = p4Defaults && p4Applied && p4Composes && p4Persisted && p4Clamped && p4OldFile && p4Reset && p4Contract && cloudShadowing && stormHaze && enclosedCloudFog && skyHazeLut && vegetationInstanceHaze;
        // Restore the persisted/default profile for callers that run this smoke
        // in-process before constructing a second world.
        RingAtmosphereModel.Enabled = true;
        reload.Reset();
        bool contracts = naturalContract && cinematicContract && zeroDensityContract && disabledContract && hazeToggle && hazeBrightness && physicalControls;
        return new Godot.Collections.Dictionary { ["passed"] = finite && saved && persisted && reset && contracts, ["haze_toggle"] = hazeToggle, ["haze_brightness"] = hazeBrightness, ["physical_controls"] = physicalControls, ["p4_detail"] = $"defaults={p4Defaults} applied={p4Applied} composes={p4Composes} persisted={p4Persisted} clamped={p4Clamped} oldFile={p4OldFile} reset={p4Reset} contract={p4Contract} cloudShadowing={cloudShadowing} stormHaze={stormHaze} enclosedCloudFog={enclosedCloudFog} skyHazeLut={skyHazeLut} vegetationInstanceHaze={vegetationInstanceHaze}", ["haze_physical"] = hazePhysical, ["haze_persisted"] = hazePersisted, ["old_file_loads"] = oldFileLoads, ["finite"] = finite, ["saved"] = saved, ["persisted"] = persisted, ["reset"] = reset, ["natural_contract"] = naturalContract, ["cinematic_contract"] = cinematicContract, ["zero_density_contract"] = zeroDensityContract, ["disabled_contract"] = disabledContract };
    }
}
