using System;
using Godot;

namespace RingworldTechDemo.Weather;

/// <summary>
/// 10 Hz cover probe (docs/storm-revamp-s1-design.md section 1, WO3): a one-thread compute on the render thread samples the published map around the listener
/// (disc of 2 m) and returns the result through BufferGetDataAsync, so the main thread never waits. The result is tied to the map frame serial it was computed
/// from; IsFresh requires the same serial as the currently published frame, an age under 0.5 s and a listener inside the map.
/// </summary>
public partial class RingPrecipitationOcclusion
{
    public const double ProbeIntervalSeconds = 0.1, ProbeFreshSeconds = 0.5;
    /// <summary>24 rain exposure columns plus the overhead texel (index 24), sampled in the same readback as the cover probe.</summary>
    public const int ProbeColumnSlots = RainSoundField.ColumnCount + 1;
    private const int ProbeParamsBytes = 32 + 13 * 16, ProbeResultBytes = 27 * 16;
    public const float ProbeRadiusMeters = 2.0f, ProbeHeadroomMeters = 0.6f, ProbeSlopeTolerance = 0.25f;
    private const string ProbeShaderPath = "res://scripts/render/ring_precip_probe.glsl";

    private sealed class ProbeLink
    {
        public readonly object Gate = new();
        public RenderingDevice? Rd;
        public Rid Shader, Pipeline, Sampler, ParamsBuffer, ResultBuffer;
        public readonly System.Collections.Generic.Dictionary<Rid, Rid> Sets = new();
        public bool Failed, Released, Pending, Inited;
        public string Error = "";
        public long Dispatches;
        // Newest completed readback.
        public float CoverHeight, OpenFraction = 1, CoverDepth;
        public int Serial = -1, Epoch;
        public ulong CompletedMsec;
        public long Completed;
        // Column samples of the newest readback: metres of surface above the listener per column (NaN = nothing drawn), validity, whether the dispatch carried a column frame.
        public readonly float[] ColumnHeights = new float[ProbeColumnSlots];
        public readonly bool[] ColumnValid = new bool[ProbeColumnSlots];
        public bool HasColumns;
    }

    private ProbeLink? _probe;
    private double _lastProbeSeconds = double.NegativeInfinity;
    private Vector3 _probeListenerWorld;
    private bool _probeListenerInside;

    /// <summary>Listener override (world position) for tools; null uses the observer camera.</summary>
    public Vector3? ProbeListenerWorldOverride { get; set; }
    public long ProbeDispatches { get { ProbeLink? p = _probe; return p == null ? 0 : p.Dispatches; } }
    public long ProbeCompleted { get { ProbeLink? p = _probe; if (p == null) return 0; lock (p.Gate) return p.Completed; } }
    public string ProbeError => _probe?.Error ?? "";

    public float ListenerCoverHeightMeters { get { ProbeLink? p = _probe; if (p == null) return 0; lock (p.Gate) return p.CoverHeight; } }
    public float ListenerOpenFraction { get { ProbeLink? p = _probe; if (p == null) return 1; lock (p.Gate) return p.OpenFraction; } }
    public float ListenerCoverDepthMeters { get { ProbeLink? p = _probe; if (p == null) return 0; lock (p.Gate) return p.CoverDepth; } }
    // Ring frame of the column samples (set by the exposure provider each update): the listener and the world-space ring axes at it.
    private Vector3 _columnListener, _columnAround, _columnAcross;
    private bool _hasColumnFrame;

    /// <summary>Tells the probe where the listener's 24 exposure columns lie: world listener position and unit world axes of the ring "around" and "across" directions at it. Without it the probe returns no column samples.</summary>
    public void SetProbeColumnFrame(Vector3 listenerWorld, Vector3 aroundAxis, Vector3 acrossAxis)
    {
        _columnListener = listenerWorld; _columnAround = aroundAxis; _columnAcross = acrossAxis;
        _hasColumnFrame = listenerWorld.IsFinite() && aroundAxis.IsFinite() && acrossAxis.IsFinite();
    }

    public void ClearProbeColumnFrame() => _hasColumnFrame = false;

    /// <summary>
    /// Copies the newest column samples when the probe is <see cref="IsFresh"/> and carried a column frame. <paramref name="heights"/> receives metres of the highest
    /// surface above the listener per column (index = RainSoundField column, 24 = overhead; negative infinity = nothing drawn below the plane), <paramref name="valid"/> whether the sample lay inside the map.
    /// </summary>
    public bool TryGetColumnSamples(float[] heights, bool[] valid)
    {
        ProbeLink? p = _probe;
        if (p == null || !IsFresh) return false;
        lock (p.Gate)
        {
            if (!p.HasColumns) return false;
            Array.Copy(p.ColumnHeights, heights, Math.Min(heights.Length, ProbeColumnSlots));
            Array.Copy(p.ColumnValid, valid, Math.Min(valid.Length, ProbeColumnSlots));
        }
        return true;
    }

    public bool IsFresh
    {
        get
        {
            ProbeLink? p = _probe;
            if (p == null || !Ready || !_probeListenerInside) return false;
            lock (p.Gate) return p.Serial == FrameGeneration && p.Completed > 0 && Time.GetTicksMsec() - p.CompletedMsec < (ulong)(ProbeFreshSeconds * 1000.0);
        }
    }

    private void TickProbe(double now)
    {
        if (!Ready || PublishedTexture == null) { _probeListenerInside = false; return; }
        Vector3? listener = ProbeListenerWorldOverride ?? ObserverWorld();
        if (listener == null) { _probeListenerInside = false; return; }
        PrecipitationOcclusionFrame.Project(listener.Value, PublishedTransform, out Vector2 texel, out float depth);
        _probeListenerInside = PrecipitationOcclusionFrame.InsideMap(texel, ProbeRadiusMeters / PrecipitationOcclusionFrame.TexelMeters + 1f) && depth > 0 && depth < PublishedFarMeters;
        _probeListenerWorld = listener.Value;
        if (!_probeListenerInside || now - _lastProbeSeconds < ProbeIntervalSeconds) return;
        ProbeLink link = _probe ??= new ProbeLink();
        if (link.Failed || link.Pending) return;
        Rid texture = PublishedTextureRid;
        if (!texture.IsValid) return;
        _lastProbeSeconds = now; link.Pending = true;
        int serial = FrameGeneration, epoch;
        lock (link.Gate) epoch = link.Epoch;
        float radiusTexels = ProbeRadiusMeters / PrecipitationOcclusionFrame.TexelMeters;
        float[]? columns = null;
        if (_hasColumnFrame)
        {
            columns = new float[ProbeColumnSlots * 2];
            for (int i = 0; i < ProbeColumnSlots; i++)
            {
                float da = 0, dc = 0;
                if (i < RainSoundField.ColumnCount) RainSoundField.ColumnGeometry(i, out _, out da, out dc);
                PrecipitationOcclusionFrame.Project(_columnListener + _columnAround * da + _columnAcross * dc, PublishedTransform, out Vector2 columnTexel, out _);
                columns[i * 2] = columnTexel.X; columns[i * 2 + 1] = columnTexel.Y;
            }
        }
        RenderingServer.CallOnRenderThread(Callable.From(() => DispatchProbe(link, texture, serial, epoch, texel, depth, radiusTexels, columns)));
    }

    private static void DispatchProbe(ProbeLink link, Rid texture, int serial, int epoch, Vector2 texel, float depth, float radiusTexels, float[]? columns)
    {
        try
        {
            if (link.Released) return;
            RenderingDevice? rd = link.Rd ??= RenderingServer.GetRenderingDevice();
            if (rd == null) { Fail(link, "no RenderingDevice"); return; }
            if (!link.Inited)
            {
                RDShaderFile? source = GD.Load<RDShaderFile>(ProbeShaderPath);
                if (source == null) { Fail(link, "probe shader missing"); return; }
                link.Shader = rd.ShaderCreateFromSpirV(source.GetSpirV());
                if (!link.Shader.IsValid) { Fail(link, "probe shader compile failed"); return; }
                link.Pipeline = rd.ComputePipelineCreate(link.Shader);
                link.Sampler = rd.SamplerCreate(new RDSamplerState());
                link.ParamsBuffer = rd.UniformBufferCreate(ProbeParamsBytes);
                link.ResultBuffer = rd.StorageBufferCreate(ProbeResultBytes);
                link.Inited = true;
            }
            if (!link.Sets.TryGetValue(texture, out Rid set) || !rd.UniformSetIsValid(set))
            {
                var sampled = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 0 }; sampled.AddId(link.Sampler); sampled.AddId(texture);
                var result = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 }; result.AddId(link.ResultBuffer);
                var parameters = new RDUniform { UniformType = RenderingDevice.UniformType.UniformBuffer, Binding = 2 }; parameters.AddId(link.ParamsBuffer);
                set = rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { sampled, result, parameters }, link.Shader, 0);
                link.Sets[texture] = set;
            }
            var data = new byte[ProbeParamsBytes];
            BitConverter.TryWriteBytes(data.AsSpan(0), texel.X); BitConverter.TryWriteBytes(data.AsSpan(4), texel.Y);
            BitConverter.TryWriteBytes(data.AsSpan(8), depth); BitConverter.TryWriteBytes(data.AsSpan(12), radiusTexels);
            BitConverter.TryWriteBytes(data.AsSpan(16), ProbeHeadroomMeters); BitConverter.TryWriteBytes(data.AsSpan(20), ProbeSlopeTolerance);
            BitConverter.TryWriteBytes(data.AsSpan(24), PrecipitationOcclusionFrame.TexelMeters);
            int columnCount = columns == null ? 0 : ProbeColumnSlots;
            BitConverter.TryWriteBytes(data.AsSpan(28), (float)columnCount);
            for (int i = 0; i < columnCount * 2; i++) BitConverter.TryWriteBytes(data.AsSpan(32 + i * 4), columns![i]);
            rd.BufferUpdate(link.ParamsBuffer, 0, ProbeParamsBytes, data);
            long list = rd.ComputeListBegin();
            rd.ComputeListBindComputePipeline(list, link.Pipeline);
            rd.ComputeListBindUniformSet(list, set, 0);
            rd.ComputeListDispatch(list, 1, 1, 1);
            rd.ComputeListEnd();
            link.Dispatches++;
            rd.BufferGetDataAsync(link.ResultBuffer, Callable.From((byte[] bytes) =>
            {
                lock (link.Gate)
                {
                    if (!link.Released && link.Epoch == epoch && bytes.Length >= 16)
                    {
                        float cover = BitConverter.ToSingle(bytes, 0), open = BitConverter.ToSingle(bytes, 4), depthMean = BitConverter.ToSingle(bytes, 8);
                        if (float.IsFinite(cover) && float.IsFinite(open) && float.IsFinite(depthMean))
                        {
                            link.CoverHeight = Math.Max(0, cover); link.OpenFraction = Math.Clamp(open, 0, 1); link.CoverDepth = Math.Max(0, depthMean);
                            link.Serial = serial; link.CompletedMsec = Time.GetTicksMsec(); link.Completed++;
                            link.HasColumns = columnCount > 0 && bytes.Length >= ProbeResultBytes;
                            if (link.HasColumns)
                                for (int i = 0; i < ProbeColumnSlots; i++)
                                {
                                    float columnDepth = BitConverter.ToSingle(bytes, (2 + i) * 16), inside = BitConverter.ToSingle(bytes, (2 + i) * 16 + 4);
                                    link.ColumnValid[i] = inside > .5f && float.IsFinite(columnDepth);
                                    link.ColumnHeights[i] = columnDepth > 0f ? depth - columnDepth : float.NegativeInfinity;
                                }
                        }
                    }
                    link.Pending = false;
                }
            }));
        }
        catch (Exception error) { Fail(link, error.Message); }
    }

    private static void Fail(ProbeLink link, string message) { lock (link.Gate) { link.Error = message; link.Failed = true; link.Pending = false; } }

    /// <summary>Drops the last result (map deactivated or rebuilt): IsFresh turns false until a new readback of a published frame lands.</summary>
    private void InvalidateProbe()
    {
        ProbeLink? p = _probe;
        if (p == null) return;
        lock (p.Gate) { p.Serial = -1; p.Completed = 0; p.Epoch++; }
        _lastProbeSeconds = double.NegativeInfinity;
        _probeListenerInside = false;
    }

    private void ReleaseProbe()
    {
        ProbeLink? link = _probe;
        _probe = null;
        if (link == null) return;
        lock (link.Gate) link.Released = true;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            RenderingDevice? rd = link.Rd;
            if (rd == null) return;
            foreach (Rid set in link.Sets.Values) if (set.IsValid && rd.UniformSetIsValid(set)) rd.FreeRid(set);
            foreach (Rid rid in new[] { link.ParamsBuffer, link.ResultBuffer, link.Pipeline, link.Sampler, link.Shader }) if (rid.IsValid) rd.FreeRid(rid);
        }));
    }
}
