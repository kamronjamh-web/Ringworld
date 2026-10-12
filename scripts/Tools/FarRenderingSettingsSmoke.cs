using System;
using System.IO;
using System.Reflection;
using Godot;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

/// <summary>Validates persistence, sanitization and preset-independent FAR application without terrain generation.</summary>
[GlobalClass]
public partial class FarRenderingSettingsSmoke : RefCounted
{
    public Godot.Collections.Dictionary Run(string isolatedPath)
    {
        var result = new Godot.Collections.Dictionary();
        RingScaledFarViewport? far = null;
        RingGraphicsQualityController? graphics = null;
        try
        {
            string absolute = ProjectSettings.GlobalizePath(isolatedPath);
            if (File.Exists(absolute)) File.Delete(absolute);
            var defaults = new GameplayFarRenderingDefaults(isolatedPath);
            defaults.Load();
            bool missing = defaults.Settings.Overrides.Count == 0 && !defaults.Dirty;
            bool sanitation = !defaults.SetOverride("ResolutionScale", float.NaN)
                && !defaults.SetOverride("AroundSegments", "bad")
                && !defaults.SetOverride("CloudDensity", 10)
                && !defaults.SetOverride("AdaptiveGeometryEnabled", 1)
                && defaults.SetOverride("FarSurfaceTileResolution", 999)
                && defaults.Value("FarSurfaceTileResolution", 0).AsInt32() == 256;
            bool changed = defaults.SetOverride("ResolutionScale", .8f)
                && defaults.SetOverride("AdaptiveGeometryEnabled", false)
                && defaults.SetOverride("NativeTextureStreamingBudgetMiB", 768)
                && defaults.SetOverride("GeometryTargetPixelError", 3f);
            bool mipOrder = defaults.SetOverride("NativeTextureStreamingFinestMip", 12)
                && defaults.Value("NativeTextureStreamingFinestMip", -1).AsInt32() == 3
                && defaults.SetOverride("NativeTextureStreamingInitialMip", 1)
                && defaults.Value("NativeTextureStreamingFinestMip", -1).AsInt32() == 1;
            bool saved = defaults.Save() && !defaults.Dirty && File.Exists(absolute);
            byte[] first = File.ReadAllBytes(absolute);
            bool noOp = !defaults.SetOverride("ResolutionScale", .8f)
                && defaults.Save() && first.AsSpan().SequenceEqual(File.ReadAllBytes(absolute));
            var reloaded = new GameplayFarRenderingDefaults(isolatedPath);
            reloaded.Load();
            bool persisted = reloaded.Value("ResolutionScale", 1f).AsSingle() == .8f
                && !reloaded.Value("AdaptiveGeometryEnabled", true).AsBool()
                && reloaded.Value("NativeTextureStreamingBudgetMiB", 0).AsInt32() == 768 && !reloaded.Dirty;
            far = new RingScaledFarViewport { Enabled = false };
            // A graphics preset may have assigned legacy Low values. Applying the
            // sparse FAR profile preserves explicit user choices and the Cinematic ceiling.
            far.ApplyRuntimeRenderQuality(.5f, 768, 12);
            reloaded.Apply(far);
            bool overrideApplied = far.ResolutionScale == .8f && far.AroundSegments == 4096
                && far.AcrossSegments == 64 && !far.AdaptiveGeometryEnabled && far.GeometryTargetPixelError == 3f;
            graphics = new RingGraphicsQualityController();
            // Inject only dependencies; exercise the real public preset path.
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(RingGraphicsQualityController).GetField("_scaledFarViewport", fields)!.SetValue(graphics, far);
            typeof(RingGraphicsQualityController).GetField("_farRenderingDefaults", fields)!.SetValue(graphics, reloaded);
            bool presetsKeepOverrides = true;
            foreach (var preset in Enum.GetValues<RingTerrainLodCoordinator.QualityPreset>())
            {
                graphics.ApplyPreset(preset, applyTerrainPreset: false);
                presetsKeepOverrides &= far.ResolutionScale == .8f && far.AroundSegments == 4096
                    && far.AcrossSegments == 64 && !far.AdaptiveGeometryEnabled;
            }
            using var corrupt = new GameplayFarRenderingSettings();
            corrupt.Overrides["ResolutionScale"] = "invalid scale";
            corrupt.Overrides["AroundSegments"] = 999999;
            corrupt.Overrides["UnknownSetting"] = 1;
            bool corruptSaved = ResourceSaver.Save(corrupt, isolatedPath) == Error.Ok;
            var invalid = new GameplayFarRenderingDefaults(isolatedPath);
            invalid.Load();
            bool invalidSafe = corruptSaved && invalid.Settings.Overrides.Count == 1
                && invalid.Value("AroundSegments", 0).AsInt32() == 4096;
            reloaded.Reset();
            reloaded.Apply(far);
            bool reset = reloaded.Settings.Overrides.Count == 0 && reloaded.Dirty
                && far.ResolutionScale == 1 && far.AroundSegments == 4096 && far.AcrossSegments == 64
                && far.AdaptiveGeometryEnabled && far.GeometryTargetPixelError == 2 && reloaded.Save();
            bool presetsKeepDefaults = true;
            foreach (var preset in Enum.GetValues<RingTerrainLodCoordinator.QualityPreset>())
            {
                graphics.ApplyPreset(preset, applyTerrainPreset: false);
                presetsKeepDefaults &= far.ResolutionScale == 1 && far.AroundSegments == 4096 && far.AcrossSegments == 64;
            }
            result["pass"] = missing && sanitation && changed && mipOrder && saved && noOp && persisted && overrideApplied && presetsKeepOverrides && invalidSafe && reset && presetsKeepDefaults;
            result["detail"] = $"missing={missing} sanitation={sanitation} changed={changed} mipOrder={mipOrder} saved={saved} noOp={noOp} persisted={persisted} applied={overrideApplied} presetOverrides={presetsKeepOverrides} invalid={invalidSafe} reset={reset} presetDefaults={presetsKeepDefaults}";
        }
        catch (Exception error) { result["pass"] = false; result["detail"] = error.ToString(); }
        finally
        {
            // Explicitly free native fixture nodes that never entered the scene tree.
            if (graphics != null && GodotObject.IsInstanceValid(graphics)) graphics.Free();
            if (far != null && GodotObject.IsInstanceValid(far)) far.Free();
            try { string path = ProjectSettings.GlobalizePath(isolatedPath); if (File.Exists(path)) File.Delete(path); } catch { }
        }
        return result;
    }
}
