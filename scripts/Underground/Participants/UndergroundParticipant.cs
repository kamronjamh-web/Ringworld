using System;
using Godot;

namespace RingworldTechDemo.Underground;

/// <summary>Independently toggleable participant groups, matching the Underground section of the tuning drawer.</summary>
[Flags]
public enum UndergroundGroups
{
    None = 0,
    AudioWeather = 1,
    Vegetation = 2,
    Clouds = 4,
    Water = 8,
    FarTerrain = 16,
    Shadows = 32,
    All = AudioWeather | Vegetation | Clouds | Water | FarTerrain | Shadows
}

/// <summary>
/// What one participant should currently do. Every field is already filtered by the participant's group toggle,
/// so a disabled group always receives the all-clear state and resumes within the same frame.
/// </summary>
/// <param name="Suspend">Fully enclosed (blend 1): cheap holds and freezes apply.</param>
/// <param name="Hide">Deep, fully enclosed and sun/sky already darkened: surface geometry may be hidden.</param>
/// <param name="ShadowSuspend">Fully enclosed and sun/sky already darkened: shadow maps may be skipped.</param>
/// <param name="Blend">Continuous enclosure blend for fades.</param>
public readonly record struct UndergroundParticipantState(bool Suspend, bool Hide, bool ShadowSuspend, float Blend)
{
    public static UndergroundParticipantState Clear => new(false, false, false, 0f);
}

/// <summary>
/// One small adapter per surface system. Adapters hold, never free: they only flip the new suspension API of the
/// system they wrap, and a missing system makes the adapter a no-op.
/// </summary>
public abstract class UndergroundParticipant
{
    public abstract string Name { get; }
    public abstract UndergroundGroups Group { get; }
    /// <summary>True once the wrapped system was found.</summary>
    public bool Present { get; protected set; }

    /// <summary>Resolves the wrapped system relative to the scene root. Called once.</summary>
    public abstract void Bind(Node sceneRoot);
    public abstract void Apply(in UndergroundParticipantState state);
    /// <summary>Observable state used by the round-trip smoke and the status label; excludes clocks.</summary>
    public abstract string Describe();
    /// <summary>Instance ids that must stay valid for the whole session (nothing may be freed by suspension).</summary>
    public virtual ulong[] RetainedInstanceIds() => Array.Empty<ulong>();

    protected static T? Find<T>(Node root, string path) where T : class
    {
        Node? node = root.GetNodeOrNull(path);
        return node as T;
    }

    protected static ulong IdOf(GodotObject? value) => value != null && GodotObject.IsInstanceValid(value) ? value.GetInstanceId() : 0UL;
}
