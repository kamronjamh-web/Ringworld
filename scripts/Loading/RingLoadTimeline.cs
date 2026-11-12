using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Loading;

/// <summary>
/// Timestamped marks of a world load (L0 of docs/load-time-plan.md). Logging only: marks never change what the load does.
/// Thread safe; marks are cleared when a load begins and read back by the loading owner for WORLD_LOADING_REPORT.
/// </summary>
public static class RingLoadTimeline
{
    private static readonly object Gate = new();
    private static readonly List<(ulong Usec, string Name, string Detail)> Marks = new();
    private const int MaxMarks = 512;

    public static void Reset()
    {
        lock (Gate) Marks.Clear();
    }

    public static void Mark(string name, string detail = "")
    {
        ulong now = Time.GetTicksUsec();
        lock (Gate)
        {
            if (Marks.Count < MaxMarks) Marks.Add((now, name, detail));
        }
    }

    /// <summary>[{name, detail, usec}] in order; usec is the engine tick (Time.get_ticks_usec), so the caller subtracts its own start.</summary>
    public static Godot.Collections.Array Snapshot()
    {
        var result = new Godot.Collections.Array();
        lock (Gate)
        {
            foreach ((ulong usec, string name, string detail) in Marks)
                result.Add(new Godot.Collections.Dictionary { ["name"] = name, ["detail"] = detail, ["usec"] = (long)usec });
        }
        return result;
    }
}
