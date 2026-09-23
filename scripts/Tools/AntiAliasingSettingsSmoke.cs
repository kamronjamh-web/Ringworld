using System;
using System.IO;
using Godot;
using RingworldTechDemo.UI;

namespace RingworldTechDemo.Tools;

/// <summary>CPU-only AA preference regression coverage; callers pass an isolated user:// path.</summary>
[GlobalClass]
public partial class AntiAliasingSettingsSmoke : RefCounted
{
    public Godot.Collections.Dictionary Run(string isolatedPath)
    {
        var result = new Godot.Collections.Dictionary();
        Variant oldRuntime = ProjectSettings.GetSetting(GameplayAntiAliasingDefaults.RuntimeModeSetting, (int)AntiAliasingMode.Fxaa);
        SubViewport? viewport = null;
        try
        {
            string absolute = ProjectSettings.GlobalizePath(isolatedPath);
            if (File.Exists(absolute)) File.Delete(absolute);
            viewport = new SubViewport { Scaling3DMode = Viewport.Scaling3DModeEnum.Bilinear, Scaling3DScale = 1f };
            var settings = new GameplayAntiAliasingDefaults(isolatedPath);
            settings.Initialize(viewport);
            bool defaultFxaa = settings.Settings.Mode == AntiAliasingMode.Fxaa;

            bool taaChanged = settings.SetMode(viewport, AntiAliasingMode.Taa);
            bool taaAppliedOrGated = viewport.UseTaa == (RenderingServer.GetCurrentRenderingMethod() == "forward_plus")
                && viewport.ScreenSpaceAA == Viewport.ScreenSpaceAAEnum.Disabled
                && viewport.Msaa3D == Viewport.Msaa.Disabled;
            bool offChanged = settings.SetMode(viewport, AntiAliasingMode.Off);
            bool offClearsTaa = !viewport.UseTaa && viewport.ScreenSpaceAA == Viewport.ScreenSpaceAAEnum.Disabled
                && viewport.Msaa3D == Viewport.Msaa.Disabled;
            settings.SetMode(viewport, AntiAliasingMode.Taa);
            bool fxaaChanged = settings.SetMode(viewport, AntiAliasingMode.Fxaa);
            bool fxaaClearsTaa = !viewport.UseTaa && viewport.Msaa3D == Viewport.Msaa.Disabled;

            settings.SetMode(viewport, AntiAliasingMode.Taa);
            bool changed = settings.SetMode(viewport, AntiAliasingMode.Msaa4X);
            bool exclusiveMsaa = viewport.ScreenSpaceAA == Viewport.ScreenSpaceAAEnum.Disabled
                && viewport.Msaa3D == Viewport.Msaa.Msaa4X && !viewport.UseTaa;
            bool saved = settings.Save();
            var loaded = new GameplayAntiAliasingDefaults(isolatedPath);
            loaded.Initialize(viewport);
            bool persisted = loaded.Settings.Mode == AntiAliasingMode.Msaa4X;

            loaded.SetMode(viewport, AntiAliasingMode.Taa);
            viewport.Scaling3DMode = Viewport.Scaling3DModeEnum.Fsr2;
            viewport.Scaling3DScale = .67f;
            loaded.Apply(viewport);
            bool fsrExclusive = viewport.ScreenSpaceAA == Viewport.ScreenSpaceAAEnum.Disabled
                && viewport.Msaa3D == Viewport.Msaa.Disabled && !viewport.UseTaa
                && loaded.GetEffectiveStatus(viewport).Contains("67% render scale");
            loaded.SetMode(viewport, AntiAliasingMode.Msaa4X);
            viewport.Scaling3DMode = Viewport.Scaling3DModeEnum.Bilinear;
            GameplayAntiAliasingDefaults.ReapplyRuntimePreference(viewport);
            bool reapplyAfterFsr = viewport.Msaa3D == Viewport.Msaa.Msaa4X;

            viewport.ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Smaa;
            viewport.Msaa3D = Viewport.Msaa.Disabled;
            viewport.UseTaa = false;
            bool statusReadsViewport = loaded.GetEffectiveStatus(viewport).Contains("SMAA");

            var invalidSaved = new GameplayAntiAliasingSettings { Mode = (AntiAliasingMode)999 };
            bool wroteInvalid = ResourceSaver.Save(invalidSaved, isolatedPath) == Error.Ok;
            var invalid = new GameplayAntiAliasingDefaults(isolatedPath);
            invalid.Initialize(viewport);
            bool invalidFallsBackToFxaa = wroteInvalid && invalid.Settings.Mode == AntiAliasingMode.Fxaa;

            invalid.Reset(viewport);
            bool reset = invalid.Settings.Mode == AntiAliasingMode.Fxaa && invalid.Dirty && invalid.Save();
            result["pass"] = defaultFxaa && taaChanged && taaAppliedOrGated && offChanged && offClearsTaa && fxaaChanged && fxaaClearsTaa && changed && exclusiveMsaa && saved && persisted && fsrExclusive && reapplyAfterFsr && statusReadsViewport && invalidFallsBackToFxaa && reset;
            result["detail"] = $"default={defaultFxaa} taa={taaChanged}/{taaAppliedOrGated} off={offChanged}/{offClearsTaa} fxaa={fxaaChanged}/{fxaaClearsTaa} changed={changed} exclusive={exclusiveMsaa} saved={saved} persisted={persisted} fsr={fsrExclusive} reapply={reapplyAfterFsr} status={statusReadsViewport} invalid={invalidFallsBackToFxaa} reset={reset}";
        }
        catch (Exception error) { result["pass"] = false; result["detail"] = error.Message; }
        finally
        {
            ProjectSettings.SetSetting(GameplayAntiAliasingDefaults.RuntimeModeSetting, oldRuntime);
            if (viewport != null && GodotObject.IsInstanceValid(viewport)) viewport.Free();
            try { string absolute = ProjectSettings.GlobalizePath(isolatedPath); if (File.Exists(absolute)) File.Delete(absolute); } catch { }
        }
        return result;
    }
}
