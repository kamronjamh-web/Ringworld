using System;

namespace RingworldTechDemo.Underground;

public enum UndergroundMode { Surface, Entering, Underground, Exiting }

/// <summary>Debug/menu override applied on top of the detected state.</summary>
public enum UndergroundModeOverride { Auto, ForceSurface, ForceUnderground }

/// <summary>Tunable enclosure thresholds shared by the state machine, the settings resource and the drawer.</summary>
public record struct UndergroundEnclosureParameters()
{
    public float EnterDepthMeters { get; set; } = 24f;
    public float ExitDepthMeters { get; set; } = 12f;
    public float EnterOpennessMax { get; set; } = .06f;
    public float ExitOpennessMin { get; set; } = .18f;
    public float EnterHoldSeconds { get; set; } = 1f;
    public float ExitHoldSeconds { get; set; } = .15f;
    public float BlendSeconds { get; set; } = 1f;
}

/// <summary>One frame of detector output fed to the state machine.</summary>
public readonly record struct UndergroundEnclosureInput(
    float DepthMeters,
    float Openness,
    bool ForceSurface,
    UndergroundModeOverride Override = UndergroundModeOverride.Auto);

/// <summary>
/// Hysteresis and blend for underground mode. Pure C#, no Godot dependency.
/// Enter needs depth and low openness held continuously; exit needs shallow depth or high openness held briefly.
/// </summary>
public sealed class UndergroundEnclosureStateMachine
{
    private float _progress;
    private double _enterTimer, _exitTimer;

    public UndergroundEnclosureParameters Parameters { get; set; } = new();
    public UndergroundMode Mode { get; private set; } = UndergroundMode.Surface;
    public float EnclosureBlend { get; private set; }
    public float Openness { get; private set; } = 1f;
    public float DepthMeters { get; private set; }
    /// <summary>True only when fully enclosed; every suspension participant resumes the frame this clears.</summary>
    public bool SuspensionActive => Mode == UndergroundMode.Underground && EnclosureBlend >= .999f;

    public event Action<UndergroundMode, UndergroundMode>? ModeChanged;

    public void Step(double delta, in UndergroundEnclosureInput input)
    {
        delta = Math.Max(0, delta);
        DepthMeters = input.DepthMeters;
        Openness = input.Openness;
        var p = Parameters;
        if (input.ForceSurface || input.Override == UndergroundModeOverride.ForceSurface) { Reset(); return; }

        bool wantEnter = input.Override == UndergroundModeOverride.ForceUnderground
            || (input.DepthMeters >= p.EnterDepthMeters && input.Openness <= p.EnterOpennessMax);
        bool wantExit = input.Override != UndergroundModeOverride.ForceUnderground
            && (input.DepthMeters <= p.ExitDepthMeters || input.Openness >= p.ExitOpennessMin);
        bool bypassHold = input.Override == UndergroundModeOverride.ForceUnderground;
        _enterTimer = wantEnter ? _enterTimer + delta : 0;
        _exitTimer = wantExit ? _exitTimer + delta : 0;
        bool enterReady = wantEnter && (bypassHold || _enterTimer >= p.EnterHoldSeconds);
        bool exitReady = wantExit && _exitTimer >= p.ExitHoldSeconds;
        double rate = delta / Math.Max(.01, p.BlendSeconds);

        switch (Mode)
        {
            case UndergroundMode.Surface:
                if (enterReady) SetMode(UndergroundMode.Entering);
                break;
            case UndergroundMode.Entering:
                if (exitReady) { SetMode(UndergroundMode.Exiting); break; }
                _progress = (float)Math.Min(1, _progress + rate);
                if (_progress >= 1) SetMode(UndergroundMode.Underground);
                break;
            case UndergroundMode.Underground:
                if (exitReady) SetMode(UndergroundMode.Exiting);
                break;
            case UndergroundMode.Exiting:
                if (enterReady) { SetMode(UndergroundMode.Entering); break; }
                _progress = (float)Math.Max(0, _progress - rate);
                if (_progress <= 0) SetMode(UndergroundMode.Surface);
                break;
        }
        EnclosureBlend = _progress * _progress * (3f - 2f * _progress);
    }

    /// <summary>Instant exit used for fast travel, teleports, loading and an unavailable source.</summary>
    public void Reset()
    {
        _progress = 0;
        EnclosureBlend = 0;
        _enterTimer = _exitTimer = 0;
        SetMode(UndergroundMode.Surface);
    }

    private void SetMode(UndergroundMode next)
    {
        if (next == Mode) return;
        var previous = Mode;
        Mode = next;
        // Fully entered state is exact so suspension never depends on float drift.
        if (next == UndergroundMode.Underground) _progress = 1;
        if (next == UndergroundMode.Surface) _progress = 0;
        ModeChanged?.Invoke(previous, next);
    }
}
