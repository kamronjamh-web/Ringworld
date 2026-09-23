using System;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Inputs that control adaptive FFT work without requiring Godot objects.</summary>
public readonly record struct RingWaterFftUpdateSchedulerConfig(
    bool AdaptiveUpdatesEnabled,
    float MinimumUpdatesPerSecond,
    float FullRateDistanceMeters,
    float FullRateAreaSquareKilometers,
    float IdleGraceSeconds,
    float GeometryUpdatesPerSecond,
    float DetailUpdatesPerSecond,
    int CascadeCount);

/// <summary>Per-frame scheduling result. DueMask contains at most one update per cascade.</summary>
public readonly record struct RingWaterFftUpdateSchedulerStep(
    float GeometryUpdatesPerSecond,
    float DetailUpdatesPerSecond,
    int DueMask,
    bool Resume,
    bool HasDemand,
    bool IsGrace,
    string Status);

/// <summary>Stateful, allocation-free FFT dispatch scheduler.</summary>
public sealed class RingWaterFftUpdateScheduler
{
    private readonly double[] _accumulators = new double[4];
    private float _geometryRate;
    private float _detailRate;
    private double _idleSeconds;
    private bool _hadDemand;
    private bool _forceUpdateAll;

    public void Reset()
    {
        Array.Fill(_accumulators, 0d);
        _geometryRate = 0;
        _detailRate = 0;
        _idleSeconds = 0;
        _hadDemand = false;
        _forceUpdateAll = false;
    }

    public void ForceUpdateAll() => _forceUpdateAll = true;

    public RingWaterFftUpdateSchedulerStep Step(
        in RingWaterFftDemandSnapshot demand,
        in RingWaterFftUpdateSchedulerConfig config,
        double deltaSeconds)
    {
        double delta = Math.Max(0d, double.IsFinite(deltaSeconds) ? deltaSeconds : 0d);
        double smoothingDelta = Math.Min(delta, .25d);
        int cascades = Math.Clamp(config.CascadeCount, 1, 4);
        float geometryCap = Math.Clamp(FiniteOr(config.GeometryUpdatesPerSecond, 5f), 5f, 120f);
        float detailCap = Math.Clamp(FiniteOr(config.DetailUpdatesPerSecond, 5f), 5f, 120f);
        float minimum = Math.Clamp(FiniteOr(config.MinimumUpdatesPerSecond, 1f), 1f, 30f);
        float geometryMinimum = Math.Min(minimum, geometryCap);
        float detailMinimum = Math.Min(minimum, detailCap);
        float grace = Math.Clamp(FiniteOr(config.IdleGraceSeconds, 0f), 0f, 5f);
        bool hasDemand = demand.HasDemand;
        bool resume = hasDemand && !_hadDemand;
        bool isGrace = false;
        float targetGeometry;
        float targetDetail;
        string status;

        if (!config.AdaptiveUpdatesEnabled)
        {
            _idleSeconds = 0;
            _hadDemand = true;
            targetGeometry = geometryCap;
            targetDetail = detailCap;
            status = "fixed";
        }
        else if (hasDemand)
        {
            _idleSeconds = 0;
            _hadDemand = true;
            float factor = DemandFactor(demand, config);
            targetGeometry = Mathf.Lerp(geometryMinimum, geometryCap, factor);
            targetDetail = Mathf.Lerp(detailMinimum, detailCap, factor);
            status = factor >= .999f ? "full" : "low";
        }
        else if (_hadDemand && (_idleSeconds += delta) < grace)
        {
            targetGeometry = geometryMinimum;
            targetDetail = detailMinimum;
            isGrace = true;
            status = "grace";
        }
        else
        {
            // A truly dry world must stop immediately. Retaining a decaying rate
            // here would keep launching FFT work for seconds after the grace.
            _geometryRate = 0;
            _detailRate = 0;
            Array.Fill(_accumulators, 0d);
            _hadDemand = false;
            return new(0, 0, 0, false, false, false, "dormant");
        }

        // Rising demand is visible immediately. Releasing work uses a half-second
        // exponential response to avoid rate jitter while terrain metadata changes.
        _geometryRate = Math.Min(geometryCap, targetGeometry >= _geometryRate ? targetGeometry : Relax(_geometryRate, targetGeometry, smoothingDelta));
        _detailRate = Math.Min(detailCap, targetDetail >= _detailRate ? targetDetail : Relax(_detailRate, targetDetail, smoothingDelta));
        if (targetGeometry == 0 && _geometryRate < .01f) _geometryRate = 0;
        if (targetDetail == 0 && _detailRate < .01f) _detailRate = 0;

        int dueMask = 0;
        bool canDispatch = hasDemand || !config.AdaptiveUpdatesEnabled || isGrace;
        if (canDispatch && (resume || _forceUpdateAll))
        {
            for (int cascade = 0; cascade < cascades; cascade++) dueMask |= 1 << cascade;
            Array.Fill(_accumulators, 0d);
            _forceUpdateAll = false;
        }
        else
        {
            int primaryCascade = cascades >= 2 ? 1 : 0;
            for (int cascade = 0; cascade < cascades; cascade++)
            {
                float rate = cascade == primaryCascade ? _geometryRate : _detailRate;
                if (rate <= 0) { _accumulators[cascade] = 0; continue; }
                double period = 1d / rate;
                _accumulators[cascade] += delta;
                if (_accumulators[cascade] >= period)
                {
                    _accumulators[cascade] %= period;
                    dueMask |= 1 << cascade;
                }
            }
        }
        return new(_geometryRate, _detailRate, dueMask, resume, hasDemand, isGrace, status);
    }

    private static float DemandFactor(in RingWaterFftDemandSnapshot demand, in RingWaterFftUpdateSchedulerConfig config)
    {
        float fullDistance = Math.Clamp(FiniteOr(config.FullRateDistanceMeters, 25f), 25f, 1000f);
        float fullAreaKm2 = Math.Clamp(FiniteOr(config.FullRateAreaSquareKilometers, .01f), .01f, 16f);
        float distance = FiniteOr(demand.NearestDistanceMeters, float.PositiveInfinity);
        float proximity = distance <= fullDistance ? 1f : distance >= fullDistance * 2f ? 0f : 1f - (distance - fullDistance) / fullDistance;
        double weightedArea = double.IsFinite(demand.WeightedAreaSquareMeters)
            ? Math.Max(0d, demand.WeightedAreaSquareMeters)
            : 0d;
        float area = (float)Math.Clamp(weightedArea / (fullAreaKm2 * 1_000_000d), 0d, 1d);
        return Math.Max(proximity, area);
    }

    private static float Relax(float current, float target, double delta) => Mathf.Lerp(current, target, 1f - Mathf.Exp((float)-delta / .5f));
    private static float FiniteOr(float value, float fallback) => float.IsFinite(value) ? value : fallback;
}
