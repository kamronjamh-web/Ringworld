using System;

namespace RingworldTechDemo.Poi;

public enum WeatherLungDetailEvent { None, Load, Unload }

/// <summary>
/// Pure hysteresis and dwell rules of the Weather Lung streamer. The Detail scene loads the moment the observer is inside
/// <c>LoadMeters</c> and unloads only after the observer has stayed beyond <c>LoadMeters + UnloadMarginMeters</c> for
/// <c>UnloadDwellSeconds</c> without interruption, so an observer oscillating around the boundary never makes it flap.
/// Collision bodies use a separate on / off-margin gate per body.
/// </summary>
public sealed class WeatherLungStreamCore
{
    public const double DefaultUnloadDwellSeconds = 2d;
    public const double CollisionOffMarginMeters = 500d;

    private double _beyondSince = double.NaN;

    public double LoadMeters { get; set; } = 36_000d;
    public double UnloadMarginMeters { get; set; } = 8_000d;
    public double UnloadDwellSeconds { get; set; } = DefaultUnloadDwellSeconds;
    public bool Enabled { get; set; } = true;
    /// <summary>True from the Load event until the Unload event.</summary>
    public bool DetailWanted { get; private set; }

    public double UnloadMeters => LoadMeters + UnloadMarginMeters;

    /// <summary>Advances the detail state for an observer <paramref name="distanceMeters"/> from the station centre at time <paramref name="now"/>.</summary>
    public WeatherLungDetailEvent Update(double now, double distanceMeters)
    {
        if (!Enabled)
        {
            _beyondSince = double.NaN;
            if (!DetailWanted) return WeatherLungDetailEvent.None;
            DetailWanted = false;
            return WeatherLungDetailEvent.Unload;
        }
        if (!double.IsFinite(distanceMeters)) return WeatherLungDetailEvent.None;
        if (!DetailWanted)
        {
            if (distanceMeters >= LoadMeters) return WeatherLungDetailEvent.None;
            DetailWanted = true;
            _beyondSince = double.NaN;
            return WeatherLungDetailEvent.Load;
        }
        if (distanceMeters <= UnloadMeters) { _beyondSince = double.NaN; return WeatherLungDetailEvent.None; }
        if (double.IsNaN(_beyondSince)) _beyondSince = now;
        if (now - _beyondSince < UnloadDwellSeconds) return WeatherLungDetailEvent.None;
        DetailWanted = false;
        _beyondSince = double.NaN;
        return WeatherLungDetailEvent.Unload;
    }

    /// <summary>Forgets the dwell timer and the wanted flag (the detail scene was released externally).</summary>
    public void Reset()
    {
        DetailWanted = false;
        _beyondSince = double.NaN;
    }

    /// <summary>Collision gate: on within <paramref name="onMeters"/> of the body bounds, off again beyond <paramref name="onMeters"/> + <paramref name="offMarginMeters"/>.</summary>
    public static bool CollisionGate(bool current, double distanceToBoundsMeters, double onMeters, double offMarginMeters)
    {
        if (!double.IsFinite(distanceToBoundsMeters)) return false;
        return current ? distanceToBoundsMeters <= onMeters + Math.Max(0d, offMarginMeters) : distanceToBoundsMeters < onMeters;
    }

    /// <summary>Distance from a point to an axis-aligned box (0 inside).</summary>
    public static double DistanceToBox(double px, double py, double pz, double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
    {
        double dx = Math.Max(Math.Max(minX - px, 0d), px - maxX);
        double dy = Math.Max(Math.Max(minY - py, 0d), py - maxY);
        double dz = Math.Max(Math.Max(minZ - pz, 0d), pz - maxZ);
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
