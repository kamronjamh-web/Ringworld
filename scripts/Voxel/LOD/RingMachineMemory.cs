using System;
using System.Runtime.InteropServices;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Machine memory facts (physical RAM, dedicated VRAM) used to scale the certification monitor's memory guard.
/// Values are queried once and cached; every failure resolves to 0 and never throws.
/// </summary>
public static class RingMachineMemory
{
    private const int FixedDescriptionChars = 128;
    private const uint SoftwareAdapterFlag = 2;
    public const int MinimumTerrainBudgetMb = 2048;
    public const int MaximumTerrainBudgetMb = 4096;
    public const int UnknownVramTerrainBudgetMb = 2560;

    private static bool _physicalResolved;
    private static long _physicalMemoryBytes;
    private static bool _videoResolved;
    private static long _dedicatedVideoMemoryBytes;
    private static string _videoAdapterDescription = "";

    public static long PhysicalMemoryBytes
    {
        get
        {
            if (!_physicalResolved)
            {
                try { _physicalMemoryBytes = Math.Max(0L, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes); }
                catch (Exception) { _physicalMemoryBytes = 0L; }
                _physicalResolved = true;
            }
            return _physicalMemoryBytes;
        }
    }

    public static long DedicatedVideoMemoryBytes
    {
        get { ResolveVideo(); return _dedicatedVideoMemoryBytes; }
    }

    public static string VideoAdapterDescription
    {
        get { ResolveVideo(); return _videoAdapterDescription; }
    }

    /// <summary>Working-set budget follows physical RAM (50%, 6-16 GiB); the terrain-estimate budget follows VRAM (1/3, 2-4 GiB, 2.5 GiB when unknown).</summary>
    public static (int workingSetLimitMb, int terrainLimitMb) ResolveLimits(long physicalBytes, long vramBytes, int baseWorkingSetMb, int baseTerrainMb)
    {
        int workingSetMb = baseWorkingSetMb;
        if (physicalBytes > 0)
            workingSetMb = (int)Math.Clamp(physicalBytes / (1024L * 1024L) / 2L, 6144L, 16384L);
        // The terrain estimate is mostly GPU mesh memory. Very High needs ~1.6 GiB once the global HLOD is queued (Ultra more), so the
        // budget never drops below 2 GiB: the fallback is a safety net for genuinely small cards, not a preset limit.
        int terrainMb = vramBytes > 0
            ? (int)Math.Clamp(vramBytes / (1024L * 1024L) / 3L, MinimumTerrainBudgetMb, MaximumTerrainBudgetMb)
            : Math.Max(baseTerrainMb, UnknownVramTerrainBudgetMb);
        return (workingSetMb, terrainMb);
    }

    private static void ResolveVideo()
    {
        if (_videoResolved)
            return;
        _videoResolved = true;
        try
        {
            if (OperatingSystem.IsWindows())
                QueryDxgi(out _dedicatedVideoMemoryBytes, out _videoAdapterDescription);
        }
        catch (Exception)
        {
            _dedicatedVideoMemoryBytes = 0L;
            _videoAdapterDescription = "";
        }
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern unsafe int CreateDXGIFactory1(Guid* riid, void** factory);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DxgiAdapterDesc1
    {
        public fixed char Description[FixedDescriptionChars];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    private static unsafe void QueryDxgi(out long dedicatedBytes, out string description)
    {
        dedicatedBytes = 0L;
        description = "";
        string renderer = "";
        try { renderer = RenderingServer.GetVideoAdapterName() ?? ""; }
        catch (Exception) { }

        Guid factoryIid = new("770aae78-f26f-4dba-a829-253c83d1b387");
        void* factory = null;
        if (CreateDXGIFactory1(&factoryIid, &factory) < 0 || factory == null)
            return;
        try
        {
            var enumAdapters = (delegate* unmanaged[Stdcall]<void*, uint, void**, int>)(*(void***)factory)[12];
            long bestBytes = -1L;
            string bestName = "";
            bool matched = false;
            for (uint index = 0; index < 64; index++)
            {
                void* adapter = null;
                if (enumAdapters(factory, index, &adapter) < 0 || adapter == null)
                    break;
                try
                {
                    var getDesc = (delegate* unmanaged[Stdcall]<void*, DxgiAdapterDesc1*, int>)(*(void***)adapter)[10];
                    DxgiAdapterDesc1 desc = default;
                    if (getDesc(adapter, &desc) < 0 || (desc.Flags & SoftwareAdapterFlag) != 0)
                        continue;
                    string name = new string(desc.Description).Trim();
                    long bytes = (long)desc.DedicatedVideoMemory;
                    bool nameMatch = name.Length > 0 && renderer.Length > 0
                        && (renderer.Contains(name, StringComparison.OrdinalIgnoreCase) || name.Contains(renderer, StringComparison.OrdinalIgnoreCase));
                    if (nameMatch && !matched)
                    {
                        matched = true;
                        bestBytes = bytes;
                        bestName = name;
                    }
                    else if (!matched && bytes > bestBytes)
                    {
                        bestBytes = bytes;
                        bestName = name;
                    }
                }
                finally
                {
                    ((delegate* unmanaged[Stdcall]<void*, uint>)(*(void***)adapter)[2])(adapter);
                }
            }
            if (bestBytes > 0)
            {
                dedicatedBytes = bestBytes;
                description = bestName;
            }
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<void*, uint>)(*(void***)factory)[2])(factory);
        }
    }
}
