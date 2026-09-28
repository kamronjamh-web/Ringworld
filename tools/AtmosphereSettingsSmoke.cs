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
        // Restore the persisted/default profile for callers that run this smoke
        // in-process before constructing a second world.
        RingAtmosphereModel.Enabled = true;
        reload.Reset();
        bool contracts = naturalContract && cinematicContract && zeroDensityContract && disabledContract;
        return new Godot.Collections.Dictionary { ["passed"] = finite && saved && persisted && reset && contracts, ["finite"] = finite, ["saved"] = saved, ["persisted"] = persisted, ["reset"] = reset, ["natural_contract"] = naturalContract, ["cinematic_contract"] = cinematicContract, ["zero_density_contract"] = zeroDensityContract, ["disabled_contract"] = disabledContract };
    }
}
