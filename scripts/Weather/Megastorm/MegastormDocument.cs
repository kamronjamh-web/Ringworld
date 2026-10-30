using System;
using System.Collections.Generic;

namespace RingworldTechDemo;

/// <summary>
/// Save-file body of the megastorm (docs/megastorm-design.md section 6.2), the optional <c>Megastorm</c> member of the save manager's runtime document.
/// A plain POCO for System.Text.Json's default options, so the stop time is nullable instead of NaN (default options cannot write NaN).
/// Absent in old saves, which therefore load untriggered.
/// </summary>
public sealed class MegastormDocument
{
    public const int CurrentVersion = 1;
    public int Version { get; set; } = CurrentVersion;
    public string TriggerId { get; set; } = string.Empty;
    public double TriggerTimeSeconds { get; set; }
    public int Direction { get; set; } = 1;
    public int Seed { get; set; }
    public double FormationSeconds { get; set; } = 1800.0;
    public List<MegastormAnchor> Anchors { get; set; } = new();
    /// <summary>Null = not stopped (the model's NaN).</summary>
    public double? StopTimeSeconds { get; set; }
    public double StopFadeSeconds { get; set; }
    /// <summary>PDA database hook (docs/megastorm-design.md 5.4): persisted until a PDA database exists.</summary>
    public bool ResonanceEntryUnlocked { get; set; }
    /// <summary>The settings snapshot taken at trigger time (and updated by live menu changes); required for an exact replay.</summary>
    public MegastormSettings Settings { get; set; } = new();

    public static MegastormDocument FromState(MegastormState s, bool resonanceEntryUnlocked)
    {
        var d = new MegastormDocument
        {
            TriggerId = s.TriggerId ?? string.Empty, TriggerTimeSeconds = s.TriggerTimeSeconds, Direction = s.Direction, Seed = s.Seed,
            FormationSeconds = s.FormationSeconds, StopTimeSeconds = double.IsNaN(s.StopTimeSeconds) ? null : s.StopTimeSeconds,
            StopFadeSeconds = s.StopFadeSeconds, ResonanceEntryUnlocked = resonanceEntryUnlocked, Settings = s.Settings
        };
        foreach (MegastormAnchor a in s.Anchors) d.Anchors.Add(new MegastormAnchor { TimeSeconds = a.TimeSeconds, AroundMeters = a.AroundMeters, Speed = a.Speed });
        return d;
    }

    /// <summary>Null when the document is not a valid triggered storm (empty id, no anchors, unknown newer version): the caller treats it as untriggered.</summary>
    public MegastormState? ToState()
    {
        if (Version < 1 || Version > CurrentVersion || string.IsNullOrEmpty(TriggerId) || Anchors == null || Anchors.Count == 0) return null;
        var s = new MegastormState
        {
            TriggerId = TriggerId, TriggerTimeSeconds = TriggerTimeSeconds, Direction = Direction < 0 ? -1 : 1, Seed = Seed,
            FormationSeconds = FormationSeconds, StopTimeSeconds = StopTimeSeconds ?? double.NaN, StopFadeSeconds = StopFadeSeconds,
            Settings = Settings ?? new MegastormSettings()
        };
        foreach (MegastormAnchor a in Anchors) s.Anchors.Add(new MegastormAnchor { TimeSeconds = a.TimeSeconds, AroundMeters = a.AroundMeters, Speed = a.Speed });
        return s;
    }
}
