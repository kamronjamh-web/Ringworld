using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Diagnostics;

public partial class PerformanceAttributionTests : RefCounted
{
    public bool RunContract()
    {
        var samples = new List<RingPerformanceTelemetry.Completed>();
        void Add(long frame, string name, double ms) => samples.Add(new(name, false, ms, true, frame, 1));
        foreach (long frame in new[] { 10L, 11L, 12L })
        {
            Add(frame, "GPU.CloudPrepass", 1); Add(frame, "GPU.CloudRaymarch", 2); Add(frame, "GPU.CloudReconstruct", 3);
            if (frame != 11) Add(frame, "GPU.CloudComposite", 4);
            Add(frame, "GPU.Clouds", 12); Add(frame, "GPU.CloudShadowNear", 90);
        }
        Add(12, "GPU.CloudRaymarch", 7);
        Add(-1, "GPU.CloudComposite", 999);
        var rows = RingPerformanceMonitor.BuildCloudPartitions(samples);
        if (rows.Length != 3 || !rows[0].Complete || rows[0].ParentClouds != 12 || rows[0].Raymarch != 2)
            throw new InvalidOperationException("Complete source-frame cloud partition or separate parent was lost.");
        if (rows[1].Complete || rows[1].Composite != null || rows[2].Complete || rows[2].Raymarch != null)
            throw new InvalidOperationException("Missing/duplicate GPU stages were accepted or cross-frame paired.");
        if (rows[0].Prepass != 1 || rows[0].Composite != 4)
            throw new InvalidOperationException("Shadow or unknown-frame scopes contaminated cloud partition.");
        return true;
    }
}
