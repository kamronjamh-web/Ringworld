using Godot;

namespace RingworldTechDemo;

/// <summary>One altitude layer of <see cref="CloudLayerTable"/>. All ranges are absolute altitudes in metres.</summary>
[GlobalClass]
public partial class CloudLayerDefinition : Resource
{
    [Export] public string LayerName { get; set; } = "";
    [Export] public CloudLayerRenderMode RenderMode { get; set; } = CloudLayerRenderMode.Volumetric;
    /// <summary>Base in fair weather (weatherness 0).</summary>
    [Export(PropertyHint.Range, "0,18000,10")] public float FairBaseMeters { get; set; } = 2000.0f;
    /// <summary>Base under a base-lowering system (weatherness 1).</summary>
    [Export(PropertyHint.Range, "0,18000,10")] public float WeatherBaseMeters { get; set; } = 2000.0f;
    /// <summary>Thickness at coverage 0 and 1 for a cellular (low stratiformity) layer; linear in coverage.</summary>
    [Export] public Vector2 ConvectiveThicknessMeters { get; set; } = new(800.0f, 2800.0f);
    /// <summary>Thickness at coverage 0 and 1 for a sheet (stratiformity 1).</summary>
    [Export] public Vector2 StratiformThicknessMeters { get; set; } = new(400.0f, 1500.0f);
    /// <summary>The top never exceeds this altitude.</summary>
    [Export(PropertyHint.Range, "0,18000,10")] public float TopCapMeters { get; set; } = 5000.0f;
    /// <summary>Row of the generated N-row height gradient texture (WO3b).</summary>
    [Export(PropertyHint.Range, "0,7,1")] public int GradientRow { get; set; }
    /// <summary>Horizontal stretch of the large-noise lookup for a fully stratiform layer (1 = none).</summary>
    [Export(PropertyHint.Range, "1,8,0.1")] public float StratiformNoiseStretch { get; set; } = 3.0f;
    /// <summary>Small-scale erosion multiplier at stratiformity 0 and 1.</summary>
    [Export] public Vector2 ErosionScale { get; set; } = new(1.0f, 0.5f);
    /// <summary>Per-layer offset of the large/medium/small noise positions so layers are uncorrelated.</summary>
    [Export] public Vector3 NoiseOffsetMeters { get; set; } = Vector3.Zero;
    /// <summary>Extinction of the layer per metre at full coverage (inter-layer sun shadowing, WO3b).</summary>
    [Export(PropertyHint.Range, "0,0.01,0.00001")] public float ExtinctionPerMeter { get; set; } = 0.0004f;
    /// <summary>Henyey-Greenstein forward-scatter g for the layer's direct light.</summary>
    [Export(PropertyHint.Range, "0,0.95,0.01")] public float PhaseG { get; set; } = 0.55f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float AmbientScale { get; set; } = 1.0f;
    /// <summary>Weight of the layer in the cloud shadow maps (cirrus is weak).</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float ShadowWeight { get; set; } = 1.0f;
}
