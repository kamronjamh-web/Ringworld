using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Poi;

/// <summary>
/// Read-only probe for tools/emitter_field_shot_capture.gd: picks an emitter, finds its next field shot and reports the shot's state, all from the
/// live <see cref="EmitterFieldShotLayer"/> schedule (GDScript cannot read the C# tuples the schedule returns).
/// </summary>
public partial class EmitterFieldShotProbe : RefCounted
{
    private readonly List<EmitterFieldShot> _scratch = new();

    /// <summary>The included regular emitter nearest (around wrapped, across) to the given point: beam, origin, contact and arrival age; empty when no schedule.</summary>
    public Godot.Collections.Dictionary NearestBeam(Node layerNode, double around, double across, double circumference)
    {
        var result = new Godot.Collections.Dictionary();
        if (layerNode is not EmitterFieldShotLayer layer || layer.Schedule is not { } s) return result;
        int best = -1;
        double bestD = double.MaxValue;
        foreach (int b in s.Beams)
        {
            if (b == 0) continue;   // the main spire is taller; use a regular emitter
            (double a, _, double x) = s.OriginOf(b);
            double da = (a - around) % circumference;
            if (da > 0.5 * circumference) da -= circumference; else if (da < -0.5 * circumference) da += circumference;
            double d = Math.Sqrt(da * da + (x - across) * (x - across));
            if (d < bestD) { bestD = d; best = b; }
        }
        if (best < 0) return result;
        (double oa, double ov, double ox) = s.OriginOf(best);
        (double ca, double cv, double cx) = s.ContactOf(best);
        result["beam"] = best;
        result["origin_around"] = oa; result["origin_vertical"] = ov; result["origin_across"] = ox;
        result["contact_around"] = ca; result["contact_vertical"] = cv; result["contact_across"] = cx;
        result["arrival_age"] = s.ArrivalAgeOf(best);
        result["life"] = s.MaxLifeSeconds;
        result["distance"] = bestD;
        return result;
    }

    /// <summary>Launch and arrival time of the first shot of <paramref name="beam"/> launching in (from, from + horizon]; empty when none.</summary>
    public Godot.Collections.Dictionary NextShot(Node layerNode, int beam, double from, double horizon)
    {
        var result = new Godot.Collections.Dictionary();
        if (layerNode is not EmitterFieldShotLayer layer || layer.Schedule is not { } s) return result;
        s.CollectLaunches(from, from + horizon, _scratch);
        foreach (EmitterFieldShot shot in _scratch)
        {
            if (shot.Beam != beam) continue;
            result["id"] = shot.Id;
            result["launch"] = shot.LaunchTimeSeconds;
            result["arrival"] = shot.ArrivalTimeSeconds;
            result["energy"] = shot.Energy;
            return result;
        }
        return result;
    }

    /// <summary>One line: the live bus's EmitterShot ripples and the adapter counters.</summary>
    public string BusState(Node hostNode, long id)
    {
        if (hostNode is not RingworldTechDemo.ContainmentField.RingContainmentDisturbanceHost host || host.Bus is not { } bus) return "no bus";
        bool live = false;
        foreach (var r in bus.Slots) if (r != null && r.SourceId == id) live = true;
        var a = host.EmitterShotAdapter;
        return FormattableString.Invariant($"bus emitter_live={bus.LiveOf(RingworldTechDemo.ContainmentField.ContainmentDisturbanceKind.EmitterShot)} shot_ripple_live={live} live={bus.LiveCount} published={a?.Published ?? -1} rejected={a?.Rejected ?? -1} out_of_range={a?.OutOfRange ?? -1}");
    }

    /// <summary>One line: the live state of shot <paramref name="id"/> (phase, envelope, flare, spread, altitude) and the layer counters.</summary>
    public string ShotState(Node layerNode, long id)
    {
        if (layerNode is not EmitterFieldShotLayer layer || layer.Schedule is not { } s) return "no schedule";
        string state = "not active";
        foreach (EmitterFieldShot shot in layer.ActiveShots)
        {
            if (shot.Id != id) continue;
            EmitterFieldShotState st = s.Evaluate(shot, layer.NowSeconds);
            state = FormattableString.Invariant($"phase={st.Phase} s={st.DistanceAlongMeters:0} vertical={st.VerticalMeters:0} env={st.OrbEnvelope:0.000} flare={st.Flare:0.000} spread={st.Spread:0.000}");
        }
        return FormattableString.Invariant($"t={layer.NowSeconds:0.000} {state} active={layer.ActiveShots.Count} near={layer.DrawnNear} far={layer.DrawnFar} us={layer.LastUpdateMicroseconds}");
    }
}
