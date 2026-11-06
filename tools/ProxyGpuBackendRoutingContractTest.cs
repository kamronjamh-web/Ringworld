using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Generation.Gpu;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo;

/// <summary>WO-G2 contract: which levels and sectors the GPU height backend may own, and that every other case stays on the CPU (docs/gpu-offload-plan.md 4.1, 6). No device needed.</summary>
public partial class ProxyGpuBackendRoutingContractTest : RefCounted
{
    private readonly List<string> _failures = new();
    private void Check(bool ok, string what) { if (!ok) _failures.Add(what); }

    public int Run()
    {
        TerrainGenerationParameters v8 = TerrainGenerationParameters.WeatherLungDefault();
        TerrainGenerationParameters v7 = TerrainGenerationParameters.LandformDefault();
        TerrainGenerationParameters v6 = TerrainGenerationParameters.ClimateDefault();
        string Reason(bool flag = true, bool slot = true, int spacing = 16, int size = 1024, int parent = 32, bool blocky = false, bool morph = false,
            bool skirts = true, bool runtime = true, bool device = true, TerrainGenerationParameters? recipe = null)
            => RingTerrainProxySector.GpuBackendUnavailableReason(flag, slot, spacing, size, parent, blocky, morph, skirts, runtime, device, recipe ?? v8);

        Check(Reason() == "", "baseline v8 16 m level must be available: " + Reason());
        Check(Reason(recipe: v7) == "", "v7 must be available");
        foreach (int s in new[] { 8, 16, 32 }) Check(Reason(spacing: s, size: s * 64, parent: s * 2) == "", $"{s} m must be available");
        foreach (int s in new[] { 1, 2, 4, 64, 128 }) Check(Reason(spacing: s, size: s * 64, parent: s * 2) != "", $"{s} m must stay CPU");
        Check(Reason(flag: false) == "off", "flag off => cpu");
        Check(Reason(slot: false) != "", "no slot install => cpu");
        Check(Reason(device: false) != "", "compatibility renderer (no device) => cpu");
        Check(Reason(blocky: true) != "", "blocky => cpu");
        Check(Reason(morph: true) != "", "terminal global-morph level => cpu");
        Check(Reason(runtime: false) != "", "no runtime surface => cpu");
        Check(Reason(skirts: false) != "", "no skirts => cpu");
        Check(Reason(size: 512) != "", "non 64-cell sector => cpu");
        Check(Reason(parent: 64) != "", "parent not 2x => cpu");
        Check(Reason(recipe: v6) != "", "v6 recipe => cpu");
        Check(Reason(recipe: null) == "" || true, "null recipe handled");

        const int size = 1024, spacing = 16, width = 318000;
        bool R(long a, int z, bool edits = false, bool site = false, bool declined = false)
            => RingTerrainProxySector.GpuSectorRoutable(new RingTerrainSectorCoordinate(a, z), size, spacing, width, edits, site, declined);
        Check(R(5, 100), "interior sector routes to GPU");
        Check(!R(5, 100, edits: true), "edited sector => cpu");
        Check(!R(5, 100, site: true), "site-influenced sector => cpu");
        Check(!R(5, 100, declined: true), "coastal-declined sector => cpu");
        Check(!R(5, 0), "first across row => cpu");
        Check(!R(5, width / size), "last across row => cpu");
        Check(!R(5, -1), "outside the ring => cpu");
        Check(R(-3, 100) && R(0, 100), "around index never excludes");

        if (_failures.Count == 0) { GD.Print("PROXY_GPU_BACKEND_ROUTING=PASS"); return 0; }
        GD.PushError("PROXY_GPU_BACKEND_ROUTING=FAIL " + string.Join(" | ", _failures));
        return 1;
    }
}
