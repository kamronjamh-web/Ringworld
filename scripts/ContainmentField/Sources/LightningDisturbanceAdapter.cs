using System;
using System.Collections.Generic;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.ContainmentField;

/// <summary>The storm a lightning event belongs to: its top (StormCeilingMeters of the render system) and a footprint radius for the own-storm occlusion.</summary>
public readonly record struct LightningStormInfo(float TopMeters, float FootprintRadiusMeters);

/// <summary>
/// Polls the shared lightning list each frame and publishes what reaches the field (docs/containment-field-design.md 4.2 "Lightning"), once per EventId.
/// An ordinary event couples by the gap between its parent storm's top and the field underside: <c>E = Energy x exp(-max(gap, 0) / 2 km)</c>, published only at
/// E &gt;= 0.1, so only near-rim storms answer. An event with the <c>FieldStrike</c> flag IS contact: it publishes <see cref="ContainmentDisturbanceKind.FieldStrike"/> at
/// its GroundPosition (the contact point) with no gap attenuation, flash = its own duration-aware pulse. Megastorm scythe attachments are skipped.
/// </summary>
public sealed class LightningDisturbanceAdapter
{
    public const float MinEnergy = 0.1f, GapScaleMeters = 2000.0f;
    public const float LightningDurationSeconds = 2.5f, StrikeDurationSeconds = 3.0f, ArcDurationSeconds = 0.8f;

    private readonly IContainmentDisturbanceSink _sink;
    private readonly Func<RingLightningEvent, LightningStormInfo?> _stormOf;
    private HashSet<long> _seen = new(), _current = new();

    public ContainmentFieldProfile Profile { get; set; }
    public int Published { get; private set; }
    public int StrikesPublished { get; private set; }
    public int Rejected { get; private set; }
    public int BelowCoupling { get; private set; }

    /// <param name="stormOf">The parent storm of an event (top and footprint); null = unknown, the event does not couple. Only called when the field could couple at all.</param>
    public LightningDisturbanceAdapter(IContainmentDisturbanceSink sink, in ContainmentFieldProfile profile, Func<RingLightningEvent, LightningStormInfo?> stormOf)
    {
        _sink = sink; Profile = profile; _stormOf = stormOf;
    }

    /// <summary>Publishes the events not seen before; returns how many the sink accepted. Ids that left the list are forgotten (a rewind publishes them again).</summary>
    public int Update(IReadOnlyList<RingLightningEvent> events)
    {
        int accepted = 0;
        _current.Clear();
        for (int i = 0; i < events.Count; i++)
        {
            RingLightningEvent e = events[i];
            _current.Add(e.EventId);
            if (!_seen.Add(e.EventId)) continue;
            if (!TryMake(e, out ContainmentDisturbance d)) continue;
            if (_sink.Publish(d)) { accepted++; Published++; if (d.Kind == ContainmentDisturbanceKind.FieldStrike) StrikesPublished++; }
            else Rejected++;
        }
        (_seen, _current) = (_current, _seen);
        return accepted;
    }

    /// <summary>The disturbance an event makes, or false (scythe, no field, outside the roots, below the coupling threshold, unknown storm).</summary>
    public bool TryMake(in RingLightningEvent e, out ContainmentDisturbance d)
    {
        d = default;
        if (e.Scythe || e.EventId == 0 || !Profile.IsValid) return false;
        RingLightningEvent ev = e;
        if (e.FieldStrike)
        {
            if (double.IsNaN(ContainmentFieldGeometry.HeightAt(Profile, e.GroundPosition.AcrossMeters))) return false;
            float energy = Math.Clamp(e.Energy, 0.0f, 1.0f);
            bool arc = e.IsFieldArc;
            d = new ContainmentDisturbance(e.EventId, ContainmentDisturbanceKind.FieldStrike, e.GroundPosition, e.StartTimeSeconds, energy,
                arc ? 1000.0f + 1000.0f * energy : 6000.0f + 9000.0f * energy, arc ? ArcDurationSeconds : StrikeDurationSeconds, 1.0f, t => ev.PulseAt(t),
                NaturalSource: e.SystemKind != MegastormModel.KindMegastorm);
            return energy > 0.0f;
        }
        double h = ContainmentFieldGeometry.HeightAt(Profile, e.GroundPosition.AcrossMeters);
        if (double.IsNaN(h)) return false;
        // The tallest a storm top can be is the 18 km cap: when even that cannot reach E >= 0.1 no storm lookup is needed (the whole habitat centre).
        if (e.Energy * Math.Exp(-Math.Max(h - ContainmentFieldGeometry.CloudCeilingMeters, 0.0) / GapScaleMeters) < MinEnergy) { BelowCoupling++; return false; }
        if (_stormOf(e) is not LightningStormInfo storm) return false;
        float coupled = CoupledEnergy(e.Energy, h, storm.TopMeters);
        if (coupled < MinEnergy) { BelowCoupling++; return false; }
        var top = new RingCanonicalPosition(e.CloudPosition.AroundMeters, storm.TopMeters, e.CloudPosition.AcrossMeters);
        d = new ContainmentDisturbance(e.EventId, ContainmentDisturbanceKind.Lightning, top, e.StartTimeSeconds, coupled, 3000.0f + 5000.0f * coupled, LightningDurationSeconds, 1.0f,
            t => ev.PulseAt(t), e.CloudPosition, storm.FootprintRadiusMeters, storm.TopMeters);
        return true;
    }

    /// <summary>E = energy x exp(-max(gap, 0) / 2 km) with gap = H - top.</summary>
    public static float CoupledEnergy(float energy, double undersideMeters, double topMeters)
        => (float)(Math.Clamp(energy, 0.0f, 1.0f) * Math.Exp(-Math.Max(undersideMeters - topMeters, 0.0) / GapScaleMeters));
}
