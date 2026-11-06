using Godot;
using RingworldTechDemo.ContainmentField;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Containment field WO6: exposes the CPU cloud ceiling (ContainmentFieldGeometry.CloudCeilingAt / CloudDensityFactor) for the Certified profile to
/// tools/containment_ceiling_parity_gpu_test.gd, which compares it with the GLSL twin (RingContainmentCeiling.glslinc) on the GPU.
/// </summary>
public partial class ContainmentCeilingProbe : RefCounted
{
    private static readonly ContainmentFieldProfile Certified = new(318_000.0, 10_400.0, 40_000.0, -64.0, 318_064.0, 1_600.0);

    public Vector4 Profile0 => new((float)Certified.WallTopMeters, (float)Certified.ApexMeters, (float)Certified.CrownMeters, 1.0f);
    public Vector4 Profile1 => new((float)Certified.RootAcross0, (float)Certified.RootAcrossW, 0.0f, 0.0f);
    public double Ceiling(double across) => ContainmentFieldGeometry.CloudCeilingAt(Certified, across);
    public double Factor(double across, double altitude) => ContainmentFieldGeometry.CloudDensityFactor(Certified, across, altitude);
}
