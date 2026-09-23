using System;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Headless regression coverage for the pure adaptive FFT scheduler.  This
/// deliberately drives time itself so cadence and dormancy stay testable
/// without creating terrain, water meshes, or a RenderingDevice.
/// </summary>
[GlobalClass]
public partial class WaterFftAdaptiveSmoke : RefCounted
{
    private static readonly RingWaterFftUpdateSchedulerConfig Adaptive = new(
        true, 4f, 100f, .04f, .25f, 60f, 18f, 4);

    public Godot.Collections.Dictionary Run()
    {
        try
        {
            VerifyDryStartupAndWake();
            VerifyDryForceAndResumeAfterIdle();
            VerifyDemandRatesAndCaps();
            VerifyGraceAndDormancy();
            VerifyCadenceAndNoCatchup();
            VerifyLegacyModeAndSmoothRelease();
            VerifyNonFiniteDemandArea();
            VerifyCollectorContracts();
            return new Godot.Collections.Dictionary { ["pass"] = true, ["detail"] = "dry,wake,demand,caps,grace,dormancy,cadence,no_catchup,legacy,smoothing,collector_geometry" };
        }
        catch (Exception error)
        {
            return new Godot.Collections.Dictionary { ["pass"] = false, ["detail"] = error.Message };
        }
    }

    private static void VerifyDryStartupAndWake()
    {
        var scheduler = new RingWaterFftUpdateScheduler();
        RingWaterFftUpdateSchedulerStep dry = scheduler.Step(Dry, Adaptive, .1);
        Check(!dry.HasDemand && dry.DueMask == 0 && dry.Status == "dormant", "dry startup scheduled an FFT update");

        RingWaterFftUpdateSchedulerStep wake = scheduler.Step(NearWater, Adaptive, .001);
        Check(wake.Resume && wake.DueMask == 0b1111 && wake.Status == "full", "first water demand did not immediately wake every cascade");
        scheduler.ForceUpdateAll();
        RingWaterFftUpdateSchedulerStep forced = scheduler.Step(NearWater, Adaptive, 0);
        Check(forced.DueMask == 0b1111, "forced topology refresh did not update every cascade");
    }

    private static void VerifyDemandRatesAndCaps()
    {
        var scheduler = new RingWaterFftUpdateScheduler();
        RingWaterFftUpdateSchedulerStep low = scheduler.Step(new(1, 1, 1_000d, 190f), Adaptive, .016);
        RingWaterFftUpdateSchedulerStep higher = scheduler.Step(new(1, 8, 20_000d, 190f), Adaptive, .016);
        Check(higher.GeometryUpdatesPerSecond > low.GeometryUpdatesPerSecond && higher.DetailUpdatesPerSecond > low.DetailUpdatesPerSecond,
            "increased visible water did not increase scheduled rate");

        RingWaterFftUpdateSchedulerStep near = scheduler.Step(NearWater, Adaptive, .016);
        Check(Near(near.GeometryUpdatesPerSecond, 60f) && Near(near.DetailUpdatesPerSecond, 18f),
            "near water did not restore separate full geometry/detail caps");

        var capped = new RingWaterFftUpdateSchedulerConfig(true, 99f, 100f, .04f, 0, 12f, 7f, 2);
        RingWaterFftUpdateSchedulerStep cap = new RingWaterFftUpdateScheduler().Step(new(1, 1, 100d, 1000f), capped, .016);
        Check(Near(cap.GeometryUpdatesPerSecond, 12f) && Near(cap.DetailUpdatesPerSecond, 7f),
            "minimum rate exceeded a configured cascade cap");

        var liveCaps = new RingWaterFftUpdateSchedulerConfig(true, 4f, 100f, .04f, .25f, 12f, 7f, 2);
        var liveScheduler = new RingWaterFftUpdateScheduler();
        liveScheduler.Step(NearWater, Adaptive, .016);
        RingWaterFftUpdateSchedulerStep lowered = liveScheduler.Step(NearWater, liveCaps, .016);
        Check(Near(lowered.GeometryUpdatesPerSecond, 12f) && Near(lowered.DetailUpdatesPerSecond, 7f),
            "lowered configured caps did not clamp an already active scheduler immediately");
    }

    private static void VerifyGraceAndDormancy()
    {
        var scheduler = new RingWaterFftUpdateScheduler();
        scheduler.Step(NearWater, Adaptive, .016);
        RingWaterFftUpdateSchedulerStep grace = scheduler.Step(Dry, Adaptive, .1);
        Check(grace.IsGrace && grace.DueMask >= 0 && grace.Status == "grace", "water loss did not enter the configured idle grace period");
        RingWaterFftUpdateSchedulerStep idle = scheduler.Step(Dry, Adaptive, .3);
        Check(!idle.IsGrace && idle.DueMask == 0 && idle.Status == "dormant", "idle grace did not reach a zero-dispatch dormant state");

        scheduler.Reset();
        scheduler.Step(NearWater, Adaptive, .016);
        RingWaterFftUpdateSchedulerStep longDry = scheduler.Step(Dry, Adaptive, 20d);
        Check(longDry.Status == "dormant" && longDry.DueMask == 0,
            "a long dry frame did not advance idle grace using wall time");
    }

    private static void VerifyCadenceAndNoCatchup()
    {
        var config = new RingWaterFftUpdateSchedulerConfig(true, 4f, 100f, .04f, 0, 20f, 5f, 2);
        var scheduler = new RingWaterFftUpdateScheduler();
        int geometry = 0, detail = 0;
        for (int frame = 0; frame < 600; frame++)
        {
            RingWaterFftUpdateSchedulerStep step = scheduler.Step(NearWater, config, 1d / 60d);
            if ((step.DueMask & 2) != 0) geometry++;
            if ((step.DueMask & 1) != 0) detail++;
        }
        Check(geometry >= 190 && geometry <= 205 && detail >= 48 && detail <= 55,
            $"ten-second cadence was wrong: geometry={geometry}, detail={detail}");

        scheduler.Reset();
        scheduler.Step(NearWater, config, .016); // resume wake
        RingWaterFftUpdateSchedulerStep longFrame = scheduler.Step(NearWater, config, 20d);
        Check((longFrame.DueMask & ~0b11) == 0 && BitCount(longFrame.DueMask) <= 2,
            "a long frame caught up more than once per cascade");
    }

    private static void VerifyLegacyModeAndSmoothRelease()
    {
        var fixedConfig = new RingWaterFftUpdateSchedulerConfig(false, 4f, 100f, .04f, .25f, 37f, 11f, 3);
        RingWaterFftUpdateSchedulerStep fixedStep = new RingWaterFftUpdateScheduler().Step(Dry, fixedConfig, .016);
        Check(fixedStep.Status == "fixed" && Near(fixedStep.GeometryUpdatesPerSecond, 37f) && Near(fixedStep.DetailUpdatesPerSecond, 11f),
            "disabling adaptive updates did not retain legacy full-rate scheduling");

        var scheduler = new RingWaterFftUpdateScheduler();
        scheduler.Step(NearWater, Adaptive, .016);
        RingWaterFftUpdateSchedulerStep falling = scheduler.Step(new(1, 1, 1d, 200f), Adaptive, .1);
        Check(falling.GeometryUpdatesPerSecond > 4f && falling.GeometryUpdatesPerSecond < 60f,
            "rate release was not smoothed after demand declined");
    }

    private static void VerifyDryForceAndResumeAfterIdle()
    {
        var scheduler = new RingWaterFftUpdateScheduler();
        for (int i = 0; i < 3; i++)
        {
            scheduler.ForceUpdateAll();
            RingWaterFftUpdateSchedulerStep dry = scheduler.Step(Dry, Adaptive, .1);
            Check(dry.DueMask == 0 && dry.Status == "dormant", "dry forced topology refresh dispatched FFT work");
        }
        RingWaterFftUpdateSchedulerStep wake = scheduler.Step(NearWater, Adaptive, .001);
        Check(wake.Resume && wake.DueMask == 0b1111,
            "water demand after dormant state did not resume every cascade");
    }

    private static void VerifyNonFiniteDemandArea()
    {
        RingWaterFftUpdateSchedulerStep invalidArea = new RingWaterFftUpdateScheduler().Step(
            new RingWaterFftDemandSnapshot(1, 1, double.NaN, 150f), Adaptive, .016);
        Check(float.IsFinite(invalidArea.GeometryUpdatesPerSecond) && float.IsFinite(invalidArea.DetailUpdatesPerSecond),
            "non-finite weighted water area propagated a non-finite target rate");
    }

    private static void VerifyCollectorContracts()
    {
        // This is deliberately a pure metadata test supplied by the collector:
        // its fixtures cover clipping, LOD-bin de-duplication, and seam math
        // without constructing a terrain world in this headless smoke.
        Check(RingTerrainProxySector.RunFftWaterDemandContractTests(), "water demand collector metadata invariants failed");
    }

    private static readonly RingWaterFftDemandSnapshot Dry = new(0, 0, 0d, float.PositiveInfinity);
    private static readonly RingWaterFftDemandSnapshot NearWater = new(1, 4, 100d, 1f);
    private static bool Near(float a, float b) => MathF.Abs(a - b) < .01f;
    private static int BitCount(int value) { int count = 0; while (value != 0) { count += value & 1; value >>= 1; } return count; }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
