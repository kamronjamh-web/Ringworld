using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo;

/// <summary>
/// Lightning channel overlay driver (megastorm, field strikes and, since natural-bolts, ordinary cloud-to-ground strikes within ChannelMaxDistanceMeters). The bolt mesh (transparent pass) is replaced by cloud wherever Sunshine composites, so the channels
/// of megastorm events (SystemKind 6) are drawn by a post-cloud raster pass (scripts/render/RingLightningChannelComposite.gd + ring_lightning_channel.glsl).
/// Slot priority: field strikes, megastorm bolts, nearest natural strikes. Beyond the reach the far-view paths own the strike. Inactive (no selected event) means zero GPU work.
/// </summary>
public partial class RingLocalWeatherEffects
{
    [ExportGroup("Megastorm lightning channels")]
    [Export] public bool EnableLightningChannels { get; set; } = true;
    [Export(PropertyHint.Range, "0,200,0.5")] public float ChannelCoreRadiance { get; set; } = 18.0f;
    [Export(PropertyHint.Range, "0,50,0.1")] public float ChannelGlowRadiance { get; set; } = 0.45f;
    [Export] public float ChannelCoreWidthMeters { get; set; } = 0.8f;
    [Export] public float ChannelGlowWidthMeters { get; set; } = 18.0f;
    [Export] public Color ChannelGlowColor { get; set; } = new(0.55f, 0.60f, 1.0f, 1.0f);
    /// <summary>Multipliers on the distance-scaled minimum pixel widths of the core and the glow (1 = the built-in curves).</summary>
    [Export] public float ChannelMinCorePixels { get; set; } = 1.0f;
    [Export] public float ChannelMinGlowPixels { get; set; } = 1.0f;
    [Export] public float ChannelMaxDistanceMeters { get; set; } = 60000.0f;

    private const int ChannelMaxBolts = 8, ChannelMaxSegments = 8192, ChannelSegmentBytes = 48, ChannelParamBytes = 192, ChannelSlots = 16, ChannelCoreSlotBase = 8, ChannelMaxCores = 8;
    private const float ChannelCoreWidthRel = 2.0f;
    private const double ChannelBlueTopAboveGroundMeters = 9500.0;
    private static readonly StringName PublishChannelFrameName = "publish_frame";
    private static readonly StringName SetChannelSunshineName = "set_sunshine";
    private static readonly byte[] NoChannelBytes = Array.Empty<byte>();

    /// <summary>A generated bolt, stored relative to nothing but remembering the ground world point it was built at; a floating-origin shift is a pure translation.</summary>
    private sealed class ChannelBoltCache { public List<ChannelSegment> Segments = new(); public Vector3 Ground; }

    private CompositorEffect? _channelCompositor;
    private WorldEnvironment? _channelEnvironment;
    private GodotObject? _channelSunshine;
    private readonly Dictionary<long, ChannelBoltCache> _channelCache = new();
    private readonly List<long> _channelStale = new();
    private readonly List<RingLightningEvent> _channelSelected = new();
    private readonly List<RingLightningEvent> _channelFieldEvents = new();
    private readonly List<float> _channelFieldWeights = new();
    private readonly float[] _channelWeights = new float[ChannelMaxBolts];
    private const int ChannelMaxFieldStrikes = 4;
    private readonly List<ChannelSegment> _channelSegments = new(1024);
    private readonly long[] _channelIds = new long[ChannelMaxBolts];
    private readonly Vector3[] _channelStarts = new Vector3[ChannelMaxBolts];
    private readonly Vector3[] _channelGrounds = new Vector3[ChannelMaxBolts];
    private readonly float[] _channelDebugLengths = new float[ChannelMaxBolts];
    private readonly float[] _channelPulses = new float[ChannelSlots];
    private readonly List<RingMegastormScythes.ChannelCore> _channelCores = new();
    private readonly long[] _channelCoreIds = new long[ChannelMaxCores];
    private readonly Vector3[] _channelCoreP0 = new Vector3[ChannelMaxCores];
    private readonly float[] _channelTips = new float[ChannelSlots];
    private int _channelCoreCount;
    private readonly byte[] _channelParams = new byte[ChannelParamBytes];
    private readonly Variant[] _channelArgs = new Variant[4];
    private byte[] _channelBytes = Array.Empty<byte>();
    private int _channelIdCount = -1;
    private int _channelBoltsPublished, _channelSegmentsPublished;
    private bool _channelPublishedActive, _channelNeedsRebuild = true;
    private bool _channelSlotOk;
    private int _channelSlotCountdown;
    private double _channelCircumference = 1.0;
    private Func<RingCanonicalPosition, double>? _channelDistance;

    public int LightningChannelBoltCount => _channelBoltsPublished;
    public int LightningChannelSegmentCount => _channelSegmentsPublished;
    public int LightningChannelPublishes { get; private set; }
    /// <summary>Exponential average (about 20 frames) of UpdateLightningChannels in microseconds (CPU), for the perf note.</summary>
    public double LightningChannelMicros { get; private set; }

    /// <summary>Debug string of the published overlay state (slot pulses and tips, segment bounds, non-finite count); capture tools only.</summary>
    public string LightningChannelDebug()
    {
        Vector3 lo = new(float.MaxValue, float.MaxValue, float.MaxValue), hi = new(float.MinValue, float.MinValue, float.MinValue);
        int bad = 0;
        foreach (ChannelSegment seg in _channelSegments)
        {
            if (!seg.A.IsFinite() || !seg.B.IsFinite()) { bad++; continue; }
            lo = new Vector3(Math.Min(lo.X, seg.A.X), Math.Min(lo.Y, seg.A.Y), Math.Min(lo.Z, seg.A.Z));
            hi = new Vector3(Math.Max(hi.X, seg.A.X), Math.Max(hi.Y, seg.A.Y), Math.Max(hi.Z, seg.A.Z));
        }
        Vector3 eye = GetViewport()?.GetCamera3D()?.GlobalPosition ?? Vector3.Zero;
        string slots = "";
        for (int i = 0; i < _channelSelected.Count && i < ChannelMaxBolts; i++)
            slots += $" [{i}] id={_channelSelected[i].EventId} fs={_channelSelected[i].FieldStrike} start={_channelStarts[i]} ground={_channelGrounds[i]} len={_channelDebugLengths[i]:0}";
        return slots + $" pulses=[{_channelPulses[0]:0.000},{_channelPulses[1]:0.000},{_channelPulses[2]:0.000}] tips=[{_channelTips[0]:0.00},{_channelTips[1]:0.00},{_channelTips[2]:0.00}] segs={_channelSegments.Count} bad={bad} "
            + $"bboxLo={lo} bboxHi={hi} eye={eye} active={_channelPublishedActive} compositor={(_channelCompositor != null)} slotOk={_channelSlotOk} selected={_channelSelected.Count} first={(_channelSegments.Count > 0 ? _channelSegments[0].A.ToString() + "->" + _channelSegments[0].B + " slot " + _channelSegments[0].Bolt + " w " + _channelSegments[0].WidthA + " i " + _channelSegments[0].Intensity + " aA " + _channelSegments[0].AlongA + " aB " + _channelSegments[0].AlongB : "none")}";
    }

    private void UpdateLightningChannels()
    {
        if (!EnableLightningChannels || _weatherSource == null || _grid == null || _explorer == null
            || !_weatherSource.Enabled || !_weatherSource.EnableSharedLightning)
        { PublishChannelsInactive(); return; }
        double now = _weatherSource.CloudSimulationTimeSeconds;
        _channelCircumference = Math.Max(1.0, _grid.GetCanonicalRingCircumferenceMeters64());
        _channelDistance ??= ChannelGroundDistance;
        // Field strikes (any source; RingFieldStrikeLayer owns the handoff to the far set) take the first slots, then megastorm bolts, then the nearest natural strikes.
        int fieldCount = RingFieldStrikeLayer.SelectMain(_weatherSource.ActiveLightningEvents, _weatherSource.ActiveContactArcs, now, _channelDistance,
            RingFieldStrikeLayer.FarSetBound, ChannelMaxFieldStrikes, _channelFieldEvents, _channelFieldWeights);
        double reach = Math.Max(0.0, ChannelMaxDistanceMeters);
        int megastormCount = LightningChannelSegments.SelectMegastormEvents(_weatherSource.ActiveLightningEvents, now, _channelDistance,
            reach, ChannelMaxBolts - fieldCount, _channelSelected);
        LightningChannelSegments.SelectNaturalEvents(_weatherSource.ActiveLightningEvents, now, _channelDistance,
            reach, ChannelMaxBolts - fieldCount - megastormCount, _channelSelected);
        for (int i = 0; i < _channelSelected.Count; i++) _channelWeights[i] = 1.0f;
        for (int i = 0; i < fieldCount; i++) { _channelWeights[_channelSelected.Count] = _channelFieldWeights[i]; _channelSelected.Add(_channelFieldEvents[i]); }
        RingMegastormScythes.Instance?.CopyChannelCores(_channelCores);
        if (_channelCores.Count == 0 && _channelSelected.Count == 0) { PublishChannelsInactive(); return; }
        if (_channelCompositor == null)
        {
            if (RenderingServer.GetRenderingDevice() == null) return;
            _channelCompositor = GD.Load<GDScript>("res://scripts/render/RingLightningChannelComposite.gd")?.New().AsGodotObject() as CompositorEffect;
            if (_channelCompositor == null) return;
            _channelNeedsRebuild = true;
        }
        // The slot check walks the compositor's effect array through interop; once placed, re-check every 30 frames (or after going inactive).
        if (!_channelSlotOk || --_channelSlotCountdown <= 0)
        {
            _channelSlotOk = EnsureLightningChannelSlot();
            _channelSlotCountdown = 30;
            if (!_channelSlotOk) return;
        }

        // Same ids as last frame? Rebuild only on a changed id set or a moved start (floating origin shift).
        bool rebuild = _channelNeedsRebuild || _channelIdCount != _channelSelected.Count ;
        int coreCount = SortChannelCores();
        // Tips and pulses go through the params only; the core lines rebuild when the scythe set / order changes or a core moved (floating origin).
        if (coreCount != _channelCoreCount) rebuild = true;
        for (int k = 0; k < coreCount; k++)
            if (_channelCoreIds[k] != _channelCores[k].Id || _channelCoreP0[k].DistanceSquaredTo(_channelCores[k].Points[0]) > 0.25f) rebuild = true;
        RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
        int count = _channelSelected.Count;
        Span<Vector3> sides = stackalloc Vector3[ChannelMaxBolts];
        Span<Vector3> downs = stackalloc Vector3[ChannelMaxBolts];
        Span<float> lengths = stackalloc float[ChannelMaxBolts];
        Span<bool> valid = stackalloc bool[ChannelMaxBolts];
        for (int i = 0; i < count; i++)
        {
            RingLightningEvent e = _channelSelected[i];
            if (_channelIds[i] != e.EventId) rebuild = true;
            valid[i] = ComputeChannelFrame(e, geometry, out Vector3 start, out sides[i], out downs[i], out lengths[i]);
            if (valid[i] && (!_channelStarts[i].IsEqualApprox(start) && _channelStarts[i].DistanceSquaredTo(start) > 0.25f)) rebuild = true;
            if (valid[i]) _channelStarts[i] = start;
        }

        if (rebuild)
        {
            PruneChannelCache();
            _channelSegments.Clear();
            for (int i = 0; i < count; i++)
            {
                RingLightningEvent e = _channelSelected[i];
                _channelIds[i] = e.EventId;
                if (!valid[i]) continue;
                int before = _channelSegments.Count;
                _channelGrounds[i] = _channelStarts[i] + downs[i] * lengths[i];
                _channelDebugLengths[i] = lengths[i];
                AppendChannelBolt(e, _channelStarts[i], _channelGrounds[i], i);
                // Selected is nearest first, so the farthest bolts are the ones dropped over the cap.
                if (_channelSegments.Count > ChannelMaxSegments) _channelSegments.RemoveRange(before, _channelSegments.Count - before);
            }
            for (int k = 0; k < coreCount; k++)
            {
                RingMegastormScythes.ChannelCore core = _channelCores[k];
                int before = _channelSegments.Count;
                LightningChannelSegments.BuildScytheCore(core.Points.AsSpan(0, core.Count), ChannelCoreWidthRel, ChannelCoreSlotBase + k, _channelSegments);
                if (_channelSegments.Count > ChannelMaxSegments) _channelSegments.RemoveRange(before, _channelSegments.Count - before);
            }
            _channelCoreCount = coreCount;
            for (int k = 0; k < coreCount; k++) { _channelCoreIds[k] = _channelCores[k].Id; _channelCoreP0[k] = _channelCores[k].Points[0]; }
            _channelIdCount = count;
            _channelNeedsRebuild = false;
            int bytesNeeded = _channelSegments.Count * ChannelSegmentBytes;
            if (_channelBytes.Length != bytesNeeded) _channelBytes = new byte[bytesNeeded];
            WriteChannelSegments();
        }

        float suppression = 1.0f - Mathf.Clamp(UndergroundFlashSuppression, 0.0f, 1.0f);
        Span<float> used = stackalloc float[ChannelSlots];
        Array.Clear(_channelTips);
        for (int i = 0; i < count; i++)
        {
            RingLightningEvent selected = _channelSelected[i];
            if (selected.FieldStrike)
            {
                // Field strike: the shared envelope; tip = 10 + leader progress marks the slot for the shader (growing channel, zone colours).
                used[i] = selected.PulseAt(now) * Math.Max(0.0f, selected.Energy) * _channelWeights[i] * suppression;
                _channelTips[i] = LightningChannelSegments.FieldSlotTipBase + selected.FieldLeaderProgress(now) + 2.0f * selected.FieldStrokeIndex(now);   // + 2 x return stroke index (branch flicker)
                continue;
            }
            used[i] = LightningChannelSegments.ChannelPulse(selected, now, out int stroke) * suppression;
            _channelTips[i] = stroke;   // bolt slots carry the stroke index in tip[] (flicker seed)
        }
        for (int k = 0; k < coreCount; k++) used[count + k] = Math.Max(0.0f, _channelCores[k].Intensity) * suppression;
        LightningVisualPolicy.LimitSummed(used.Slice(0, count + coreCount));   // one cap over every slot in use
        Array.Clear(_channelPulses);
        for (int i = 0; i < count; i++) _channelPulses[i] = used[i];
        for (int k = 0; k < coreCount; k++) _channelPulses[ChannelCoreSlotBase + k] = used[count + k];
        for (int k = 0; k < coreCount; k++) _channelTips[ChannelCoreSlotBase + k] = Mathf.Clamp(_channelCores[k].TipFraction, 0.0f, 1.0f);
        WriteChannelParams();

        bool hasSegments = _channelSegments.Count > 0;
        _channelArgs[0] = rebuild ? _channelBytes : NoChannelBytes;
        _channelArgs[1] = _channelSegments.Count;
        _channelArgs[2] = _channelParams;
        _channelArgs[3] = hasSegments;
        _channelCompositor.Call(PublishChannelFrameName, _channelArgs);
        _channelPublishedActive = hasSegments;
        _channelBoltsPublished = hasSegments ? count + coreCount : 0;
        _channelSegmentsPublished = _channelSegments.Count;
        LightningChannelPublishes++;
    }

    // Nearest eight scythe cores first (distance of point 0 to the camera); truncates _channelCores and returns the kept count.
    private int SortChannelCores()
    {
        int n = _channelCores.Count;
        if (n == 0) return 0;
        Vector3 eye = GetViewport()?.GetCamera3D()?.GlobalPosition ?? Vector3.Zero;
        Span<float> distance = stackalloc float[n];
        for (int i = 0; i < n; i++)
        {
            distance[i] = _channelCores[i].Points[0].DistanceSquaredTo(eye);
            RingMegastormScythes.ChannelCore item = _channelCores[i];
            float di = distance[i];
            int j = i - 1;
            while (j >= 0 && distance[j] > di) { distance[j + 1] = distance[j]; _channelCores[j + 1] = _channelCores[j]; j--; }
            distance[j + 1] = di; _channelCores[j + 1] = item;
        }
        if (n > ChannelMaxCores) { _channelCores.RemoveRange(ChannelMaxCores, n - ChannelMaxCores); n = ChannelMaxCores; }
        return n;
    }

    /// <summary>True when the post-cloud overlay draws this event's channel, so the spatial bolt mesh stays hidden: megastorm bolts, and natural strikes within the overlay's reach.</summary>
    private bool ChannelOverlayDrawsEvent(RingLightningEvent? e)
    {
        if (!EnableLightningChannels || !e.HasValue) return false;
        if (e.Value.SystemKind == LightningChannelSegments.MegastormKind) return true;
        if (e.Value.FieldStrike || _weatherSource == null || !_weatherSource.Enabled || !_weatherSource.EnableSharedLightning || _grid == null || _explorer == null) return false;
        _channelCircumference = Math.Max(1.0, _grid.GetCanonicalRingCircumferenceMeters64());
        return ChannelGroundDistance(e.Value.GroundPosition) <= Math.Max(0.0, ChannelMaxDistanceMeters);
    }

    private double ChannelGroundDistance(RingCanonicalPosition p)
    {
        RingCanonicalPosition o = _explorer!.CanonicalPosition;
        double around = ShortestAroundDelta(p.AroundMeters, o.AroundMeters, _channelCircumference);
        double across = p.AcrossMeters - o.AcrossMeters;
        return Math.Sqrt(around * around + across * across);
    }

    // Mirrors RefreshSharedBoltTransform; "bolt from the blue" events start at the anvil top instead of mid-air.
    private bool ComputeChannelFrame(in RingLightningEvent e, RingLodGeometrySnapshot geometry, out Vector3 start, out Vector3 side, out Vector3 down, out float length)
    {
        RingCanonicalPosition top = e.CloudPosition;
        if (!e.FieldStrike && (e.EventId & MegastormLightning.BlueIdTag) != 0)
            top = top with { VerticalMeters = Math.Max(top.VerticalMeters, e.GroundPosition.VerticalMeters + ChannelBlueTopAboveGroundMeters) };
        start = _grid!.CanonicalRingPositionToWorld(top);
        Vector3 end = _grid.CanonicalRingPositionToWorld(e.GroundPosition);
        down = end - start;
        length = down.Length();
        side = Vector3.Right;
        if (!start.IsFinite() || !end.IsFinite() || length < 10.0f) return false;
        down /= length;
        Vector3 center = geometry.CanonicalToWorld(e.GroundPosition.AroundMeters, e.GroundPosition.VerticalMeters, e.GroundPosition.AcrossMeters);
        Vector3 around = (geometry.CanonicalToWorld(e.GroundPosition.AroundMeters + 10.0, e.GroundPosition.VerticalMeters, e.GroundPosition.AcrossMeters) - center).Normalized();
        Vector3 across = (geometry.CanonicalToWorld(e.GroundPosition.AroundMeters, e.GroundPosition.VerticalMeters, e.GroundPosition.AcrossMeters + 10.0) - center).Normalized();
        side = around * Mathf.Cos(e.PlaneAngleRadians) + across * Mathf.Sin(e.PlaneAngleRadians);
        side = (side - down * side.Dot(down)).Normalized();
        if (!side.IsFinite() || side.LengthSquared() < 0.5f)
            side = Math.Abs(down.Dot(Vector3.Up)) < 0.95f ? down.Cross(Vector3.Up).Normalized() : down.Cross(Vector3.Right).Normalized();
        return true;
    }

    // Generated once per event id, stored as built at the ground world point; later rebuilds translate it by the ground's current world point.
    private void AppendChannelBolt(in RingLightningEvent e, Vector3 top, Vector3 ground, int slot)
    {
        if (!_channelCache.TryGetValue(e.EventId, out ChannelBoltCache? cached))
        {
            cached = new ChannelBoltCache { Ground = ground };
            if (e.FieldStrike) LightningChannelSegments.BuildFieldStrikeChannel(top, ground, e.ShapeSeed, e.IsFieldArc, 0, cached.Segments);
            else if (e.SystemKind == LightningChannelSegments.MegastormKind) LightningChannelSegments.BuildMegastormChannel(top, ground, e.ShapeSeed, 0, cached.Segments);
            else LightningChannelSegments.BuildMegastormChannel(top, ground, e.ShapeSeed, 0, cached.Segments, LightningChannelSegments.NaturalBranchBudget(top.DistanceTo(ground)));   // natural: forks scale with the channel length
            _channelCache[e.EventId] = cached;
        }
        Vector3 delta = ground - cached.Ground;
        int before = _channelSegments.Count;
        for (int i = 0; i < cached.Segments.Count; i++)
        {
            ChannelSegment seg = cached.Segments[i];
            _channelSegments.Add(seg with { A = seg.A + delta, B = seg.B + delta, Bolt = slot });
        }
        // Selected is nearest first, so the farthest bolts are the ones dropped over the cap.
        if (_channelSegments.Count > ChannelMaxSegments) _channelSegments.RemoveRange(before, _channelSegments.Count - before);
    }

    private void PruneChannelCache()
    {
        if (_channelCache.Count == 0) return;
        _channelStale.Clear();
        foreach (long id in _channelCache.Keys)
        {
            bool keep = false;
            for (int i = 0; i < _channelSelected.Count; i++) if (_channelSelected[i].EventId == id) { keep = true; break; }
            if (!keep) _channelStale.Add(id);
        }
        for (int i = 0; i < _channelStale.Count; i++) _channelCache.Remove(_channelStale[i]);
    }

    private void WriteChannelSegments()
    {
        Span<byte> span = _channelBytes;
        int o = 0;
        for (int i = 0; i < _channelSegments.Count; i++)
        {
            ChannelSegment s = _channelSegments[i];
            PutFloats(span, ref o, s.A.X, s.A.Y, s.A.Z, s.WidthA);
            PutFloats(span, ref o, s.B.X, s.B.Y, s.B.Z, s.WidthB);
            PutFloats(span, ref o, s.Intensity, s.Bolt, s.AlongA, s.AlongB);
        }
    }

    private void WriteChannelParams()
    {
        Span<byte> span = _channelParams;
        int o = 0;
        for (int i = 0; i < ChannelSlots; i += 4) PutFloats(span, ref o, _channelPulses[i], _channelPulses[i + 1], _channelPulses[i + 2], _channelPulses[i + 3]);
        for (int i = 0; i < ChannelSlots; i += 4) PutFloats(span, ref o, _channelTips[i], _channelTips[i + 1], _channelTips[i + 2], _channelTips[i + 3]);
        PutFloats(span, ref o, Mathf.Max(0.0f, ChannelCoreRadiance), Mathf.Max(0.0f, ChannelGlowRadiance), Mathf.Max(0.01f, ChannelCoreWidthMeters), Mathf.Max(0.01f, ChannelGlowWidthMeters));
        PutFloats(span, ref o, Mathf.Max(0.0f, ChannelMinCorePixels), Mathf.Max(0.0f, ChannelMinGlowPixels), 0.0f, 0.0f);
        PutFloats(span, ref o, Mathf.Lerp(1.0f, LightningColor.R, 0.25f), Mathf.Lerp(1.0f, LightningColor.G, 0.25f), Mathf.Lerp(1.0f, LightningColor.B, 0.25f), 0.0f);   // core: near white
        PutFloats(span, ref o, ChannelGlowColor.R, ChannelGlowColor.G, ChannelGlowColor.B, 0.0f);
    }

    private static void PutFloats(Span<byte> span, ref int offset, float a, float b, float c, float d)
    {
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset), a);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 4), b);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 8), c);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 12), d);
        offset += 16;
    }

    private void PublishChannelsInactive()
    {
        _channelSlotOk = false;
        _channelBoltsPublished = 0; _channelSegmentsPublished = 0;
        if (_channelCache.Count > 0) _channelCache.Clear();
        _channelIdCount = -1; _channelNeedsRebuild = true;
        if (!_channelPublishedActive || _channelCompositor == null) return;
        _channelArgs[0] = NoChannelBytes; _channelArgs[1] = 0; _channelArgs[2] = _channelParams; _channelArgs[3] = false;
        _channelCompositor.Call(PublishChannelFrameName, _channelArgs);
        _channelPublishedActive = false;
    }

    private static string ChannelScriptPath(CompositorEffect effect) => (effect.GetScript().AsGodotObject() as Resource)?.ResourcePath ?? string.Empty;

    // Slot: after the Sunshine effect, before the first rain streak / rain haze compositor (or the end of the list). Anything in between (rainbow) is tolerated.
    private bool EnsureLightningChannelSlot()
    {
        if (_channelCompositor == null) return false;
        if (_channelEnvironment != null && !GodotObject.IsInstanceValid(_channelEnvironment)) _channelEnvironment = null;
        WorldEnvironment? environment = _rainHazeWorldEnvironment != null && GodotObject.IsInstanceValid(_rainHazeWorldEnvironment) ? _rainHazeWorldEnvironment : _channelEnvironment;
        if (environment == null)
        {
            Viewport? viewport = GetViewport();
            if (viewport == null) return false;
            foreach (Node candidate in viewport.FindChildren("*", "WorldEnvironment", true, false))
                if (candidate is WorldEnvironment owner && owner.GetViewport() == viewport) { environment = owner; break; }
            if (environment == null) return false;
        }
        if (_channelEnvironment != null && _channelEnvironment != environment) RemoveChannelFromCompositor(_channelEnvironment);
        _channelEnvironment = environment;
        environment.Compositor ??= new Compositor();
        var effects = environment.Compositor.CompositorEffects;
        using Godot.Collections.Array effectsLifetime = (Godot.Collections.Array)effects;
        int self = -1, sunshine = -1, tail = effects.Count;
        for (int i = 0; i < effects.Count; i++)
        {
            CompositorEffect fx = effects[i];
            if (fx == _channelCompositor) self = i;
            else if (sunshine < 0 && ChannelScriptPath(fx).Contains("SunshineClouds2/SunshineClouds", StringComparison.Ordinal)) sunshine = i;
            if (tail == effects.Count && fx != null && (fx == _streakCompositor || fx == _rainHazeCompositor)) tail = i;
        }
        if (!(self >= 0 && self > sunshine && self < tail))
        {
            if (self >= 0) { effects.RemoveAt(self); if (tail > self) tail--; if (sunshine > self) sunshine--; }
            int insert = tail;
            if (insert > 0 && ChannelScriptPath(effects[insert - 1]).Contains("RingRainbowComposite", StringComparison.Ordinal)) insert--;
            insert = Math.Max(insert, sunshine + 1);
            effects.Insert(insert, _channelCompositor);
            environment.Compositor.CompositorEffects = effects;
        }
        GodotObject? sunshineEffect = null;
        for (int i = 0; i < effects.Count; i++)
            if (effects[i] != _channelCompositor && ChannelScriptPath(effects[i]).Contains("SunshineClouds2/SunshineClouds", StringComparison.Ordinal)) { sunshineEffect = effects[i]; break; }
        if (!ReferenceEquals(sunshineEffect, _channelSunshine))
        {
            _channelSunshine = sunshineEffect;
            _channelCompositor.Call(SetChannelSunshineName, sunshineEffect == null ? default : Variant.From(sunshineEffect));
        }
        return true;
    }

    private void RemoveChannelFromCompositor(WorldEnvironment? environment)
    {
        if (_channelCompositor == null || environment == null || !GodotObject.IsInstanceValid(environment) || environment.Compositor == null) return;
        var effects = environment.Compositor.CompositorEffects;
        using Godot.Collections.Array effectsLifetime = (Godot.Collections.Array)effects;
        if (effects.Remove(_channelCompositor)) environment.Compositor.CompositorEffects = effects;
    }

    private void ReleaseLightningChannels()
    {
        _channelSlotOk = false;
        if (_channelCompositor == null) return;
        _channelArgs[0] = NoChannelBytes; _channelArgs[1] = 0; _channelArgs[2] = _channelParams; _channelArgs[3] = false;
        _channelCompositor.Call(PublishChannelFrameName, _channelArgs);
        RemoveChannelFromCompositor(_channelEnvironment);
        if (_rainHazeWorldEnvironment != _channelEnvironment) RemoveChannelFromCompositor(_rainHazeWorldEnvironment);
        _channelEnvironment = null;
        _channelSunshine = null;
        _channelPublishedActive = false;
        _channelBoltsPublished = 0; _channelSegmentsPublished = 0;
        _channelCache.Clear();
        _channelCompositor.Dispose();
        _channelCompositor = null;
    }
}
