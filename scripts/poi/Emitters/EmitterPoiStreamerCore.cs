using System;
using System.Collections.Generic;
using RingworldTechDemo.Terrain.Sites;

namespace RingworldTechDemo.Poi;

/// <summary>Effective streamer values (the node resolves per-kind radii before building this).</summary>
public readonly record struct EmitterPoiStreamerConfig(
    bool Enabled, double RegularLoadM, double MainLoadM, double UnloadMarginM, double MinDwellS, double CollisionOnM, double CollisionOffMarginM)
{
    public double LoadFor(SiteKind kind) => kind == SiteKind.Main ? MainLoadM : RegularLoadM;
    public double UnloadFor(SiteKind kind) => LoadFor(kind) + UnloadMarginM;
}

/// <summary>One site near the observer: <see cref="Distance"/> is the mid-height metric, <see cref="GroundDistance"/> the pad metric.</summary>
public readonly record struct EmitterPoiObservation(int Site, SiteKind Kind, double Distance, double GroundDistance);

public sealed class EmitterPoiSlotState
{
    public int Index { get; init; }
    public SiteKind Kind { get; init; }
    public int Site { get; internal set; } = -1;
    public double AssignedAt { get; internal set; }
    public double Distance { get; internal set; }
    public double GroundDistance { get; internal set; }
    public bool Collision { get; internal set; }
    public bool Active => Site >= 0;
}

public enum EmitterPoiSlotEventKind { Assign, Release, CollisionOn, CollisionOff }

public readonly record struct EmitterPoiSlotEvent(EmitterPoiSlotEventKind Kind, int Slot, int Site);

/// <summary>
/// Pure per-frame state machine of the emitter POI pool (docs/emitter-poi-streaming-spec.md section A.4): load/unload
/// hysteresis with a dwell, nearest-first assignment per kind, eviction of out-of-range slots and collision gating.
/// Releases are always emitted before assigns; collision events come last.
/// </summary>
public sealed class EmitterPoiStreamerCore
{
    public const double RegularMidHeightMeters = 900d, MainMidHeightMeters = 2000d;
    private const double DwellEpsilon = 1e-9;

    private static readonly Comparison<EmitterPoiObservation> NearestFirst = static (a, b) => a.Distance.CompareTo(b.Distance);

    private readonly EmitterPoiSlotState[] _slots;
    private readonly List<EmitterPoiObservation> _candidates = new();
    private readonly List<EmitterPoiSlotEvent> _assigns = new();

    public EmitterPoiStreamerConfig Config { get; set; }
    public IReadOnlyList<EmitterPoiSlotState> Slots => _slots;
    public int ActiveCount { get; private set; }

    /// <summary>Slot index i has kind <paramref name="slotKinds"/>[i]; kinds without a slot are never assigned.</summary>
    public EmitterPoiStreamerCore(ReadOnlySpan<SiteKind> slotKinds, EmitterPoiStreamerConfig config)
    {
        Config = config;
        _slots = new EmitterPoiSlotState[slotKinds.Length];
        for (int i = 0; i < _slots.Length; i++) _slots[i] = new EmitterPoiSlotState { Index = i, Kind = slotKinds[i] };
    }

    public bool HasKind(SiteKind kind)
    {
        foreach (EmitterPoiSlotState slot in _slots) if (slot.Kind == kind) return true;
        return false;
    }

    /// <summary>Wraps <paramref name="delta"/> to [-C/2, C/2].</summary>
    public static double SignedWrap(double delta, double circumference)
    {
        if (circumference <= 0d) return delta;
        delta %= circumference;
        if (delta > circumference * 0.5d) delta -= circumference;
        else if (delta < -circumference * 0.5d) delta += circumference;
        return delta;
    }

    /// <summary>Distance to the spire mid-height (900 m regular, 2000 m main) above the pad.</summary>
    public static double Metric(double du, double dw, double padM, double observerVerticalM, SiteKind kind)
    {
        double dv = padM + (kind == SiteKind.Main ? MainMidHeightMeters : RegularMidHeightMeters) - observerVerticalM;
        return Math.Sqrt(du * du + dw * dw + dv * dv);
    }

    /// <summary>Distance to the pad itself, used for collision gating.</summary>
    public static double GroundMetric(double du, double dw, double padM, double observerVerticalM)
    {
        double dv = padM - observerVerticalM;
        return Math.Sqrt(du * du + dw * dw + dv * dv);
    }

    public int FindSlotForSite(int site)
    {
        if (site < 0) return -1;
        foreach (EmitterPoiSlotState slot in _slots) if (slot.Site == site) return slot.Index;
        return -1;
    }

    /// <summary>Releases every active slot (emitting Release events).</summary>
    public void ReleaseAll(List<EmitterPoiSlotEvent> events)
    {
        foreach (EmitterPoiSlotState slot in _slots)
            if (slot.Active) Release(slot, events);
    }

    private void Release(EmitterPoiSlotState slot, List<EmitterPoiSlotEvent> events)
    {
        events.Add(new EmitterPoiSlotEvent(EmitterPoiSlotEventKind.Release, slot.Index, slot.Site));
        slot.Site = -1;
        slot.Collision = false;
        slot.Distance = slot.GroundDistance = double.PositiveInfinity;
        ActiveCount--;
    }

    /// <summary>
    /// Advances the pool to <paramref name="now"/>. <paramref name="observations"/> must list every site closer than its kind's
    /// unload radius; an active site that is absent counts as infinitely far.
    /// </summary>
    public void Update(double now, ReadOnlySpan<EmitterPoiObservation> observations, List<EmitterPoiSlotEvent> events)
    {
        EmitterPoiStreamerConfig config = Config;
        if (!config.Enabled) { ReleaseAll(events); return; }

        // 1. Refresh active distances, then release what left the unload radius after the dwell.
        foreach (EmitterPoiSlotState slot in _slots)
        {
            if (!slot.Active) continue;
            slot.Distance = slot.GroundDistance = double.PositiveInfinity;
            foreach (EmitterPoiObservation observation in observations)
                if (observation.Site == slot.Site) { slot.Distance = observation.Distance; slot.GroundDistance = observation.GroundDistance; break; }
            if (slot.Distance > config.UnloadFor(slot.Kind) && now - slot.AssignedAt + DwellEpsilon >= config.MinDwellS)
                Release(slot, events);
        }

        // 2. Assign unassigned in-range sites, nearest first.
        _candidates.Clear();
        _assigns.Clear();
        foreach (EmitterPoiObservation observation in observations)
            if (observation.Distance < config.LoadFor(observation.Kind) && FindSlotForSite(observation.Site) < 0 && HasKind(observation.Kind))
                _candidates.Add(observation);
        _candidates.Sort(NearestFirst);
        foreach (EmitterPoiObservation candidate in _candidates)
        {
            EmitterPoiSlotState? target = null;
            foreach (EmitterPoiSlotState slot in _slots)
                if (!slot.Active && slot.Kind == candidate.Kind) { target = slot; break; }
            if (target == null)
            {
                // Evict the farthest same-kind slot that is already outside its unload radius, ignoring the dwell.
                double farthest = config.UnloadFor(candidate.Kind);
                foreach (EmitterPoiSlotState slot in _slots)
                    if (slot.Active && slot.Kind == candidate.Kind && slot.Distance > farthest) { farthest = slot.Distance; target = slot; }
                if (target == null) continue;
                Release(target, events);
            }
            target.Site = candidate.Site;
            target.AssignedAt = now;
            target.Distance = candidate.Distance;
            target.GroundDistance = candidate.GroundDistance;
            target.Collision = false;
            ActiveCount++;
            _assigns.Add(new EmitterPoiSlotEvent(EmitterPoiSlotEventKind.Assign, target.Index, candidate.Site));
        }
        events.AddRange(_assigns);

        // 3. Collision gating, not dwell-gated; slots assigned this frame are included.
        foreach (EmitterPoiSlotState slot in _slots)
        {
            if (!slot.Active) continue;
            if (!slot.Collision && slot.GroundDistance < config.CollisionOnM)
            {
                slot.Collision = true;
                events.Add(new EmitterPoiSlotEvent(EmitterPoiSlotEventKind.CollisionOn, slot.Index, slot.Site));
            }
            else if (slot.Collision && slot.GroundDistance > config.CollisionOnM + config.CollisionOffMarginM)
            {
                slot.Collision = false;
                events.Add(new EmitterPoiSlotEvent(EmitterPoiSlotEventKind.CollisionOff, slot.Index, slot.Site));
            }
        }
    }
}
