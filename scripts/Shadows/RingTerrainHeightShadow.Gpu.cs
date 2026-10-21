using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Shadows;

/// <summary>Render-thread glue and shader globals of <see cref="RingTerrainHeightShadow"/>.</summary>
public partial class RingTerrainHeightShadow
{
    public const string GlobalL0 = "ringworld_height_shadow_l0", GlobalL1 = "ringworld_height_shadow_l1", GlobalFrame = "ringworld_height_shadow_frame",
        GlobalL0Params = "ringworld_height_shadow_l0_params", GlobalL1Params = "ringworld_height_shadow_l1_params",
        GlobalL0Bounds = "ringworld_height_shadow_l0_bounds", GlobalL0PrevBounds = "ringworld_height_shadow_l0_prev_bounds",
        GlobalL1Bounds = "ringworld_height_shadow_l1_bounds", GlobalL1PrevBounds = "ringworld_height_shadow_l1_prev_bounds",
        GlobalState = "ringworld_height_shadow_state", GlobalShape = "ringworld_height_shadow_shape", GlobalLight = "ringworld_height_shadow_light";
    private const string TimestampPrefix = "ringhs:";
    private static readonly Vector4 EmptyBounds = new(1e9f, -1e9f, 1e9f, -1e9f);

    private sealed class GpuLink
    {
        public readonly object Gate = new();
        public RingTerrainHeightShadowGpu? Gpu;
        public RenderingDevice? Rd;
        public volatile bool Ready, Failed, Released;
        public string Error = "";
        public int UploadCallbacksDone;
        public int BuildRecordedJob;
        public long NextSeq;
        public readonly double[] MsPerRow = { double.NaN, double.NaN };
        public readonly Dictionary<long, (int Job, int Level, int Rows)> Pending = new();
        public readonly Dictionary<int, (double Sum, int Measured)> JobTiming = new();
        public readonly Dictionary<int, int> JobIssued = new();
        public double LastCompletedBuildMs = double.NaN;
        public double LastCallbackCpuMs, MaxCallbackCpuMs;
        public ulong LastTimestampFrame = ulong.MaxValue;
        public long MeasuredSlices;
        public double TotalMeasuredMs, MaxSliceMs;
        public readonly float[] Unresolved = { float.NaN, float.NaN };
        public readonly float[] UnresolvedShadowed = { float.NaN, float.NaN };
    }

    private GpuLink? _link;
    private readonly Texture2DArrayRD?[] _arrays = new Texture2DArrayRD?[2];
    private bool _globalArraysRegistered, _globalsDirty, _statsWanted;
    private readonly Dictionary<string, Variant> _globalCache = new();
    private double[]? _frameRows;
    private long _anchor, _anchorAcross;

    private bool GpuReady => _link is { Ready: true };
    private bool GpuFailed => _link is { Failed: true };
    private string GpuError => _link?.Error ?? "";

    /// <summary>Asks the next publishes to also read back the unresolved fraction of both levels (menu status, tests); a few ms of render-thread work per publish.</summary>
    public void RequestUnresolvedStats() => _statsWanted = true;
    public float? UnresolvedPercent(int level)
    {
        if (_link == null) return null;
        float v = _link.Unresolved[level];
        return float.IsNaN(v) ? null : v * 100f;
    }
    /// <summary>Unresolved texels (G &lt; 0) whose in-window lower bound already shadows the surface (R &gt; 0.5 m), percent of the level.</summary>
    public float? UnresolvedShadowedPercent(int level)
    {
        if (_link == null) return null;
        float v = _link.UnresolvedShadowed[level];
        return float.IsNaN(v) ? null : v * 100f;
    }
    /// <summary>Unresolved texels that are only lit so far (R &lt;= 0.5 m), percent of the level.</summary>
    public float? UnresolvedLitPercent(int level) => UnresolvedPercent(level) is { } total && UnresolvedShadowedPercent(level) is { } shadowed ? Mathf.Max(total - shadowed, 0f) : null;
    public double GpuMsPerRow(int level)
    {
        if (_link == null) return double.NaN;
        lock (_link.Gate) return _link.MsPerRow[level];
    }
    /// <summary>Total GPU milliseconds of the last build whose slice timestamps all arrived (NaN until one did).</summary>
    public double LastBuildGpuMs { get { if (_link == null) return double.NaN; lock (_link.Gate) return _link.LastCompletedBuildMs; } }
    public double GetRenderCallbackCpuMs(out double max) { max = 0; if (_link == null) return 0; lock (_link.Gate) { max = _link.MaxCallbackCpuMs; return _link.LastCallbackCpuMs; } }

    // ------------------------------------------------------------------ GPU session

    private bool EnsureGpu()
    {
        if (_link is { Ready: true }) return true;
        if (_link is { Failed: true } failed) { Fail("height shadow GPU failed: " + failed.Error); return false; }
        if (_link != null) return false;
        RegisterGlobalArrays();
        var link = new GpuLink();
        _link = link;
        RenderingServer.CallOnRenderThread(Callable.From(() => InitOnRenderThread(link)));
        return false;
    }

    private void RegisterGlobalArrays()
    {
        if (_globalArraysRegistered) return;
        for (int l = 0; l < 2; l++) _arrays[l] ??= new Texture2DArrayRD();
        RenderingServer.GlobalShaderParameterSet(GlobalL0, _arrays[0]!);
        RenderingServer.GlobalShaderParameterSet(GlobalL1, _arrays[1]!);
        _globalArraysRegistered = true;
    }

    private void InitOnRenderThread(GpuLink link)
    {
        if (link.Released) return;
        try
        {
            RenderingDevice? rd = RenderingServer.GetRenderingDevice();
            if (rd == null) { link.Error = "no RenderingDevice"; link.Failed = true; return; }
            var gpu = new RingTerrainHeightShadowGpu(rd, RingHeightShadowClipmap.LevelCount, HeightShadowLayerRoles.LayerCount);
            if (!gpu.Initialize()) { link.Error = gpu.Error; link.Failed = true; gpu.Dispose(); return; }
            link.Rd = rd;
            link.Gpu = gpu;
            for (int l = 0; l < 2; l++) _arrays[l]!.TextureRdRid = gpu.OutputTexture(l);
            link.Ready = true;
        }
        catch (Exception error) { link.Error = error.Message; link.Failed = true; }
    }

    private Rid _placeholderArray;

    /// <summary>A valid 1x1x3 RG16F array texture the global samplers point at while no GPU session exists: a Texture2DArrayRD without an RID falls back to a plain 2D texture, which invalidates every material uniform set that bound the 2DArray global (type mismatch errors every frame).</summary>
    private void PointArraysAtPlaceholder(bool free)
    {
        RenderingDevice? main = RenderingServer.GetRenderingDevice();
        if (main != null && !_placeholderArray.IsValid && !free)
        {
            var format = new RDTextureFormat
            {
                Format = RenderingDevice.DataFormat.R16G16Sfloat, Width = 1, Height = 1, Depth = 1, ArrayLayers = HeightShadowLayerRoles.LayerCount, Mipmaps = 1,
                TextureType = RenderingDevice.TextureType.Type2DArray, UsageBits = RenderingDevice.TextureUsageBits.SamplingBit,
            };
            _placeholderArray = main.TextureCreate(format, new RDTextureView(), new Godot.Collections.Array<byte[]> { new byte[4], new byte[4], new byte[4] });
        }
        for (int l = 0; l < 2; l++)
            if (_arrays[l] != null && GodotObject.IsInstanceValid(_arrays[l])) _arrays[l]!.TextureRdRid = free ? default : _placeholderArray;
        if (free && main != null && _placeholderArray.IsValid) { main.FreeRid(_placeholderArray); _placeholderArray = default; }
    }

    private void ReleaseGpu(bool exiting = false)
    {
        GpuLink? link = _link;
        _link = null;
        if (link == null) return;
        link.Released = true;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            if (!exiting) PointArraysAtPlaceholder(false);
            link.Gpu?.Dispose();
            if (exiting) PointArraysAtPlaceholder(true);
            link.Gpu = null;
        }));
    }

    private void PollGpuLink()
    {
        if (_link is { Failed: true } failed && _phase != HeightShadowPhase.Failed) { Fail("height shadow GPU failed: " + failed.Error); return; }
        // Slice timestamps of frame N are readable a couple of frames later; keep polling while any slice is still unmeasured.
        GpuLink? link = _link;
        if (link is not { Ready: true }) return;
        bool pending; lock (link.Gate) pending = link.Pending.Count > 0;
        if (!pending) return;
        RenderingServer.CallOnRenderThread(Callable.From(() => { if (!link.Released && link.Rd is { } rd) PollTimestamps(link, rd); }));
    }

    // ------------------------------------------------------------------ uploads

    private void ResetUploadCounters() { if (_link != null) Interlocked.Exchange(ref _link.UploadCallbacksDone, 0); _uploadCallbacksPosted = 0; }
    private bool UploadCallbacksFinished(int posted) => _link != null && Volatile.Read(ref _link.UploadCallbacksDone) >= posted;

    private void QueueUpload(List<(int Level, HeightShadowSlotRect Rect)> items, bool[]? mipLevels, RingHeightShadowClipmap clipmap)
    {
        GpuLink link = _link!;
        _uploadCallbacksPosted++;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            try
            {
                if (link.Released || link.Gpu is not { } gpu) return;
                var watch = Stopwatch.StartNew();
                using (RingPerformanceGpu.BeginScope(RingPerformanceGpu.HeightShadow))
                {
                    foreach ((int level, HeightShadowSlotRect rect) in items) gpu.UploadSlotRect(level, clipmap, rect);
                    if (mipLevels != null) for (int l = 0; l < mipLevels.Length; l++) if (mipLevels[l]) gpu.BuildMips(l);
                }
                RecordCallbackCpu(link, watch.Elapsed.TotalMilliseconds);
            }
            catch (Exception error) { link.Error = "upload failed: " + error.Message; link.Failed = true; }
            finally { Interlocked.Increment(ref link.UploadCallbacksDone); }
        }));
    }

    private static void RecordCallbackCpu(GpuLink link, double ms) { lock (link.Gate) { link.LastCallbackCpuMs = ms; link.MaxCallbackCpuMs = Math.Max(link.MaxCallbackCpuMs, ms); } }

    // ------------------------------------------------------------------ build slices and timing

    private bool BuildRecorded(int jobId) => _link != null && Volatile.Read(ref _link.BuildRecordedJob) == jobId;

    private void QueueBuildSlice(BuildJob job, int level, int row, int count, bool last)
    {
        GpuLink link = _link!;
        long seq = ++link.NextSeq;
        HeightShadowBuildInputs? inputs = job.InputsSent ? null : job.Inputs;
        int layer = job.Layer, jobId = job.Id, slicesTotal = job.Slices + 1;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            try
            {
                if (link.Released || link.Gpu is not { } gpu || link.Rd is not { } rd) return;
                var watch = Stopwatch.StartNew();
                using (RingPerformanceGpu.BeginScope(RingPerformanceGpu.HeightShadow))
                {
                    if (inputs != null) gpu.SetBuildInputs(inputs);
                    rd.CaptureTimestamp(TimestampPrefix + seq + ":B");
                    gpu.DispatchBuild(level, row, count, layer);
                    rd.CaptureTimestamp(TimestampPrefix + seq + ":E");
                }
                lock (link.Gate)
                {
                    link.Pending[seq] = (jobId, level, count);
                    if (last) link.JobIssued[jobId] = slicesTotal;
                }
                PollTimestamps(link, rd);
                RecordCallbackCpu(link, watch.Elapsed.TotalMilliseconds);
            }
            catch (Exception error) { link.Error = "build failed: " + error.Message; link.Failed = true; }
            finally { if (last) Volatile.Write(ref link.BuildRecordedJob, jobId); }
        }));
    }

    /// <summary>Reads the newest captured timestamp set for our begin/end pairs and folds them into the per-level ms/row estimate (render thread).</summary>
    private static void PollTimestamps(GpuLink link, RenderingDevice rd)
    {
        ulong frame = rd.GetCapturedTimestampsFrame();
        if (frame == link.LastTimestampFrame) return;
        link.LastTimestampFrame = frame;
        uint count = rd.GetCapturedTimestampsCount();
        Dictionary<long, (ulong Begin, ulong End)>? found = null;
        for (uint i = 0; i < count; i++)
        {
            string name = rd.GetCapturedTimestampName(i);
            if (!name.StartsWith(TimestampPrefix, StringComparison.Ordinal)) continue;
            int colon = name.LastIndexOf(':');
            if (colon < 0 || !long.TryParse(name.AsSpan(TimestampPrefix.Length, colon - TimestampPrefix.Length), out long seq)) continue;
            found ??= new();
            found.TryGetValue(seq, out var pair);
            ulong time = rd.GetCapturedTimestampGpuTime(i);
            if (name[colon + 1] == 'B') pair.Begin = time; else pair.End = time;
            found[seq] = pair;
        }
        if (found == null) return;
        lock (link.Gate)
        {
            foreach ((long seq, (ulong b, ulong e)) in found)
            {
                if (b == 0 || e <= b || !link.Pending.Remove(seq, out var info)) continue;
                double ms = (e - b) / 1_000_000d;
                if (!double.IsFinite(ms)) continue;
                link.MeasuredSlices++; link.TotalMeasuredMs += ms; link.MaxSliceMs = Math.Max(link.MaxSliceMs, ms);
                link.MsPerRow[info.Level] = HeightShadowSlicing.Smooth(link.MsPerRow[info.Level], ms / Math.Max(1, info.Rows));
                link.JobTiming.TryGetValue(info.Job, out var timing);
                timing = (timing.Sum + ms, timing.Measured + 1);
                link.JobTiming[info.Job] = timing;
                if (link.JobIssued.TryGetValue(info.Job, out int issued) && timing.Measured >= issued)
                {
                    link.LastCompletedBuildMs = timing.Sum;
                    link.JobTiming.Remove(info.Job); link.JobIssued.Remove(info.Job);
                }
            }
            if (link.Pending.Count > 512) { long cutoff = link.NextSeq - 256; foreach (long key in new List<long>(link.Pending.Keys)) if (key < cutoff) link.Pending.Remove(key); }
        }
    }

    private void AddGpuDiagnostics(Godot.Collections.Dictionary result)
    {
        result["gpu_ms_per_row_l0"] = GpuMsPerRow(0); result["gpu_ms_per_row_l1"] = GpuMsPerRow(1);
        result["gpu_ms_last_build"] = LastBuildGpuMs;
        double callbackMax; double callbackLast = GetRenderCallbackCpuMs(out callbackMax);
        result["render_callback_cpu_ms_last"] = callbackLast; result["render_callback_cpu_ms_max"] = callbackMax;
        result["unresolved_l0"] = UnresolvedPercent(0) is { } a ? a : -1f; result["unresolved_l1"] = UnresolvedPercent(1) is { } b ? b : -1f;
        for (int l = 0; l < 2; l++)
        {
            result[$"unresolved_shadowed_l{l}"] = UnresolvedShadowedPercent(l) is { } s ? s : -1f;
            result[$"unresolved_lit_l{l}"] = UnresolvedLitPercent(l) is { } u ? u : -1f;
        }
        if (_link != null) { lock (_link.Gate) { result["gpu_slices_measured"] = _link.MeasuredSlices; result["gpu_ms_total"] = _link.TotalMeasuredMs; result["gpu_ms_max_slice"] = _link.MaxSliceMs; } }
        result["anchor_around"] = _anchor; result["anchor_across"] = _anchorAcross;
        result["array_rids"] = _link?.Gpu != null ? new Godot.Collections.Array<Rid> { _link.Gpu.OutputTexture(0), _link.Gpu.OutputTexture(1) } : new Godot.Collections.Array<Rid>();
    }

    private void QueueStats(int layer)
    {
        GpuLink? link = _link;
        if (link == null) return;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            try
            {
                if (link.Released || link.Gpu is not { } gpu || link.Rd is not { } rd) return;
                for (int l = 0; l < 2; l++)
                {
                    int level = l;
                    rd.TextureGetDataAsync(gpu.OutputTexture(l), (uint)layer, Callable.From<byte[]>(bytes =>
                    {
                        if (link.Released) return;
                        int n = RingHeightShadowLattice.TextureSize, hit = 0, shadowed = 0, total = 0;
                        for (int y = 0; y < n; y += 4)
                            for (int x = 0; x < n; x += 4)
                            {
                                int offset = (y * n + x) * 4 + 2;
                                if (offset + 1 >= bytes.Length) continue;
                                total++;
                                if ((float)BitConverter.ToHalf(bytes, offset) < 0f)
                                {
                                    hit++;
                                    if ((float)BitConverter.ToHalf(bytes, offset - 2) > 0.5f) shadowed++;
                                }
                            }
                        if (total > 0) lock (link.Gate) { link.Unresolved[level] = hit / (float)total; link.UnresolvedShadowed[level] = shadowed / (float)total; }
                    }));
                }
            }
            catch (Exception) { /* statistics are optional */ }
        }));
    }

    // ------------------------------------------------------------------ globals

    private Variant _frameGlobal;
    /// <summary>The values last written to the ringworld_height_shadow_* globals (RenderingServer.GlobalShaderParameterGet is not reliable for runtime-set values; tests read this).</summary>
    public Godot.Collections.Dictionary GetGlobalValues()
    {
        var result = new Godot.Collections.Dictionary();
        foreach (var pair in _globalCache) result[pair.Key] = pair.Value;
        result[GlobalFrame] = _frameGlobal;
        return result;
    }

    /// <summary>The heightfield globals as the shaders see them now plus the render-device texture arrays behind ringworld_height_shadow_l0 / _l1 (S7b: the vegetation compute snapshots these).</summary>
    public readonly record struct GlobalsSnapshot(Projection Frame, Vector4 L0Params, Vector4 L1Params, Vector4 L0Bounds, Vector4 L0PrevBounds, Vector4 L1Bounds, Vector4 L1PrevBounds,
        Vector4 State, Vector4 Shape, Vector4 Light, Rid L0, Rid L1)
    {
        public static readonly GlobalsSnapshot Empty = new(Projection.Identity, default, default, EmptyBounds, EmptyBounds, EmptyBounds, EmptyBounds, new Vector4(-1, -1, 1, 0), default, default, default, default);
    }

    /// <summary>Main thread. Current values of the published globals (the invalid defaults before the first push).</summary>
    public GlobalsSnapshot GetGlobalsSnapshot()
    {
        Vector4 Read(string name, Vector4 fallback) => _globalCache.TryGetValue(name, out Variant value) && value.VariantType == Variant.Type.Vector4 ? value.AsVector4() : fallback;
        Projection frame = _frameGlobal.VariantType == Variant.Type.Projection ? _frameGlobal.AsProjection() : Projection.Identity;
        Rid l0 = _arrays[0] is { } a0 && GodotObject.IsInstanceValid(a0) ? a0.TextureRdRid : default;
        Rid l1 = _arrays[1] is { } a1 && GodotObject.IsInstanceValid(a1) ? a1.TextureRdRid : default;
        return new GlobalsSnapshot(frame, Read(GlobalL0Params, default), Read(GlobalL1Params, default), Read(GlobalL0Bounds, EmptyBounds), Read(GlobalL0PrevBounds, EmptyBounds),
            Read(GlobalL1Bounds, EmptyBounds), Read(GlobalL1PrevBounds, EmptyBounds), Read(GlobalState, new Vector4(-1, -1, 1, 0)), Read(GlobalShape, default), Read(GlobalLight, default), l0, l1);
    }

    private void SetGlobal(string name, Variant value)
    {
        if (_globalCache.TryGetValue(name, out Variant cached) && cached.VariantType == value.VariantType && cached.Equals(value)) return;
        _globalCache[name] = value;
        RenderingServer.GlobalShaderParameterSet(name, value);
    }

    private HeightShadowLight _readyLight;
    private Vector3 _frontLightWorld;
    /// <summary>
    /// True while the published front layer was built for <paramref name="light"/>, the receivers read it and the camera sits inside the full-weight zone of its level-0
    /// window (256 m from the edge): the controller shrinks that light's shadow map to its near map only then (S5b sun, S7a planet). False at once when anything
    /// invalidates the globals.
    /// </summary>
    public bool IsHeightfieldReadyFor(HeightShadowLight light) => light != HeightShadowLight.None && _readyLight == light && Settings.Enabled && _phase != HeightShadowPhase.Failed;
    public bool IsSunHeightfieldReady => IsHeightfieldReadyFor(HeightShadowLight.Sun);
    /// <summary>The light the published front layer was built for (None while nothing is published).</summary>
    public HeightShadowLight PublishedLight => _roles.Front >= 0 ? _roles.Light : HeightShadowLight.None;

    private void InvalidateGlobals()
    {
        _readyLight = HeightShadowLight.None;
        SetGlobal(GlobalLight, Variant.From(new Vector4(0, 0, 0, 0)));
        _frameRows = null;
        var none = Variant.From(new Vector4(0, 0, 0, 0));
        SetGlobal(GlobalL0Params, none); SetGlobal(GlobalL1Params, none);
        var empty = Variant.From(EmptyBounds);
        SetGlobal(GlobalL0Bounds, empty); SetGlobal(GlobalL0PrevBounds, empty); SetGlobal(GlobalL1Bounds, empty); SetGlobal(GlobalL1PrevBounds, empty);
        SetGlobal(GlobalState, Variant.From(new Vector4(-1, -1, 1, 0)));
        SetGlobal(GlobalShape, none);
    }

    /// <summary>Writes every ringworld_height_shadow_* global in one main-thread call (atomic from the shaders' point of view); the frame matrix is rebuilt every frame (floating origin).</summary>
    private void PushGlobals(RingLodGeometrySnapshot geometry, float delta)
    {
        if (_roles.Front < 0 || _frontRect[0] == null || _clipmap == null || _focus == null)
        {
            _readyLight = HeightShadowLight.None;
            if (_roles.Front < 0)
            {
                InvalidateGlobals();
                // Switched on but nothing published yet: receivers that need to know (ocean planet macro) see the feature as on.
                if (Settings.Enabled && ReceiversEnabled) SetGlobal(GlobalLight, Variant.From(new Vector4(0, 0, 0, 1)));
            }
            return;
        }
        double radius = geometry.RingRadiusMeters - geometry.RingVerticalBaseOffsetMeters;
        long circumference = _clipmap.Circumference;
        int t0 = _frontTexel[0], t1 = _frontTexel[1];
        var camera = _focus.CanonicalPosition;
        HeightShadowIndexRect w0 = _frontRect[0]!.Value;
        long cameraIndex = RingHeightShadowLattice.UnwrapNear(camera.AroundMeters, w0.I0 + RingHeightShadowLattice.TextureSize / 2, t0, circumference);
        int period = t1;
        long anchor = RingHeightShadowLattice.AnchorMeters(cameraIndex * (double)t0, period);
        long anchorAcross = RingHeightShadowLattice.AnchorMeters(camera.AcrossMeters, period);

        _anchor = anchor; _anchorAcross = anchorAcross;
        double[] m = BuildFrameFor(geometry, anchor, anchorAcross, circumference);
        _frameRows = m;
        // Projection is column major: column j = (M[0][j], M[1][j], M[2][j], 0), translation column = (t0, t1, t2, 1).
        var frame = new Projection(new Vector4((float)m[0], (float)m[4], (float)m[8], 0), new Vector4((float)m[1], (float)m[5], (float)m[9], 0),
            new Vector4((float)m[2], (float)m[6], (float)m[10], 0), new Vector4((float)m[3], (float)m[7], (float)m[11], 1));
        _frameGlobal = Variant.From(frame);
        RenderingServer.GlobalShaderParameterSet(GlobalFrame, _frameGlobal);

        for (int l = 0; l < 2; l++)
        {
            bool valid = _frontRect[l] != null;
            int t = _frontTexel[l];
            SetGlobal(l == 0 ? GlobalL0Params : GlobalL1Params, Variant.From(new Vector4(t, 1f / (RingHeightShadowLattice.TextureSize * t), valid ? 1 : 0, 0)));
            Vector4 front = valid ? ToBounds(_frontRect[l]!.Value, t, anchor, anchorAcross) : EmptyBounds;
            Vector4 prev = _roles.Prev >= 0 && _prevRect[l] is { Width: > 0 } p ? ToBounds(p, t, anchor, anchorAcross) : EmptyBounds;
            SetGlobal(l == 0 ? GlobalL0Bounds : GlobalL1Bounds, Variant.From(front));
            SetGlobal(l == 0 ? GlobalL0PrevBounds : GlobalL1PrevBounds, Variant.From(prev));
        }
        HeightShadowPublished state = _roles.Current;
        {
            Vector4 b0 = ToBounds(w0, t0, anchor, anchorAcross);
            double cameraAround = cameraIndex * (double)t0 - anchor, cameraAcross = camera.AcrossMeters - anchorAcross;
            double edge = Math.Min(Math.Min(cameraAround - b0.X, b0.Y - cameraAround), Math.Min(cameraAcross - b0.Z, b0.W - cameraAcross));
            _readyLight = ReceiversEnabled && edge >= 256.0 ? state.Light : HeightShadowLight.None;
        }
        SetGlobal(GlobalLight, Variant.From(new Vector4(_frontLightWorld.X, _frontLightWorld.Y, _frontLightWorld.Z, Settings.Enabled && ReceiversEnabled ? 1f : 0f)));
        SetGlobal(GlobalState, Variant.From(new Vector4(state.Front, state.Prev, state.Crossfade, ReceiversEnabled ? (float)state.Light : 0f)));
        SetGlobal(GlobalShape, Variant.From(new Vector4((float)Math.Tan(Settings.PenumbraDegrees * Math.PI / 180.0), ReceiverBiasMeters, (float)radius, ResolveAuthorityMeters())));
        if (_globalsDirty)
        {
            _globalsDirty = false;
            if (_statsWanted) QueueStats(_roles.Front);
        }
    }

    /// <summary>
    /// Authority distance A (shape.w): the sun map owns every blocker it contains, so a texel whose blocker lies within camera distance + G &lt; A gives the heightfield no
    /// weight. A = sun shadow max distance x fade start while the sun map is rendering, 0 while it is suspended (below the horizon, zero energy, underground) or off.
    /// </summary>
    public float ResolveAuthorityMeters()
    {
        if (_roles.Current.Light == HeightShadowLight.Planet)
        {
            // The layer was built for the planet: the planet map owns the blockers it contains (sun macro and sun maps are not involved).
            if (_planet == null || !GodotObject.IsInstanceValid(_planet) || !_planet.ShadowEnabled || _planet.LightEnergy <= RingGraphicsQualityController.SunShadowMinimumEnergy) return 0f;
            float planetDistance = _planet.DirectionalShadowMaxDistance * _planet.DirectionalShadowFadeStart;
            return float.IsFinite(planetDistance) && planetDistance > 0f ? planetDistance : 0f;
        }
        if (_sun == null || !GodotObject.IsInstanceValid(_sun) || !_sun.ShadowEnabled || _sun.LightEnergy <= RingGraphicsQualityController.SunShadowMinimumEnergy) return 0f;
        if (_graphics != null && GodotObject.IsInstanceValid(_graphics) && (_graphics.LocalSunShadowSuspended || _graphics.UndergroundShadowSuspended)) return 0f;
        // Near mode (extended by a POI or not): the heightfield owns terrain past near x 0.8, not past the widened light range.
        float distance = _graphics != null && GodotObject.IsInstanceValid(_graphics) ? _graphics.SunHeightShadowAuthorityMeters : _sun.DirectionalShadowMaxDistance * _sun.DirectionalShadowFadeStart;
        return float.IsFinite(distance) && distance > 0f ? distance : 0f;
    }

    /// <summary>The 3x4 world -> anchor-tangent matrix for the current geometry snapshot (see <see cref="HeightShadowFrameMath.BuildFrame"/>).</summary>
    public static double[] BuildFrameFor(RingLodGeometrySnapshot geometry, long anchorAroundMeters, long anchorAcrossMeters, long circumference)
    {
        Basis inverse = geometry.GridGlobalTransform.Basis.Inverse();
        Vector3 origin = geometry.GridGlobalTransform.Origin - geometry.FloatingOriginWorldOffset;
        double ax = 0, ay = 0, az = 0;
        if (geometry.FloatingOriginCanonicalAnchor is { } canonical) (ax, ay, az) = geometry.CanonicalToAbsoluteLocal(canonical.AroundMeters, canonical.VerticalMeters, canonical.AcrossMeters);
        Span<double> inv = stackalloc double[] { inverse.Row0.X, inverse.Row0.Y, inverse.Row0.Z, inverse.Row1.X, inverse.Row1.Y, inverse.Row1.Z, inverse.Row2.X, inverse.Row2.Y, inverse.Row2.Z };
        Span<double> org = stackalloc double[] { origin.X, origin.Y, origin.Z };
        Span<double> anc = stackalloc double[] { ax, ay, az };
        Span<double> centre = stackalloc double[] { geometry.RingCenterLocalOffset.X, geometry.RingCenterLocalOffset.Y, geometry.RingCenterLocalOffset.Z };
        return HeightShadowFrameMath.BuildFrame(inv, org, anc, centre, geometry.RingRadiusMeters - geometry.RingVerticalBaseOffsetMeters, geometry.RingAcrossBaseOffsetMeters,
            RingHeightShadowLattice.PosMod(anchorAroundMeters, circumference), anchorAcrossMeters);
    }

    private static Vector4 ToBounds(HeightShadowIndexRect rect, int texel, long anchor, long anchorAcross)
    {
        var b = HeightShadowFrameMath.Bounds(rect, texel, anchor, anchorAcross);
        return new Vector4((float)b.AroundMin, (float)b.AroundMax, (float)b.AcrossMin, (float)b.AcrossMax);
    }
}
