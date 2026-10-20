using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Atmosphere;

/// <summary>
/// Registry and per-frame ray-frame feed for POI architecture haze. POI architecture (emitter / spire / reactor / Weather Lung) renders
/// with <c>ring_poi_architecture*.gdshader</c>, which evaluates the ring haze per fragment from the camera ray frame
/// (<see cref="RingAtmosphereRayFrame"/>). Static parameters come from <see cref="RingAtmosphereModel.ConfigureMaterial"/> (registered once
/// per material); the nine frame uniforms are written here, per field, only when they change (a flying camera changes about two).
/// The POI systems register what they instantiate (<see cref="RegisterTree"/>, <see cref="RegisterMaterial"/>) and tick from <c>_Process</c>.
/// </summary>
public static class RingPoiHaze
{
    private static readonly StringName ValidName = "ring_atmosphere_ray_frame_valid";
    private static readonly StringName CameraWorldName = "ring_atmosphere_camera_world_position";
    private static readonly StringName CameraHeightName = "ring_atmosphere_camera_height_meters";
    private static readonly StringName OutwardName = "ring_atmosphere_outward_world";
    private static readonly StringName AxisName = "ring_atmosphere_axis_world";
    private static readonly StringName SidewallNegativeName = "ring_atmosphere_sidewall_negative_meters";
    private static readonly StringName SidewallPositiveName = "ring_atmosphere_sidewall_positive_meters";
    private static readonly StringName CoordinateScaleName = "ring_atmosphere_coordinate_scale";
    private static readonly StringName ToSunName = "ring_atmosphere_to_sun";

    private sealed class Entry
    {
        public Entry(ShaderMaterial material, bool poiShader) { Material = new WeakReference<ShaderMaterial>(material); IsPoiShader = poiShader; }
        public readonly WeakReference<ShaderMaterial> Material;
        public readonly bool IsPoiShader;
        public bool Initialized;
        public bool Valid;
        public RingAtmosphereRayFrame Frame;
    }

    private static readonly List<Entry> Entries = new();
    private static readonly HashSet<ulong> Known = new();
    private static bool _enabled = true;
    private static bool _cloudShadow;
    private static ulong _lastTickFrame = ulong.MaxValue;

    /// <summary>"POI haze" setting. Drives <c>poi_haze_enabled</c> on POI-shader materials only (stand-ins and bore keep their own haze).</summary>
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            PushToggle(PoiMaterialParams.EnabledName, value);
        }
    }

    /// <summary>"POI cloud shadows" setting (default off). Drives <c>poi_cloud_shadow_enabled</c> and registers the materials with the cloud-shadow projector.</summary>
    public static bool CloudShadowEnabled
    {
        get => _cloudShadow;
        set
        {
            if (_cloudShadow == value) return;
            _cloudShadow = value;
            PushToggle(PoiMaterialParams.CloudShadowName, value);
            if (value) ForEachPoiMaterial(material => RingCloudShadowProjector.Register(material));
        }
    }

    public static int RegisteredCount
    {
        get { Prune(); return Entries.Count; }
    }

    /// <summary>Number of distinct registered POI-shader materials (excludes stand-in / bore shaders).</summary>
    public static int RegisteredPoiShaderCount
    {
        get
        {
            Prune();
            int count = 0;
            foreach (Entry entry in Entries) if (entry.IsPoiShader) count++;
            return count;
        }
    }

    public static List<ShaderMaterial> Snapshot()
    {
        Prune();
        var list = new List<ShaderMaterial>(Entries.Count);
        foreach (Entry entry in Entries) if (entry.Material.TryGetTarget(out ShaderMaterial? m)) list.Add(m);
        return list;
    }

    /// <summary>Test hook: forgets every registration.</summary>
    public static void Reset()
    {
        Entries.Clear();
        Known.Clear();
        _lastTickFrame = ulong.MaxValue;
    }

    /// <summary>Registers every POI-shader material under <paramref name="root"/> (material overrides, surface overrides, mesh surface materials).</summary>
    public static int RegisterTree(Node? root, double ringRadiusMeters)
    {
        if (root == null || !GodotObject.IsInstanceValid(root)) return 0;
        int added = 0;
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            Node node = stack.Pop();
            foreach (Node child in node.GetChildren()) stack.Push(child);
            if (node is GeometryInstance3D geometry && geometry.MaterialOverride is ShaderMaterial overrideMaterial
                && PoiMaterialParams.IsPoiShader(overrideMaterial) && Add(overrideMaterial, ringRadiusMeters)) added++;
            Mesh? mesh = node switch
            {
                MeshInstance3D meshInstance => meshInstance.Mesh,
                MultiMeshInstance3D multiMeshInstance => multiMeshInstance.Multimesh?.Mesh,
                _ => null
            };
            if (mesh == null) continue;
            for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
            {
                if (node is MeshInstance3D mi && mi.GetSurfaceOverrideMaterial(surface) is ShaderMaterial surfaceOverride
                    && PoiMaterialParams.IsPoiShader(surfaceOverride) && Add(surfaceOverride, ringRadiusMeters)) added++;
                if (mesh.SurfaceGetMaterial(surface) is ShaderMaterial meshMaterial
                    && PoiMaterialParams.IsPoiShader(meshMaterial) && Add(meshMaterial, ringRadiusMeters)) added++;
            }
        }
        return added;
    }

    /// <summary>Registers any material that includes ring_atmosphere (stand-in and bore duplicates). Returns true when newly added.</summary>
    public static bool RegisterMaterial(ShaderMaterial? material, double ringRadiusMeters)
        => material != null && GodotObject.IsInstanceValid(material) && Add(material, ringRadiusMeters);

    private static bool Add(ShaderMaterial material, double ringRadiusMeters)
    {
        ulong id = material.GetInstanceId();
        if (!Known.Add(id)) return false;
        bool poi = PoiMaterialParams.IsPoiShader(material);
        Entries.Add(new Entry(material, poi));
        RingAtmosphereModel.ConfigureMaterial(material, ringRadiusMeters);
        if (poi)
        {
            // Defaults are poi_haze_enabled = true, cloud shadow off: a registration only writes when the setting differs.
            if (!_enabled) material.SetShaderParameter(PoiMaterialParams.EnabledName, false);
            if (_cloudShadow)
            {
                material.SetShaderParameter(PoiMaterialParams.CloudShadowName, true);
                RingCloudShadowProjector.Register(material);
            }
        }
        return true;
    }

    private static void PushToggle(StringName name, bool value)
    {
        ForEachPoiMaterial(material => material.SetShaderParameter(name, value));
    }

    private static void ForEachPoiMaterial(Action<ShaderMaterial> action)
    {
        Prune();
        foreach (Entry entry in Entries)
            if (entry.IsPoiShader && entry.Material.TryGetTarget(out ShaderMaterial? material)) action(material);
    }

    private static void Prune()
    {
        for (int index = Entries.Count - 1; index >= 0; index--)
        {
            if (Entries[index].Material.TryGetTarget(out ShaderMaterial? material) && GodotObject.IsInstanceValid(material)) continue;
            Entries.RemoveAt(index);
        }
        if (Known.Count > Entries.Count * 2 + 64) RebuildKnown();
    }

    private static void RebuildKnown()
    {
        Known.Clear();
        foreach (Entry entry in Entries) if (entry.Material.TryGetTarget(out ShaderMaterial? material)) Known.Add(material.GetInstanceId());
    }

    /// <summary>
    /// Convenience for the POI hosts: builds the frame from the host viewport camera and the star light, then ticks.
    /// A null grid or camera publishes the Invalid frame (the shader then returns identity haze).
    /// </summary>
    public static void Tick(Node host, VoxelChunkGridManager? grid, DirectionalLight3D? sun, bool force = false)
    {
        if (Entries.Count == 0) return;
        if (!force && Engine.GetProcessFrames() == _lastTickFrame) return;
        Camera3D? camera = host.GetViewport()?.GetCamera3D();
        Tick(grid, camera, sun?.GlobalTransform.Basis.Z.Normalized() ?? Vector3.Up, force);
    }

    public static void Tick(VoxelChunkGridManager? grid, Camera3D? camera, Vector3 toSun, bool force = false)
    {
        ulong frameIndex = Engine.GetProcessFrames();
        if (!force && frameIndex == _lastTickFrame) return;
        _lastTickFrame = frameIndex;
        RingAtmosphereRayFrame frame = grid == null || camera == null || !GodotObject.IsInstanceValid(grid) || !GodotObject.IsInstanceValid(camera)
            ? RingAtmosphereRayFrame.Invalid
            : RingAtmosphereRayFrame.FromMainCamera(grid, camera, toSun);
        ApplyFrame(frame);
    }

    /// <summary>Publishes <paramref name="frame"/> to every registered material (per-field gated). For tests and standalone scenes.</summary>
    public static void ApplyFrame(in RingAtmosphereRayFrame frame)
    {
        Prune();
        bool valid = IsPublishable(frame);
        foreach (Entry entry in Entries)
        {
            if (entry.IsPoiShader && !_enabled) continue;
            if (!entry.Material.TryGetTarget(out ShaderMaterial? material)) continue;
            ApplyEntry(entry, material, frame, valid);
        }
    }

    private static void ApplyEntry(Entry entry, ShaderMaterial material, in RingAtmosphereRayFrame frame, bool valid)
    {
        if (entry.Initialized && entry.Frame == frame) return;
        bool full = !entry.Initialized || !entry.Valid;
        RingAtmosphereRayFrame previous = entry.Frame;
        int writes = 0;
        void Set(StringName name, Variant value) { material.SetShaderParameter(name, value); writes++; }
        if (!entry.Initialized || entry.Valid != valid) Set(ValidName, valid);
        if (valid)
        {
            if (full || !previous.CameraWorld.Equals(frame.CameraWorld)) Set(CameraWorldName, frame.CameraWorld);
            if (full || !previous.CameraHeightMeters.Equals(frame.CameraHeightMeters)) Set(CameraHeightName, frame.CameraHeightMeters);
            if (full || !previous.OutwardWorld.Equals(frame.OutwardWorld)) Set(OutwardName, frame.OutwardWorld);
            if (full || !previous.AxisWorld.Equals(frame.AxisWorld)) Set(AxisName, frame.AxisWorld);
            if (full || !previous.SidewallNegativeMeters.Equals(frame.SidewallNegativeMeters)) Set(SidewallNegativeName, frame.SidewallNegativeMeters);
            if (full || !previous.SidewallPositiveMeters.Equals(frame.SidewallPositiveMeters)) Set(SidewallPositiveName, frame.SidewallPositiveMeters);
            if (full || !previous.CoordinateScale.Equals(frame.CoordinateScale)) Set(CoordinateScaleName, frame.CoordinateScale);
            if (full || !previous.ToSun.Equals(frame.ToSun)) Set(ToSunName, frame.ToSun);
        }
        entry.Frame = frame;
        entry.Valid = valid;
        entry.Initialized = true;
        if (writes > 0) RingMaterialWriteCounters.Record(RingMaterialWriteCounters.Site.PoiAtmosphere, writes);
    }

    private static bool IsPublishable(in RingAtmosphereRayFrame frame) => frame.Valid
        && frame.CameraWorld.IsFinite() && float.IsFinite(frame.CameraHeightMeters)
        && frame.OutwardWorld.IsFinite() && frame.OutwardWorld.LengthSquared() > 0.000001f
        && frame.AxisWorld.IsFinite() && frame.AxisWorld.LengthSquared() > 0.000001f
        && float.IsFinite(frame.SidewallNegativeMeters) && float.IsFinite(frame.SidewallPositiveMeters)
        && float.IsFinite(frame.CoordinateScale) && frame.CoordinateScale > 0.0f
        && frame.ToSun.IsFinite() && frame.ToSun.LengthSquared() > 0.000001f;
}
