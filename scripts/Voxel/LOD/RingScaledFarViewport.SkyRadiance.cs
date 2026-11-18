using Godot;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingScaledFarViewport
{
    /// <summary>
    /// R1-6d (docs/reflections-plan.md 9.4, docs/work-orders/reflections/R1-6-design.md 4.5): the far and the main Environment render the same Sky, and the engine
    /// rebuilds that Sky's radiance map whenever the fog values of the Environment being rendered differ from those the Sky saw last, even with fog off.
    /// With different values in the two Environments the map was rebuilt twice per frame (0.19 ms each). The far Environment has fog off, so its six
    /// fog values are unused there (sky and scene fog apply only when enabled; volumetric fog is off); they are copied from the main Environment every frame,
    /// so both match. The main Environment is not touched: its values are read by the cloud ambience (SunshineCloudsDriver samples fog_light_color)
    /// and drive water and cave fog. fog_enabled is not copied: while the main fog is on (underwater, caves) the map is rebuilt as before.
    /// </summary>
    private void SyncFarFogValuesToMain()
    {
        if (_farEnvironment == null || _mainWorldEnvironment?.Environment is not { } main) return;
        Godot.Environment far = _farEnvironment;
        float density = main.FogDensity;
        if (far.FogDensity != density) far.FogDensity = density;
        Color lightColor = main.FogLightColor;
        if (far.FogLightColor != lightColor) far.FogLightColor = lightColor;
        float lightEnergy = main.FogLightEnergy;
        if (far.FogLightEnergy != lightEnergy) far.FogLightEnergy = lightEnergy;
        float sunScatter = main.FogSunScatter;
        if (far.FogSunScatter != sunScatter) far.FogSunScatter = sunScatter;
        float aerialPerspective = main.FogAerialPerspective;
        if (far.FogAerialPerspective != aerialPerspective) far.FogAerialPerspective = aerialPerspective;
        float skyAffect = main.FogSkyAffect;
        if (far.FogSkyAffect != skyAffect) far.FogSkyAffect = skyAffect;
    }
}
