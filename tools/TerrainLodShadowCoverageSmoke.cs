using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

/// <summary>CPU-only contract for bounded terrain proxy directional-shadow coverage.</summary>
[GlobalClass]
public partial class TerrainLodShadowCoverageSmoke : RefCounted
{
    public Godot.Collections.Dictionary Run()
    {
        bool activeRange = RingTerrainLodCoordinator.ShouldCastTerrainShadowForLevel(true, 0, 0, 4, true, false)
            && RingTerrainLodCoordinator.ShouldCastTerrainShadowForLevel(true, 1, 0, 4, true, false)
            && RingTerrainLodCoordinator.ShouldCastTerrainShadowForLevel(true, 2, 0, 4, true, false)
            && RingTerrainLodCoordinator.ShouldCastTerrainShadowForLevel(true, 3, 0, 4, true, false)
            && RingTerrainLodCoordinator.ShouldCastTerrainShadowForLevel(true, 4, 0, 4, true, false)
            && !RingTerrainLodCoordinator.ShouldCastTerrainShadowForLevel(true, 5, 0, 4, true, true);
        bool inactiveNeverCasts = !RingTerrainLodCoordinator.ShouldCastTerrainShadowForLevel(false, 2, 0, 4, true, true);
        bool unrestrictedPreservesInspector = RingTerrainLodCoordinator.ShouldCastTerrainShadowForLevel(true, 2, 0, 4, false, true)
            && !RingTerrainLodCoordinator.ShouldCastTerrainShadowForLevel(true, 2, 0, 4, false, false);

        var sun = new DirectionalLight3D { ShadowEnabled = true, LightEnergy = 1.0f, DirectionalShadowMaxDistance = 4000.0f };
        var planet = new DirectionalLight3D { ShadowEnabled = true, LightEnergy = .2f, DirectionalShadowMaxDistance = 6000.0f };
        bool dualLightCoverage = Mathf.IsEqualApprox(
            RingGraphicsQualityController.ResolveTerrainShadowCasterCoverage(sun, planet), 6000.0f);
        planet.ShadowEnabled = false;
        bool inactivePlanetExcluded = Mathf.IsEqualApprox(
            RingGraphicsQualityController.ResolveTerrainShadowCasterCoverage(sun, planet), 4000.0f);
        sun.LightEnergy = 0.0f;
        bool disabledLightsExcluded = Mathf.IsZeroApprox(
            RingGraphicsQualityController.ResolveTerrainShadowCasterCoverage(sun, planet));
        Vector2 cappedPlanetHandoff = RingTerrainGlobalLod.ResolvePlanetMacroShadowHandoff(
            8000.0f, 13000.0f, true, 6000.0f, .85f);
        bool cappedPlanetHandoffMatches = Mathf.IsEqualApprox(cappedPlanetHandoff.X, 5100.0f)
            && Mathf.IsEqualApprox(cappedPlanetHandoff.Y, 6000.0f);
        Vector2 retainedNearHandoff = RingTerrainGlobalLod.ResolvePlanetMacroShadowHandoff(
            1000.0f, 2000.0f, true, 6000.0f, .85f);
        bool retainedNearHandoffMatches = Mathf.IsEqualApprox(retainedNearHandoff.X, 1000.0f)
            && Mathf.IsEqualApprox(retainedNearHandoff.Y, 2000.0f);
        Vector2 noAtlasHandoff = RingTerrainGlobalLod.ResolvePlanetMacroShadowHandoff(
            8000.0f, 13000.0f, false, 6000.0f, .85f);
        bool noAtlasHandoffMatches = Mathf.IsEqualApprox(noAtlasHandoff.X, 0.0f)
            && Mathf.IsEqualApprox(noAtlasHandoff.Y, 1.0f);
        bool projectorReceiverTransform = VerifyPlanetProjectorReceiverTransform();
        sun.Free();
        planet.Free();

        Node? nativeTerrain = ClassDB.Instantiate("Terrain3D").AsGodotObject() as Node;
        if (nativeTerrain == null) return Result(false, "Terrain3D native class unavailable", -1);
        int nativeDefault = nativeTerrain.Get("cast_shadows").AsInt32();
        nativeTerrain.Free();

        bool passed = activeRange && inactiveNeverCasts && unrestrictedPreservesInspector
            && dualLightCoverage && inactivePlanetExcluded && disabledLightsExcluded
            && cappedPlanetHandoffMatches && retainedNearHandoffMatches && noAtlasHandoffMatches
            && projectorReceiverTransform;
        return Result(passed,
            passed ? "active proxy tiers through the local shadow range cast; inactive and distant tiers do not" : "terrain LOD shadow coverage policy mismatch",
            nativeDefault);
    }

    /// <summary>S5b near sun map: the sun map shrinks to 400 m / 2 cascades / fade 0.8 (terrain shadow caster coverage follows) only while the heightfield is ready, leaves after one second not ready, and restores the preset range.</summary>
    public Godot.Collections.Dictionary RunNearMap(SceneTree tree)
    {
        var controller = new RingGraphicsQualityController();
        var sun = new DirectionalLight3D { Name = "Sun", ShadowEnabled = true, LightEnergy = 1.0f };
        controller.AddChild(sun);
        controller.SunLightPath = new NodePath("Sun");
        tree.Root.AddChild(controller);
        controller.ApplyPreset(RingTerrainLodCoordinator.QualityPreset.VeryHigh, applyTerrainPreset: false);
        var planet = new DirectionalLight3D { ShadowEnabled = false, LightEnergy = 0.0f };
        float presetDistance = sun.DirectionalShadowMaxDistance;
        var mode0 = sun.DirectionalShadowMode;
        float fade0 = sun.DirectionalShadowFadeStart;
        bool presetCoverage = Mathf.IsEqualApprox(RingGraphicsQualityController.ResolveTerrainShadowCasterCoverage(sun, planet), presetDistance) && presetDistance > 1000.0f;

        bool ready = false;
        controller.NearSunMapReadyProbe = () => ready;
        var overrides = new Godot.Collections.Dictionary { ["HeightShadowEnabled"] = true, ["MaxDistanceMeters"] = 9000.0f, ["CascadeCount"] = 4, ["FadeStart"] = .5f, ["NearCascadeMeters"] = 30.0f };
        controller.SetShadowTuningOverrides(overrides);
        controller.StepNearSunMapForTest(0.1);
        bool notReadyStaysFull = !controller.NearSunMapActive && Mathf.IsEqualApprox(sun.DirectionalShadowMaxDistance, 9000.0f) && sun.DirectionalShadowMode == DirectionalLight3D.ShadowMode.Parallel4Splits;

        ready = true;
        controller.StepNearSunMapForTest(0.1);
        bool entered = controller.NearSunMapActive && Mathf.IsEqualApprox(sun.DirectionalShadowMaxDistance, 400.0f) && sun.DirectionalShadowMode == DirectionalLight3D.ShadowMode.Parallel2Splits
            && Mathf.IsEqualApprox(sun.DirectionalShadowFadeStart, .8f) && Mathf.IsEqualApprox(sun.DirectionalShadowSplit1, 30.0f / 400.0f)
            && Mathf.IsEqualApprox(RingGraphicsQualityController.ResolveTerrainShadowCasterCoverage(sun, planet), 400.0f);
        controller.SetShadowTuningOverride("HeightShadowNearMapDistanceMeters", 300);
        bool distance300 = Mathf.IsEqualApprox(sun.DirectionalShadowMaxDistance, 300.0f) && Mathf.IsEqualApprox(sun.DirectionalShadowSplit1, 30.0f / 300.0f);
        controller.SetShadowTuningOverride("HeightShadowNearMapDistanceMeters", 400);

        ready = false;
        controller.StepNearSunMapForTest(0.5);
        bool graceHolds = controller.NearSunMapActive && Mathf.IsEqualApprox(sun.DirectionalShadowMaxDistance, 400.0f);
        ready = true;
        controller.StepNearSunMapForTest(0.2);
        ready = false;
        controller.StepNearSunMapForTest(0.6);
        bool graceResets = controller.NearSunMapActive;
        controller.StepNearSunMapForTest(0.6);
        bool left = !controller.NearSunMapActive && Mathf.IsEqualApprox(sun.DirectionalShadowMaxDistance, 9000.0f) && sun.DirectionalShadowMode == DirectionalLight3D.ShadowMode.Parallel4Splits
            && Mathf.IsEqualApprox(sun.DirectionalShadowFadeStart, .5f) && Mathf.IsEqualApprox(sun.DirectionalShadowSplit1, 30.0f / 9000.0f)
            && Mathf.IsEqualApprox(RingGraphicsQualityController.ResolveTerrainShadowCasterCoverage(sun, planet), 9000.0f);

        ready = true;
        controller.StepNearSunMapForTest(0.1);
        bool reentered = controller.NearSunMapActive;
        controller.SetShadowTuningOverride("HeightShadowNearMapEnabled", false);
        controller.StepNearSunMapForTest(0.0);
        bool optionOffLeavesAtOnce = !controller.NearSunMapActive && Mathf.IsEqualApprox(sun.DirectionalShadowMaxDistance, 9000.0f);
        controller.SetShadowTuningOverride("HeightShadowNearMapEnabled", true);
        controller.StepNearSunMapForTest(0.1);
        bool backOn = controller.NearSunMapActive;
        controller.SetShadowTuningOverride("HeightShadowEnabled", false);
        controller.StepNearSunMapForTest(0.0);
        bool featureOffLeavesAtOnce = !controller.NearSunMapActive;
        controller.SetShadowTuningOverrides(new Godot.Collections.Dictionary());
        bool restoresPreset = Mathf.IsEqualApprox(sun.DirectionalShadowMaxDistance, presetDistance) && sun.DirectionalShadowMode == mode0 && Mathf.IsEqualApprox(sun.DirectionalShadowFadeStart, fade0);

        tree.Root.RemoveChild(controller);
        controller.Free();
        planet.Free();
        bool passed = presetCoverage && notReadyStaysFull && entered && distance300 && graceHolds && graceResets && left && reentered && optionOffLeavesAtOnce && backOn && featureOffLeavesAtOnce && restoresPreset;
        return Result(passed, $"nearMap[preset={presetCoverage} notReady={notReadyStaysFull} enter={entered} d300={distance300} grace={graceHolds}/{graceResets} leave={left} reenter={reentered} optionOff={optionOffLeavesAtOnce} backOn={backOn} featureOff={featureOffLeavesAtOnce} restore={restoresPreset}]", -1);
    }

    private static Godot.Collections.Dictionary Result(bool passed, string detail, int nativeDefault) => new()
    {
        ["pass"] = passed,
        ["detail"] = detail,
        ["terrain3d_default_cast_shadows"] = nativeDefault
    };

    private static bool VerifyPlanetProjectorReceiverTransform()
    {
        var shader = new Shader { Code = "shader_type spatial; uniform vec3 terrain_planet_shadow_direction; uniform float terrain_planet_shadow_fade_start_meters; uniform float terrain_planet_shadow_fade_end_meters; void fragment() { ALBEDO = vec3(0.0); }" };
        var material = new ShaderMaterial { Shader = shader };
        Image image = Image.CreateEmpty(2, 2, false, Image.Format.R8);
        var texture = ImageTexture.CreateFromImage(image);
        Basis transform = new Basis(Vector3.Up, Mathf.Pi * .5f);
        RingTerrainMacroShadowProjector.Register(material, 2.0f);
        RingTerrainMacroShadowProjector.UpdatePlanet(texture, Vector3.Forward, .3f, 100.0f, 200.0f);
        RingTerrainMacroShadowProjector.SetReceiverDirectionTransform(material, transform);
        Vector3 initialDirection = material.GetShaderParameter("terrain_planet_shadow_direction").AsVector3();
        RingTerrainMacroShadowProjector.Register(material, 2.0f);
        RingTerrainMacroShadowProjector.UpdatePlanet(texture, Vector3.Right, .3f, 100.0f, 200.0f);
        Vector3 updatedDirection = material.GetShaderParameter("terrain_planet_shadow_direction").AsVector3();
        float fadeStart = material.GetShaderParameter("terrain_planet_shadow_fade_start_meters").AsSingle();
        float fadeEnd = material.GetShaderParameter("terrain_planet_shadow_fade_end_meters").AsSingle();
        bool valid = initialDirection.IsEqualApprox(transform * Vector3.Forward)
            && updatedDirection.IsEqualApprox(transform * Vector3.Right)
            && Mathf.IsEqualApprox(fadeStart, 200.0f)
            && Mathf.IsEqualApprox(fadeEnd, 400.0f);
        RingTerrainMacroShadowProjector.DisablePlanet();
        return valid;
    }
}
