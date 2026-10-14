using Godot;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingScaledFarViewport
{
    /// <summary>
    /// Binds one published terrain solar-visibility field to every owned cloud
    /// receiver. The field binder owns validity and unknown-sample fallback;
    /// this owner supplies physical receiver heights rather than density probes.
    /// </summary>
    private static readonly StringName TerrainCloudReceiverHeightName = "terrain_cloud_receiver_height_meters";
    private readonly LastUniformValue<float> _cloudReceiverHeightApplied = new(), _clipmapReceiverHeightApplied = new(),
        _canopyReceiverHeightApplied = new(), _volumeReceiverHeightApplied = new();

    private void BindTerrainCloudShadowReceiverMaterials()
    {
        RingTerrainCloudSunVisibility? field = RingTerrainCloudSunVisibility.Instance;
        if (field == null) return;
        float minimum = Mathf.Min(CloudAltitudeMeters, LocalCloudBaseMeters);
        float maximum = Mathf.Max(CloudAltitudeMeters, LocalCloudTopMeters);
        foreach (CloudPhysicalDefinition cloud in CloudPhysicalCatalog.All)
        {
            minimum = Mathf.Min(minimum, cloud.MinimumAltitudeMeters);
            maximum = Mathf.Max(maximum, cloud.MaximumAltitudeMeters);
        }
        for (int layer = 0; layer < _farCloudClipmapMaterials.Length; layer++)
        {
            float height = GetFarCloudClipmapHeightMeters(layer);
            minimum = Mathf.Min(minimum, height);
            maximum = Mathf.Max(maximum, height);
        }
        field.RegisterRequiredAltitudeRange("far_clouds", minimum, maximum);
        bool visible = Enabled && ((_scaledCloudShell?.Visible ?? false)
            || (_localCloudCanopy?.Visible ?? false) || (_scaledCloudVolume?.Visible ?? false)
            || CloudMaskActive);
        foreach (MeshInstance3D? layer in _farCloudClipmapLayers) visible |= Enabled && (layer?.Visible ?? false);
        field.SetReceiverDemand("far_clouds", visible);
        if (_cloudMaterial != null)
        {
            _cloudReceiverHeightApplied.Set(_cloudMaterial, TerrainCloudReceiverHeightName, CloudAltitudeMeters);
            field.BindToMaterial(_cloudMaterial);
        }

        for (int layer = 0; layer < _farCloudClipmapMaterials.Length; layer++)
        {
            ShaderMaterial? material = _farCloudClipmapMaterials[layer];
            if (material == null) continue;
            _clipmapReceiverHeightApplied.Set(material, TerrainCloudReceiverHeightName, GetFarCloudClipmapHeightMeters(layer));
            field.BindToMaterial(material);
        }

        foreach (ShaderMaterial? material in _cloudMaskMaterials)
            if (material != null) field.BindToMaterial(material);

        if (_localCloudCanopyMaterial != null)
        {
            _canopyReceiverHeightApplied.Set(_localCloudCanopyMaterial, TerrainCloudReceiverHeightName,
                Mathf.Lerp(LocalCloudBaseMeters, LocalCloudTopMeters, 0.62f));
            field.BindToMaterial(_localCloudCanopyMaterial);
        }

        if (_scaledCloudVolumeMaterial != null)
        {
            _volumeReceiverHeightApplied.Set(_scaledCloudVolumeMaterial, TerrainCloudReceiverHeightName,
                (LocalCloudBaseMeters + LocalCloudTopMeters) * 0.5f);
            field.BindToMaterial(_scaledCloudVolumeMaterial);
        }
    }
}
