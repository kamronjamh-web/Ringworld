using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Configuration half of the graphics-preset contract (docs/quality-presets-plan.md section 1, C1-C3) for all five menu presets
/// (Low, Medium, High, VeryHigh, UltraVideo) on factory-default atmosphere settings. No GPU needed.
/// Run: Godot --headless --path . --script res://tools/quality_preset_contract_headless_test.gd. Prints QP_CONTRACT_HEADLESS=PASS.
/// </summary>
public partial class QualityPresetContractHeadlessTest : RefCounted
{
    private static readonly string[] Names = { "Low", "Medium", "High", "VeryHigh", "UltraVideo" };

    public int Run()
    {
        var failures = new List<string>();
        var notes = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        SceneTree? tree = Engine.GetMainLoop() as SceneTree;
        if (tree?.Root == null) { Console.Error.WriteLine("QP_CONTRACT_HEADLESS=FAIL no SceneTree root"); return 1; }

        bool savedEnabled = RingAtmosphereModel.Enabled;
        RingAtmosphereModel.HazeModelKind savedModel = RingAtmosphereModel.HazeModel;
        string atmoPath = $"res://.godot/qp_contract_atmo_{Time.GetTicksUsec()}.tres";
        RingTerrainGlobalLod? globalLod = null;
        RingGraphicsQualityController? controller = null;
        DirectionalLight3D? sun = null;
        try
        {
            controller = new RingGraphicsQualityController();
            sun = new DirectionalLight3D { Name = "Sun", ShadowEnabled = true };
            controller.AddChild(sun);
            controller.SunLightPath = new NodePath("Sun");
            globalLod = new RingTerrainGlobalLod { Name = "GlobalTerrainLod3", EnableMacroTerrainShadows = true, EnableMacroTerrainWallShadows = false };
            tree.Root.AddChild(globalLod);
            tree.Root.AddChild(controller);
            // Factory defaults: the path does not exist, so no user preference is loaded.
            controller.InstallAtmosphereDefaults(atmoPath);

            bool atlasFallbackNoted = false;
            for (int p = 0; p <= 4; p++)
            {
                string tag = $"p={p}({Names[p]})";
                controller.ApplyPreset((RingTerrainLodCoordinator.QualityPreset)p, applyTerrainPreset: false);
                // C1 config
                Check(sun.DirectionalShadowMaxDistance >= 250.0f, $"C1 {tag} sun DirectionalShadowMaxDistance={sun.DirectionalShadowMaxDistance} < 250");
                Check(controller.RequestedSunShadowsEnabled, $"C1 {tag} RequestedSunShadowsEnabled=false");
                Check(controller.EffectiveShadowDistanceMeters >= 250.0f, $"C1 {tag} EffectiveShadowDistanceMeters={controller.EffectiveShadowDistanceMeters} < 250");
                int atlas = controller.EffectiveDirectionalShadowAtlasSize;
                string atlasName = "EffectiveDirectionalShadowAtlasSize";
                if (atlas <= 0)
                {
                    atlas = controller.RequestedDirectionalShadowAtlasSize;
                    atlasName = "RequestedDirectionalShadowAtlasSize";
                    if (!atlasFallbackNoted) { notes.Add("effective atlas reported 0 headless (no RD); asserting Requested atlas"); atlasFallbackNoted = true; }
                }
                Check(atlas >= 2048, $"C1 {tag} {atlasName}={atlas} < 2048");
                // C2 config
                Check(controller.EffectiveTerrainMacroShadowsEnabled, $"C2 {tag} EffectiveTerrainMacroShadowsEnabled=false");
                Check(controller.EffectiveTerrainMacroShadowMaxTraceMeters >= 64000.0f, $"C2 {tag} EffectiveTerrainMacroShadowMaxTraceMeters={controller.EffectiveTerrainMacroShadowMaxTraceMeters} < 64000");
                // C3
                Check(RingAtmosphereModel.Enabled, $"C3 {tag} RingAtmosphereModel.Enabled=false");
                Check(RingAtmosphereModel.HazeModel == RingAtmosphereModel.HazeModelKind.Physical, $"C3 {tag} HazeModel={RingAtmosphereModel.HazeModel} (want Physical)");
            }
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); failures.Add("exception " + e.Message); }
        finally
        {
            RingAtmosphereModel.Enabled = savedEnabled;
            RingAtmosphereModel.HazeModel = savedModel;
            if (controller != null && GodotObject.IsInstanceValid(controller)) { tree.Root.RemoveChild(controller); controller.Free(); }
            if (globalLod != null && GodotObject.IsInstanceValid(globalLod)) { tree.Root.RemoveChild(globalLod); globalLod.Free(); }
            string abs = ProjectSettings.GlobalizePath(atmoPath);
            if (File.Exists(abs)) File.Delete(abs);
        }
        foreach (string note in notes) Console.WriteLine("QP_CONTRACT_HEADLESS note: " + note);
        if (failures.Count != 0) { Console.Error.WriteLine("QP_CONTRACT_HEADLESS=FAIL " + string.Join(" | ", failures.Take(40))); return 1; }
        Console.WriteLine("QP_CONTRACT_HEADLESS=PASS");
        return 0;
    }
}
