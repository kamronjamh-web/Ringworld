using System;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Water follows mean storm wind on the simulation clock, never fast gusts.</summary>
public sealed class RingWaterWindResponse
{
    private double _lastTime = double.NaN;
    private double _lastSpectrumTime = double.NegativeInfinity;
    private bool _initialized;
    public Vector2 Wind { get; private set; }

    public Vector2 Step(Vector2 meanWind, double simulationTime, float responseSeconds, bool followWeather)
    {
        if (!float.IsFinite(meanWind.X) || !float.IsFinite(meanWind.Y)) meanWind = Vector2.Zero;
        if (!double.IsFinite(simulationTime)) simulationTime = double.IsFinite(_lastTime) ? _lastTime : 0;
        bool rewind = double.IsFinite(_lastTime) && simulationTime < _lastTime;
        double elapsed = double.IsFinite(_lastTime) ? Math.Max(0, simulationTime - _lastTime) : 0;
        if (!_initialized || rewind || !followWeather)
            Wind = meanWind;
        else
        {
            double seconds = float.IsFinite(responseSeconds) ? Math.Clamp(responseSeconds, .1f, 120f) : 8;
            Wind = Wind.Lerp(meanWind, (float)(1 - Math.Exp(-elapsed / seconds)));
        }
        if (rewind) _lastSpectrumTime = double.NegativeInfinity;
        _lastTime = simulationTime;
        _initialized = true;
        return Wind;
    }

    public bool SpectrumUpdateDue(double simulationTime, bool followWeather)
        => !followWeather || simulationTime < _lastSpectrumTime || simulationTime - _lastSpectrumTime >= 1;

    public void MarkSpectrumUpdated(double simulationTime) => _lastSpectrumTime = simulationTime;

    public static float DirectionDifferenceDegrees(float a, float b)
        => Math.Abs(Mathf.Wrap(a - b, -180f, 180f));

    /// <summary>Compute before narrowing to float, preserving arbitrary long clocks and rewinds.</summary>
    public static float BoundedPhase(double simulationTime, double speed, double coefficient)
    {
        if (!double.IsFinite(simulationTime) || !double.IsFinite(speed)) return 0;
        double phase = simulationTime * speed * coefficient % Math.Tau;
        return (float)(phase < 0 ? phase + Math.Tau : phase);
    }
}
