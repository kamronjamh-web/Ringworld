using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Worker-safe, horizontal soft ellipse authored in canonical ring metres.</summary>
public readonly record struct RingGrassModifierZone(double AroundMeters, double AcrossMeters, Vector2 HalfSizeMeters,
    float FeatherMeters, float DensityMultiplier, Color Tint, float TintStrength, bool Enabled = true)
{
    public bool IsValid => double.IsFinite(AroundMeters) && double.IsFinite(AcrossMeters)
        && HalfSizeMeters.X > 0 && HalfSizeMeters.Y > 0 && float.IsFinite(FeatherMeters)
        && float.IsFinite(HalfSizeMeters.X) && float.IsFinite(HalfSizeMeters.Y) && float.IsFinite(DensityMultiplier) && float.IsFinite(TintStrength)
        && float.IsFinite(Tint.R) && float.IsFinite(Tint.G) && float.IsFinite(Tint.B) && float.IsFinite(Tint.A);
}

public readonly record struct RingGrassModifierRegion(double AroundMin, double AroundMax, double AcrossMin, double AcrossMax);

/// <summary>Immutable deterministic modifier view retained by grass workers.</summary>
public sealed class RingGrassModifierSnapshot
{
    public readonly record struct Entry(string Id, RingGrassModifierZone Zone);
    private readonly Entry[] _entries;
    private readonly IReadOnlyList<Entry> _readonlyEntries;
    public IReadOnlyList<Entry> Entries => _readonlyEntries;
    public long AroundLength { get; }
    public ulong Revision { get; }
    public bool HasDensity => _entries.Length != 0;
    public RingGrassModifierSnapshot(ulong revision, long aroundLength, IEnumerable<Entry>? entries)
    {
        Revision = revision; AroundLength = Math.Max(1, aroundLength);
        List<Entry> list = new();
        if (entries != null) foreach (Entry entry in entries)
            if (!string.IsNullOrWhiteSpace(entry.Id) && entry.Zone.IsValid && entry.Zone.Enabled) list.Add(entry with { Zone = entry.Zone with { DensityMultiplier = Mathf.Clamp(entry.Zone.DensityMultiplier, 0, 1), TintStrength = Mathf.Clamp(entry.Zone.TintStrength, 0, 1), FeatherMeters = Mathf.Max(0, entry.Zone.FeatherMeters) } });
        list.Sort(static (a,b) => StringComparer.Ordinal.Compare(a.Id,b.Id));
        if (list.Count > 256) list.RemoveRange(256, list.Count-256);
        for (int i=list.Count-1;i>0;i--) if (list[i].Id==list[i-1].Id) list.RemoveAt(i);
        _entries = list.ToArray();
        _readonlyEntries = Array.AsReadOnly(_entries);
    }
    public static float Weight(in RingGrassModifierZone z, double around, double across, long length = 1)
    {
        double da = around - z.AroundMeters;
        if (length > 1) { da %= length; if (da > length * .5) da -= length; if (da < -length * .5) da += length; }
        double db = across - z.AcrossMeters;
        double radius = Math.Sqrt((da / z.HalfSizeMeters.X) * (da / z.HalfSizeMeters.X) + (db / z.HalfSizeMeters.Y) * (db / z.HalfSizeMeters.Y));
        double feather = Math.Clamp(z.FeatherMeters / Math.Max(.001, Math.Min(z.HalfSizeMeters.X, z.HalfSizeMeters.Y)), 0, 1);
        if (radius >= 1) return 0;
        if (feather <= 0) return 1;
        float t = Mathf.Clamp((float)((1 - radius) / feather), 0, 1);
        return t * t * (3 - 2 * t);
    }
    public float DensityAt(double around, double across) { float result=1; foreach (Entry e in _entries) result*=Mathf.Lerp(1,e.Zone.DensityMultiplier,Weight(e.Zone,around,across,AroundLength)); return result; }
    internal RingGrassModifierSnapshot FilterForTile(in RingGrassTileBounds bounds)
    {
        List<Entry> entries = new();
        foreach (Entry entry in _entries) if (Overlaps(bounds, entry.Zone, AroundLength)) entries.Add(entry);
        return entries.Count == _entries.Length ? this : new RingGrassModifierSnapshot(Revision, AroundLength, entries);
    }
    private static bool Overlaps(in RingGrassTileBounds bounds, in RingGrassModifierZone zone, long length)
    {
        double aroundExtent=zone.HalfSizeMeters.X,acrossExtent=zone.HalfSizeMeters.Y;
        if(bounds.AcrossMin>=zone.AcrossMeters+acrossExtent||bounds.AcrossMax<=zone.AcrossMeters-acrossExtent)return false;
        double min=bounds.AroundMin,max=bounds.AroundMax,center=RingGrassCoordinates.Canonicalize(zone.AroundMeters,length);
        bool hit(double value)=>min<value+aroundExtent&&max>value-aroundExtent;
        return hit(center)||(center<aroundExtent&&hit(center+length))||(center>length-aroundExtent&&hit(center-length));
    }
}
