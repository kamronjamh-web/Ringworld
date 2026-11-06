using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Godot;
using RingworldTechDemo.Atmosphere;
using RingworldTechDemo.Diagnostics;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// GPU build of the far weather atlases (GPU offload GPU-4). Two jobs, both one compute list on the main RenderingDevice (render thread):
/// <list type="bullet">
/// <item><description>Whole-ring: RingFarWeatherAtlas.glsl writes the 128 x 32 sample grids, RingFarWeatherAtlasPost.glsl upsamples them to the weather atlas (RGBA8, full mip chain) and the layer atlas.</description></item>
/// <item><description>Fine layer atlas: RingFarWeatherAtlas.glsl (fine mode) writes the 128 x 64 camera-centred layer window.</description></item>
/// </list>
/// Atlases are double buffered: a job writes the back buffer and the main thread swaps the published texture only after the job completed, so a frame never
/// sees a half-built atlas and the snapshot age uniform always matches the texture. All RenderingDevice work happens on the render thread.
/// The CPU path in RingScaledFarViewport stays as the fallback (no RenderingDevice, manual weather, a triggered megastorm) and as the parity reference.
/// </summary>
public sealed class RingFarWeatherAtlasGpu
{
    private const string AtlasShaderPath = "res://addons/SunshineClouds2/RingFarWeatherAtlas.glsl";
    private const string PostShaderPath = "res://addons/SunshineClouds2/RingFarWeatherAtlasPost.glsl";
    public const int SampleWidth = 128, SampleHeight = 32, FineWidth = 128, FineHeight = 64;
    public const int ParamFloats = 36;
    private const int PostParamBytes = 16;

    private readonly Texture2Drd[] _weatherTexture = { new(), new() };
    private readonly Texture2Drd[] _layerTexture = { new(), new() };
    private readonly Texture2Drd[] _fineTexture = { new(), new() };

    private RenderingDevice? _rd;
    private bool _failed;
    private string _status = "Not initialised";
    private Rid _coarseShader, _coarsePipeline, _fineShader, _finePipeline, _postShader, _postPipeline, _mipShader, _mipPipeline;
    private Rid _paramBuffer, _layerBuffer, _postBuffer, _sampleWeather, _sampleLayers;
    private Rid _coarseSet;
    private readonly Rid[] _fineSet = new Rid[2];
    private readonly Rid[] _weatherRd = new Rid[2], _layerRd = new Rid[2], _fineRd = new Rid[2];
    private readonly Rid[] _resolveSet = new Rid[2];
    private readonly List<Rid>[] _mipSets = { new(), new() };
    private readonly List<Rid>[] _mipViews = { new(), new() };
    private readonly List<Rid> _owned = new(), _atlasOwned = new(), _fineOwned = new();
    private int _width, _height, _mipCount;
    private int _completedWeatherJob, _completedFineJob, _jobCounter;

    /// <summary>False once initialisation or a dispatch failed (the viewport then stays on the CPU build).</summary>
    public bool Failed => Volatile.Read(ref _failed);
    public string Status => _status;
    /// <summary>Diagnostics: seconds-free job counters.</summary>
    public int WeatherJobsCompleted { get; private set; }
    public int FineJobsCompleted { get; private set; }

    public Texture2D WeatherTexture(int buffer) => _weatherTexture[buffer];
    public Texture2D LayerTexture(int buffer) => _layerTexture[buffer];
    public Texture2D FineTexture(int buffer) => _fineTexture[buffer];

    public static bool DeviceAvailable() => RenderingServer.GetRenderingDevice() != null;

    public int NextJobId() => Interlocked.Increment(ref _jobCounter);
    public bool WeatherJobDone(int jobId) => Volatile.Read(ref _completedWeatherJob) >= jobId;
    public bool FineJobDone(int jobId) => Volatile.Read(ref _completedFineJob) >= jobId;

    /// <summary>Queues the whole-ring build into <paramref name="buffer"/>. <paramref name="parameters"/> is the 36 float block of FarAtlasParams, <paramref name="layerBlock"/> the 656 byte layers table.</summary>
    public void QueueWeather(int jobId, int buffer, int width, int height, float[] parameters, byte[] layerBlock, int detailSeed)
    {
        float[] copy = (float[])parameters.Clone();
        byte[] block = (byte[])layerBlock.Clone();
        RenderingServer.CallOnRenderThread(Callable.From(() => RunWeather(jobId, buffer, width, height, copy, block, detailSeed)));
    }

    /// <summary>Queues the fine layer window build into <paramref name="buffer"/> (128 x 64).</summary>
    public void QueueFine(int jobId, int buffer, float[] parameters, byte[] layerBlock)
    {
        float[] copy = (float[])parameters.Clone();
        byte[] block = (byte[])layerBlock.Clone();
        RenderingServer.CallOnRenderThread(Callable.From(() => RunFine(jobId, buffer, copy, block)));
    }

    /// <summary>Reads back level 0 of the published weather atlas, layer atlas or fine atlas (parity test). Completes on the render thread; poll <paramref name="done"/>.</summary>
    public void QueueReadback(int kind, int buffer, byte[][] result, int slot, int[] done)
    {
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            try
            {
                Rid rid = kind == 0 ? _weatherRd[buffer] : kind == 1 ? _layerRd[buffer] : _fineRd[buffer];
                byte[] data = rid.IsValid && _rd != null ? _rd.TextureGetData(rid, 0) : Array.Empty<byte>();
                // TextureGetData returns every mip level back to back: keep level 0.
                int level0 = kind == 2 ? FineWidth * FineHeight * 4 : _width * _height * 4;
                result[slot] = data.Length > level0 ? data[..level0] : data;
            }
            catch (Exception error) { result[slot] = Array.Empty<byte>(); GD.PushError("Far weather atlas readback failed: " + error.Message); }
            Volatile.Write(ref done[slot], 1);
        }));
    }

    public void Release()
    {
        // Detach the texture wrappers first (main thread) so no material samples a freed RD texture.
        for (int i = 0; i < 2; i++) { _weatherTexture[i].TextureRdRid = default; _layerTexture[i].TextureRdRid = default; _fineTexture[i].TextureRdRid = default; }
        RenderingServer.CallOnRenderThread(Callable.From(FreeAll));
    }

    // ---- Render thread ----

    private void RunWeather(int jobId, int buffer, int width, int height, float[] parameters, byte[] layerBlock, int detailSeed)
    {
        try
        {
            if (_failed) { Volatile.Write(ref _completedWeatherJob, jobId); return; }
            if (!EnsureDevice() || !EnsureAtlas(width, height)) { Fail(_status); Volatile.Write(ref _completedWeatherJob, jobId); return; }
            UploadParams(parameters, layerBlock);
            Span<byte> post = stackalloc byte[PostParamBytes];
            BitConverter.TryWriteBytes(post, detailSeed);
            _rd!.BufferUpdate(_postBuffer, 0, PostParamBytes, post.ToArray());
            using (RingPerformanceGpu.BeginScope(RingPerformanceGpu.FarWeatherAtlas))
            {
                long list = _rd.ComputeListBegin();
                _rd.ComputeListBindComputePipeline(list, _coarsePipeline);
                _rd.ComputeListBindUniformSet(list, _coarseSet, 0);
                _rd.ComputeListDispatch(list, SampleWidth / 8, SampleHeight / 8, 1);
                _rd.ComputeListAddBarrier(list);
                _rd.ComputeListBindComputePipeline(list, _postPipeline);
                _rd.ComputeListBindUniformSet(list, _resolveSet[buffer], 0);
                _rd.ComputeListDispatch(list, (uint)((_width + 7) / 8), (uint)((_height + 7) / 8), 1);
                _rd.ComputeListAddBarrier(list);
                _rd.ComputeListBindComputePipeline(list, _mipPipeline);
                int mipWidth = _width, mipHeight = _height;
                for (int level = 1; level < _mipCount; level++)
                {
                    mipWidth = Math.Max(1, mipWidth / 2);
                    mipHeight = Math.Max(1, mipHeight / 2);
                    _rd.ComputeListBindUniformSet(list, _mipSets[buffer][level - 1], 0);
                    _rd.ComputeListDispatch(list, (uint)((mipWidth + 7) / 8), (uint)((mipHeight + 7) / 8), 1);
                    _rd.ComputeListAddBarrier(list);
                }
                _rd.ComputeListEnd();
            }
            WeatherJobsCompleted++;
            Volatile.Write(ref _completedWeatherJob, jobId);
        }
        catch (Exception error)
        {
            Fail("Far weather atlas GPU build failed: " + error.Message);
            Volatile.Write(ref _completedWeatherJob, jobId);
        }
    }

    private void RunFine(int jobId, int buffer, float[] parameters, byte[] layerBlock)
    {
        try
        {
            if (_failed) { Volatile.Write(ref _completedFineJob, jobId); return; }
            if (!EnsureDevice() || !EnsureFine()) { Fail(_status); Volatile.Write(ref _completedFineJob, jobId); return; }
            UploadParams(parameters, layerBlock);
            using (RingPerformanceGpu.BeginScope(RingPerformanceGpu.FarWeatherAtlas))
            {
                long list = _rd!.ComputeListBegin();
                _rd.ComputeListBindComputePipeline(list, _finePipeline);
                _rd.ComputeListBindUniformSet(list, _fineSet[buffer], 0);
                _rd.ComputeListDispatch(list, FineWidth / 8, FineHeight / 8, 1);
                _rd.ComputeListEnd();
            }
            FineJobsCompleted++;
            Volatile.Write(ref _completedFineJob, jobId);
        }
        catch (Exception error)
        {
            Fail("Far fine layer atlas GPU build failed: " + error.Message);
            Volatile.Write(ref _completedFineJob, jobId);
        }
    }

    private void UploadParams(float[] parameters, byte[] layerBlock)
    {
        var bytes = new byte[ParamFloats * 4];
        Buffer.BlockCopy(parameters, 0, bytes, 0, bytes.Length);
        _rd!.BufferUpdate(_paramBuffer, 0, (uint)bytes.Length, bytes);
        _rd.BufferUpdate(_layerBuffer, 0, (uint)layerBlock.Length, layerBlock);
    }

    private bool EnsureDevice()
    {
        if (_rd != null && _paramBuffer.IsValid) return true;
        _rd = RenderingServer.GetRenderingDevice();
        if (_rd == null) { _status = "Far weather atlas GPU unavailable: no RenderingDevice"; return false; }
        if (!Compile(AtlasShaderPath, string.Empty, out _coarseShader, out _coarsePipeline)
            || !Compile(AtlasShaderPath, "#define RING_FAR_ATLAS_FINE\n", out _fineShader, out _finePipeline)
            || !Compile(PostShaderPath, string.Empty, out _postShader, out _postPipeline)
            || !Compile(PostShaderPath, "#define RING_FAR_ATLAS_MIP\n", out _mipShader, out _mipPipeline))
            return false;
        _paramBuffer = Own(_rd.UniformBufferCreate(ParamFloats * 4 + 0));
        _layerBuffer = Own(_rd.UniformBufferCreate(656));
        _postBuffer = Own(_rd.UniformBufferCreate(PostParamBytes));
        if (!_paramBuffer.IsValid || !_layerBuffer.IsValid || !_postBuffer.IsValid) { _status = "Far weather atlas GPU buffers failed"; return false; }
        _status = "Ready";
        return true;
    }

    private bool Compile(string path, string defines, out Rid shader, out Rid pipeline)
    {
        shader = default; pipeline = default;
        if (!FileAccess.FileExists(path)) { _status = "Far weather atlas shader missing: " + path; return false; }
        string code = RingAtmosphereLuts.ExpandIncludes(path);
        if (code.Length == 0) { _status = "Far weather atlas shader includes could not be expanded: " + path; return false; }
        code = code.Replace("#[compute]", "", StringComparison.Ordinal);
        if (defines.Length > 0) code = code.Replace("#version 450", "#version 450\n" + defines, StringComparison.Ordinal);
        using var source = new RDShaderSource { SourceCompute = code };
        using RDShaderSpirV spirv = _rd!.ShaderCompileSpirVFromSource(source);
        if (!string.IsNullOrWhiteSpace(spirv.CompileErrorCompute)) { _status = "Far weather atlas shader compile error (" + path + "): " + spirv.CompileErrorCompute; return false; }
        shader = Own(_rd.ShaderCreateFromSpirV(spirv));
        if (!shader.IsValid) { _status = "Far weather atlas shader creation failed: " + path; return false; }
        pipeline = Own(_rd.ComputePipelineCreate(shader));
        return pipeline.IsValid;
    }

    private bool EnsureAtlas(int width, int height)
    {
        if (_coarseSet.IsValid && _width == width && _height == height) return true;
        FreeAtlas();
        _width = width; _height = height;
        _mipCount = 1;
        for (int size = Math.Max(width, height); size > 1; size >>= 1) _mipCount++;
        var sampleFormat = new RDTextureFormat
        {
            TextureType = RenderingDevice.TextureType.Type2D, Format = RenderingDevice.DataFormat.R32G32B32A32Sfloat,
            Width = SampleWidth, Height = SampleHeight, ArrayLayers = 1, Mipmaps = 1,
            UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.CanCopyFromBit,
        };
        _sampleWeather = OwnAtlas(_rd!.TextureCreate(sampleFormat, new RDTextureView()));
        _sampleLayers = OwnAtlas(_rd.TextureCreate(sampleFormat, new RDTextureView()));
        RDUniform Image(int binding, Rid rid) { var u = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = binding }; u.AddId(rid); return u; }
        RDUniform Block(int binding, Rid rid) { var u = new RDUniform { UniformType = RenderingDevice.UniformType.UniformBuffer, Binding = binding }; u.AddId(rid); return u; }
        _coarseSet = OwnAtlas(_rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { Image(0, _sampleWeather), Image(1, _sampleLayers), Block(2, _paramBuffer), Block(3, _layerBuffer) }, _coarseShader, 0));
        for (int buffer = 0; buffer < 2; buffer++)
        {
            var weatherFormat = new RDTextureFormat
            {
                TextureType = RenderingDevice.TextureType.Type2D, Format = RenderingDevice.DataFormat.R8G8B8A8Unorm,
                Width = (uint)width, Height = (uint)height, ArrayLayers = 1, Mipmaps = (uint)_mipCount,
                UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanCopyFromBit,
            };
            var layerFormat = new RDTextureFormat
            {
                TextureType = RenderingDevice.TextureType.Type2D, Format = RenderingDevice.DataFormat.R8G8B8A8Unorm,
                Width = (uint)width, Height = (uint)height, ArrayLayers = 1, Mipmaps = 1,
                UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanCopyFromBit,
            };
            _weatherRd[buffer] = OwnAtlas(_rd.TextureCreate(weatherFormat, new RDTextureView()));
            _layerRd[buffer] = OwnAtlas(_rd.TextureCreate(layerFormat, new RDTextureView()));
            if (!_weatherRd[buffer].IsValid || !_layerRd[buffer].IsValid) { _status = "Far weather atlas textures failed"; return false; }
            // Level views for the writes (the sampled texture keeps its whole chain).
            _mipViews[buffer].Clear();
            _mipSets[buffer].Clear();
            Rid previous = _weatherRd[buffer];
            Rid level0 = OwnAtlas(_rd.TextureCreateSharedFromSlice(new RDTextureView(), _weatherRd[buffer], 0, 0, 1));
            _mipViews[buffer].Add(level0);
            previous = level0;
            for (uint level = 1; level < _mipCount; level++)
            {
                Rid view = OwnAtlas(_rd.TextureCreateSharedFromSlice(new RDTextureView(), _weatherRd[buffer], 0, level, 1));
                _mipViews[buffer].Add(view);
                _mipSets[buffer].Add(OwnAtlas(_rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { Image(0, previous), Image(1, view) }, _mipShader, 0)));
                previous = view;
            }
            _resolveSet[buffer] = OwnAtlas(_rd.UniformSetCreate(new Godot.Collections.Array<RDUniform>
                { Image(0, _sampleWeather), Image(1, _sampleLayers), Image(2, level0), Image(3, _layerRd[buffer]), Block(4, _postBuffer) }, _postShader, 0));
            _weatherTexture[buffer].TextureRdRid = _weatherRd[buffer];
            _layerTexture[buffer].TextureRdRid = _layerRd[buffer];
        }
        return _coarseSet.IsValid;
    }

    private bool EnsureFine()
    {
        if (_fineSet[0].IsValid) return true;
        RDUniform Image(int binding, Rid rid) { var u = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = binding }; u.AddId(rid); return u; }
        RDUniform Block(int binding, Rid rid) { var u = new RDUniform { UniformType = RenderingDevice.UniformType.UniformBuffer, Binding = binding }; u.AddId(rid); return u; }
        for (int buffer = 0; buffer < 2; buffer++)
        {
            var format = new RDTextureFormat
            {
                TextureType = RenderingDevice.TextureType.Type2D, Format = RenderingDevice.DataFormat.R8G8B8A8Unorm,
                Width = FineWidth, Height = FineHeight, ArrayLayers = 1, Mipmaps = 1,
                UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanCopyFromBit,
            };
            _fineRd[buffer] = OwnFine(_rd!.TextureCreate(format, new RDTextureView()));
            if (!_fineRd[buffer].IsValid) { _status = "Far fine atlas texture failed"; return false; }
            _fineSet[buffer] = OwnFine(_rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { Image(0, _fineRd[buffer]), Block(2, _paramBuffer), Block(3, _layerBuffer) }, _fineShader, 0));
            _fineTexture[buffer].TextureRdRid = _fineRd[buffer];
        }
        return _fineSet[0].IsValid && _fineSet[1].IsValid;
    }

    private Rid Own(Rid rid) { if (rid.IsValid) _owned.Add(rid); return rid; }
    private Rid OwnAtlas(Rid rid) { if (rid.IsValid) _atlasOwned.Add(rid); return rid; }
    private Rid OwnFine(Rid rid) { if (rid.IsValid) _fineOwned.Add(rid); return rid; }

    private void Fail(string message)
    {
        if (Volatile.Read(ref _failed)) return;
        Volatile.Write(ref _failed, true);
        _status = message;
        GD.PushWarning(message + " (far weather atlases fall back to the CPU build)");
    }

    // Dependents (uniform sets, shared slice views) were created after their textures: free in reverse creation order.
    private void FreeList(List<Rid> list)
    {
        if (_rd != null)
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i].IsValid) _rd.FreeRid(list[i]);
        list.Clear();
    }

    private void FreeAtlas()
    {
        for (int i = 0; i < 2; i++) { _weatherTexture[i].TextureRdRid = default; _layerTexture[i].TextureRdRid = default; }
        FreeList(_atlasOwned);
        _coarseSet = default; _sampleWeather = default; _sampleLayers = default;
        for (int i = 0; i < 2; i++) { _weatherRd[i] = default; _layerRd[i] = default; _resolveSet[i] = default; _mipSets[i].Clear(); _mipViews[i].Clear(); }
    }

    private void FreeAll()
    {
        for (int i = 0; i < 2; i++) { _weatherTexture[i].TextureRdRid = default; _layerTexture[i].TextureRdRid = default; _fineTexture[i].TextureRdRid = default; }
        FreeList(_fineOwned);
        FreeList(_atlasOwned);
        FreeList(_owned);
        _rd = null;
        _coarseSet = default; _paramBuffer = default; _layerBuffer = default; _postBuffer = default;
        Array.Clear(_fineSet); Array.Clear(_fineRd); Array.Clear(_weatherRd); Array.Clear(_layerRd); Array.Clear(_resolveSet);
        _mipSets[0].Clear(); _mipSets[1].Clear(); _mipViews[0].Clear(); _mipViews[1].Clear();
    }
}
