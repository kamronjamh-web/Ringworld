using Godot;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace RingworldTechDemo.Voxel.LOD;

public static partial class RingWaterModel
{
    // Keys are weak, so cache entries disappear with their material and cannot
    // keep pooled or discarded sector resources alive.
    private sealed class OceanMaterialSnapshot
    {
        public bool Initialized;
        public bool Ready;
        public Texture2DArrayRD? NormalFoam;
        public Texture2DArrayRD? Displacement;
        public Vector3 AnchorWorld;
        public Vector2 CoverageCenter;
        public Vector3 AroundAxis;
        public Vector3 AcrossAxis;
        public Vector2 Period0;
        public Vector2 Period1;
        public Vector2 Period2;
        public Vector2 Period3;
        public Vector2 Phase0;
        public Vector2 Phase1;
        public Vector2 Phase2;
        public Vector2 Phase3;
        public int CascadeCount;
        public float DetailDistance;
        public float NormalStrength;
        public float FoamStrength;
        public float GeometryHeight;
        public float GeometryChoppiness;
        public float GeometryDistance;
        public float GeometryFilter;
        public float CrestLighting;
        public float Resolution;
        public int ShorelineFieldMode;
        public Texture2D? ShorelineField;
        public Vector2 ShorelineFieldCenter;
        public float ShorelineFieldCellMeters;
        public float ShorelineFadeDistance;
        public float ShorelineChopFadeDistance;
        public float ShorelineShallowDepth;
        public float DeepWaterWaveDepth;
    }

    private static readonly ConditionalWeakTable<ShaderMaterial, OceanMaterialSnapshot> OceanMaterialSnapshots = new();
    public static long OceanMaterialParameterPushes { get; private set; }
    public static long OceanMaterialSkippedRefreshes { get; private set; }
    public static int LastOceanMaterialRefreshPushes { get; private set; }

    public static Godot.Collections.Dictionary GetOceanMaterialPushDiagnostics() => new()
    {
        ["parameter_pushes"] = OceanMaterialParameterPushes,
        ["skipped_refreshes"] = OceanMaterialSkippedRefreshes,
        ["last_refresh_pushes"] = LastOceanMaterialRefreshPushes
    };

    /// <summary>Refresh only the nearby ocean bindings as the canonical frame moves.</summary>
    public static void RefreshOceanMaterials()
    {
        LastOceanMaterialRefreshPushes = 0;
        for (int index = Materials.Count - 1; index >= 0; index--)
        {
            MaterialBinding binding = Materials[index];
            if (!binding.Material.TryGetTarget(out ShaderMaterial? material)
                || !GodotObject.IsInstanceValid(material))
            {
                Materials.RemoveAt(index);
                continue;
            }
            int before = LastOceanMaterialRefreshPushes;
            ApplyOceanMaterial(material, binding.CoordinateScale);
            if (before == LastOceanMaterialRefreshPushes) OceanMaterialSkippedRefreshes++;
        }
    }

    /// <summary>
    /// Releases the Texture2DArrayRD wrappers currently sampled by proxy-ocean
    /// materials. This must run on the main thread before an FFT topology
    /// rebuild frees the backing RenderingDevice RIDs on the render thread.
    /// </summary>
    public static void RetireFftTextureBindings()
    {
        for (int index = Materials.Count - 1; index >= 0; index--)
        {
            MaterialBinding binding = Materials[index];
            if (!binding.Material.TryGetTarget(out ShaderMaterial? material)
                || !GodotObject.IsInstanceValid(material))
            {
                Materials.RemoveAt(index);
                continue;
            }

            if (!Mathf.IsEqualApprox(binding.CoordinateScale, 1f)
                || material.Shader?.ResourcePath != "res://materials/ring_ocean_proxy.gdshader") continue;

            OceanMaterialSnapshot snapshot = OceanMaterialSnapshots.GetOrCreateValue(material);
            SetOceanParameter(material, "fft_ready", false);
            SetOceanParameter(material, "fft_normal_foam", default(Variant));
            SetOceanParameter(material, "fft_displacement", default(Variant));
            snapshot.Ready = false;
            snapshot.NormalFoam = null;
            snapshot.Displacement = null;
            snapshot.Initialized = true;
        }
    }

    private static void ApplyOceanMaterial(ShaderMaterial material, float coordinateScale)
    {
        // Global and scaled-far water deliberately retain their opaque, inexpensive path.
        if (!Mathf.IsEqualApprox(coordinateScale, 1f)
            || material.Shader?.ResourcePath != "res://materials/ring_ocean_proxy.gdshader") return;

        OceanMaterialSnapshot snapshot = OceanMaterialSnapshots.GetOrCreateValue(material);
        RingWaterFftController? controller = RingWaterFftController.Instance;
        bool ready = controller != null && GodotObject.IsInstanceValid(controller)
            && controller.Enabled && controller.Ready && controller.NormalFoamTexture != null && controller.DisplacementTexture != null;
        bool previouslyReady = snapshot.Ready;
        SetIfChanged(material, "fft_ready", ready, ref snapshot.Ready, !snapshot.Initialized);
        if (!ready)
        {
            if (!snapshot.Initialized || previouslyReady || snapshot.NormalFoam != null)
                SetOceanParameter(material, "fft_normal_foam", default(Variant));
            if (!snapshot.Initialized || previouslyReady || snapshot.Displacement != null)
                SetOceanParameter(material, "fft_displacement", default(Variant));
            snapshot.NormalFoam = null;
            snapshot.Displacement = null;
            snapshot.Initialized = true;
            return;
        }

        // A disabled binding only contains cleared textures.  On the first
        // ready frame, force every value through even if its cached default
        // equals the desired value, because a shader resource may have a
        // non-default authored uniform.
        bool forceReady = !snapshot.Initialized || !previouslyReady;
        Texture2DArrayRD normalFoam = controller!.NormalFoamTexture!;
        Texture2DArrayRD displacement = controller.DisplacementTexture!;
        SetIfChanged(material, "fft_normal_foam", normalFoam, ref snapshot.NormalFoam, forceReady);
        SetIfChanged(material, "fft_displacement", displacement, ref snapshot.Displacement, forceReady);
        SetIfChanged(material, "fft_anchor_world", controller.CanonicalAnchorWorld, ref snapshot.AnchorWorld, forceReady);
        // Resolve after the controller updates its anchor, so a moving or
        // rebased observer cannot shift the coverage proof by one frame.
        SetIfChanged(material, "fft_ocean_coverage_center_canonical", RingTerrainProxySector.SharedFftCoverageShaderCenter, ref snapshot.CoverageCenter, forceReady);
        SetIfChanged(material, "fft_around_axis", controller.CanonicalAroundAxis, ref snapshot.AroundAxis, forceReady);
        SetIfChanged(material, "fft_across_axis", controller.CanonicalAcrossAxis, ref snapshot.AcrossAxis, forceReady);
        SetIfChanged(material, "fft_period_0", controller.Period0, ref snapshot.Period0, forceReady);
        SetIfChanged(material, "fft_period_1", controller.Period1, ref snapshot.Period1, forceReady);
        SetIfChanged(material, "fft_period_2", controller.Period2, ref snapshot.Period2, forceReady);
        SetIfChanged(material, "fft_period_3", controller.Period3, ref snapshot.Period3, forceReady);
        SetIfChanged(material, "fft_phase_0", controller.Phase0, ref snapshot.Phase0, forceReady);
        SetIfChanged(material, "fft_phase_1", controller.Phase1, ref snapshot.Phase1, forceReady);
        SetIfChanged(material, "fft_phase_2", controller.Phase2, ref snapshot.Phase2, forceReady);
        SetIfChanged(material, "fft_phase_3", controller.Phase3, ref snapshot.Phase3, forceReady);
        SetIfChanged(material, "fft_cascade_count", controller.ActiveCascadeCount, ref snapshot.CascadeCount, forceReady);
        SetIfChanged(material, "fft_detail_distance_meters", controller.DetailDistanceMeters, ref snapshot.DetailDistance, forceReady);
        SetIfChanged(material, "fft_normal_strength", controller.NormalStrength, ref snapshot.NormalStrength, forceReady);
        SetIfChanged(material, "fft_foam_strength", controller.FoamStrength, ref snapshot.FoamStrength, forceReady);
        SetIfChanged(material, "fft_geometry_height_scale", controller.GeometryHeightScale, ref snapshot.GeometryHeight, forceReady);
        SetIfChanged(material, "fft_geometry_choppiness", controller.GeometryChoppiness, ref snapshot.GeometryChoppiness, forceReady);
        SetIfChanged(material, "fft_geometry_distance_meters", controller.EffectiveGeometryDistanceMeters, ref snapshot.GeometryDistance, forceReady);
        SetIfChanged(material, "fft_geometry_filter_meters", controller.GeometryFilterMeters, ref snapshot.GeometryFilter, forceReady);
        SetIfChanged(material, "fft_crest_lighting_strength", controller.CrestLightingStrength, ref snapshot.CrestLighting, forceReady);
        SetIfChanged(material, "fft_resolution", (float)controller.ActiveResolution, ref snapshot.Resolution, forceReady);
        bool shorelineReady = RingTerrainProxySector.SharedFftShorelineFieldValid
            && RingTerrainProxySector.SharedFftShorelineFieldTexture is not null;
        int shorelineMode = !controller.RequireShorelineField ? 0 : shorelineReady ? 2 : 1;
        SetIfChanged(material, "fft_shoreline_field_mode", shorelineMode, ref snapshot.ShorelineFieldMode, forceReady);
        Texture2D? shorelineField = shorelineReady ? RingTerrainProxySector.SharedFftShorelineFieldTexture : null;
        SetIfChanged(material, "fft_shoreline_field", shorelineField, ref snapshot.ShorelineField, forceReady);
        SetIfChanged(material, "fft_shoreline_field_center_canonical", RingTerrainProxySector.SharedFftShorelineFieldShaderCenter, ref snapshot.ShorelineFieldCenter, forceReady);
        SetIfChanged(material, "fft_shoreline_field_cell_meters", RingTerrainProxySector.SharedFftShorelineFieldCellMeters, ref snapshot.ShorelineFieldCellMeters, forceReady);
        SetIfChanged(material, "fft_shoreline_fade_distance_meters", controller.ShorelineFadeDistanceMeters, ref snapshot.ShorelineFadeDistance, forceReady);
        SetIfChanged(material, "fft_shoreline_chop_fade_distance_meters", controller.ShorelineChopFadeDistanceMeters, ref snapshot.ShorelineChopFadeDistance, forceReady);
        SetIfChanged(material, "fft_shoreline_shallow_depth_meters", controller.ShorelineShallowDepthMeters, ref snapshot.ShorelineShallowDepth, forceReady);
        SetIfChanged(material, "fft_deep_water_wave_depth_meters", controller.DeepWaterWaveDepthMeters, ref snapshot.DeepWaterWaveDepth, forceReady);
        snapshot.Initialized = true;
    }

    private static void SetOceanParameter(ShaderMaterial material, StringName name, Variant value)
    {
        material.SetShaderParameter(name, value);
        OceanMaterialParameterPushes++;
        LastOceanMaterialRefreshPushes++;
    }

    private static void SetIfChanged<T>(ShaderMaterial material, StringName name, T value, ref T previous, bool force)
    {
        if (force || !EqualityComparer<T>.Default.Equals(previous, value))
            SetOceanParameter(material, name, Variant.From(value));
        previous = value;
    }
}
