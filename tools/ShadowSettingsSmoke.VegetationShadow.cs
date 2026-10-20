using Godot;
using RingworldTechDemo.Shadows;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

public partial class ShadowSettingsSmoke
{
    /// <summary>S7b: vegetation shadow keys (validation, clamping, persistence, old-file load, default off, static host applied).</summary>
    private static (bool Pass, string Detail) RunVegetationShadowKeys(string path, SceneTree tree)
    {
        static bool Ok(string key, Variant raw, out Variant clean) => RingGraphicsQualityController.TryValidateShadowOverride(key, raw, out clean);
        bool valid = Ok("VegetationShadowInstanceEnabled", true, out Variant on) && on.AsBool() && Ok("VegetationUnifiedShadowCompose", false, out Variant off) && !off.AsBool()
            && Ok("VegetationShadowRefreshHz", 5, out Variant hz) && hz.AsInt32() == 5 && Ok("VegetationShadowRefreshHz", 1, out Variant low) && low.AsInt32() == 2 && Ok("VegetationShadowRefreshHz", 99.0f, out Variant high) && high.AsInt32() == 30;
        bool rejected = !Ok("VegetationShadowInstanceEnabled", 1, out _) && !Ok("VegetationShadowInstanceEnabled", "true", out _) && !Ok("VegetationUnifiedShadowCompose", .5f, out _)
            && !Ok("VegetationShadowRefreshHz", float.NaN, out _) && !Ok("VegetationShadowRefreshHz", "10", out _) && !Ok("VegetationShadowRefreshHz", true, out _);

        string vegetationPath = path + ".s7b.tres";
        RingGraphicsQualityController NewController()
        {
            var controller = new RingGraphicsQualityController();
            var sun = new DirectionalLight3D { Name = "Sun", ShadowEnabled = true };
            controller.AddChild(sun);
            controller.SunLightPath = new NodePath("Sun");
            tree.Root.AddChild(controller);
            controller.ApplyPreset(RingTerrainLodCoordinator.QualityPreset.VeryHigh, applyTerrainPreset: false);
            return controller;
        }
        RingGraphicsQualityController controller = NewController();
        var settings = new GameplayShadowDefaults(vegetationPath);
        settings.Initialize(controller);
        bool defaults = !controller.EffectiveVegetationShadowInstanceEnabled && !controller.EffectiveVegetationUnifiedShadowCompose && controller.EffectiveVegetationShadowRefreshHz == 10
            && !RingVegetationShadow.Enabled && !RingVegetationShadow.UnifiedCompose && RingVegetationShadow.RefreshHz == 10 && controller.EffectiveVegetationShadowStatus.Contains("off");
        bool set = settings.SetOverride(controller, "VegetationShadowInstanceEnabled", true) && settings.SetOverride(controller, "VegetationUnifiedShadowCompose", true)
            && settings.SetOverride(controller, "VegetationShadowRefreshHz", 20) && !settings.SetOverride(controller, "VegetationShadowInstanceEnabled", 1);
        bool applied = set && controller.EffectiveVegetationShadowInstanceEnabled && controller.EffectiveVegetationUnifiedShadowCompose && controller.EffectiveVegetationShadowRefreshHz == 20
            && RingVegetationShadow.Enabled && RingVegetationShadow.UnifiedCompose && RingVegetationShadow.RefreshHz == 20 && !controller.EffectiveVegetationShadowStatus.Contains("off");
        bool saved = settings.Save();
        RingGraphicsQualityController reloaded = NewController();
        new GameplayShadowDefaults(vegetationPath).Initialize(reloaded);
        bool roundTrip = saved && reloaded.EffectiveVegetationShadowInstanceEnabled && reloaded.EffectiveVegetationUnifiedShadowCompose && reloaded.EffectiveVegetationShadowRefreshHz == 20;

        var oldFile = new GameplayShadowDefaults(vegetationPath);
        oldFile.Settings.Overrides["Split1"] = .18f;
        oldFile.Settings.Overrides["VegetationShadowRefreshHz"] = "fast";
        bool oldSaved = ResourceSaver.Save(oldFile.Settings, vegetationPath) == Error.Ok;
        RingGraphicsQualityController old = NewController();
        var oldLoaded = new GameplayShadowDefaults(vegetationPath);
        oldLoaded.Initialize(old);
        bool oldFileOk = oldSaved && oldLoaded.Settings.Overrides.ContainsKey("Split1") && !oldLoaded.Settings.Overrides.ContainsKey("VegetationShadowRefreshHz")
            && !old.EffectiveVegetationShadowInstanceEnabled && !RingVegetationShadow.Enabled;
        settings.Reset(controller);
        bool reset = !controller.EffectiveVegetationShadowInstanceEnabled && !RingVegetationShadow.Enabled && !RingVegetationShadow.UnifiedCompose && RingVegetationShadow.RefreshHz == 10;
        foreach (RingGraphicsQualityController extra in new[] { controller, reloaded, old })
        {
            tree.Root.RemoveChild(extra);
            extra.Free();
        }
        try { System.IO.File.Delete(ProjectSettings.GlobalizePath(vegetationPath)); } catch (System.Exception) { }
        bool pass = valid && rejected && defaults && set && applied && roundTrip && oldFileOk && reset;
        return (pass, $"vegetationShadow={pass}[{valid}/{rejected}/{defaults}/{set}/{applied}/{roundTrip}/{oldFileOk}/{reset}]");
    }
}
