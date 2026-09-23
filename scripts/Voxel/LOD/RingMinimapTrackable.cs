using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Attach to a Node3D to expose it to the player-centered 3D scanner.</summary>
public partial class RingMinimapTrackable : Node3D
{
    public enum TrackableCategory
    {
        Station,
        Asteroid,
        Ship,
        Waypoint,
        Ring
    }

    public static readonly StringName GroupName = new("ring_minimap_trackable");

    [Export] public TrackableCategory Category { get; set; } = TrackableCategory.Station;
    [Export] public string DisplayName { get; set; } = "Trackable";
    [Export] public Color MapColor { get; set; } = new(0.25f, 0.86f, 1.0f);
    [Export(PropertyHint.Range, "1,100000,1")] public float DisplaySizeMeters { get; set; } = 250.0f;
    [Export(PropertyHint.Range, "1000,20000000,1000")] public float DetectionRangeMeters { get; set; } = 1000000.0f;
    [Export] public bool UseCanonicalPositionOverride { get; set; }
    [Export] public double CanonicalAroundMeters { get; set; }
    [Export] public double CanonicalVerticalMeters { get; set; }
    [Export] public double CanonicalAcrossMeters { get; set; }

    public override void _Ready() => AddToGroup(GroupName);

    public RingCanonicalPosition ResolveCanonicalPosition(VoxelChunkGridManager grid)
    {
        return UseCanonicalPositionOverride
            ? new RingCanonicalPosition(CanonicalAroundMeters, CanonicalVerticalMeters, CanonicalAcrossMeters)
                .Wrap(grid.GetCanonicalRingCircumferenceMeters64())
            : grid.WorldToCanonicalRingPosition64(GlobalPosition);
    }
}
