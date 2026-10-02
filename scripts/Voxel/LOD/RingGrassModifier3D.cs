using System;
using Godot;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Editor-authored horizontal grass density/tint ellipse. It has no vertical-volume semantics.</summary>
[Tool, GlobalClass]
public partial class RingGrassModifier3D : Node3D
{
    public static readonly StringName GroupName = new("ring_grass_modifier");
    [Export] public string ModifierId { get; set; } = "";
    [Export] public NodePath GrassLayerPath { get; set; } = new NodePath();
    [Export] public bool UseCanonicalPositionOverride { get; set; }
    [Export] public double CanonicalAroundMeters { get; set; }
    [Export] public double CanonicalAcrossMeters { get; set; }
    [Export] public Vector2 HalfSizeMeters { get; set; } = new(8, 8);
    [Export] public float FeatherMeters { get; set; } = 2;
    [Export(PropertyHint.Range, "0,1,.01")] public float DensityMultiplier { get; set; } = 1;
    [Export] public Color Tint { get; set; } = Colors.White;
    [Export(PropertyHint.Range, "0,1,.01")] public float TintStrength { get; set; }
    [Export] public bool Enabled { get; set; } = true;
    private bool _anchored; private double _around, _across;
    private double _vertical; private Vector3 _expectedWorld; private bool _hasExpectedWorld;
    public override void _Ready() { AddToGroup(GroupName); ModifierId = string.IsNullOrWhiteSpace(ModifierId) ? GetPath().ToString() : ModifierId; }
    public bool TryCapture(VoxelChunkGridManager grid, out RingGrassModifierSnapshot.Entry entry)
    {
        if (UseCanonicalPositionOverride) { _around = CanonicalAroundMeters; _across = CanonicalAcrossMeters; _vertical = 0; _anchored = true; }
        else
        {
            // Resolve each throttled discovery in canonical space. Floating-origin
            // rebases change world coordinates but map back to the same anchor;
            // an author moving this node produces a new canonical centre.
            // If the node stayed where the prior grid frame projected it, it
            // was carried by a rebase. Otherwise its transform was authored.
            if (!_hasExpectedWorld || GlobalPosition.DistanceTo(_expectedWorld) > .01f)
            { var p = grid.WorldToCanonicalRingPosition64(GlobalPosition); _around = p.AroundMeters; _across = p.AcrossMeters; _vertical = p.VerticalMeters; _anchored = true; }
        }
        if (_anchored)
        { _expectedWorld = grid.CaptureLodGeometrySnapshot().CanonicalToWorld(_around,_vertical,_across); GlobalPosition = _expectedWorld; _hasExpectedWorld=true; }
        entry = new RingGrassModifierSnapshot.Entry(ModifierId, new RingGrassModifierZone(_around, _across, HalfSizeMeters, FeatherMeters, DensityMultiplier, Tint, TintStrength, Enabled));
        return entry.Zone.IsValid;
    }
    public bool Targets(RingStandingGrassLayer layer) => GrassLayerPath.IsEmpty || GetNodeOrNull<RingStandingGrassLayer>(GrassLayerPath) == layer;
}
