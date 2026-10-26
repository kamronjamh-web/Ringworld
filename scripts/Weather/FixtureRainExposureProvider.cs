using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Weather;

/// <summary>
/// Synthetic rain exposure for tests: a flat ground, optional water patches and solid boxes (constructed or natural) in canonical ring space.
/// It lets porch, room and doorway cases run in worlds whose voxel edits are dormant. Install with <c>RingWeatherAmbience.ExposureProvider</c>.
/// </summary>
public partial class FixtureRainExposureProvider : RefCounted, IRainExposureProvider
{
    private readonly record struct Box(double MinAround, double MinVertical, double MinAcross, double MaxAround, double MaxVertical, double MaxAcross, bool Constructed);
    private readonly record struct Patch(double MinAround, double MaxAround, double MinAcross, double MaxAcross);
    private readonly List<Box> _boxes = new();
    private readonly List<Patch> _water = new();

    public double GroundTop { get; set; }
    public RainMaterial GroundMaterial { get; set; } = RainMaterial.Soil;
    /// <summary>Material reported for constructed columns (roofs, walls).</summary>
    public RainMaterial ConstructedMaterial { get; set; } = RainMaterial.Hard;

    public void Clear() { _boxes.Clear(); _water.Clear(); }

    public void AddBox(double minAround, double minVertical, double minAcross, double maxAround, double maxVertical, double maxAcross, bool constructed) =>
        _boxes.Add(new Box(minAround, minVertical, minAcross, maxAround, maxVertical, maxAcross, constructed));

    public void AddWater(double minAround, double maxAround, double minAcross, double maxAcross) => _water.Add(new Patch(minAround, maxAround, minAcross, maxAcross));

    /// <summary>A walled room: 5 x 5 footprint centred on a cell, walls and a roof, optionally with a three-wide doorway on the +around wall.</summary>
    public void AddRoom(long centerAround, long centerAcross, double floor, double roofBottom, bool doorway)
    {
        double a0 = centerAround - 2, a1 = centerAround + 3, c0 = centerAcross - 2, c1 = centerAcross + 3;
        AddBox(a0, roofBottom, c0, a1, roofBottom + 1, c1, true);
        AddBox(a0, floor, c0, a0 + 1, roofBottom, c1, true);
        AddBox(a0, floor, c0, a1, roofBottom, c0 + 1, true);
        AddBox(a0, floor, c1 - 1, a1, roofBottom, c1, true);
        if (!doorway) { AddBox(a1 - 1, floor, c0, a1, roofBottom, c1, true); return; }
        AddBox(a1 - 1, floor, c0, a1, roofBottom, centerAcross - 1, true);
        AddBox(a1 - 1, floor, centerAcross + 2, a1, roofBottom, c1, true);
    }

    public bool TryProbeColumn(long around, int across, out RainColumnProbe probe)
    {
        double top = GroundTop, natural = GroundTop;
        bool constructed = false;
        foreach (Box box in _boxes)
        {
            if (around < box.MinAround || around + 1 > box.MaxAround || across < box.MinAcross || across + 1 > box.MaxAcross) continue;
            if (box.Constructed) { if (box.MaxVertical > top) { top = box.MaxVertical; constructed = true; } }
            else { natural = System.Math.Max(natural, box.MaxVertical); if (box.MaxVertical > top) { top = box.MaxVertical; constructed = false; } }
        }
        RainMaterial material = constructed ? ConstructedMaterial : GroundMaterial;
        if (!constructed)
            foreach (Patch patch in _water)
                if (around >= patch.MinAround && around + 1 <= patch.MaxAround && across >= patch.MinAcross && across + 1 <= patch.MaxAcross) { material = RainMaterial.Water; break; }
        probe = new RainColumnProbe(true, (float)natural, (float)top, material, constructed);
        return true;
    }

    public bool IsSolidAt(long around, double vertical, int across)
    {
        foreach (Box box in _boxes)
            if (around >= box.MinAround && around + 1 <= box.MaxAround && across >= box.MinAcross && across + 1 <= box.MaxAcross && vertical >= box.MinVertical && vertical < box.MaxVertical) return true;
        return false;
    }
}
