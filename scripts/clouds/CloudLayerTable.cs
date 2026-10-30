using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo;

public enum CloudLayerRenderMode
{
    Volumetric = 0,
    /// <summary>Analytic thin shell at the layer's base altitude (cirrus); a later flight work order may switch it to Volumetric.</summary>
    Shell = 1,
}

/// <summary>When a genus pulls the low layer's base down to the weather base.</summary>
public enum CloudLowersBase
{
    No = 0,
    Always = 1,
    /// <summary>Major or exceptional front, or a cloud bank.</summary>
    FrontOrBank = 2,
    /// <summary>Major or exceptional front only.</summary>
    FrontOnly = 3,
}

/// <summary>How the front sequence (<see cref="RingCloudLayers"/>) shapes a genus along the downwind axis.</summary>
public enum CloudFrontType
{
    None = 0,
    /// <summary>Cirrus then cirrostratus ahead of the core, altostratus thickening, nimbostratus at the core, Sc behind.</summary>
    Warm = 1,
    /// <summary>A narrow band about the core followed by quick clearing.</summary>
    Cold = 2,
}

/// <summary>
/// Data-driven cloud layer table (docs/storm-revamp-s6-cloud-layers-design.md sections 2 and 8): the altitude layers (4 slots, 3 used),
/// the genus x layer matrix and the parameters of the macro-map layer build. <see cref="RingCloudLayers"/> evaluates it on the CPU;
/// the same values are uploaded to the layers UBO that RingMacroWeatherMap.glsl reads. A new layer or genus is a row here.
/// </summary>
[GlobalClass]
public partial class CloudLayerTable : Resource
{
    public const string FactoryPath = "res://resources/clouds/cloud_layer_table.tres";
    public const int MaximumLayers = 4;
    public const int LowIndex = 0;
    public const int MidIndex = 1;
    public const int HighIndex = 2;

    [Export] public CloudLayerDefinition[] Layers { get; set; } = Array.Empty<CloudLayerDefinition>();
    [Export] public CloudGenusRow[] Genera { get; set; } = Array.Empty<CloudGenusRow>();

    /// <summary>Fair-weather mid / high coverage as a fraction of the cluster coverage at their own noise salts (sparse Ac, common Ci).</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float MidFairScale { get; set; } = 0.35f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float HighFairScale { get; set; } = 0.5f;
    /// <summary>Cluster-term noise salts (small, large) for the mid and high layers; the low layer uses the background pair 30 / 40.</summary>
    [Export] public Vector2I MidClusterSalts { get; set; } = new(231, 232);
    [Export] public Vector2I HighClusterSalts { get; set; } = new(233, 234);
    /// <summary>Daytime cumulus: low fair-weather coverage is multiplied by lerp(1, floor + (1 - floor) * d, strength); d peaks PeakHoursAfterNoon after local noon.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float DiurnalStrength { get; set; } = 1.0f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float DiurnalFloor { get; set; } = 0.35f;
    [Export(PropertyHint.Range, "0,12,0.1")] public float DiurnalPeakHoursAfterNoon { get; set; } = 3.0f;
    /// <summary>Storm support (ellipse multiple) shared by the ray bands, sub-mask, shadow band and bake (used from WO3a).</summary>
    [Export(PropertyHint.Range, "1,2,0.01")] public float StormSupportFactor { get; set; } = 1.45f;
    /// <summary>A system lowers the base only above this density; weatherness reaches its full value at x + y.</summary>
    [Export] public Vector2 WeathernessDensityRamp { get; set; } = new(0.2f, 0.4f);


    /// <summary>
    /// Player tuning (grave menu), pushed by RingSunshineCloudBridge; not part of the factory table. Amount biases (-1..1) lift (+) or thin (-) the
    /// fair-weather coverage of the low / mid / high layer (0 = unchanged, bit-identical); DaytimeCumulus (0..1) scales DiurnalStrength.
    /// Weather systems keep their own coverage. Uploaded through spare layers-block slots, so the block size is unchanged.
    /// </summary>
    public float LowAmountBias { get; set; }
    public float MidAmountBias { get; set; }
    public float HighAmountBias { get; set; }
    public float DaytimeCumulus { get; set; } = 1.0f;
    /// <summary>The diurnal strength the build and the shader use: the table value times the menu's daytime cumulus.</summary>
    public float EffectiveDiurnalStrength => DiurnalStrength * DaytimeCumulus;

    private static CloudLayerTable? _shared;
    private Dictionary<CloudFormationProfile, CloudGenusRow>? _byProfile;

    /// <summary>The factory table (loaded once); an empty table, with an error, if the resource cannot be loaded.</summary>
    public static CloudLayerTable Shared
    {
        get
        {
            if (_shared != null && GodotObject.IsInstanceValid(_shared)) return _shared;
            _shared = ResourceLoader.Load<CloudLayerTable>(FactoryPath);
            if (_shared == null)
            {
                GD.PushError("Cloud layer table missing: " + FactoryPath);
                _shared = new CloudLayerTable();
            }
            return _shared;
        }
    }

    public CloudGenusRow? Genus(CloudFormationProfile profile)
    {
        if (_byProfile == null)
        {
            _byProfile = new Dictionary<CloudFormationProfile, CloudGenusRow>();
            foreach (CloudGenusRow row in Genera)
                if (row != null) _byProfile[row.Profile] = row;
        }
        return _byProfile.TryGetValue(profile, out CloudGenusRow? found) ? found : null;
    }

    /// <summary>True when a system of this genus and kind carries RING_WS_FLAG_LOWERS_BASE.</summary>
    public bool LowersBase(CloudFormationProfile profile, RingVolumetricCloudLayer.WeatherSystemKind kind)
    {
        CloudGenusRow? row = Genus(profile);
        if (row == null) return false;
        bool front = RingVolumetricCloudLayer.IsFrontKind(kind);
        return row.LowersBase switch
        {
            CloudLowersBase.Always => true,
            CloudLowersBase.FrontOrBank => front || kind == RingVolumetricCloudLayer.WeatherSystemKind.CloudBank,
            CloudLowersBase.FrontOnly => front,
            _ => false,
        };
    }

    /// <summary>Structural validity: 1..4 layers with ordered fair bases, positive thickness and every altitude within the ceiling.</summary>
    public bool Validate(out string error)
    {
        error = "";
        if (Layers.Length < 1 || Layers.Length > MaximumLayers) { error = "layer count " + Layers.Length; return false; }
        float previousBase = -1.0f;
        for (int i = 0; i < Layers.Length; i++)
        {
            CloudLayerDefinition layer = Layers[i];
            if (layer == null) { error = "layer " + i + " is null"; return false; }
            if (layer.FairBaseMeters < previousBase || layer.WeatherBaseMeters > layer.FairBaseMeters)
            { error = "layer " + layer.LayerName + " bases are not ordered"; return false; }
            if (layer.TopCapMeters > RingCloudLayers.CeilingMeters || layer.TopCapMeters <= layer.FairBaseMeters)
            { error = "layer " + layer.LayerName + " top cap"; return false; }
            if (layer.ConvectiveThicknessMeters.X <= 0.0f || layer.StratiformThicknessMeters.X <= 0.0f)
            { error = "layer " + layer.LayerName + " thickness"; return false; }
            previousBase = layer.FairBaseMeters;
        }
        return true;
    }
}
