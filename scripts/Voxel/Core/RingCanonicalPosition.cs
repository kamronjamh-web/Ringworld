using Godot;

namespace RingworldTechDemo.Voxel.Core;

/// <summary>
/// Permanent high-precision ring position. Never store an authoritative world
/// location in Godot's float Vector3 when the ring can span planetary scales.
/// </summary>
public readonly record struct RingCanonicalPosition(
    double AroundMeters,
    double VerticalMeters,
    double AcrossMeters)
{
    public RingCanonicalPosition AddLocal(Vector3 delta) => new(
        AroundMeters + delta.X,
        VerticalMeters + delta.Y,
        AcrossMeters + delta.Z);

    public RingCanonicalPosition Wrap(double circumferenceMeters)
    {
        if (circumferenceMeters <= 0.0) return this;
        double wrapped = AroundMeters % circumferenceMeters;
        if (wrapped < 0.0) wrapped += circumferenceMeters;
        return this with { AroundMeters = wrapped };
    }

    public Vector3 RelativeTo(in RingCanonicalPosition origin) => new(
        (float)(AroundMeters - origin.AroundMeters),
        (float)(VerticalMeters - origin.VerticalMeters),
        (float)(AcrossMeters - origin.AcrossMeters));
}
