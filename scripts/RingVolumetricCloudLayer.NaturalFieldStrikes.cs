using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.ContainmentField;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo;

/// <summary>Natural near-wall field strikes (docs/containment-field-design.md 3.10.6, WO14): the layer side of <see cref="NaturalFieldStrikes"/>.</summary>
public partial class RingVolumetricCloudLayer
{
    private readonly Dictionary<(long Bucket, long Key), NaturalFieldStrike?> _naturalFieldMemo = new();
    private readonly List<NaturalFieldStrike> _naturalFieldLive = new();
    private (long Bucket, int Cells, ulong Manual, Func<double, double>? Provider, float Scale, double Width) _naturalFieldPass;
    private ulong _naturalFieldMemoManual;
    /// <summary>Test hook: how many natural field strikes the layer has put into the lightning list since the world started.</summary>
    public int NaturalFieldStrikesScheduled { get; private set; }
    private const long ManualKeyTag = 1L << 62;

    /// <summary>The field the stream reads: the live provider (the same function the field installs as the megastorm seam) and the roots of the installed profile (the habitat edges without one).</summary>
    private bool TryNaturalFieldGeometry(out NaturalFieldGeometry field)
    {
        field = default;
        Func<double, double>? provider = MegastormFieldCoupling.FieldUndersideProvider;
        if (provider == null || _grid == null) return false;
        double width = _grid.RingAcrossWidthMeters;
        ContainmentFieldProfile? profile = ContainmentFieldProvider.Profile;
        field = new NaturalFieldGeometry(provider, profile?.RootAcross0 ?? 0.0, profile?.RootAcrossW ?? width, width, _grid.GetCanonicalRingCircumferenceMeters64());
        return true;
    }

    private bool TryNaturalFieldStormAtCell(int a, int z, int seed, double time, out NaturalFieldStorm storm)
    {
        storm = default;
        if (!TryBuildWeatherSystem(a, z, seed, time, out WeatherSystemDefinition s) || s.Kind is not (WeatherSystemKind.MajorFront or WeatherSystemKind.ExceptionalFront)) return false;
        RenderWeatherSystem render = BuildNaturalRenderSystem(s, a, z, seed, time, 0.0f, 500.0f);
        storm = new NaturalFieldStorm(s.SystemId, (int)s.Kind, WrapAround(s.SourceAroundMeters + WeatherWindMetersPerSecond.X * time), s.SourceAcrossMeters + WeatherWindMetersPerSecond.Y * time,
            s.MajorRadiusMeters, s.MinorRadiusMeters, s.OrientationCosine, s.OrientationSine, s.PrecipitationPotential, render.StormCeilingMeters, render.Sculpts,
            render.Lifecycle >= 1.0f && render.Lifecycle < 2.0f);
        return true;
    }

    /// <summary>A registered (manual / parked) storm as a field-strike storm: stationary, always mature; the top is the one the render system gives it.</summary>
    private static NaturalFieldStorm NaturalFieldStormOf(in LocalWeatherState m)
        => new(m.SystemId, (int)m.SystemKind, m.SystemCenterMeters.X, m.SystemCenterMeters.Y, m.SystemRadiiMeters.X, m.SystemRadiiMeters.Y, Mathf.Cos(m.SystemOrientationRadians),
            Mathf.Sin(m.SystemOrientationRadians), m.PrecipitationPotential, Math.Min(RingCloudLayers.CeilingMeters, Math.Max(m.CloudBaseAltitudeMeters + 500.0f, m.CloudTopAltitudeMeters)),
            IsStormSculptProfile(m.DominantFormationProfile), true);

    private NaturalFieldStrike? NaturalFieldOfCell(in NaturalFieldGeometry field, int seed, int a, int z, long bucket, float scale)
    {
        long key = ((long)a << 32) | (uint)z;
        if (_naturalFieldMemo.TryGetValue((bucket, key), out NaturalFieldStrike? known)) return known;
        NaturalFieldStrike? made = null;
        if (NaturalFieldStrikes.MayRoll(seed, key, bucket, scale)
            && TryNaturalFieldStormAtCell(a, z, seed, bucket * NaturalFieldStrikes.BucketSeconds, out NaturalFieldStorm storm)
            && NaturalFieldStrikes.TryStrike(seed, key, field, storm, bucket, scale, out NaturalFieldStrike strike)) made = strike;
        _naturalFieldMemo[(bucket, key)] = made;
        return made;
    }

    private NaturalFieldStrike? NaturalFieldOfManual(in NaturalFieldGeometry field, int seed, in LocalWeatherState m, long bucket, float scale)
    {
        long key = ManualKeyTag | (uint)m.SystemId;
        if (_naturalFieldMemo.TryGetValue((bucket, key), out NaturalFieldStrike? known)) return known;
        NaturalFieldStrike? made = null;
        if (m.SystemId != 0 && NaturalFieldStrikes.MayRoll(seed, key, bucket, scale) && NaturalFieldStrikes.TryStrike(seed, key, field, NaturalFieldStormOf(m), bucket, scale, out NaturalFieldStrike strike)) made = strike;
        _naturalFieldMemo[(bucket, key)] = made;
        return made;
    }

    /// <summary>
    /// Natural near-wall field strikes whose 0.85-1.85 s life covers <paramref name="now"/>, as <c>FieldStrike</c>-flagged events of the natural kind (GroundPosition = contact
    /// point, CloudPosition = channel base, own duration), so the field's lightning adapter publishes one FieldStrike each (natural source: at most 1/s). Candidate systems are the
    /// lightning scan's front cells plus the registered storms; each (system, bucket) result is memoised and the pass only reruns when the bucket, the candidate set, the
    /// registered storms, the field or the strike-rate scale change. A no-op without a field provider, so ordinary worlds and the natural weather fingerprint are unchanged.
    /// </summary>
    private void TryScheduleNaturalFieldStrikes(double now)
    {
        float scale = LightningVisualPolicy.NaturalStrikeChanceScale;
        if (!TryNaturalFieldGeometry(out NaturalFieldGeometry field) || scale <= 0.0f)
        {
            if (_naturalFieldMemo.Count > 0 || _naturalFieldLive.Count > 0) { _naturalFieldMemo.Clear(); _naturalFieldLive.Clear(); }
            _naturalFieldPass = default;
            return;
        }
        long bucket = NaturalFieldStrikes.BucketOf(now);
        var pass = (bucket, _lightningCandidateCache.Count, ManualWeatherRevision, field.Underside, scale, field.WidthMeters);
        if (!pass.Equals(_naturalFieldPass))
        {
            if (_naturalFieldPass.Provider != field.Underside || _naturalFieldPass.Scale != scale || _naturalFieldPass.Width != field.WidthMeters || _naturalFieldMemoManual != ManualWeatherRevision) _naturalFieldMemo.Clear();
            _naturalFieldMemoManual = ManualWeatherRevision;
            _naturalFieldPass = pass;
            RebuildNaturalFieldLive(field, bucket, now, scale);
        }
        for (int i = 0; i < _naturalFieldLive.Count; i++)
        {
            NaturalFieldStrike strike = _naturalFieldLive[i];
            if (strike.StartTimeSeconds > now || now - strike.StartTimeSeconds > strike.DurationSeconds || _activeLightningEvents.Count >= 128 || FindActiveLightningIndex(strike.Id) >= 0) continue;
            _activeLightningEvents.Add(NaturalFieldStrikes.ToEvent(strike));
            NaturalFieldStrikesScheduled++;
        }
    }

    private void RebuildNaturalFieldLive(in NaturalFieldGeometry field, long bucket, double now, float scale)
    {
        _naturalFieldLive.Clear();
        int seed = _grid!.TerrainSampler?.Seed ?? 1337;
        // A strike starts inside its own bucket and lives under 2 s, so only this bucket and the one before it can cover `now`.
        double reach = CenterReach(0.0f, false);
        for (long b = Math.Max(0, bucket - 1); b <= bucket; b++)
        {
            foreach (var entry in _lightningCandidateCache)
            {
                double advected = entry.Value.SourceAcrossMeters + WeatherWindMetersPerSecond.Y * now;
                if (Math.Min(advected, field.WidthMeters - advected) > reach) continue;
                if (NaturalFieldOfCell(field, seed, entry.Key.Around, entry.Key.Across, b, scale) is NaturalFieldStrike strike) _naturalFieldLive.Add(strike);
            }
            foreach (LocalWeatherState manual in _manualWeather.Values)
                if (manual.SystemKind is WeatherSystemKind.MajorFront or WeatherSystemKind.ExceptionalFront && NaturalFieldOfManual(field, seed, manual, b, scale) is NaturalFieldStrike strike) _naturalFieldLive.Add(strike);
        }
        if (_naturalFieldMemo.Count > 4096)
        {
            var stale = new List<(long, long)>();
            foreach (var key in _naturalFieldMemo.Keys) if (key.Bucket < bucket - 2) stale.Add(key);
            foreach (var key in stale) _naturalFieldMemo.Remove(key);
        }
    }

    /// <summary>How far from a wall a system's centre can be and still strike (12 km, 1 km of slack for the roots), plus the cell jitter when only the nominal cell centre is known.</summary>
    private static double CenterReach(float cellMeters, bool nominalCentre)
        => NaturalFieldStrikes.WallCutoffMeters + 1000.0 + (nominalCentre ? 0.41 * cellMeters : 0.0);

    /// <summary>
    /// Pure production-path replay of the natural field strikes whose start lies in [t0, t1) (tests and captures): every macro cell that can reach a wall plus the registered storms, with
    /// the live field provider and the given strike-rate scale. Independent of the lightning scan cache, so it is the reference the live stream is compared with. Sorted by start.
    /// </summary>
    public List<NaturalFieldStrike> QueryNaturalFieldStrikesInWindow(double t0, double t1, float scale)
    {
        var result = new List<NaturalFieldStrike>();
        if (!TryNaturalFieldGeometry(out NaturalFieldGeometry field) || scale <= 0.0f || t1 <= t0) return result;
        int seed = _grid!.TerrainSampler?.Seed ?? 1337;
        float cell = Mathf.Clamp(MacroWeatherCellMeters, 16000.0f, 64000.0f);
        int aroundCount = Math.Max(1, Mathf.CeilToInt((float)(field.CircumferenceMeters / cell)));
        float maxRadius = Mathf.Clamp(MaximumMacroSystemRadiusMeters, 40000.0f, 160000.0f);
        double reach = CenterReach(cell, true);
        var saved = _naturalFieldMemo.Count > 0 ? new Dictionary<(long Bucket, long Key), NaturalFieldStrike?>(_naturalFieldMemo) : null;
        _naturalFieldMemo.Clear();   // the memo belongs to the live scale; the query keeps its own and restores the live one
        for (long b = NaturalFieldStrikes.BucketOf(t0) - 1; b <= NaturalFieldStrikes.BucketOf(t1); b++)
        {
            if (b < 0) continue;
            double tb = b * NaturalFieldStrikes.BucketSeconds;
            int minAcross = Mathf.FloorToInt((float)((-maxRadius - WeatherWindMetersPerSecond.Y * tb) / cell)), maxAcross = Mathf.CeilToInt((float)((field.WidthMeters + maxRadius - WeatherWindMetersPerSecond.Y * tb) / cell));
            for (int z = minAcross; z <= maxAcross; z++)
            {
                double advected = (z + 0.5) * cell + WeatherWindMetersPerSecond.Y * tb;
                if (Math.Min(advected, field.WidthMeters - advected) > reach) continue;
                for (int a = 0; a < aroundCount; a++)
                    if (NaturalFieldOfCell(field, seed, a, z, b, scale) is NaturalFieldStrike strike && strike.StartTimeSeconds >= t0 && strike.StartTimeSeconds < t1) result.Add(strike);
            }
            foreach (LocalWeatherState manual in _manualWeather.Values)
                if (manual.SystemKind is WeatherSystemKind.MajorFront or WeatherSystemKind.ExceptionalFront && NaturalFieldOfManual(field, seed, manual, b, scale) is NaturalFieldStrike strike
                    && strike.StartTimeSeconds >= t0 && strike.StartTimeSeconds < t1) result.Add(strike);
            if (_naturalFieldMemo.Count > 8192) _naturalFieldMemo.Clear();
        }
        _naturalFieldMemo.Clear();
        if (saved != null) foreach (var kv in saved) _naturalFieldMemo[kv.Key] = kv.Value;
        result.Sort((x, y) => x.StartTimeSeconds.CompareTo(y.StartTimeSeconds));
        return result;
    }
}
