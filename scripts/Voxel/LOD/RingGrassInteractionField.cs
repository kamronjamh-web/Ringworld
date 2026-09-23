using System;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Fixed, allocation-free canonical trail projected into the current render frame.</summary>
internal sealed class RingGrassInteractionField
{
    internal const int Capacity = 8;
    private readonly RingCanonicalPosition[] _samples = new RingCanonicalPosition[Capacity];
    private readonly float[] _ages = new float[Capacity];
    private readonly Vector4[] _projected = new Vector4[Capacity];
    private int _head, _count;
    private RingCanonicalPosition _previousInput;
    private bool _hasPreviousInput;
    public int Count => _count;

    public void Clear() { _head = _count = 0; _hasPreviousInput = false; }
    public void Push(RingCanonicalPosition point, float spacing, long circumference)
    {
        if (!double.IsFinite(point.AroundMeters) || !double.IsFinite(point.VerticalMeters) || !double.IsFinite(point.AcrossMeters)) { Clear(); return; }
        if (_hasPreviousInput)
        {
            double distance = DistanceSquared(point, _previousInput, circumference);
            if (!double.IsFinite(distance) || distance > Math.Pow(Math.Max(8, spacing * Capacity * 2), 2)) { Clear(); }
        }
        _previousInput = point; _hasPreviousInput = true;
        if (_count > 0 && DistanceSquared(point, _samples[(_head - 1 + Capacity) % Capacity], circumference) < spacing * spacing)
        { _ages[(_head - 1 + Capacity) % Capacity] = 0; return; }
        _samples[_head] = point; _ages[_head] = 0; _head = (_head + 1) % Capacity;
        _count = Math.Min(Capacity, _count + 1);
    }
    public void Age(float delta) { delta = float.IsFinite(delta) ? Mathf.Clamp(delta, 0, 1) : 0; for (int i = 0; i < _count; i++) _ages[i] = Mathf.Min(1_000_000, _ages[i] + delta); }
    public Vector4[] Project(in RingLodGeometrySnapshot geometry, float recovery, out int count)
    {
        Array.Clear(_projected, 0, Capacity); count = 0;
        for (int i = 0; i < _count; i++)
        {
            int index = (_head - 1 - i + Capacity) % Capacity;
            float strength = Mathf.Clamp(1 - _ages[index] / Mathf.Max(.01f, recovery), 0, 1);
            if (strength <= 0) continue;
            Vector3 world = geometry.CanonicalToWorld(_samples[index].AroundMeters, _samples[index].VerticalMeters, _samples[index].AcrossMeters);
            _projected[count++] = new Vector4(world.X, world.Y, world.Z, strength);
        }
        return _projected;
    }
    private static double DistanceSquared(in RingCanonicalPosition a, in RingCanonicalPosition b, long circumference)
    {
        double x = a.AroundMeters - b.AroundMeters;
        if (circumference > 0) { x %= circumference; if (x > circumference * .5) x -= circumference; if (x < -circumference * .5) x += circumference; }
        double y = a.VerticalMeters - b.VerticalMeters, z = a.AcrossMeters - b.AcrossMeters;
        return x * x + y * y + z * z;
    }
}
