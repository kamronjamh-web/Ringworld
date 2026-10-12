using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Diagnostics;

namespace RingworldTechDemo.Tools;

/// <summary>Deterministic lifecycle probe; injected renderer calls perform no GPU work.</summary>
public partial class PerformanceFarViewportContractTest : RefCounted
{
    public Godot.Collections.Dictionary ExecuteFixture(Node parent)
    {
        var changes = new List<(Rid Rid, bool Enabled)>();
        ulong frame = 10;
        double cpu = 1.25, gpu = 2.5;
        var slot = new RingPerformanceMonitor.ViewportMeasurement((rid, value) => changes.Add((rid, value)),
            _ => cpu, _ => gpu, () => frame);
        SubViewport? first = null, replacement = null;
        RingPerformanceMonitor? monitor = null;
        try
        {
            void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
            SubViewport Create()
            {
                var viewport = new SubViewport { Size = new Vector2I(32, 32), OwnWorld3D = true,
                    RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
                parent.AddChild(viewport);
                var camera = new Camera3D(); viewport.AddChild(camera); camera.MakeCurrent();
                return viewport;
            }
            slot.Reconcile(null, true);
            Require(slot.Read() == (null, null) && changes.Count == 0, "late viewport must remain null");
            first = Create();
            slot.Reconcile(first, false);
            Require(changes.Count == 0 && slot.Read() == (null, null), "disabled/unavailable must not measure");
            slot.Reconcile(first, true);
            Require(changes.Count == 1 && changes[0].Enabled && slot.Read() == (null, null), "new target must warm before reading");
            frame += 2;
            Require(slot.Read() == (1.25, 2.5), "CPU/GPU measurements must remain separate");
            slot.Reconcile(first, true);
            Require(changes.Count == 1, "steady target resent measurement toggles");
            replacement = Create();
            slot.Reconcile(replacement, true);
            Require(changes.Count == 3 && !changes[1].Enabled && changes[2].Enabled && slot.Read() == (null, null), "replacement must release old target and warm new one");
            frame += 2;
            cpu = double.NaN; gpu = 0;
            Require(slot.Read() == (null, null), "invalid/unavailable renderer values must remain null");
            cpu = 3; gpu = 4;
            Require(slot.Read() == (3.0, 4.0), "replacement did not become readable");
            replacement.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            slot.Reconcile(replacement, true);
            Require(changes.Count == 4 && !changes[3].Enabled && slot.Read() == (null, null), "inactive render target retained stale measurements");
            replacement.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
            slot.Reconcile(replacement, true);
            frame += 2;
            slot.Reconcile(replacement, false);
            Require(changes.Count == 6 && !changes[5].Enabled && slot.Read() == (null, null), "monitor disable must release measurement");
            slot.Reconcile(replacement, true);
            Require(slot.Read() == (null, null), "re-enable must warm instead of reporting stale values");
            replacement.Free();
            slot.Reconcile(null, true);
            Require(changes.Count == 7 && slot.Read() == (null, null), "disposed target must not receive stale RID calls");
            replacement = null;
            monitor = new RingPerformanceMonitor { Enabled = false };
            monitor.InstallSettingsPathForTests($"res://.godot/far_viewport_contract_{Guid.NewGuid():N}/monitor.tres");
            parent.AddChild(monitor);
            monitor.SetProcess(false);
            monitor._Process(0);
            Require(monitor.GetSnapshot().History.Count == 0, "disabled monitor recorded frames");
            monitor.Enabled = true;
            monitor._Process(0);
            var observed = monitor.GetSnapshot().History[^1];
            Require(observed.FarViewportCpuMilliseconds == null && observed.FarViewportGpuMilliseconds == null
                && observed.RegionalFarViewportCpuMilliseconds == null && observed.RegionalFarViewportGpuMilliseconds == null,
                "missing far targets must export null fields rather than zero or aggregate values");
            monitor.Enabled = false; monitor._Process(0);
            return new() { ["pass"] = true, ["measurement_toggles"] = changes.Count,
                ["checks"] = "late,disabled,unavailable,warmup,steady,replacement,inactive,reenable,disposed,separate_timings,monitor_null_fields" };
        }
        catch (Exception error) { return new() { ["pass"] = false, ["reason"] = error.Message }; }
        finally
        {
            slot.Reconcile(null, false);
            if (first != null && GodotObject.IsInstanceValid(first)) first.Free();
            if (replacement != null && GodotObject.IsInstanceValid(replacement)) replacement.Free();
            if (monitor != null && GodotObject.IsInstanceValid(monitor)) monitor.Free();
        }
    }
}
