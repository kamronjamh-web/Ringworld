using System;
using System.Collections.Generic;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>Canonical, surface-only lattice with sparse absolute-height overrides.</summary>
public sealed class RingSurfaceHeightField
{
    public const float DefaultSpacingMeters = 2.0f;
    public const float MinimumBrushRadiusMeters = 1.0f;
    public const float MaximumBrushRadiusMeters = 64.0f;
    private readonly Func<long, int, float> _baseline;
    private readonly Dictionary<(long Around, int Across), float> _edits = new();
    private readonly int _spacingMeters;
    private readonly bool _useTriangleInterpolation;

    public RingSurfaceHeightField(ulong terrainDefinitionHash, long circumferenceMeters, int widthMeters, float minimumHeight, float maximumHeight,
        Func<long, int, float> baseline, float spacingMeters = DefaultSpacingMeters, bool useTriangleInterpolation = false)
    {
        if (terrainDefinitionHash == 0 || circumferenceMeters <= 0 || widthMeters <= 0 || baseline == null || !float.IsFinite(spacingMeters) || spacingMeters <= 0 || spacingMeters != MathF.Round(spacingMeters) || spacingMeters > int.MaxValue ||
            !float.IsFinite(minimumHeight) || !float.IsFinite(maximumHeight) || minimumHeight > maximumHeight)
            throw new ArgumentException("Surface height field definition is invalid.");
        _spacingMeters = checked((int)spacingMeters);
        if (circumferenceMeters % _spacingMeters != 0 || widthMeters % _spacingMeters != 0) throw new ArgumentException("Surface dimensions must align to lattice spacing.");
        TerrainDefinitionHash = terrainDefinitionHash; CircumferenceMeters = circumferenceMeters; WidthMeters = widthMeters;
        CircumferenceSamples = circumferenceMeters / _spacingMeters; WidthSamples = widthMeters / _spacingMeters;
        SpacingMeters = spacingMeters; MinimumHeight = minimumHeight; MaximumHeight = maximumHeight; _baseline = baseline;
        _useTriangleInterpolation = useTriangleInterpolation;
    }

    public ulong TerrainDefinitionHash { get; }
    public long CircumferenceMeters { get; }
    public int WidthMeters { get; }
    public long CircumferenceSamples { get; }
    public int WidthSamples { get; }
    public float SpacingMeters { get; }
    public float MinimumHeight { get; }
    public float MaximumHeight { get; }
    public ulong Revision { get; private set; }
    public int ChangedSampleCount => _edits.Count;
    public int EditCount => _edits.Count;
    public bool UsesTriangleInterpolation => _useTriangleInterpolation;

    public float GetSample(long around, int across)
    {
        if (across < 0 || across >= WidthMeters) throw new ArgumentOutOfRangeException(nameof(across));
        long canonicalAround = WrapAround(around);
        if (canonicalAround % _spacingMeters != 0 || across % _spacingMeters != 0) throw new ArgumentOutOfRangeException("Sample coordinates must lie on the surface lattice.");
        return _edits.TryGetValue((canonicalAround, across), out float value) ? value : Baseline(canonicalAround, across);
    }

    /// <summary>Returns the procedural surface alone, allowing render caches to remove stale overrides after Clear or Restore.</summary>
    public float GetBaselineSample(long aroundMeters, int acrossMeters)
    {
        if (acrossMeters < 0 || acrossMeters >= WidthMeters) throw new ArgumentOutOfRangeException(nameof(acrossMeters));
        long canonicalAround = WrapAround(aroundMeters);
        if (canonicalAround % _spacingMeters != 0 || acrossMeters % _spacingMeters != 0) throw new ArgumentOutOfRangeException("Sample coordinates must lie on the surface lattice.");
        return Baseline(canonicalAround, acrossMeters);
    }

    public float SampleHeight(double aroundMeters, double acrossMeters)
    {
        if (!double.IsFinite(aroundMeters) || !double.IsFinite(acrossMeters)) throw new ArgumentOutOfRangeException("Sample coordinates must be finite.");
        double acrossSample = acrossMeters / SpacingMeters;
        if (acrossSample < 0 || acrossSample > WidthSamples - 1) throw new ArgumentOutOfRangeException(nameof(acrossMeters));
        double aroundSample = aroundMeters / SpacingMeters;
        if (aroundSample <= long.MinValue || aroundSample >= long.MaxValue) throw new ArgumentOutOfRangeException(nameof(aroundMeters));
        long a0 = checked((long)Math.Floor(aroundSample)); int x0 = (int)Math.Floor(acrossSample);
        long a1 = a0 + 1; int x1 = Math.Min(x0 + 1, WidthSamples - 1);
        double ta = aroundSample - Math.Floor(aroundSample), tx = acrossSample - x0;
        long around0 = LatticeAround(a0), around1 = LatticeAround(a1); int across0 = LatticeAcross(x0), across1 = LatticeAcross(x1);
        float h00 = GetSample(around0, across0);
        float h10 = GetSample(around1, across0);
        float h01 = GetSample(around0, across1);
        float h11 = GetSample(around1, across1);
        if (_useTriangleInterpolation)
            return RingSurfaceLattice.TriangleInterpolate(h00, h10, h01, h11, ta, tx);
        double low = h00 + ((double)h10 - h00) * ta;
        double high = h01 + ((double)h11 - h01) * ta;
        return (float)(low + (high - low) * tx);
    }

    public bool ApplyBrush(double aroundMeters, double acrossMeters, float radiusMeters, float delta)
    {
        if (!double.IsFinite(aroundMeters) || !double.IsFinite(acrossMeters) || !float.IsFinite(radiusMeters) || !float.IsFinite(delta) ||
            radiusMeters < MinimumBrushRadiusMeters || radiusMeters > MaximumBrushRadiusMeters || delta == 0)
            return false;
        double acrossCenter = acrossMeters / SpacingMeters;
        if (acrossCenter < 0 || acrossCenter > WidthSamples - 1) return false;
        double aroundCenter = aroundMeters / SpacingMeters, radiusSamples = radiusMeters / SpacingMeters;
        if (aroundCenter - radiusSamples <= long.MinValue || aroundCenter + radiusSamples >= long.MaxValue) return false;
        long aroundStart = checked((long)Math.Floor(aroundCenter - radiusSamples));
        long aroundEnd = checked((long)Math.Ceiling(aroundCenter + radiusSamples));
        int acrossStart = Math.Max(0, (int)Math.Floor(acrossCenter - radiusSamples));
        int acrossEnd = Math.Min(WidthSamples - 1, (int)Math.Ceiling(acrossCenter + radiusSamples));
        bool changed = false;
        for (long around = aroundStart; around <= aroundEnd; around++)
        for (int across = acrossStart; across <= acrossEnd; across++)
        {
            double da = WrappedDistanceSamples(around, aroundCenter), dx = across - acrossCenter, distance = Math.Sqrt(da * da + dx * dx);
            if (distance > radiusSamples) continue;
            long latticeAround = LatticeAround(around); int latticeAcross = LatticeAcross(across);
            float next = Math.Clamp(GetSample(latticeAround, latticeAcross) + delta * (float)(1.0 - distance / radiusSamples), MinimumHeight, MaximumHeight);
            changed |= SetAbsoluteCanonical(latticeAround, latticeAcross, next);
        }
        if (changed) Revision++;
        return changed;
    }

    public void Clear() { if (_edits.Count == 0) return; _edits.Clear(); Revision++; }

    /// <summary>Enumerates absolute overrides in an inclusive canonical-metre window for cache refreshes.</summary>
    public IEnumerable<RingSurfaceHeightEdit> GetEditsInWindow(long aroundStartMeters, long aroundEndMeters, int acrossStartMeters, int acrossEndMeters)
    {
        if (acrossStartMeters < 0 || acrossEndMeters >= WidthMeters || acrossStartMeters > acrossEndMeters ||
            aroundStartMeters % _spacingMeters != 0 || aroundEndMeters % _spacingMeters != 0 ||
            acrossStartMeters % _spacingMeters != 0 || acrossEndMeters % _spacingMeters != 0)
            throw new ArgumentOutOfRangeException("Edit window must be a valid lattice-aligned surface window.");
        bool allAround = aroundEndMeters >= aroundStartMeters && aroundEndMeters - aroundStartMeters >= CircumferenceMeters - _spacingMeters;
        long start = WrapAround(aroundStartMeters), end = WrapAround(aroundEndMeters);
        foreach (var pair in _edits)
        {
            long around = pair.Key.Around; int across = pair.Key.Across;
            bool inAround = allAround || (start <= end ? around >= start && around <= end : around >= start || around <= end);
            if (inAround && across >= acrossStartMeters && across <= acrossEndMeters)
                yield return new RingSurfaceHeightEdit(around, across, pair.Value);
        }
    }

    public RingSurfaceEditSnapshot CreateSnapshot() => new(RingSurfaceEditStore.FormatVersion, TerrainDefinitionHash, CircumferenceMeters, WidthMeters, SpacingMeters, MinimumHeight, MaximumHeight, Revision, ToEdits());

    public RingSurfaceEditStoreResult Save(string path) => RingSurfaceEditStore.Save(path, CreateSnapshot());

    public RingSurfaceEditStoreResult Load(string path)
    {
        RingSurfaceEditStoreResult result = RingSurfaceEditStore.Load(path, out RingSurfaceEditSnapshot snapshot);
        return !result.Success ? result : Restore(snapshot) ? result : new(false, "Surface edit save does not match this terrain definition.");
    }

    public bool Restore(RingSurfaceEditSnapshot snapshot)
    {
        try { RingSurfaceEditStore.Validate(snapshot); } catch { return false; }
        if (snapshot.TerrainDefinitionHash != TerrainDefinitionHash || snapshot.CircumferenceMeters != CircumferenceMeters || snapshot.WidthMeters != WidthMeters ||
            snapshot.SpacingMeters != SpacingMeters || snapshot.MinimumHeight != MinimumHeight || snapshot.MaximumHeight != MaximumHeight) return false;
        var replacement = new Dictionary<(long, int), float>();
        foreach (RingSurfaceHeightEdit edit in snapshot.Edits) replacement.Add((edit.AroundMeters, edit.AcrossMeters), edit.Height);
        _edits.Clear(); foreach (var pair in replacement) _edits.Add(pair.Key, pair.Value); Revision = snapshot.Revision;
        return true;
    }

    private RingSurfaceHeightEdit[] ToEdits() { var result = new RingSurfaceHeightEdit[_edits.Count]; int i = 0; foreach (var pair in _edits) result[i++] = new(pair.Key.Around, pair.Key.Across, pair.Value); return result; }
    private bool SetAbsoluteCanonical(long around, int across, float value) { long canonicalAround = WrapAround(around); float old = GetSample(canonicalAround, across); if (old == value) return false; _edits[(canonicalAround, across)] = value; return true; }
    private float Baseline(long around, int across) { float value = _baseline(around, across); return float.IsFinite(value) ? Math.Clamp(value, MinimumHeight, MaximumHeight) : MinimumHeight; }
    private long WrapAround(long around) { long wrapped = around % CircumferenceMeters; return wrapped < 0 ? wrapped + CircumferenceMeters : wrapped; }
    private double WrappedDistanceSamples(long around, double center) { double distance = Math.Abs(around - center) % CircumferenceSamples; return Math.Min(distance, CircumferenceSamples - distance); }
    private long LatticeAround(long sample) => checked(sample * (long)_spacingMeters);
    private int LatticeAcross(int sample) => checked(sample * _spacingMeters);
}
