using System;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Simulation-clock driven canonical offset for the shared grass gust field.</summary>
internal sealed class RingGrassGustState
{
    private const double RebaselineGapSeconds = 5.0;
    private bool _hasTime;
    private double _time;
    private double _smoothedAround;
    private double _smoothedAcross;
    private double _offsetAround;
    private double _offsetAcross;

    /// <summary>Float diagnostic only. Accumulation stays double precision.</summary>
    public Vector2 OffsetMeters => new((float)_offsetAround, (float)_offsetAcross);
    public double SimulationTime => _time;
    public Vector2 SmoothedWind => new((float)_smoothedAround, (float)_smoothedAcross);
    public Vector2 OriginUv(double around, double across, double aroundPeriod, double acrossPeriod) =>
        new((float)(Modulo(around - _offsetAround, aroundPeriod) / aroundPeriod),
            (float)(Modulo(across - _offsetAcross, acrossPeriod) / acrossPeriod));

    public void Advance(Vector2 resolvedWind, double simulationTime, float travelMultiplier, double aroundPeriod, double acrossPeriod)
    {
        if (!double.IsFinite(simulationTime) || !float.IsFinite(resolvedWind.X) || !float.IsFinite(resolvedWind.Y)) return;
        resolvedWind = resolvedWind.Clamp(new Vector2(-200, -200), new Vector2(200, 200));
        if (!_hasTime || simulationTime < _time || simulationTime - _time > RebaselineGapSeconds)
        {
            _hasTime = true;
            _time = simulationTime;
            _smoothedAround = resolvedWind.X;
            _smoothedAcross = resolvedWind.Y;
            return;
        }
        double delta = simulationTime - _time;
        _time = simulationTime;
        if (delta <= 0) return;
        double blend = 1d - Math.Exp(-Math.Min(20.0, delta * 3.0));
        _smoothedAround += (resolvedWind.X - _smoothedAround) * blend;
        _smoothedAcross += (resolvedWind.Y - _smoothedAcross) * blend;
        double multiplier = float.IsFinite(travelMultiplier) ? Math.Clamp(travelMultiplier, 0f, 4f) : 0d;
        _offsetAround = Modulo(_offsetAround + _smoothedAround * delta * multiplier, Math.Max(1d, aroundPeriod));
        _offsetAcross = Modulo(_offsetAcross + _smoothedAcross * delta * multiplier, Math.Max(1d, acrossPeriod));
    }

    public void Reset() { _hasTime = false; _time = 0; _smoothedAround = _smoothedAcross = _offsetAround = _offsetAcross = 0; }
    private static double Modulo(double value, double period) { double result = value % period; return result < 0 ? result + period : result; }
}
