using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

public partial class MemoryGuardScalingTest : RefCounted
{
    private const long Gib = 1024L * 1024L * 1024L;

    public Godot.Collections.Dictionary Run()
    {
        string failure = "";
        Check(ref failure, 96 * Gib, 12 * Gib, 6144, 1536, 16384, 4096);
        Check(ref failure, 16 * Gib, 4 * Gib, 6144, 1536, 8192, 2048);
        Check(ref failure, 8 * Gib, 8 * Gib, 6144, 1536, 6144, 2730);
        Check(ref failure, 0, 0, 6144, 1536, 6144, 2560);
        Check(ref failure, 64 * Gib, 24 * Gib, 6144, 1536, 16384, 4096);
        long physical = RingMachineMemory.PhysicalMemoryBytes;
        long vram = RingMachineMemory.DedicatedVideoMemoryBytes;
        string adapter = RingMachineMemory.VideoAdapterDescription;
        if (physical <= 0)
            failure += "PhysicalMemoryBytes was not positive; ";
        return new Godot.Collections.Dictionary
        {
            ["passed"] = failure.Length == 0,
            ["detail"] = failure.Length == 0
                ? $"ram_gib={physical / (double)Gib:0.0} vram_gib={vram / (double)Gib:0.0} adapter={adapter}"
                : failure,
        };
    }

    private static void Check(ref string failure, long physical, long vram, int baseWs, int baseTerrain, int expectWs, int expectTerrain)
    {
        var (ws, terrain) = RingMachineMemory.ResolveLimits(physical, vram, baseWs, baseTerrain);
        if (ws != expectWs || terrain != expectTerrain)
            failure += $"ResolveLimits({physical / Gib} GiB, {vram / Gib} GiB) = ({ws}, {terrain}) expected ({expectWs}, {expectTerrain}); ";
    }
}
