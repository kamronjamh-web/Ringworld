using System;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

/// <summary>
/// A small real-provider companion to <see cref="WaterFftAdaptiveSmoke"/>.
/// It deliberately owns only an FFT provider and the production scheduler;
/// terrain demand is represented by deterministic collector snapshots.
/// </summary>
public partial class WaterFftAdaptiveGpuSmoke : Node
{
    private readonly RingWaterFftUpdateScheduler _scheduler = new();
    private readonly RingWaterFftUpdateSchedulerConfig _config = new(true, 3f, 100f, .03f, .2f, 30f, 9f, 2);
    private readonly RingWaterFftDemandSnapshot _dry = new(0, 0, 0d, float.PositiveInfinity);
    private readonly RingWaterFftDemandSnapshot _low = new(1, 1, 1_000d, 180f);
    private readonly RingWaterFftDemandSnapshot _near = new(1, 8, 40_000d, 1f);
    private RingWaterFftProvider? _provider;
    private int _frame, _stageFrames, _lowDispatches, _nearDispatches;
    private long _settledIdleUpdates;
    private string _stage = "dry";

    public bool Completed { get; private set; }
    public bool Passed { get; private set; }
    public string LastFailure { get; private set; } = string.Empty;

    public override void _Ready()
    {
        if (RenderingServer.GetRenderingDevice() is null) { Fail("unsupported GPU renderer (no main RenderingDevice)"); return; }
        _provider = new RingWaterFftProvider();
        _provider.Configure(new RingWaterFftSettings(128, 2, 12017, new Vector2(12, 12), new Vector2(48, 48), 6, 20, 3, .5f));
    }

    public override void _Process(double delta)
    {
        if (Completed || _provider is null) return;
        try
        {
            if (++_frame > 1200) { Fail("timeout at " + _stage); return; }
            float dt = Mathf.Clamp((float)delta, .001f, .05f);
            switch (_stage)
            {
                case "dry":
                    Dispatch(_dry, dt);
                    if (++_stageFrames >= 30) Next("low");
                    break;
                case "low":
                    _lowDispatches += Dispatch(_low, dt);
                    if (++_stageFrames >= 180) Next("near");
                    break;
                case "near":
                    _nearDispatches += Dispatch(_near, dt);
                    if (++_stageFrames >= 180) Next("idle_settle");
                    break;
                case "idle_settle":
                    Dispatch(_dry, dt);
                    if (++_stageFrames >= 45) { _settledIdleUpdates = _provider.CompletedUpdates; Next("idle_verify"); }
                    break;
                case "idle_verify":
                    RingWaterFftUpdateSchedulerStep idle = Dispatch(_dry, dt, out int dispatched);
                    if (idle.DueMask != 0 || dispatched != 0) { Fail("dormant scheduler requested an FFT update"); return; }
                    if (++_stageFrames >= 90)
                    {
                        if (_provider.CompletedUpdates != _settledIdleUpdates) { Fail("FFT completed an update after its dormant queue settled"); return; }
                        Next("wake");
                    }
                    break;
                case "wake":
                    RingWaterFftUpdateSchedulerStep wake = Dispatch(_near, dt, out int wakeDispatches);
                    if (_stageFrames == 0 && (!wake.Resume || wake.DueMask != 3 || wakeDispatches != 2)) { Fail("water wake did not issue both cascade requests"); return; }
                    if (++_stageFrames >= 120)
                    {
                        if (!_provider.Ready || _provider.CompletedUpdates <= _settledIdleUpdates) { Fail("water wake did not produce a real FFT update"); return; }
                        if (_nearDispatches <= _lowDispatches) { Fail($"near demand dispatches ({_nearDispatches}) did not exceed low demand ({_lowDispatches})"); return; }
                        Passed = true; Completed = true;
                        GD.Print($"WATER_FFT_ADAPTIVE_GPU_SMOKE=PASS low_dispatches={_lowDispatches} near_dispatches={_nearDispatches} completed={_provider.CompletedUpdates}");
                        _provider.Dispose(); _provider = null;
                    }
                    break;
            }
        }
        catch (Exception error) { Fail(error.Message); }
    }

    private RingWaterFftUpdateSchedulerStep Dispatch(in RingWaterFftDemandSnapshot demand, float delta, out int dispatched)
    {
        RingWaterFftUpdateSchedulerStep step = _scheduler.Step(demand, _config, delta);
        dispatched = BitCount(step.DueMask);
        if (step.DueMask != 0) _provider!.RequestUpdate(_frame / 60d, step.DueMask);
        return step;
    }
    private int Dispatch(in RingWaterFftDemandSnapshot demand, float delta) { Dispatch(demand, delta, out int dispatched); return dispatched; }
    private void Next(string stage) { _stage = stage; _stageFrames = 0; }
    private static int BitCount(int value) { int count = 0; while (value != 0) { count += value & 1; value >>= 1; } return count; }
    private void Fail(string reason)
    {
        if (Completed) return;
        LastFailure = reason; Completed = true; Passed = false;
        GD.PushError("WATER_FFT_ADAPTIVE_GPU_SMOKE=FAIL " + reason);
        _provider?.Dispose(); _provider = null;
    }
    public override void _ExitTree() { _provider?.Dispose(); _provider = null; }
}
