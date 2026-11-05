using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Megastorm WO7 (docs/megastorm-design.md 3.3 and section 8): the near strike stream. Pure checks (rates against a numeric expectation, wall-depth
/// distribution, "Lightning frequency" and "Reduced flashes" scaling, window-split determinism, save/load document round trip, the sprite share) plus a staged
/// run through the real layer on the weather clock (strike appears with the shared cell id, expires, reappears identically after a rewind, absent without
/// a source). Run: tools/megastorm_lightning_test.gd (headless). Prints MEGASTORM_LIGHTNING=PASS.
/// </summary>
public partial class MegastormLightningTest : RefCounted
{
    const double ObserverAcross = 159_000.0, ObserverAround = 5_000_000.0, WallXi = 10_000.0;
    readonly List<string> _failures = new();
    int _stage;
    int _wait;
    MegastormModel? _model;
    MegastormLightning.NearStrike _target, _second;
    RingLightningEvent _first;

    void Check(bool ok, string message) { if (!ok) _failures.Add(message); }
    static void Info(string message) => Console.WriteLine("MEGASTORM_LIGHTNING_INFO " + message);

    static MegastormModel NewModel(double leadAround, int seed = 21)
    {
        var model = new MegastormModel();
        model.Trigger(new MegastormTrigger("lightning_test", leadAround, 1, 0.0, 5.0f, 0.0f, seed));
        return model;
    }

    static int Count(MegastormModel model, double t0, double t1, double around, double across, float scale, bool blue, List<MegastormLightning.NearStrike> scratch)
    {
        MegastormLightning.NearStrikesInWindow(model, t0, t1, around, across, scale, scratch);
        return scratch.Count(s => s.Blue == blue);
    }

    /// <summary>Numeric expectation of strikes per second for one stream: sum over cells of chance x P(point within reach).</summary>
    static double ExpectedRate(MegastormModel model, double around, double across, bool blue, double t)
    {
        double circumference = model.Settings.CircumferenceMeters, rate = 0.0;
        // Physical contract, independent of the production constants: one main strike per 10 s per segment, blue at 10%.
        double chance = blue ? 0.01 : 0.1;
        double xiLo = blue ? -15000.0 : 500.0, xiHi = blue ? -5000.0 : 37500.0;
        for (int seg = 0; seg < MegastormLightning.Segments; seg++)
        {
            int inside = 0, total = 0;
            for (int a = 0; a < 24; a++)
                for (int x = 0; x < 24; x++)
                {
                    double across2 = (seg + 0.15 + 0.7 * (a + 0.5) / 24) * MegastormLightning.SegmentMeters, xi = xiLo + (xiHi - xiLo) * (x + 0.5) / 24;
                    double p = MegastormModel.WrapPositive(model.AroundAt(t) - model.State.Direction * (xi + model.LeadOffset(across2, t)), circumference);
                    double da = MegastormModel.WrapSigned(p - around, circumference), dz = across2 - across;
                    total++;
                    if (da * da + dz * dz <= MegastormLightning.NearReachMeters * MegastormLightning.NearReachMeters) inside++;
                }
            rate += chance * inside / total;
        }
        return rate;
    }

    public int RunPure()
    {
        try
        {
            var scratch = new List<MegastormLightning.NearStrike>();
            MegastormModel model = NewModel(ObserverAround + WallXi);
            const double T1 = 4000.0;
            // Rates (speed 5 m/s so the wall is nearly stationary around the observer).
            int main = Count(model, 0.0, T1, ObserverAround, ObserverAcross, 1.0f, false, scratch), blue = scratch.Count(s => s.Blue);
            double mainRate = main / T1, blueRate = blue / T1;
            double mainExpected = ExpectedRate(model, ObserverAround, ObserverAcross, false, 2000.0), blueExpected = ExpectedRate(model, ObserverAround, ObserverAcross, true, 2000.0);
            Info($"main {mainRate:0.000}/s (expected {mainExpected:0.000}), blue {blueRate:0.000}/s (expected {blueExpected:0.000})");
            Check(Math.Abs(mainRate / mainExpected - 1.0) < 0.10, $"main rate {mainRate:0.000} vs expected {mainExpected:0.000}");
            Check(Math.Abs(blueRate / blueExpected - 1.0) < 0.20, $"blue rate {blueRate:0.000} vs expected {blueExpected:0.000}");
            Check(mainRate >= 1.2 && mainRate <= 2.2, $"main rate {mainRate:0.000} outside the 1.2-2.2/s observer range");
            Check(scratch.All(s => s.Blue ? s.XiMeters is >= -15000.0 and <= -5000.0 : s.XiMeters is >= 500.0 and <= 37500.0), "xi range");
            // Sample the whole wall so observer reach cannot bias front/middle/rear depth shares.
            var wallFlashes = new List<MegastormLightning.MegastormFlash>();
            MegastormLightning.FlashesInWindow(model, 0.0, T1, wallFlashes);
            double wallRate = wallFlashes.Count / T1;
            Check(Math.Abs(wallRate / 6.4 - 1.0) < 0.10, $"whole-wall rate {wallRate:0.000} vs 6.4/s");
            Check(wallFlashes.All(f => f.XiMeters is >= 500.0 and <= 37500.0), "whole-wall xi range");
            for (int bin = 0; bin < 3; bin++)
            {
                double lo = 500.0 + 37000.0 * bin / 3.0, hi = 500.0 + 37000.0 * (bin + 1) / 3.0;
                double depthShare = (double)wallFlashes.Count(f => f.XiMeters >= lo && f.XiMeters < hi) / Math.Max(1, wallFlashes.Count);
                Check(Math.Abs(depthShare - 1.0 / 3.0) < 0.03, $"wall depth bin {bin} share {depthShare:0.000} vs 1/3");
            }
            // Frequency and reduced flashes scale it.
            double half = Count(model, 0.0, T1, ObserverAround, ObserverAcross, 0.5f, false, scratch) / T1;
            double twice = Count(model, 0.0, T1, ObserverAround, ObserverAcross, 2.0f, false, scratch) / T1;
            Check(Math.Abs(half / mainRate - 0.5) < 0.05, $"frequency 0.5 gave {half / mainRate:0.000}x");
            Check(Math.Abs(twice / mainRate - 2.0) < 0.15, $"frequency 2 gave {twice / mainRate:0.000}x");
            Check(Count(model, 0.0, 100.0, ObserverAround, ObserverAcross, 0.0f, false, scratch) == 0, "frequency 0 still strikes");
            try
            {
                LightningVisualPolicy.Apply(1.0f, 1.0f, true);
                double reduced = Count(model, 0.0, T1, ObserverAround, ObserverAcross, LightningVisualPolicy.NaturalStrikeChanceScale, false, scratch) / T1;
                Check(Math.Abs(reduced / mainRate - LightningVisualPolicy.ReducedStrikeShare) < 0.05, $"reduced flashes gave {reduced / mainRate:0.000}x");
            }
            finally { LightningVisualPolicy.Reset(); }
            // Subset by scale, and ids shared with the far flash cells.
            var ids1 = new HashSet<long>();
            MegastormLightning.NearStrikesInWindow(model, 0.0, 600.0, ObserverAround, ObserverAcross, 1.0f, scratch);
            foreach (var s in scratch) ids1.Add(s.Flash.Id);
            MegastormLightning.NearStrikesInWindow(model, 0.0, 600.0, ObserverAround, ObserverAcross, 0.5f, scratch);
            Check(scratch.All(s => ids1.Contains(s.Flash.Id)), "scale 0.5 is not a subset of scale 1");
            var far = new List<MegastormLightning.MegastormFlash>();
            MegastormLightning.FlashesInWindow(model, 0.0, 600.0, far);
            var farById = far.ToDictionary(f => f.Id);
            MegastormLightning.NearStrikesInWindow(model, 0.0, 600.0, ObserverAround, ObserverAcross, 1.0f, scratch);
            Check(scratch.Count > 0 && scratch.Where(s => !s.Blue).All(s => farById.TryGetValue(s.Flash.Id, out var f) && f == s.Flash), "near strikes are not the far flash cells");
            Check(scratch.Where(s => s.Blue).All(s => s.Flash.Id >= MegastormLightning.BlueIdTag && !farById.ContainsKey(s.Flash.Id)), "blue ids collide with the main stream");
            // Window-split determinism: frame-sized windows cover exactly the one big window.
            var whole = new List<(long, double)>();
            MegastormLightning.NearStrikesInWindow(model, 100.0, 400.0, ObserverAround, ObserverAcross, 1.0f, scratch);
            foreach (var s in scratch) whole.Add((s.Flash.Id, s.Flash.StartTimeSeconds));
            var split = new List<(long, double)>();
            double step = 1.0 / 60.0;
            for (int i = 0; 100.0 + i * step < 400.0; i++)
            {
                double t = 100.0 + i * step;
                MegastormLightning.NearStrikesInWindow(model, t, Math.Min(400.0, 100.0 + (i + 1) * step), ObserverAround, ObserverAcross, 1.0f, scratch);
                foreach (var s in scratch) split.Add((s.Flash.Id, s.Flash.StartTimeSeconds));
            }
            Check(whole.Count > 0 && whole.OrderBy(x => x).SequenceEqual(split.OrderBy(x => x)), $"window split differs ({whole.Count} vs {split.Count})");
            // Save/load: the model document round trip reproduces ids, starts and positions bit exactly.
            MegastormModel loaded = MegastormModel.FromJson(model.ToJson());
            var a = new List<(long, double, double)>();
            var b = new List<(long, double, double)>();
            MegastormLightning.NearStrikesInWindow(model, 0.0, 1000.0, ObserverAround, ObserverAcross, 1.0f, scratch);
            foreach (var s in scratch) a.Add((s.Flash.Id, s.Flash.StartTimeSeconds, s.AroundMeters));
            MegastormLightning.NearStrikesInWindow(loaded, 0.0, 1000.0, ObserverAround, ObserverAcross, 1.0f, scratch);
            foreach (var s in scratch) b.Add((s.Flash.Id, s.Flash.StartTimeSeconds, s.AroundMeters));
            Check(a.Count > 0 && a.SequenceEqual(b), "save/load changed near strikes");
            Check(Count(new MegastormModel(), 0.0, 1000.0, ObserverAround, ObserverAcross, 1.0f, false, scratch) == 0, "untriggered model strikes");
            // Sprite share through the unchanged layer derivation (kind 6 uses the superstorm chance).
            int derived = 0, total = 0;
            MegastormLightning.NearStrikesInWindow(model, 0.0, T1, ObserverAround, ObserverAcross, 1.0f, scratch);
            foreach (var s in scratch.Where(s => !s.Blue))
            {
                var e = new RingLightningEvent(s.Flash.Id, MegastormModel.WallRecordId, s.Flash.StartTimeSeconds, default, default, 0, 0f, s.Flash.Energy, 5000f, false, MegastormModel.KindMegastorm);
                total++;
                if (UpperAtmosphereLightning.TryDerive(e, 16500.0f, out UpperAtmosphereSprite sprite, 1.0f)) { derived++; Check(sprite.EventId == s.Flash.Id, "sprite id not shared"); }
            }
            double share = (double)derived / Math.Max(1, total);
            Check(Math.Abs(share - UpperAtmosphereLightning.ChanceSuperstorm) < 0.05, $"sprite share {share:0.000}");
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.ToString()); return 1; }
        return Finish("PURE");
    }

    int Finish(string what)
    {
        if (_failures.Count != 0) { Console.Error.WriteLine("MEGASTORM_LIGHTNING=FAIL " + what + " " + string.Join(" | ", _failures.Take(20))); return 1; }
        return 0;
    }

    static RingLightningEvent? Find(RingVolumetricCloudLayer layer, long id)
    {
        foreach (RingLightningEvent e in layer.ActiveLightningEvents) if (e.EventId == id) return e;
        return null;
    }

    /// <summary>One step per frame; returns -1 while running, else the exit code. Stages set the weather clock and wait three frames for the layer's own pass.</summary>
    public int Tick(Node layerNode)
    {
        var layer = (RingVolumetricCloudLayer)layerNode;
        try
        {
            if (_wait > 0) { _wait--; return -1; }
            var explorer = layer.GetNode<RingVoxelExplorerController>(layer.ExplorerPath);
            RingCanonicalPosition observer = explorer.CanonicalPosition;
            switch (_stage++)
            {
                case 0:
                {
                    layer.CloudSimulationPaused = true;
                    _model = NewModel(observer.AroundMeters + WallXi);
                    var scratch = new List<MegastormLightning.NearStrike>();
                    MegastormLightning.NearStrikesInWindow(_model, 50.0, 4000.0, observer.AroundMeters, observer.AcrossMeters, 1.0f, scratch);
                    Check(scratch.Count > 0, "no near strike for the explorer position (test is vacuous)");
                    if (scratch.Count == 0) return Finish("LAYER");
                    _target = scratch[0];
                    _second = scratch.First(x => x.Flash.StartTimeSeconds > _target.Flash.StartTimeSeconds + 5.0);
                    layer.MegastormSource = _model;
                    layer.CloudSimulationTimeSeconds = _target.Flash.StartTimeSeconds + 0.1;
                    _wait = 3; return -1;
                }
                case 1:
                {
                    RingLightningEvent? e = Find(layer, _target.Flash.Id);
                    Check(e.HasValue, "near strike did not appear on the weather clock");
                    if (e is RingLightningEvent ev)
                    {
                        _first = ev;
                        Check(ev.StartTimeSeconds == _target.Flash.StartTimeSeconds && ev.SystemKind == MegastormModel.KindMegastorm && ev.SystemId == MegastormModel.WallRecordId, "event fields");
                        Check(Math.Abs(ev.GroundPosition.AroundMeters - _target.AroundMeters) < 1.0 && Math.Abs(ev.GroundPosition.AcrossMeters - _target.Flash.AcrossMeters) < 1.0, "strike position differs from the flash cell");
                        Check(ev.CloudPosition.VerticalMeters > ev.GroundPosition.VerticalMeters + 200.0, "cloud point not above the ground");
                    }
                    layer.CloudSimulationTimeSeconds = _target.Flash.StartTimeSeconds + 5.0;
                    _wait = 3; return -1;
                }
                case 2:
                    Check(Find(layer, _target.Flash.Id) == null, "strike outlived its lifetime");
                    layer.CloudSimulationTimeSeconds = _target.Flash.StartTimeSeconds + 0.1;   // rewind
                    _wait = 3; return -1;
                case 3:
                {
                    RingLightningEvent? e = Find(layer, _target.Flash.Id);
                    Check(e.HasValue && e.Value == _first, "rewind did not reproduce the identical event");
                    layer.MegastormSource = null;
                    layer.CloudSimulationTimeSeconds = _second.Flash.StartTimeSeconds + 0.1;
                    _wait = 3; return -1;
                }
                default:
                {
                    Check(layer.ActiveLightningEvents.All(e => e.SystemKind != MegastormModel.KindMegastorm) && Find(layer, _second.Flash.Id) == null, "megastorm strikes without a source");
                    int code = Finish("LAYER");
                    if (code == 0) Console.WriteLine("MEGASTORM_LIGHTNING=PASS");
                    return code;
                }
            }
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.ToString()); return 1; }
    }
}
