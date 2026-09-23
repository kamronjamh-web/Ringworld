using System;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Tools;

[GlobalClass]
public partial class TreeGpuRebaseSmoke : Node
{
    private const double Radius = 5_000_000.0;
    private const double Circumference = 31_415_936.0;

    public Godot.Collections.Dictionary Run()
    {
        RingLodGeometrySnapshot oldGeometry = Geometry(Circumference - 12, new Vector3(120, 4, -70));
        RingLodGeometrySnapshot newGeometry = Geometry(9, new Vector3(120, 4, -70));
        RingCanonicalPosition focus = new(Circumference - 6, 18, 35);
        bool accepted = RingBiomeDecorationLayer.TryGetGpuTreePureTranslationDelta(oldGeometry, newGeometry, focus, out Vector3 delta);
        bool precise = accepted;
        foreach (double around in new[] { Circumference - 11.0, Circumference - 3.0, 4.0 })
        {
            Vector3 before = oldGeometry.CanonicalToWorld(around, 18, 35) + delta;
            Vector3 after = newGeometry.CanonicalToWorld(around, 18, 35);
            precise &= before.DistanceTo(after) <= .02f;
        }
        bool rejects = !RingBiomeDecorationLayer.TryGetGpuTreePureTranslationDelta(oldGeometry,
                Geometry(9, Vector3.Zero, radius: Radius + 1), focus, out _)
            && !RingBiomeDecorationLayer.TryGetGpuTreePureTranslationDelta(oldGeometry,
                Geometry(9, Vector3.Zero, circumference: Circumference + 1), focus, out _)
            && !RingBiomeDecorationLayer.TryGetGpuTreePureTranslationDelta(oldGeometry,
                Geometry(9, Vector3.Zero, acrossBase: 1), focus, out _)
            && !RingBiomeDecorationLayer.TryGetGpuTreePureTranslationDelta(oldGeometry,
                Geometry(9, Vector3.Zero, verticalBase: 1), focus, out _)
            && !RingBiomeDecorationLayer.TryGetGpuTreePureTranslationDelta(oldGeometry,
                Geometry(9, Vector3.Zero, basis: new Basis(Vector3.Up, .1f)), focus, out _);

        var layer = new RingBiomeDecorationLayer { Enabled = false }; AddChild(layer);
        Transform3D initial = new(Basis.Identity, new Vector3(4, 5, 6));
        layer.ConfigureGpuTreeRebaseFixture(oldGeometry, initial);
        bool first = layer.StepGpuTreeRebaseFixture(newGeometry);
        Vector3 once = layer.GlobalPosition;
        bool second = layer.StepGpuTreeRebaseFixture(newGeometry);
        bool idempotent = first && !second && once == layer.GlobalPosition && once.DistanceTo(initial.Origin + delta) <= .02f;
        layer.QueueFree();
        bool pass = accepted && precise && rejects && idempotent;
        return new Godot.Collections.Dictionary { ["pass"] = pass, ["detail"] = $"accepted={accepted} precise={precise} rejects={rejects} idempotent={idempotent}" };
    }

    private static RingLodGeometrySnapshot Geometry(double anchorAround, Vector3 center, double radius = Radius,
        double circumference = Circumference, double acrossBase = 0, double verticalBase = 0, Basis? basis = null)
        => new(new Transform3D(basis ?? Basis.Identity, Vector3.Zero), Vector3.Zero, center, radius, acrossBase, verticalBase,
            circumference, -100, 100, new RingCanonicalPosition(anchorAround, 0, 0));
}
