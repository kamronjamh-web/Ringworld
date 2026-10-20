using Godot;
using RingworldTechDemo.Shadows;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

public partial class ShadowSettingsSmoke
{
    /// <summary>S6: contact shadow keys (validation, clamping, persistence, old-file load, effective getters, default off).</summary>
    private static (bool Pass, string Detail) RunContactShadowKeys(string path, SceneTree tree)
    {
        static bool Ok(string key, Variant raw, out Variant clean) => RingGraphicsQualityController.TryValidateShadowOverride(key, raw, out clean);
        bool valid = Ok("ContactShadowEnabled", true, out Variant on) && on.AsBool()
            && Ok("ContactShadowLengthMeters", 2.5f, out Variant length) && Mathf.IsEqualApprox(length.AsSingle(), 2.5f)
            && Ok("ContactShadowLengthMeters", 0.01f, out Variant lengthLow) && Mathf.IsEqualApprox(lengthLow.AsSingle(), .25f) && Ok("ContactShadowLengthMeters", 99f, out Variant lengthHigh) && Mathf.IsEqualApprox(lengthHigh.AsSingle(), 4f)
            && Ok("ContactShadowThicknessMeters", 0.5f, out Variant thickness) && Mathf.IsEqualApprox(thickness.AsSingle(), .5f) && Ok("ContactShadowThicknessMeters", 0f, out Variant thicknessLow) && Mathf.IsEqualApprox(thicknessLow.AsSingle(), .05f)
            && Ok("ContactShadowStrength", .5f, out Variant strength) && Mathf.IsEqualApprox(strength.AsSingle(), .5f) && Ok("ContactShadowStrength", 3f, out Variant strengthHigh) && Mathf.IsEqualApprox(strengthHigh.AsSingle(), 1f)
            && Ok("ContactShadowFadeMeters", 10f, out Variant fadeLow) && Mathf.IsEqualApprox(fadeLow.AsSingle(), 25f) && Ok("ContactShadowFadeMeters", 1000f, out Variant fadeHigh) && Mathf.IsEqualApprox(fadeHigh.AsSingle(), 400f)
            && Ok("ContactShadowGrassStrength", .2f, out Variant grass) && Mathf.IsEqualApprox(grass.AsSingle(), .2f) && Ok("ContactShadowGrassStrength", -1f, out Variant grassLow) && grassLow.AsSingle() == 0f
            && Ok("ContactShadowSteps", 8, out Variant steps8) && steps8.AsInt32() == 8 && Ok("ContactShadowSteps", 32.0f, out Variant steps32) && steps32.AsInt32() == 32 && Ok("ContactShadowSteps", 16, out _);
        bool rejected = !Ok("ContactShadowEnabled", 1, out _) && !Ok("ContactShadowEnabled", "true", out _) && !Ok("ContactShadowEnabled", .5f, out _)
            && !Ok("ContactShadowLengthMeters", float.NaN, out _) && !Ok("ContactShadowLengthMeters", "1", out _) && !Ok("ContactShadowThicknessMeters", float.PositiveInfinity, out _)
            && !Ok("ContactShadowStrength", true, out _) && !Ok("ContactShadowFadeMeters", float.NaN, out _) && !Ok("ContactShadowGrassStrength", "x", out _)
            && !Ok("ContactShadowSteps", 10, out _) && !Ok("ContactShadowSteps", 16.5f, out _) && !Ok("ContactShadowSteps", 0, out _) && !Ok("ContactShadowSteps", float.NaN, out _) && !Ok("ContactShadowSteps", "16", out _)
            && !Ok("ContactShadowUnknown", 1, out _);

        string contactPath = path + ".s6.tres";
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
        var settings = new GameplayShadowDefaults(contactPath);
        settings.Initialize(controller);
        bool defaults = controller.ResolveContactShadowSettings() == new RingContactShadowSettings() && !controller.EffectiveContactShadowEnabled
            && Mathf.IsEqualApprox(controller.EffectiveContactShadowLengthMeters, 1.5f) && Mathf.IsEqualApprox(controller.EffectiveContactShadowThicknessMeters, .25f) && Mathf.IsEqualApprox(controller.EffectiveContactShadowStrength, 1f)
            && Mathf.IsEqualApprox(controller.EffectiveContactShadowFadeMeters, 150f) && Mathf.IsEqualApprox(controller.EffectiveContactShadowGrassStrength, .35f) && controller.EffectiveContactShadowSteps == 16
            && controller.EffectiveContactShadowStatus.Contains("off");
        bool set = settings.SetOverride(controller, "ContactShadowEnabled", true) && settings.SetOverride(controller, "ContactShadowLengthMeters", 2f) && settings.SetOverride(controller, "ContactShadowThicknessMeters", .4f)
            && settings.SetOverride(controller, "ContactShadowStrength", .8f) && settings.SetOverride(controller, "ContactShadowFadeMeters", 200f) && settings.SetOverride(controller, "ContactShadowGrassStrength", .5f)
            && settings.SetOverride(controller, "ContactShadowSteps", 24) && !settings.SetOverride(controller, "ContactShadowSteps", 20) && !settings.SetOverride(controller, "ContactShadowEnabled", 1);
        RingContactShadowSettings applied = controller.ResolveContactShadowSettings();
        bool appliedOk = set && applied.Enabled && Mathf.IsEqualApprox(applied.LengthMeters, 2f) && Mathf.IsEqualApprox(applied.ThicknessMeters, .4f) && Mathf.IsEqualApprox(applied.Strength, .8f)
            && Mathf.IsEqualApprox(applied.FadeMeters, 200f) && Mathf.IsEqualApprox(applied.GrassStrength, .5f) && applied.Steps == 24 && controller.EffectiveContactShadowEnabled && controller.EffectiveContactShadowSteps == 24
            && controller.EffectiveContactShadowStatus.Contains("starting");
        bool saved = settings.Save();
        RingGraphicsQualityController reloaded = NewController();
        new GameplayShadowDefaults(contactPath).Initialize(reloaded);
        bool roundTrip = saved && reloaded.ResolveContactShadowSettings() == controller.ResolveContactShadowSettings() && reloaded.EffectiveContactShadowEnabled && reloaded.EffectiveContactShadowSteps == 24;

        // An old file (no contact keys) loads with the feature off and every other override intact; invalid contact values in a file are dropped.
        var oldFile = new GameplayShadowDefaults(contactPath);
        oldFile.Settings.Overrides["Split1"] = .18f;
        oldFile.Settings.Overrides["ContactShadowSteps"] = 10;
        oldFile.Settings.Overrides["ContactShadowEnabled"] = 1;
        bool oldSaved = ResourceSaver.Save(oldFile.Settings, contactPath) == Error.Ok;
        RingGraphicsQualityController old = NewController();
        var oldLoaded = new GameplayShadowDefaults(contactPath);
        oldLoaded.Initialize(old);
        bool oldFileOk = oldSaved && oldLoaded.Settings.Overrides.ContainsKey("Split1") && !oldLoaded.Settings.Overrides.ContainsKey("ContactShadowSteps") && !oldLoaded.Settings.Overrides.ContainsKey("ContactShadowEnabled")
            && old.ResolveContactShadowSettings() == new RingContactShadowSettings();
        settings.Reset(controller);
        bool reset = controller.ResolveContactShadowSettings() == new RingContactShadowSettings() && !controller.EffectiveContactShadowEnabled;
        foreach (RingGraphicsQualityController extra in new[] { controller, reloaded, old })
        {
            tree.Root.RemoveChild(extra);
            extra.Free();
        }
        try { System.IO.File.Delete(ProjectSettings.GlobalizePath(contactPath)); } catch (System.Exception) { }
        bool pass = valid && rejected && defaults && set && appliedOk && roundTrip && oldFileOk && reset;
        return (pass, $"contactShadow={pass}[{valid}/{rejected}/{defaults}/{set}/{appliedOk}/{roundTrip}/{oldFileOk}/{reset}]");
    }
}
