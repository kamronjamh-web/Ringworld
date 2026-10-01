using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo;

/// <summary>Test-only reflection adapter for the private far-cloud render state.
/// It deliberately adds no diagnostic surface to the runtime renderer.</summary>
public partial class FarCloudDetailProbe : RefCounted
{
    private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Dictionary<ulong, WeatherFixtureState> _weatherFixtureStates = new();

    public Godot.Collections.Array<ShaderMaterial> GetClipmapMaterials(Node node)
    {
        var result = new Godot.Collections.Array<ShaderMaterial>();
        if (node is not RingScaledFarViewport far) return result;
        if (typeof(RingScaledFarViewport).GetField("_farCloudClipmapMaterials", PrivateInstance)?.GetValue(far) is not ShaderMaterial?[] materials)
            return result;
        foreach (ShaderMaterial? material in materials)
            if (material != null) result.Add(material);
        return result;
    }

    public Godot.Collections.Array<Mesh> GetClipmapMeshes(Node node)
    {
        var result = new Godot.Collections.Array<Mesh>();
        if (node is not RingScaledFarViewport far) return result;
        if (typeof(RingScaledFarViewport).GetField("_farCloudClipmapLayers", PrivateInstance)?.GetValue(far) is not MeshInstance3D?[] layers)
            return result;
        foreach (MeshInstance3D? layer in layers)
            if (layer?.Mesh != null) result.Add(layer.Mesh);
        return result;
    }

    public ShaderMaterial? GetShellCloudMaterial(Node node)
    {
        if (node is not RingScaledFarViewport far) return null;
        return typeof(RingScaledFarViewport).GetField("_cloudMaterial", PrivateInstance)?.GetValue(far) as ShaderMaterial;
    }

    /// <summary>Returns bridge-owned effective values without making the runtime
    /// diagnostics API part of the Godot property surface.</summary>
    public Vector3 GetEffectiveCloudHandoff(Node node)
    {
        return node is RingSunshineCloudBridge bridge
            ? bridge.GetEffectiveCloudHandoff()
            : Vector3.Zero;
    }

    /// <summary>Applies the bridge's existing private quality-to-resource path
    /// for fixtures without expanding the production Godot API.</summary>
    public bool ApplyBridgeTemporalQuality(Node node)
    {
        if (node is not RingSunshineCloudBridge bridge) return false;
        MethodInfo? apply = typeof(RingSunshineCloudBridge).GetMethod(
            "ApplyTuningToResource", PrivateInstance);
        if (apply == null) return false;
        apply.Invoke(bridge, new object[] { false, false });
        return true;
    }

    /// <summary>Reads the effective clipmap range after its owning renderer has
    /// published it. This is deliberately material-facing because Sunshine and
    /// mesh fallback have different ownership while suspended.</summary>
    public Vector2 GetClipmapHandoff(Node node)
    {
        var materials = GetClipmapMaterials(node);
        if (materials.Count == 0) return Vector2.Zero;
        ShaderMaterial material = materials[0];
        return new Vector2(
            material.GetShaderParameter("handoff_start_meters").AsSingle(),
            material.GetShaderParameter("handoff_end_meters").AsSingle());
    }

    public float GetWeatherFallbackTransitionRadius(Node node)
    {
        return node is RingVolumetricCloudLayer weather
            ? weather.TransitionCloudRadiusMeters
            : 0.0f;
    }

    /// <summary>Fixture-only fast-travel state switch. It exercises the same
    /// far-owner synchronization path without requiring a real 50 km/s world
    /// jump or mutating gameplay save data.</summary>
    public bool SetWeatherFastTravelFallbackFixture(Node node)
    {
        if (node is not RingVolumetricCloudLayer weather) return false;
        Type type = typeof(RingVolumetricCloudLayer);
        FieldInfo? fastTravel = type.GetField(
            "<FastTravelStreamingActive>k__BackingField", PrivateInstance);
        FieldInfo? archetypesReady = type.GetField("_archetypesReady", PrivateInstance);
        FieldInfo? runtimeEnabled = type.GetField("_runtimeEnabled", PrivateInstance);
        FieldInfo? localMeshesVisible = type.GetField("_localMeshCloudsVisible", PrivateInstance);
        MethodInfo? sync = type.GetMethod(
            "SyncFarCloudOwnership", PrivateInstance);
        if (fastTravel == null || archetypesReady == null || runtimeEnabled == null || localMeshesVisible == null || sync == null)
            return false;
        ulong id = weather.GetInstanceId();
        if (!_weatherFixtureStates.ContainsKey(id))
        {
            _weatherFixtureStates[id] = new WeatherFixtureState(
                (bool)fastTravel.GetValue(weather)!, (bool)archetypesReady.GetValue(weather)!,
                (bool)runtimeEnabled.GetValue(weather)!, (bool)localMeshesVisible.GetValue(weather)!);
        }
        // Match the real high-speed sequence: Sunshine first relinquishes
        // ownership, then the weather layer evaluates the cached mesh state.
        weather.SetExternalLocalVolumetricCloudsState(false, false, 0.0f, 1.0f);
        fastTravel.SetValue(weather, true);
        archetypesReady.SetValue(weather, true);
        runtimeEnabled.SetValue(weather, true);
        localMeshesVisible.SetValue(weather, true);
        sync.Invoke(weather, null);
        return true;
    }

    public bool RestoreWeatherFixture(Node node, float sunshineStartMeters, float sunshineEndMeters)
    {
        if (node is not RingVolumetricCloudLayer weather) return false;
        ulong id = weather.GetInstanceId();
        if (!_weatherFixtureStates.Remove(id, out WeatherFixtureState saved)) return false;
        Type type = typeof(RingVolumetricCloudLayer);
        type.GetField("<FastTravelStreamingActive>k__BackingField", PrivateInstance)?.SetValue(weather, saved.FastTravel);
        type.GetField("_archetypesReady", PrivateInstance)?.SetValue(weather, saved.ArchetypesReady);
        type.GetField("_runtimeEnabled", PrivateInstance)?.SetValue(weather, saved.RuntimeEnabled);
        type.GetField("_localMeshCloudsVisible", PrivateInstance)?.SetValue(weather, saved.LocalMeshesVisible);
        weather.SetExternalLocalVolumetricCloudsState(true, true, sunshineStartMeters, sunshineEndMeters);
        return true;
    }

    public bool SetWeatherFallbackFixtureSlow(Node node)
    {
        if (node is not RingVolumetricCloudLayer weather
            || !_weatherFixtureStates.ContainsKey(weather.GetInstanceId())) return false;
        Type type = typeof(RingVolumetricCloudLayer);
        FieldInfo? fastTravel = type.GetField("<FastTravelStreamingActive>k__BackingField", PrivateInstance);
        MethodInfo? sync = type.GetMethod("SyncFarCloudOwnership", PrivateInstance);
        if (fastTravel == null || sync == null) return false;
        fastTravel.SetValue(weather, false);
        sync.Invoke(weather, null);
        return true;
    }

    private readonly record struct WeatherFixtureState(
        bool FastTravel, bool ArchetypesReady, bool RuntimeEnabled, bool LocalMeshesVisible);

    public bool MaterialsShareSunshineResources(Node node, Resource clouds)
    {
        foreach (ShaderMaterial material in GetClipmapMaterials(node))
        {
            if (!SameResource(material, "far_density_extra_large_noise_patterns", clouds.Get("extra_large_noise_patterns"))
                || !SameResource(material, "far_density_height_gradient", clouds.Get("height_gradient"))
                || !SameResource(material, "far_density_large_scale_noise", clouds.Get("large_scale_noise"))
                || !SameResource(material, "far_density_medium_scale_noise", clouds.Get("medium_scale_noise"))
                || !SameResource(material, "far_density_small_scale_noise", clouds.Get("small_scale_noise")))
                return false;
        }
        return GetClipmapMaterials(node).Count > 0;
    }

    public bool MaterialsMatchSourceOffsets(Node node, Resource clouds)
    {
        foreach (ShaderMaterial material in GetClipmapMaterials(node))
        {
            if (material.GetShaderParameter("far_density_extra_large_position").AsVector3() != clouds.Get("extra_large_scale_clouds_position").AsVector3()
                || material.GetShaderParameter("far_density_large_position").AsVector3() != clouds.Get("large_scale_clouds_position").AsVector3()
                || material.GetShaderParameter("far_density_medium_position").AsVector3() != clouds.Get("medium_scale_clouds_position").AsVector3()
                || material.GetShaderParameter("far_density_detail_position").AsVector3() != clouds.Get("detail_clouds_position").AsVector3())
                return false;
        }
        return GetClipmapMaterials(node).Count > 0;
    }

    public bool AllMeshesChanged(Godot.Collections.Array<Mesh> before, Godot.Collections.Array<Mesh> after)
    {
        if (before.Count == 0 || before.Count != after.Count) return false;
        for (int i = 0; i < before.Count; i++)
            if (ReferenceEquals(before[i], after[i])) return false;
        return true;
    }

    public bool AllMeshesUnchanged(Godot.Collections.Array<Mesh> before, Godot.Collections.Array<Mesh> after)
    {
        if (before.Count == 0 || before.Count != after.Count) return false;
        for (int i = 0; i < before.Count; i++)
            if (!ReferenceEquals(before[i], after[i])) return false;
        return true;
    }

    public bool CurvedCanopyMatchesRing(Node node)
    {
        if (node is not RingScaledFarViewport far) return false;
        double radius = (double)(typeof(RingScaledFarViewport).GetField("_radiusMeters", PrivateInstance)?.GetValue(far) ?? 0.0);
        float scale = far.CoordinateScale;
        if (radius <= 1.0 || scale <= 0.0f) return false;
        MethodInfo? height = typeof(RingScaledFarViewport).GetMethod("GetFarCloudClipmapHeightMeters", PrivateInstance);
        if (height == null) return false;
        int layer = 0;
        foreach (Mesh mesh in GetClipmapMeshes(far))
        {
            Vector3[] faces = mesh.GetFaces();
            if (faces.Length == 0) return false;
            Vector3 edge = Vector3.Zero;
            foreach (Vector3 point in faces)
                if (Mathf.Abs(point.X) > Mathf.Abs(edge.X)) edge = point;
            float altitude = (float)(height.Invoke(far, new object[] { layer++ }) ?? float.NaN);
            float curvedRadius = (float)((radius - altitude) * scale);
            float expectedRise = curvedRadius - Mathf.Sqrt(Mathf.Max(0.0f, curvedRadius * curvedRadius - edge.X * edge.X));
            if (expectedRise <= 0.00001f || Mathf.Abs(edge.Y - expectedRise) > Mathf.Max(0.002f, expectedRise * 0.12f))
                return false;
        }
        return true;
    }

    public bool CanopyHeightsStayInSourceBand(Node node)
    {
        if (node is not RingScaledFarViewport far) return false;
        Vector2 band = (Vector2)(typeof(RingScaledFarViewport).GetField("_sunshineBackgroundAltitudeMeters", PrivateInstance)?.GetValue(far) ?? Vector2.Zero);
        MethodInfo? height = typeof(RingScaledFarViewport).GetMethod("GetFarCloudClipmapHeightMeters", PrivateInstance);
        if (height == null || band.Y <= band.X) return false;
        for (int layer = 0; layer < 3; layer++)
        {
            float value = (float)(height.Invoke(far, new object[] { layer }) ?? float.NaN);
            if (!float.IsFinite(value) || value < band.X || value > band.Y) return false;
        }
        return true;
    }

    private static bool SameResource(ShaderMaterial material, string uniform, Variant source)
    {
        Variant published = material.GetShaderParameter(uniform);
        return published.VariantType == Variant.Type.Object && source.VariantType == Variant.Type.Object
            && ReferenceEquals(published.AsGodotObject(), source.AsGodotObject());
    }
}
