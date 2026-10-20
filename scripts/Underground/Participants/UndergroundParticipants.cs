using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Celestial.Eclipse;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Shadows;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Underground;

/// <summary>Creates the default participant set. Each adapter binds by node path and is a no-op when its system is absent.</summary>
public static class UndergroundParticipantFactory
{
    public static List<UndergroundParticipant> CreateDefaults() => new()
    {
        new GrassParticipant(), new TreesParticipant(),
        new CloudSunVisibilityParticipant(), new SunshineCloudsParticipant(), new LocalVolumetricCloudsParticipant(),
        new WeatherEffectsParticipant(), new WeatherAudioParticipant(), new AmbienceFadeParticipant(),
        new WaterFftParticipant(),
        new TerrainProxyParticipant(), new GlobalTerrainParticipant(), new EmitterStandInParticipant(), new EmitterGridParticipant(), new EclipseHubBeamsParticipant(), new EclipseHubBodyParticipant(), new FarViewportParticipant(),
        new ShadowParticipant()
    };
}

internal sealed class GrassParticipant : UndergroundParticipant
{
    private RingStandingGrassLayer? _grass;
    public override string Name => "Grass";
    public override UndergroundGroups Group => UndergroundGroups.Vegetation;
    public override void Bind(Node root) { _grass = Find<RingStandingGrassLayer>(root, "StandingGrass"); Present = _grass != null; }
    public override void Apply(in UndergroundParticipantState state) { if (_grass != null && GodotObject.IsInstanceValid(_grass)) _grass.UndergroundHold = state.Suspend; }
    public override string Describe() => _grass == null ? "absent" : $"hold={_grass.UndergroundHold} streamingSuspended={_grass.StreamingSuspended} children={_grass.GetChildCount()}";
    public override ulong[] RetainedInstanceIds() => new[] { IdOf(_grass) };
}

internal sealed class TreesParticipant : UndergroundParticipant
{
    private RingBiomeDecorationLayer? _trees;
    public override string Name => "Trees";
    public override UndergroundGroups Group => UndergroundGroups.Vegetation;
    public override void Bind(Node root) { _trees = Find<RingBiomeDecorationLayer>(root, "BiomeDecorations"); Present = _trees != null; }
    public override void Apply(in UndergroundParticipantState state) { if (_trees != null && GodotObject.IsInstanceValid(_trees)) _trees.UndergroundHold = state.Suspend; }
    public override string Describe() => _trees == null ? "absent" : $"hold={_trees.UndergroundHold} children={_trees.GetChildCount()}";
    public override ulong[] RetainedInstanceIds() => new[] { IdOf(_trees) };
}

internal sealed class CloudSunVisibilityParticipant : UndergroundParticipant
{
    private RingTerrainCloudSunVisibility? _visibility;
    public override string Name => "TerrainCloudSunVisibility";
    public override UndergroundGroups Group => UndergroundGroups.Clouds;
    public override void Bind(Node root) { _visibility = Find<RingTerrainCloudSunVisibility>(root, "TerrainCloudSunVisibility"); Present = _visibility != null; }
    public override void Apply(in UndergroundParticipantState state) { if (_visibility != null && GodotObject.IsInstanceValid(_visibility)) _visibility.UndergroundHold = state.Suspend; }
    public override string Describe() => _visibility == null ? "absent" : $"hold={_visibility.UndergroundHold}";
    public override ulong[] RetainedInstanceIds() => new[] { IdOf(_visibility) };
}

internal sealed class SunshineCloudsParticipant : UndergroundParticipant
{
    private RingCloudProviderController? _provider;
    private RingSunshineCloudBridge? _bridge;
    private Node? _driver;
    public override string Name => "SunshineClouds";
    public override UndergroundGroups Group => UndergroundGroups.Clouds;
    public override void Bind(Node root)
    {
        _provider = Find<RingCloudProviderController>(root, "CloudProviderController");
        _bridge = Find<RingSunshineCloudBridge>(root, "SunshineCloudBridge");
        _driver = root.GetNodeOrNull("SunshineCloudsDriver");
        Present = _bridge != null;
    }
    public override void Apply(in UndergroundParticipantState state)
    {
        if (_provider != null && GodotObject.IsInstanceValid(_provider)) _provider.SetUndergroundRenderSuspended(state.Suspend);
        else if (_bridge != null && GodotObject.IsInstanceValid(_bridge)) _bridge.SetRenderSuspended(state.Suspend);
    }
    public override string Describe() => _bridge == null ? "absent"
        : $"renderSuspended={_bridge.RenderSuspended} enabled={_bridge.Enabled} compositorOn={_bridge.CompositorRenderEnabled} runtimeVisible={_bridge.RuntimeVisible} continuous={(_driver?.Get("update_continuously").AsBool() ?? false)}";
    public override ulong[] RetainedInstanceIds() => new[] { IdOf(_bridge), IdOf(_driver), IdOf(_driver?.Get("clouds_resource").AsGodotObject()) };
}

internal sealed class LocalVolumetricCloudsParticipant : UndergroundParticipant
{
    private RingLocalVolumetricCloudRenderer? _renderer;
    public override string Name => "LocalVolumetricClouds";
    public override UndergroundGroups Group => UndergroundGroups.Clouds;
    public override void Bind(Node root) { _renderer = Find<RingLocalVolumetricCloudRenderer>(root, "LocalVolumetricCloudRenderer"); Present = _renderer != null; }
    public override void Apply(in UndergroundParticipantState state) { if (_renderer != null && GodotObject.IsInstanceValid(_renderer)) _renderer.UndergroundSuspended = state.Suspend; }
    public override string Describe() => _renderer == null ? "absent" : $"suspended={_renderer.UndergroundSuspended} ownsLocal={_renderer.LocalVolumeVisible}";
    public override ulong[] RetainedInstanceIds() => new[] { IdOf(_renderer) };
}

internal sealed class WeatherEffectsParticipant : UndergroundParticipant
{
    private RingLocalWeatherEffects? _effects;
    private OmniLight3D? _flash;
    public override string Name => "LocalWeatherEffects";
    public override UndergroundGroups Group => UndergroundGroups.AudioWeather;
    public override void Bind(Node root)
    {
        _effects = Find<RingLocalWeatherEffects>(root, "LocalWeatherEffects");
        _flash = _effects?.GetNodeOrNull<OmniLight3D>("LocalStormFlash");
        Present = _effects != null;
    }
    public override void Apply(in UndergroundParticipantState state)
    {
        if (_effects == null || !GodotObject.IsInstanceValid(_effects)) return;
        _effects.UndergroundSuspended = state.Suspend;
        // Reaches exactly zero slightly before the blend completes, so no flash ever lights cave interiors.
        _effects.UndergroundFlashSuppression = state.Suspend ? 1f : Mathf.Clamp(state.Blend / .9f, 0f, 1f);
    }
    public override string Describe() => _effects == null ? "absent"
        : $"suspended={_effects.UndergroundSuspended} flashSuppression={_effects.UndergroundFlashSuppression:0.###}";
    public float FlashEnergy => _flash != null && GodotObject.IsInstanceValid(_flash) ? _flash.LightEnergy : 0f;
    public override ulong[] RetainedInstanceIds() => new[] { IdOf(_effects), IdOf(_flash) };
}

internal sealed class WeatherAudioParticipant : UndergroundParticipant
{
    private RingWeatherAmbience? _ambience;
    private RingWeatherAudio? _thunder;
    public override string Name => "WeatherAudio";
    public override UndergroundGroups Group => UndergroundGroups.AudioWeather;
    public override void Bind(Node root)
    {
        _ambience = Find<RingWeatherAmbience>(root, "WeatherAmbience");
        _thunder = Find<RingWeatherAudio>(root, "WeatherAudio");
        Present = _ambience != null || _thunder != null;
    }
    public override void Apply(in UndergroundParticipantState state)
    {
        if (_ambience != null && GodotObject.IsInstanceValid(_ambience)) { _ambience.UndergroundBlend = state.Blend; _ambience.UndergroundSuspended = state.Suspend; }
        if (_thunder != null && GodotObject.IsInstanceValid(_thunder)) _thunder.UndergroundBlend = state.Blend;
    }
    public override string Describe() => !Present ? "absent"
        : $"ambienceSuspended={_ambience?.UndergroundSuspended} thunderMuffled={(_thunder?.UndergroundBlend ?? 0f) > 0f}";
    public override ulong[] RetainedInstanceIds() => new[] { IdOf(_ambience), IdOf(_thunder) };
}

/// <summary>Publishes the enclosure blend on the explorer for the GDScript outside/water ambience layers to fade by.</summary>
internal sealed class AmbienceFadeParticipant : UndergroundParticipant
{
    public const string MetaName = "ring_underground_ambience_blend";
    private Node? _explorer;
    private Node? _outside, _water;
    public override string Name => "AmbienceFade";
    public override UndergroundGroups Group => UndergroundGroups.AudioWeather;
    public override void Bind(Node root)
    {
        _explorer = root.GetNodeOrNull("Explorer");
        _outside = root.GetNodeOrNull("OutsideAmbience");
        _water = root.GetNodeOrNull("WaterAmbience");
        Present = _explorer != null;
    }
    public override void Apply(in UndergroundParticipantState state)
    {
        if (_explorer == null || !GodotObject.IsInstanceValid(_explorer)) return;
        float blend = state.Blend;
        float current = _explorer.HasMeta(MetaName) ? (float)_explorer.GetMeta(MetaName) : 0f;
        if (!Mathf.IsEqualApprox(current, blend) || (blend == 0f && current != 0f)) _explorer.SetMeta(MetaName, blend);
    }
    public override string Describe() => _explorer == null ? "absent"
        : $"blend={(_explorer.HasMeta(MetaName) ? (float)_explorer.GetMeta(MetaName) : 0f):0.###}";
    public override ulong[] RetainedInstanceIds() => new[] { IdOf(_explorer), IdOf(_outside), IdOf(_water) };
}

internal sealed class WaterFftParticipant : UndergroundParticipant
{
    private RingWaterFftController? _water;
    public override string Name => "WaterFft";
    public override UndergroundGroups Group => UndergroundGroups.Water;
    public override void Bind(Node root) { _water = Find<RingWaterFftController>(root, "WaterFftController"); Present = _water != null; }
    public override void Apply(in UndergroundParticipantState state) { if (_water != null && GodotObject.IsInstanceValid(_water)) _water.ExternalDormant = state.Suspend; }
    public override string Describe() => _water == null ? "absent" : $"externalDormant={_water.ExternalDormant} enabled={_water.Enabled}";
    public override ulong[] RetainedInstanceIds() => new[] { IdOf(_water) };
}

/// <summary>Freeze tier when enclosed; hide tier only when deep, sealed and sun/sky are already dark. Structural sidewalls stay visible.</summary>
internal sealed class TerrainProxyParticipant : UndergroundParticipant
{
    private RingTerrainLodCoordinator? _coordinator;
    public override string Name => "TerrainProxies";
    public override UndergroundGroups Group => UndergroundGroups.FarTerrain;
    public override void Bind(Node root) { _coordinator = Find<RingTerrainLodCoordinator>(root, "TerrainLodCoordinator"); Present = _coordinator != null; }
    public override void Apply(in UndergroundParticipantState state)
    {
        if (_coordinator == null || !GodotObject.IsInstanceValid(_coordinator)) return;
        _coordinator.SetUndergroundTier(state.Hide ? UndergroundProxyTier.Hide : state.Suspend ? UndergroundProxyTier.Freeze : UndergroundProxyTier.None);
    }
    public override string Describe()
    {
        if (_coordinator == null) return "absent";
        int frozen = _coordinator.Levels.Count(level => level.FastTravelSuspended);
        int hidden = _coordinator.Levels.Count(level => level.UndergroundHidden);
        return $"tier={_coordinator.UndergroundTier} levels={_coordinator.Levels.Count} frozen={frozen} hidden={hidden}";
    }
    public override ulong[] RetainedInstanceIds() => new[] { IdOf(_coordinator) }.Concat(_coordinator?.Levels.Select(level => IdOf(level)) ?? System.Array.Empty<ulong>()).ToArray();
}

internal sealed class GlobalTerrainParticipant : UndergroundParticipant
{
    private RingTerrainGlobalLod? _global;
    public override string Name => "GlobalTerrainLod3";
    public override UndergroundGroups Group => UndergroundGroups.FarTerrain;
    public override void Bind(Node root) { _global = Find<RingTerrainGlobalLod>(root, "GlobalTerrainLod3"); Present = _global != null; }
    public override void Apply(in UndergroundParticipantState state) { if (_global != null && GodotObject.IsInstanceValid(_global)) _global.SetUndergroundHidden(state.Hide); }
    public override string Describe() => _global == null ? "absent" : $"hidden={_global.UndergroundHidden} segments={_global.ActiveSegmentCount} children={_global.GetChildCount()}";
    public override ulong[] RetainedInstanceIds() => new[] { IdOf(_global) };
}

internal sealed class EmitterStandInParticipant : UndergroundParticipant
{
    private EmitterStandInLayer? _layer;
    public override string Name => "EmitterStandIns";
    public override UndergroundGroups Group => UndergroundGroups.FarTerrain;
    public override void Bind(Node root) { _layer = Find<EmitterStandInLayer>(root, "EmitterStandIns"); Present = _layer != null; }
    public override void Apply(in UndergroundParticipantState state)
    {
        if (_layer == null || !GodotObject.IsInstanceValid(_layer)) return;
        _layer.UndergroundHold = state.Suspend;
        _layer.SetUndergroundHidden(state.Hide);
    }
    public override string Describe() => _layer == null ? "absent"
        : $"hidden={_layer.UndergroundHidden} hold={_layer.UndergroundHold} visible={_layer.Visible} bound={_layer.IsBound} instances={_layer.RegularInstanceCount + _layer.MainInstanceCount}";
    public override ulong[] RetainedInstanceIds() => new[] { IdOf(_layer), IdOf(_layer?.RegularInstances), IdOf(_layer?.MainInstances), IdOf(_layer?.RegularInstances?.Multimesh), IdOf(_layer?.MainInstances?.Multimesh) };
}

/// <summary>Emitter relay grid (links, heads and the far layer): held while enclosed, hidden deep underground. Registered with the hub beams in phase E4b (user decision 2026-10-17: the grid was never registered before and drew underground).</summary>
internal sealed class EmitterGridParticipant : UndergroundParticipant
{
    private EmitterGridBeamLayer? _layer;
    public override string Name => "EmitterGrid";
    public override UndergroundGroups Group => UndergroundGroups.FarTerrain;
    public override void Bind(Node root) { _layer = Find<EmitterGridBeamLayer>(root, "EmitterGrid"); Present = _layer != null; }
    public override void Apply(in UndergroundParticipantState state)
    {
        if (_layer == null || !GodotObject.IsInstanceValid(_layer)) return;
        _layer.UndergroundHold = state.Suspend;
        _layer.SetUndergroundHidden(state.Hide);
    }
    public override string Describe() => _layer == null ? "absent"
        : $"hidden={_layer.UndergroundHidden} hold={_layer.UndergroundHold} visible={_layer.Visible} bound={_layer.IsBound} links={_layer.LinkCount} farVisible={_layer.FarLayer?.Visible}";
    public override ulong[] RetainedInstanceIds() => new[] { IdOf(_layer), IdOf(_layer?.LinkInstances), IdOf(_layer?.HeadInstances), IdOf(_layer?.LinkInstances?.Multimesh), IdOf(_layer?.FarLayer) };
}

/// <summary>Eclipse spire to hub beams (near and far layers): held while enclosed, hidden deep underground (phase E4b). The layer exists only during the event; the flags persist across its builds.</summary>
internal sealed class EclipseHubBeamsParticipant : UndergroundParticipant
{
    private EclipseHubBeamLayer? _layer;
    public override string Name => "EclipseHubBeams";
    public override UndergroundGroups Group => UndergroundGroups.FarTerrain;
    public override void Bind(Node root) { _layer = Find<EclipseHubBeamLayer>(root, "EclipseBeams"); Present = _layer != null; }
    public override void Apply(in UndergroundParticipantState state)
    {
        if (_layer == null || !GodotObject.IsInstanceValid(_layer)) return;
        _layer.UndergroundHold = state.Suspend;
        _layer.SetUndergroundHidden(state.Hide);
    }
    public override string Describe() => _layer == null ? "absent"
        : $"hidden={_layer.UndergroundHidden} hold={_layer.UndergroundHold} visible={_layer.Visible} bound={_layer.IsBound} farVisible={_layer.FarLayer?.Visible}";
    public override ulong[] RetainedInstanceIds() => new[] { IdOf(_layer) };
}

/// <summary>Eclipse hub sun body (the main-viewport disc proxy, phase E4c): held while enclosed, hidden deep underground.</summary>
internal sealed class EclipseHubBodyParticipant : UndergroundParticipant
{
    private EclipseHubBody? _body;
    public override string Name => "EclipseHubBody";
    public override UndergroundGroups Group => UndergroundGroups.FarTerrain;
    public override void Bind(Node root) { _body = Find<EclipseHubBody>(root, "EclipseHubBody"); Present = _body != null; }
    public override void Apply(in UndergroundParticipantState state)
    {
        if (_body == null || !GodotObject.IsInstanceValid(_body)) return;
        _body.UndergroundHold = state.Suspend;
        _body.SetUndergroundHidden(state.Hide);
    }
    public override string Describe() => _body == null ? "absent"
        : $"hidden={_body.UndergroundHidden} hold={_body.UndergroundHold} quadVisible={_body.Quad?.Visible}";
    public override ulong[] RetainedInstanceIds() => new[] { IdOf(_body) };
}

internal sealed class FarViewportParticipant : UndergroundParticipant
{
    private RingScaledFarViewport? _far;
    public override string Name => "ScaledFarViewport";
    public override UndergroundGroups Group => UndergroundGroups.FarTerrain;
    public override void Bind(Node root) { _far = Find<RingScaledFarViewport>(root, "ScaledFarViewport"); Present = _far != null; }
    public override void Apply(in UndergroundParticipantState state) { if (_far != null && GodotObject.IsInstanceValid(_far)) _far.SetRenderSuspended(state.Hide); }
    public override string Describe() => _far == null ? "absent"
        : $"suspended={_far.RenderSuspended} outerMode={_far.OuterViewportForDiagnostics?.RenderTargetUpdateMode} regionalMode={_far.RegionalViewportForDiagnostics?.RenderTargetUpdateMode} enabled={_far.Enabled}";
    public override ulong[] RetainedInstanceIds() => new[] { IdOf(_far), IdOf(_far?.OuterViewportForDiagnostics), IdOf(_far?.RegionalViewportForDiagnostics) };
}

internal sealed class ShadowParticipant : UndergroundParticipant
{
    private RingGraphicsQualityController? _graphics;
    public override string Name => "SunShadows";
    public override UndergroundGroups Group => UndergroundGroups.Shadows;
    public override void Bind(Node root) { _graphics = Find<RingGraphicsQualityController>(root, "GraphicsQuality"); Present = _graphics != null; }
    public override void Apply(in UndergroundParticipantState state) { if (_graphics != null && GodotObject.IsInstanceValid(_graphics)) _graphics.UndergroundShadowSuspended = state.ShadowSuspend; }
    public override string Describe() => _graphics == null ? "absent" : $"shadowSuspendRequested={_graphics.UndergroundShadowSuspended}";
    public override ulong[] RetainedInstanceIds() => new[] { IdOf(_graphics) };
}

internal sealed class HeightShadowParticipant : UndergroundParticipant
{
    private RingTerrainHeightShadow? _heightShadow;
    public override string Name => "TerrainHeightShadow";
    public override UndergroundGroups Group => UndergroundGroups.Shadows;
    public override void Bind(Node root) { _heightShadow = Find<RingTerrainHeightShadow>(root, "TerrainHeightShadow"); Present = _heightShadow != null; }
    public override void Apply(in UndergroundParticipantState state) { if (_heightShadow != null && GodotObject.IsInstanceValid(_heightShadow)) _heightShadow.UndergroundHold = state.ShadowSuspend; }
    public override string Describe() => _heightShadow == null ? "absent" : $"hold={_heightShadow.UndergroundHold}";
    public override ulong[] RetainedInstanceIds() => new[] { IdOf(_heightShadow) };
}
