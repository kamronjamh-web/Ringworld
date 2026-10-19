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
        // Restore the persisted/default profile for callers that run this smoke
        // in-process before constructing a second world.
        RingAtmosphereModel.Enabled = true;
        reload.Reset();
        bool contracts = naturalContract && cinematicContract && zeroDensityContract && disabledContract && hazeToggle;
        return new Godot.Collections.Dictionary { ["passed"] = finite && saved && persisted && reset && contracts, ["haze_toggle"] = hazeToggle, ["haze_physical"] = hazePhysical, ["haze_persisted"] = hazePersisted, ["old_file_loads"] = oldFileLoads, ["finite"] = finite, ["saved"] = saved, ["persisted"] = persisted, ["reset"] = reset, ["natural_contract"] = naturalContract, ["cinematic_contract"] = cinematicContract, ["zero_density_contract"] = zeroDensityContract, ["disabled_contract"] = disabledContract };
    }
}
