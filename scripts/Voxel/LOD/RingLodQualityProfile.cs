using Godot;

namespace RingworldTechDemo.Voxel.LOD;

[GlobalClass]
public partial class RingLodQualityProfile : Resource
{
    [Export] public string DisplayName { get; set; } = "Medium";
    [Export(PropertyHint.Range, "1,11,1")] public int ActiveLevelCount { get; set; } = 5;
    [Export] public Vector2 Lod1OutMeters { get; set; } = new(240.0f, 320.0f);
    [Export] public Vector2 Lod2OutMeters { get; set; } = new(480.0f, 640.0f);
    [Export] public Vector2 Lod3OutMeters { get; set; } = new(960.0f, 1280.0f);
    [Export] public Vector2 Lod4OutMeters { get; set; } = new(1920.0f, 2560.0f);
    [Export] public Vector2 Lod5OutMeters { get; set; } = new(4000.0f, 5500.0f);
    [Export] public Vector2 Lod6OutMeters { get; set; } = new(15000.0f, 20000.0f);
    [Export] public Vector2 Lod7OutMeters { get; set; } = new(40000.0f, 55000.0f);
    [Export] public Vector2 Lod8OutMeters { get; set; } = new(110000.0f, 140000.0f);
    [Export] public Vector2 Lod9OutMeters { get; set; } = new(500000.0f, 700000.0f);
    [Export] public Vector2 Lod10OutMeters { get; set; } = new(1800000.0f, 2500000.0f);
    [Export] public Vector2 Lod11OutMeters { get; set; } = new(6000000.0f, 8000000.0f);
    [Export(PropertyHint.Range, "64,4096,64")] public int GlobalAroundSegments { get; set; } = 512;
    [Export(PropertyHint.Range, "4,256,1")] public int GlobalAcrossSegments { get; set; } = 32;
    [Export(PropertyHint.Range, "1,16,1")] public int SectorUploadsPerFrame { get; set; } = 3;
    [Export(PropertyHint.Range, "1,32,1")] public int ConcurrentWorkers { get; set; } = 2;
    [Export] public bool ExpandSectorWindowsToFadeEnd { get; set; }

    [ExportGroup("Phase C Screen-Space Selection")]
    [Export] public bool UseScreenSpaceError { get; set; } = true;
    [Export(PropertyHint.Range, "0.5,8.0,0.1")] public float TargetPixelError { get; set; } = 3.0f;
    [Export(PropertyHint.Range, "0.05,0.4,0.01")] public float TransitionFraction { get; set; } = 0.18f;
    [Export(PropertyHint.Range, "0.25,8.0,0.05")] public float UploadBudgetMilliseconds { get; set; } = 2.0f;

    public Vector2 GetOutgoingBand(int levelIndex) => levelIndex switch
    {
        0 => Lod1OutMeters,
        1 => Lod2OutMeters,
        2 => Lod3OutMeters,
        3 => Lod4OutMeters,
        4 => Lod5OutMeters,
        5 => Lod6OutMeters,
        6 => Lod7OutMeters,
        7 => Lod8OutMeters,
        8 => Lod9OutMeters,
        9 => Lod10OutMeters,
        _ => Lod11OutMeters
    };
}
