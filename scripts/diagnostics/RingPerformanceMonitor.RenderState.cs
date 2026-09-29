using System;
using Godot;
using RingworldTechDemo;
using RingworldTechDemo.Voxel.Integration;

namespace RingworldTechDemo.Diagnostics;

public partial class RingPerformanceMonitor
{
    // This is deliberately capture-only: do not call it from _Process or mutate any render setting here.
    private object CaptureRenderState()
    {
        CacheContextNodes();
        Node? root = GetParent();
        DirectionalLight3D? star = root?.GetNodeOrNull<DirectionalLight3D>("StarLight");
        DirectionalLight3D? planet = root?.GetNodeOrNull<DirectionalLight3D>("PlanetLight");
        RingSunshineCloudBridge? bridge = root?.GetNodeOrNull<RingSunshineCloudBridge>("SunshineCloudBridge");
        Node? driver = root?.GetNodeOrNull<Node>("SunshineCloudsDriver");
        Node? clouds = root?.GetNodeOrNull<Node>("RingVolumetricClouds");
        RingDayNightCycle? cycle = root?.GetNodeOrNull<RingDayNightCycle>("RingDayNightCycle");
        Viewport viewport = GetViewport();
        var graphics = _contextGraphics;
        Godot.Collections.Dictionary handoff = graphics?.GetLocalShadowHandoffDiagnostics() ?? new();
        Variant resource = driver?.Get("clouds_resource") ?? default;
        GodotObject? cloudResource = resource.VariantType == Variant.Type.Object ? resource.AsGodotObject() : null;
        return new
        {
            capturedUtc = DateTime.UtcNow.ToString("O"),
            note = "Capture-time render-state snapshot only; it is not sampled per frame or a full history.",
            viewport = new { viewportSize = new[] { viewport.GetVisibleRect().Size.X, viewport.GetVisibleRect().Size.Y }, scaling3DScale = Finite(viewport.Scaling3DScale), windowFocused = DisplayServer.WindowIsFocused(), gpuAdapter = RenderingServer.GetVideoAdapterName() },
            lights = new { star = Light(star), planet = Light(planet) },
            shadows = new
            {
                requestedSunShadows = Bool(handoff, "requested_sun_shadows"), handoffEnabled = Bool(handoff, "handoff_enabled"), suspended = Bool(handoff, "suspended"), reason = Text(handoff, "reason"),
                offElevationDegrees = Number(handoff, "off_elevation_degrees"), onElevationDegrees = Number(handoff, "on_elevation_degrees"), maxLocalSunElevationDegrees = Number(handoff, "max_local_sun_elevation_degrees"),
                atlasSize = graphics?.EffectiveDirectionalShadowAtlasSize, depthBits = graphics?.EffectiveDirectionalShadowDepthBits, filter = graphics?.EffectiveDirectionalShadowFilter
            },
            explorer = Explorer(_contextExplorer),
            celestial = new { simulationTimeSeconds = Finite(cycle?.GetSimulationSeconds() ?? double.NaN), sunElevationDegrees = Number(cycle, "SunElevationDegrees"), cloudSimulationTimeSeconds = Number(clouds, "CloudSimulationTimeSeconds") },
            clouds = new
            {
                bridgeRequested = bridge?.CloudRenderingEnabled, resourceEnabled = Bool(cloudResource, "enabled"), resolutionEnum = Number(cloudResource, "resolution_scale"),
                actualMaxStepCount = Number(cloudResource, "max_step_count"), actualMaxLightingSteps = Number(cloudResource, "max_lighting_steps"), resourceDensity = Number(cloudResource, "clouds_density"), bridgeDensityMultiplier = Finite(bridge?.CloudDensityMultiplier ?? float.NaN), coverage = Number(cloudResource, "clouds_coverage"), sharpness = Number(cloudResource, "clouds_sharpness"), cloudFloor = Number(cloudResource, "cloud_floor"), cloudCeiling = Number(cloudResource, "cloud_ceiling"), backgroundAltitude = Vector4(cloudResource, "background_altitude"), stormAltitude = Vector4(cloudResource, "storm_altitude"),
                raymarch = new { steps = bridge?.RaymarchSteps, lightingSteps = bridge?.LightingSteps, minStepDistanceMeters = Finite(bridge?.MinStepDistanceMeters ?? float.NaN), maxStepDistanceMeters = Finite(bridge?.MaxStepDistanceMeters ?? float.NaN) },
                cloudShadows = new { enabled = bridge?.CastCloudShadows, gpuGenerationEnabled = bridge?.GpuShadowGenerationEnabled, preferGpu = bridge?.PreferGpuCloudShadows, status = bridge?.GpuCloudShadowStatus, nearResolution = bridge?.GpuNearShadowMapResolution, farResolution = bridge?.GpuFarShadowMapResolution, nearUpdatesPerSecond = Finite(bridge?.GpuNearShadowUpdatesPerSecond ?? float.NaN), farUpdatesPerSecond = Finite(bridge?.GpuFarShadowUpdatesPerSecond ?? float.NaN) },
                driver = new { currentTime = Number(cloudResource, "current_time"), extraLargeNoiseOrigin = Vector(cloudResource, "extra_large_scale_clouds_position"), largeNoiseOrigin = Vector(cloudResource, "large_scale_clouds_position"), mediumNoiseOrigin = Vector(cloudResource, "medium_scale_clouds_position"), detailNoiseOrigin = Vector(cloudResource, "detail_clouds_position") }
            }
        };
    }

    private static object? Light(DirectionalLight3D? light) => light == null || !GodotObject.IsInstanceValid(light) ? null : new { energy = Finite(light.LightEnergy), visible = light.Visible, shadowEnabled = light.ShadowEnabled, angularDistance = Finite(light.LightAngularDistance), shadowRange = Finite(light.DirectionalShadowMaxDistance), direction = Vector(light.GlobalBasis.Z) };
    private static object? Explorer(RingVoxelExplorerController? e) => e == null || !GodotObject.IsInstanceValid(e) ? null : new { around = Finite(e.CanonicalPosition.AroundMeters), across = Finite(e.CanonicalPosition.AcrossMeters), height = Finite(e.CanonicalPosition.VerticalMeters), yaw = Finite(e.SavedYaw), pitch = Finite(e.SavedPitch) };
    private static double? Finite(double value) => double.IsFinite(value) ? value : null;
    private static float? Finite(float value) => float.IsFinite(value) ? value : null;
    private static float?[] Vector(Vector3 value) => new[] { Finite(value.X), Finite(value.Y), Finite(value.Z) };
    private static float?[]? Vector(GodotObject? source, string property) { if (source == null || !GodotObject.IsInstanceValid(source)) return null; Variant value = source.Get(property); return value.VariantType == Variant.Type.Vector3 ? Vector(value.AsVector3()) : null; }
    private static float?[]? Vector4(GodotObject? source, string property) { if (source == null || !GodotObject.IsInstanceValid(source)) return null; Variant value = source.Get(property); return value.VariantType == Variant.Type.Vector4 ? new[] { Finite(value.AsVector4().X), Finite(value.AsVector4().Y), Finite(value.AsVector4().Z), Finite(value.AsVector4().W) } : null; }
    private static double? Number(GodotObject? source, string property) => source == null || !GodotObject.IsInstanceValid(source) ? null : Number(source.Get(property));
    private static bool? Bool(GodotObject? source, string property) { if (source == null || !GodotObject.IsInstanceValid(source)) return null; Variant value = source.Get(property); return value.VariantType == Variant.Type.Bool ? value.AsBool() : null; }
    private static double? Number(Godot.Collections.Dictionary source, string property) => source.TryGetValue(property, out Variant v) ? Number(v) : null;
    private static bool? Bool(Godot.Collections.Dictionary source, string property) => source.TryGetValue(property, out Variant v) && v.VariantType == Variant.Type.Bool ? v.AsBool() : null;
    private static string? Text(Godot.Collections.Dictionary source, string property) => source.TryGetValue(property, out Variant v) && v.VariantType == Variant.Type.String ? v.AsString() : null;
    private static double? Number(Variant v) => v.VariantType is Variant.Type.Int or Variant.Type.Float ? Finite(v.AsDouble()) : null;
}
