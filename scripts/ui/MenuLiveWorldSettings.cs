using Godot;

namespace RingworldTechDemo.UI;

/// <summary>
/// Start-menu live-world tunables. To add a row: add the property here (with its clamp in <see cref="Sanitize"/>), its range to the
/// row list in <see cref="MenuLiveWorldTuningDrawer"/>, then read it where it is consumed.
/// </summary>
[GlobalClass]
public partial class MenuLiveWorldSettings : Resource
{
    /// <summary>Hard ceiling for the menu camera above ground; matches the snapshot placement band in the loading bridge.</summary>
    public const float MaxCruiseAltitudeKm = 18f;

    /// <summary>Warm-world cruise height above the saved spot's ground when no valid snapshot pose is used.</summary>
    [Export] public float CruiseAltitudeKm { get; set; } = 16f;
    /// <summary>Warm-world cruise camera pitch in degrees (negative looks down).</summary>
    [Export] public float CruisePitchDegrees { get; set; } = -25f;
    /// <summary>Seconds the boot cover dissolves into the live view.</summary>
    [Export] public float CrossfadeSeconds { get; set; } = 2.5f;
    /// <summary>Starting zoom of the boot snapshot's Ken Burns drift, as a percentage of the screen.</summary>
    [Export] public float SnapshotDriftPercent { get; set; } = 8f;
    /// <summary>The live menu camera loiters within this distance of your save spot.</summary>
    [Export] public float LoiterRadiusKm { get; set; } = 30f;
    /// <summary>Ground speed of the fly-in when you press Continue.</summary>
    [Export] public float RushSpeedKmPerSecond { get; set; } = 1.2f;
    /// <summary>Seconds the Continue descent takes from the hold point down to first person.</summary>
    [Export] public float DescentSeconds { get; set; } = 12f;

    public void Sanitize()
    {
        CruiseAltitudeKm = Clean(CruiseAltitudeKm, 16f, 8f, MaxCruiseAltitudeKm);
        CruisePitchDegrees = Clean(CruisePitchDegrees, -25f, -60f, 0f);
        CrossfadeSeconds = Clean(CrossfadeSeconds, 2.5f, .5f, 6f);
        SnapshotDriftPercent = Clean(SnapshotDriftPercent, 8f, 0f, 20f);
        LoiterRadiusKm = Clean(LoiterRadiusKm, 30f, 5f, 60f);
        RushSpeedKmPerSecond = Clean(RushSpeedKmPerSecond, 1.2f, .3f, 3f);
        DescentSeconds = Clean(DescentSeconds, 12f, 4f, 30f);
    }

    public void CopyFrom(MenuLiveWorldSettings other)
    {
        CruiseAltitudeKm = other.CruiseAltitudeKm;
        CruisePitchDegrees = other.CruisePitchDegrees;
        CrossfadeSeconds = other.CrossfadeSeconds;
        SnapshotDriftPercent = other.SnapshotDriftPercent;
        LoiterRadiusKm = other.LoiterRadiusKm;
        RushSpeedKmPerSecond = other.RushSpeedKmPerSecond;
        DescentSeconds = other.DescentSeconds;
        Sanitize();
    }

    private static float Clean(float value, float fallback, float min, float max)
        => float.IsFinite(value) ? Mathf.Clamp(value, min, max) : fallback;
}
