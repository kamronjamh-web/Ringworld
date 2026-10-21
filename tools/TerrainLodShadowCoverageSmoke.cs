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

    /// <summary>POI sun map extension: gated on the near map and a registered POI, D quantised/clamped, splits, terrain casters and heightfield authority stay at the near distance, nothing changes without a POI.</summary>
    public Godot.Collections.Dictionary RunPoiExtension(SceneTree tree)
    {
        var controller = new RingGraphicsQualityController();
        var sun = new DirectionalLight3D { Name = "Sun", ShadowEnabled = true, LightEnergy = 1.0f };
        controller.AddChild(sun);
        controller.SunLightPath = new NodePath("Sun");
        tree.Root.AddChild(controller);
        controller.ApplyPreset(RingTerrainLodCoordinator.QualityPreset.VeryHigh, applyTerrainPreset: false);
        var planet = new DirectionalLight3D { ShadowEnabled = false, LightEnergy = 0.0f };
        float presetDistance = sun.DirectionalShadowMaxDistance;
        bool ready = true;
        float? poiDistance = null;
        controller.NearSunMapReadyProbe = () => ready;
        controller.PoiFarthestDistanceProbe = () => poiDistance;
        // Split2 / Split3 / FadeStart overrides must be ignored while extended, exactly as near mode already ignores them.
        controller.SetShadowTuningOverrides(new Godot.Collections.Dictionary { ["HeightShadowEnabled"] = true, ["NearCascadeMeters"] = 30.0f, ["Split2"] = .3f, ["Split3"] = .6f, ["FadeStart"] = .5f });
        controller.StepNearSunMapForTest(0.1);
        controller.StepPoiSunMapForTest(0.1);
        bool Baseline() => controller.NearSunMapActive && !controller.PoiSunMapExtensionActive && Mathf.IsEqualApprox(sun.DirectionalShadowMaxDistance, 400.0f) && sun.DirectionalShadowMode == DirectionalLight3D.ShadowMode.Parallel2Splits
            && Mathf.IsEqualApprox(sun.DirectionalShadowFadeStart, .8f) && Mathf.IsEqualApprox(sun.DirectionalShadowSplit1, 30.0f / 400.0f)
            && Mathf.IsEqualApprox(controller.SunTerrainCasterDistanceMeters, 400.0f) && Mathf.IsEqualApprox(controller.SunHeightShadowAuthorityMeters, 320.0f) && !sun.HasMeta(RingGraphicsQualityController.SunNearDistanceMetaKey)
            && Mathf.IsEqualApprox(RingGraphicsQualityController.ResolveTerrainShadowCasterCoverage(sun, planet, controller.SunTerrainCasterDistanceMeters), 400.0f);
        bool noPoiIsNear = Baseline();
        float split2Near = sun.DirectionalShadowSplit2, split3Near = sun.DirectionalShadowSplit3;

        bool quantise = Mathf.IsEqualApprox(RingGraphicsQualityController.QuantizePoiSunMapDistance(3200.0f, 400.0f, 20000.0f), 3500.0f)
            && Mathf.IsEqualApprox(RingGraphicsQualityController.QuantizePoiSunMapDistance(3500.0f, 400.0f, 20000.0f), 3500.0f)
            && Mathf.IsEqualApprox(RingGraphicsQualityController.QuantizePoiSunMapDistance(3501.0f, 400.0f, 20000.0f), 4000.0f)
            && Mathf.IsEqualApprox(RingGraphicsQualityController.QuantizePoiSunMapDistance(100.0f, 400.0f, 20000.0f), 500.0f)
            && Mathf.IsEqualApprox(RingGraphicsQualityController.QuantizePoiSunMapDistance(50000.0f, 400.0f, 20000.0f), 20000.0f);

        poiDistance = 3200.0f;
        controller.StepPoiSunMapForTest(0.1);
        const float D = 3500.0f;
        bool entered = controller.PoiSunMapExtensionActive && Mathf.IsEqualApprox(controller.PoiSunMapDistanceMeters, D)
            && Mathf.IsEqualApprox(sun.DirectionalShadowMaxDistance, D) && sun.DirectionalShadowMode == DirectionalLight3D.ShadowMode.Parallel4Splits && Mathf.IsEqualApprox(sun.DirectionalShadowFadeStart, .9f);
        bool splits = Mathf.IsEqualApprox(sun.DirectionalShadowSplit1, 30.0f / D) && Mathf.IsEqualApprox(sun.DirectionalShadowSplit2, 400.0f / D) && Mathf.IsEqualApprox(sun.DirectionalShadowSplit3, Mathf.Sqrt(400.0f * D) / D);
        bool coverageStays = Mathf.IsEqualApprox(controller.SunTerrainCasterDistanceMeters, 400.0f)
            && Mathf.IsEqualApprox(RingGraphicsQualityController.ResolveTerrainShadowCasterCoverage(sun, planet, controller.SunTerrainCasterDistanceMeters), 400.0f)
            && sun.HasMeta(RingGraphicsQualityController.SunNearDistanceMetaKey) && Mathf.IsEqualApprox((float)sun.GetMeta(RingGraphicsQualityController.SunNearDistanceMetaKey), 400.0f);
        bool authorityStays = Mathf.IsEqualApprox(controller.SunHeightShadowAuthorityMeters, 320.0f);

        int revision = controller.HeightShadowRevision;
        controller.StepPoiSunMapForTest(0.1);
        bool quietWhenUnchanged = controller.HeightShadowRevision == revision;
        poiDistance = 100.0f;
        controller.StepPoiSunMapForTest(0.1);
        bool floorsAtOneStep = Mathf.IsEqualApprox(sun.DirectionalShadowMaxDistance, 500.0f);
        poiDistance = 50000.0f;
        controller.StepPoiSunMapForTest(0.1);
        bool capsAtSetting = Mathf.IsEqualApprox(sun.DirectionalShadowMaxDistance, 20000.0f);
        controller.SetShadowTuningOverride("HeightShadowPoiMapDistanceMeters", 12000.0f);
        controller.StepPoiSunMapForTest(0.1);
        bool customCap = Mathf.IsEqualApprox(sun.DirectionalShadowMaxDistance, 12000.0f) && Mathf.IsEqualApprox(controller.SunTerrainCasterDistanceMeters, 400.0f);
        controller.SetShadowTuningOverride("HeightShadowPoiMapDistanceMeters", 20000.0f);

        poiDistance = null;
        controller.StepPoiSunMapForTest(0.5);
        bool graceHolds = controller.PoiSunMapExtensionActive;
        poiDistance = 3200.0f;
        controller.StepPoiSunMapForTest(0.2);
        poiDistance = null;
        controller.StepPoiSunMapForTest(0.6);
        bool graceResets = controller.PoiSunMapExtensionActive;
        controller.StepPoiSunMapForTest(0.6);
        bool left = Baseline() && Mathf.IsEqualApprox(sun.DirectionalShadowSplit2, split2Near) && Mathf.IsEqualApprox(sun.DirectionalShadowSplit3, split3Near);

        poiDistance = 3200.0f;
        controller.StepPoiSunMapForTest(0.1);
        bool reentered = controller.PoiSunMapExtensionActive;
        controller.PoiSunMapExtensionForcedOff = true;
        controller.StepPoiSunMapForTest(0.0);
        bool forcedOff = Baseline();
        controller.PoiSunMapExtensionForcedOff = false;
        controller.StepPoiSunMapForTest(0.1);
        bool backOn = controller.PoiSunMapExtensionActive;

        ready = false;
        controller.StepNearSunMapForTest(1.1);
        controller.StepPoiSunMapForTest(0.1);
        bool nearLeaveEndsIt = !controller.NearSunMapActive && !controller.PoiSunMapExtensionActive && Mathf.IsEqualApprox(sun.DirectionalShadowMaxDistance, presetDistance)
            && Mathf.IsEqualApprox(controller.SunTerrainCasterDistanceMeters, presetDistance) && !sun.HasMeta(RingGraphicsQualityController.SunNearDistanceMetaKey);

        tree.Root.RemoveChild(controller);
        controller.Free();
        planet.Free();
        bool passed = noPoiIsNear && quantise && entered && splits && coverageStays && authorityStays && quietWhenUnchanged && floorsAtOneStep && capsAtSetting && customCap && graceHolds && graceResets && left && reentered && forcedOff && backOn && nearLeaveEndsIt;
        return Result(passed, $"poiExtension[noPoi={noPoiIsNear} quantise={quantise} enter={entered} splits={splits} coverage={coverageStays} authority={authorityStays} quiet={quietWhenUnchanged} floor={floorsAtOneStep} cap={capsAtSetting}/{customCap} grace={graceHolds}/{graceResets} leave={left} reenter={reentered} forcedOff={forcedOff}/{backOn} nearLeave={nearLeaveEndsIt}]", -1);
    }

    /// <summary>S7a near planet map: enters with a ready planet layer, leaves at once for receivers off / a sun layer / the option off, after 1 s for a missing or uncovering front; the planet split is restored on exit.</summary>
    public Godot.Collections.Dictionary RunNearPlanetMap(SceneTree tree)
    {
        var controller = new RingGraphicsQualityController();
        var sun = new DirectionalLight3D { Name = "Sun", ShadowEnabled = true, LightEnergy = 1.0f };
        var planet = new DirectionalLight3D { Name = "Planet", ShadowEnabled = true, LightEnergy = 0.3f, DirectionalShadowMaxDistance = 6000.0f, DirectionalShadowSplit1 = 0.04f };
        controller.AddChild(sun);
        controller.AddChild(planet);
        controller.SunLightPath = new NodePath("Sun");
        controller.PlanetLightPath = new NodePath("Planet");
        tree.Root.AddChild(controller);
        controller.ApplyPreset(RingTerrainLodCoordinator.QualityPreset.VeryHigh, applyTerrainPreset: false);
        bool ready = false, leaveNow = false;
        controller.NearPlanetMapProbe = () => (ready, leaveNow);
        controller.SetShadowTuningOverrides(new Godot.Collections.Dictionary { ["HeightShadowEnabled"] = true, ["NearCascadeMeters"] = 30.0f, ["PlanetShadowDistanceMeters"] = 9000.0f });
        controller.StepNearPlanetMapForTest(0.1);
        bool notReady = !controller.NearPlanetMapActive && Mathf.IsEqualApprox(planet.DirectionalShadowSplit1, 0.04f);
        ready = true;
        controller.StepNearPlanetMapForTest(0.1);
        bool entered = controller.NearPlanetMapActive && Mathf.IsEqualApprox(planet.DirectionalShadowSplit1, 30.0f / 250.0f);
        controller.SetShadowTuningOverride("HeightShadowPlanetNearMapDistanceMeters", 150);
        bool distance150 = Mathf.IsEqualApprox(planet.DirectionalShadowSplit1, 30.0f / 150.0f);
        controller.SetShadowTuningOverride("HeightShadowPlanetNearMapDistanceMeters", 250);
        ready = false;
        controller.StepNearPlanetMapForTest(0.5);
        bool graceHolds = controller.NearPlanetMapActive;
        ready = true;
        controller.StepNearPlanetMapForTest(0.2);
        ready = false;
        controller.StepNearPlanetMapForTest(0.6);
        bool graceResets = controller.NearPlanetMapActive;
        controller.StepNearPlanetMapForTest(0.6);
        bool left = !controller.NearPlanetMapActive && Mathf.IsEqualApprox(planet.DirectionalShadowSplit1, 0.04f);
        ready = true;
        controller.StepNearPlanetMapForTest(0.1);
        bool reentered = controller.NearPlanetMapActive;
        leaveNow = true;
        controller.StepNearPlanetMapForTest(0.0);
        bool sunLayerLeavesAtOnce = !controller.NearPlanetMapActive && Mathf.IsEqualApprox(planet.DirectionalShadowSplit1, 0.04f);
        leaveNow = false;
        controller.StepNearPlanetMapForTest(0.1);
        bool again = controller.NearPlanetMapActive;
        controller.SetShadowTuningOverride("HeightShadowPlanetNearMapEnabled", false);
        controller.StepNearPlanetMapForTest(0.0);
        bool optionOffLeavesAtOnce = !controller.NearPlanetMapActive;
        controller.SetShadowTuningOverride("HeightShadowPlanetNearMapEnabled", true);
        controller.StepNearPlanetMapForTest(0.1);
        bool backOn = controller.NearPlanetMapActive;
        controller.SetShadowTuningOverride("HeightShadowEnabled", false);
        controller.StepNearPlanetMapForTest(0.0);
        bool featureOffLeavesAtOnce = !controller.NearPlanetMapActive && Mathf.IsEqualApprox(planet.DirectionalShadowSplit1, 0.04f);
        // The planet macro handoff follows the near map (200 m -> 250 m at fade 0.8) instead of 5.1 km -> 6 km.
        Vector2 nearHandoff = RingTerrainGlobalLod.ResolvePlanetMacroShadowHandoff(8000.0f, 13000.0f, true, 250.0f, .8f);
        bool handoffFollows = Mathf.IsEqualApprox(nearHandoff.X, 200.0f) && Mathf.IsEqualApprox(nearHandoff.Y, 250.0f);

        tree.Root.RemoveChild(controller);
        controller.Free();
        bool passed = notReady && entered && distance150 && graceHolds && graceResets && left && reentered && sunLayerLeavesAtOnce && again && optionOffLeavesAtOnce && backOn && featureOffLeavesAtOnce && handoffFollows;
        return Result(passed, $"nearPlanetMap[notReady={notReady} enter={entered} d150={distance150} grace={graceHolds}/{graceResets} leave={left} reenter={reentered} sunLayer={sunLayerLeavesAtOnce}/{again} optionOff={optionOffLeavesAtOnce} backOn={backOn} featureOff={featureOffLeavesAtOnce} handoff={handoffFollows}]", -1);
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
