using System;
using System.Threading;
using Godot;
using RingworldTechDemo.Diagnostics;

namespace RingworldTechDemo.Tools;

/// <summary>Pure concurrency contract: nested callbacks and stale enqueue reads cannot publish premature trace completion.</summary>
public partial class VegetationWorkTraceCompletionTest : RefCounted
{
    public int Run()
    {
        RingVegetationWorkTrace.Capture? capture = null;
        try
        {
            capture = RingVegetationWorkTrace.Begin(4);
            Require(capture.TryRetain(), "outer callback was not admitted");
            Require(capture.TryRetain(), "nested callback was not admitted");
            RingVegetationWorkTrace.Stop(capture);
            Require(!capture.Complete, "Stop completed while callbacks were still admitted");
            var nested = new RingVegetationWorkTrace.Sample { Instance = 2 };
            capture.Record(in nested);
            capture.Release();
            Require(!capture.Complete, "nested callback completed capture before the outer callback recorded");
            var outer = new RingVegetationWorkTrace.Sample { Instance = 1 };
            capture.Record(in outer);
            capture.Release();
            Require(capture.Complete && capture.Count == 2, "last callback did not publish all samples before completion");

            // Another thread holds a stale Current reference across Stop, as an enqueue can do.
            capture = RingVegetationWorkTrace.Begin(4);
            RingVegetationWorkTrace.Capture stale = RingVegetationWorkTrace.Current!;
            using var proceed = new ManualResetEventSlim();
            bool admitted = true;
            var producer = new System.Threading.Thread(() =>
            {
                proceed.Wait();
                admitted = stale.TryRetain();
                if (admitted) stale.Release();
            });
            producer.Start();
            RingVegetationWorkTrace.Stop(capture);
            proceed.Set();
            Require(producer.Join(5000), "stale producer did not finish");
            Require(!admitted && capture.Complete && RingVegetationWorkTrace.Current == null, "stale read admitted a callback after completion");
            GD.Print("VEGETATION_WORK_TRACE_COMPLETION=PASS");
            return 0;
        }
        catch (Exception error)
        {
            GD.PushError("VEGETATION_WORK_TRACE_COMPLETION=FAIL " + error.Message);
            return 1;
        }
        finally
        {
            if (capture != null) RingVegetationWorkTrace.Stop(capture);
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
