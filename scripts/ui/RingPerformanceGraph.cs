using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Diagnostics;

namespace RingworldTechDemo.UI;

public enum RingPerformanceGraphMetric { Cpu, Gpu }

/// <summary>Small retained-history graph; unavailable GPU samples deliberately leave gaps.</summary>
public partial class RingPerformanceGraph : Control
{
    [Export] public RingPerformanceGraphMetric Metric { get; set; }
    private IReadOnlyList<RingPerformanceFrame> _frames = Array.Empty<RingPerformanceFrame>();
    public void SetFrames(IReadOnlyList<RingPerformanceFrame> frames) { _frames = frames; QueueRedraw(); }
    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(.02f, .05f, .08f, .78f));
        if (_frames.Count < 2 || Size.X < 4 || Size.Y < 4) return;
        double max = 1;
        foreach (RingPerformanceFrame frame in _frames) { double? value = Metric == RingPerformanceGraphMetric.Cpu ? frame.ViewportCpuMilliseconds : frame.ViewportGpuMilliseconds; if (value.HasValue && double.IsFinite(value.Value)) max = Math.Max(max, value.Value); }
        max = Math.Max(4, Math.Ceiling(max / 4) * 4); Vector2? previous = null;
        for (int index = 0; index < _frames.Count; index++)
        {
            double? value = Metric == RingPerformanceGraphMetric.Cpu ? _frames[index].ViewportCpuMilliseconds : _frames[index].ViewportGpuMilliseconds;
            if (!value.HasValue || !double.IsFinite(value.Value)) { previous = null; continue; }
            Vector2 point = new(index * Size.X / Math.Max(1, _frames.Count - 1), Size.Y - (float)Math.Clamp(value.Value / max, 0, 1) * Size.Y);
            if (previous.HasValue) DrawLine(previous.Value, point, Metric == RingPerformanceGraphMetric.Cpu ? new Color(.25f, .8f, 1f) : new Color(1f, .65f, .18f), 2);
            previous = point;
        }
        DrawString(ThemeDB.FallbackFont, new Vector2(4, 14), $"0-{max:0} ms", HorizontalAlignment.Left, -1, 12, RingUiStyle.Muted);
    }
}
