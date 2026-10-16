using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tests;

/// <summary>Headless counter test for persistent tree-tile streaming: stationary, tile-boundary jitter, floating-origin
/// rebase, tile crossing and cache-cap scenarios run through the production planner, keep band and eviction on
/// synthetic tiles. Run via tools/tree_streaming_contract_test.gd.</summary>
[GlobalClass]
public partial class TreeStreamingContractTest : Node
{
    private const double Circumference = 31_415_936.0;
    private const double RingWidth = 318_000.0;
    private readonly List<string> _failures = new();

    public Godot.Collections.Dictionary Run()
    {
        var lines = new List<string>();
        foreach (int cacheSetting in new[] { 768, 4096 })
        {
            var layer = new RingBiomeDecorationLayer { Enabled = false, MaximumCachedVegetationTiles = cacheSetting };
            AddChild(layer);
            try { Scenarios(layer, cacheSetting, lines); }
            finally { layer.QueueFree(); }
        }
        foreach (string line in lines) GD.Print(line);
        return new Godot.Collections.Dictionary { ["pass"] = _failures.Count == 0, ["detail"] = _failures.Count == 0 ? "ok" : string.Join("; ", _failures) };
    }

    private void Scenarios(RingBiomeDecorationLayer layer, int setting, List<string> lines)
    {
        double cell = Circumference / Math.Ceiling(Circumference / Math.Max(20f, layer.CellSizeMeters));
        double tile = cell * layer.SpatialTileCells;
        double boundary = Math.Ceiling(186_000.0 / tile) * tile;
        RingCanonicalPosition home = new(boundary - 40, 0, 141_000);
        string tag = $"TREE_STREAM[cache={setting}]";

        var start = layer.CaptureVegetationStreamingCounters();
        Require(layer.StepVegetationStreamingFixture(home, Circumference, RingWidth), "initial plan did not run");
        var settled = layer.CaptureVegetationStreamingCounters();
        lines.Add($"{tag} initial: plans={settled.Plans - start.Plans} generated={settled.Generated - start.Generated} cached={settled.Cached} desired={settled.Desired} keep={settled.Keep}");
        Require(settled.Cached >= settled.Desired, $"cache {settled.Cached} smaller than the desired set {settled.Desired} (setting {setting})");

        // 1. Stationary for 3 minutes of 60 fps frames: nothing is replanned, built or evicted.
        for (int i = 0; i < 10_800; i++) layer.StepVegetationStreamingFixture(home, Circumference, RingWidth);
        Delta(lines, tag, "stationary_3min", settled, layer.CaptureVegetationStreamingCounters(), 0, 0, 0, 0);

        // 2. Jitter of +/-0.75 m across a tile boundary. The first crossing may build the leading edge; after one
        // full cycle nothing may be built, evicted or reloaded: the keep band holds the tiles that just left.
        var before = layer.CaptureVegetationStreamingCounters();
        for (int i = 0; i < 2; i++) layer.StepVegetationStreamingFixture(home with { AroundMeters = boundary + (i % 2 == 0 ? .75 : -.75) }, Circumference, RingWidth);
        var firstCycle = layer.CaptureVegetationStreamingCounters();
        Delta(lines, tag, "boundary_jitter_first_cycle", before, firstCycle, long.MaxValue, long.MaxValue, 0, 0);
        for (int i = 2; i < 42; i++) layer.StepVegetationStreamingFixture(home with { AroundMeters = boundary + (i % 2 == 0 ? .75 : -.75) }, Circumference, RingWidth);
        Delta(lines, tag, "boundary_jitter_later_cycles", firstCycle, layer.CaptureVegetationStreamingCounters(), long.MaxValue, 0, 0, 0);

        // 3. Floating-origin rebase. A translation-only move must not replan or touch the cache; a basis/shape
        // change takes the full path (one replan) but still builds and evicts nothing.
        RingCanonicalPosition at = home with { AroundMeters = boundary - .75 };
        before = layer.CaptureVegetationStreamingCounters();
        for (int i = 0; i < 10; i++) layer.StepVegetationStreamingFixture(at, Circumference, RingWidth, ringCenterMoved: true, translationOnlyRebase: true);
        var rebased = layer.CaptureVegetationStreamingCounters();
        Delta(lines, tag, "rebase_translation_only_x10", before, rebased, 0, 0, 0, 0);
        Require(rebased.TranslationRebases - before.TranslationRebases == 10 && rebased.FullRebases == before.FullRebases, "translation rebases were not counted as such");
        layer.StepVegetationStreamingFixture(at, Circumference, RingWidth, ringCenterMoved: true, translationOnlyRebase: false);
        var full = layer.CaptureVegetationStreamingCounters();
        Delta(lines, tag, "rebase_full_path", rebased, full, 1, 0, 0, 0);

        // 4. A cell crossing builds only the leading edge: bounded by the swept perimeter, and returning builds nothing.
        double radius = Math.Max(layer.VisibilityRadiusMeters, layer.MaximumLod5DistanceMeters);
        long sweptBound = (long)Math.Ceiling(2.0 * (radius + tile) / tile) * 2 + 8;
        RingCanonicalPosition far = home with { AroundMeters = boundary + tile * 1.5 };
        before = layer.CaptureVegetationStreamingCounters();
        layer.StepVegetationStreamingFixture(far, Circumference, RingWidth);
        var crossed = layer.CaptureVegetationStreamingCounters();
        Delta(lines, tag, "cross_1.5_tiles", before, crossed, 1, sweptBound, long.MaxValue, 0);
        layer.StepVegetationStreamingFixture(home, Circumference, RingWidth);
        var returned = layer.CaptureVegetationStreamingCounters();
        Delta(lines, tag, "return_to_start", crossed, returned, 1, 0, 0, 0);

        // 5. The cache stays bounded while travelling far, and always holds the whole desired set.
        for (int step = 1; step <= 30; step++) layer.StepVegetationStreamingFixture(home with { AroundMeters = boundary + tile * 1.5 * step }, Circumference, RingWidth);
        var traveled = layer.CaptureVegetationStreamingCounters();
        lines.Add($"{tag} travel_30_tiles: plans={traveled.Plans - returned.Plans} generated={traveled.Generated - returned.Generated} evicted={traveled.Evicted - returned.Evicted} cached={traveled.Cached} desired={traveled.Desired} keep={traveled.Keep}");
        Require(traveled.Cached >= traveled.Desired && traveled.Cached <= Math.Max(setting, traveled.Desired + 2 * 200) + traveled.Keep, "cache exceeded its bound while travelling");
    }

    private void Delta(List<string> lines, string tag, string scenario, RingBiomeDecorationLayer.VegetationStreamingCounters a,
        RingBiomeDecorationLayer.VegetationStreamingCounters b, long maxPlans, long maxGenerated, long maxEvicted, long maxReloaded)
    {
        long plans = b.Plans - a.Plans, generated = b.Generated - a.Generated, evicted = b.Evicted - a.Evicted, reloaded = b.Reloaded - a.Reloaded;
        lines.Add($"{tag} {scenario}: plans={plans} generated={generated} evicted={evicted} reloaded={reloaded} cached={b.Cached}");
        if (plans > maxPlans) _failures.Add($"{tag}/{scenario}: plans {plans} > {maxPlans}");
        if (generated > maxGenerated) _failures.Add($"{tag}/{scenario}: generated {generated} > {maxGenerated}");
        if (evicted > maxEvicted) _failures.Add($"{tag}/{scenario}: evicted {evicted} > {maxEvicted}");
        if (reloaded > maxReloaded) _failures.Add($"{tag}/{scenario}: reloaded {reloaded} > {maxReloaded}");
    }

    private void Require(bool value, string message) { if (!value) _failures.Add(message); }
}
