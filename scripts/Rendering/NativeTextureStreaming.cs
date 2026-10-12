using System;
using Godot;

namespace RingworldTechDemo.Rendering;

/// <summary>Runtime controls for the pinned Godot 4.8 native mip streamer.</summary>
public static class NativeTextureStreaming
{
    private static GodotObject? Singleton => Engine.HasSingleton("TextureStreaming")
        ? Engine.GetSingleton("TextureStreaming") : null;

    public static bool Available => Singleton is not null;
    public static bool Enabled => Available
        && ProjectSettings.GetSetting("rendering/textures/streaming/enabled", false).AsBool()
        && RenderingServer.GetRenderingDevice() is not null;
    public static int MemoryBudgetMiB => Read("get_memory_budget_mb_override", "rendering/textures/streaming/memory_budget_mb", 512);
    // Dev7 source: min_lod is the finest allowed mip; max_lod is the coarsest.
    // InitialMip is also the coarsest resident floor, including for invisible textures.
    public static int InitialMip => Read("get_max_lod_override", "rendering/textures/streaming/max_lod", 3);
    public static int FinestMip => Read("get_min_lod_override", "rendering/textures/streaming/min_lod", 0);
    public static long UsedBytes => Singleton?.Call("get_memory_budget_bytes_used").AsInt64() ?? 0;
    public static string Status => !Available ? "Native texture streaming unavailable: requires Godot 4.8"
        : !Enabled ? "Native texture streaming inactive: requires Forward+ and enabled project setting"
        : $"Native texture streaming active: {UsedBytes / (1024.0 * 1024.0):F1} MiB / {MemoryBudgetMiB} MiB, mips {FinestMip}..{InitialMip}";

    public static void Apply(int budgetMiB, int initialMip, int finestMip)
    {
        GodotObject? singleton = Singleton;
        if (singleton is null) return;
        int coarsest = Math.Clamp(initialMip, 0, 13);
        int finest = Math.Clamp(finestMip, 0, coarsest);
        singleton.Call("set_memory_budget_mb_override", Math.Clamp(budgetMiB, 64, 8192));
        singleton.Call("set_min_lod_override", finest);
        singleton.Call("set_max_lod_override", coarsest);
    }

    private static int Read(string getter, string projectKey, int fallback)
    {
        long value = Singleton?.Call(getter).AsInt64() ?? -1;
        return value is < 0 or > 65535
            ? ProjectSettings.GetSetting(projectKey, fallback).AsInt32() : (int)value;
    }
}
