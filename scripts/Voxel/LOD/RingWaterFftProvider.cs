using Godot;
using System;
using System.Collections.Generic;
using RingworldTechDemo.Diagnostics;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Bounded main-RD JONSWAP/TMA FFT provider.  It intentionally has no scene ownership.</summary>
public readonly record struct RingWaterFftSettings(
    int Resolution, int CascadeCount, int Seed, Vector2 Period0, Vector2 Period1,
    float WindSpeedMetersPerSecond, float WindDirectionDegrees, float FoamAmount, float Whitecap,
    Vector2 Period2 = default, Vector2 Period3 = default);
public readonly record struct RingWaterFftSurfaceQueryResult(float[] Heights, float[] ResidualMeters);

public sealed class RingWaterFftProvider : IDisposable
{
    private readonly object _gate = new();
    private RingWaterFftSettings _settings;
    private bool _disposed, _queued, _fatalUnsupported, _topologyDirty = true;
    private bool _ready;
    private int _generation, _legacyNextCascade, _configurationVersion, _topologyEpoch, _topologyPreparedEpoch, _spectrumDirtyMask;
    private int _initializedCascadeMask;
    private readonly double[] _lastCascadeTimes = { double.NaN, double.NaN, double.NaN, double.NaN };
    private readonly float[] _cascadeUpdateRates = new float[4];
    private long _completedUpdates;
    private double _requestedTime;
    private int _requestedCascadeMask;
    private Texture2DArrayRD? _normalFoam;
    private Texture2DArrayRD? _displacementTexture;
    // Kept separately from the public reference so render-thread retirement can
    // detach TextureRdRid even after public availability was withdrawn.
    private Texture2DArrayRD? _renderTexture;
    private Texture2DArrayRD? _renderDisplacementTexture;
    private readonly List<Rid> _rids = new();
    private readonly Dictionary<Rid, Rid> _pipelineShaders = new();
    private RenderingDevice? _rd;
    private Rid _spectrum, _butterfly, _fft, _normal, _displacement;
    private Rid _spectrumPipeline, _modulatePipeline, _butterflyPipeline, _fftPipeline, _transposePipeline, _unpackPipeline, _mipmapPipeline, _surfaceQueryPipeline;
    private Rid _spectrumSet, _butterflySet, _fftSet, _modulateSpectrumSet, _modulateFftSet, _unpackImageSet, _unpackFftSet;
    private Rid _surfaceQuerySet, _surfaceQueryBuffer, _surfaceSampler;
    // The shoreline bindings are always present so standalone FFT tests retain
    // their legacy raw surface path with controls.w == 0.
    private Rid _shorelineQueryBuffer, _shorelineFallbackTexture, _shorelineSampler;
    private Rid _shorelineQuerySet, _shorelineQueryTexture;
    private bool _surfaceQueryPending;
    private int _surfaceQuerySerial;
    private readonly List<MipLevel> _mipLevels = new();
    private string _status = "Uninitialized";

    public bool Ready { get { lock (_gate) return _ready; } }
    public Texture2DArrayRD? NormalFoamTexture { get { lock (_gate) return _ready ? _normalFoam : null; } }
    /// <summary>
    /// FFT displacement, encoded as (around, height, across, unused) in metres.
    /// It has a minimum of two backing layers because Texture2DArrayRD cannot
    /// publish a one-layer RD array on Godot 4.7.2. Only CascadeCount layers are
    /// meaningful. Mips use the same periodic box filter as normal/foam.
    /// </summary>
    public Texture2DArrayRD? DisplacementTexture { get { lock (_gate) return _ready ? _displacementTexture : null; } }
    public string Status { get { lock (_gate) return _status; } }
    public int Generation { get { lock (_gate) return _generation; } }
    /// <summary>Changes immediately when a request's spectrum/topology inputs change.</summary>
    public int ConfigurationVersion { get { lock (_gate) return _configurationVersion; } }
    public long CompletedUpdates { get { lock (_gate) return _completedUpdates; } }
    public float PrimaryGeometryUpdatesPerSecond { get { lock (_gate) return _cascadeUpdateRates[Math.Max(0, _settings.CascadeCount >= 2 ? 1 : 0)]; } }
    public float DetailUpdatesPerSecond { get { lock (_gate) { int primary = _settings.CascadeCount >= 2 ? 1 : 0; float total = 0; int count = 0; for (int i = 0; i < _settings.CascadeCount; i++) if (i != primary) { total += _cascadeUpdateRates[i]; count++; } return count > 0 ? total / count : 0; } } }
    public long EstimatedBytes { get; private set; }

    /// <summary>
    /// Updates the requested simulation. For a topology change,
    /// <paramref name="prepareTopologyRebuild"/> runs on the calling main thread
    /// before a render-thread acknowledgement permits retirement of published
    /// Texture2DArrayRD backing RIDs. This prevents a queued render tick from
    /// invalidating material bindings before they have been detached.
    /// </summary>
    public bool Configure(RingWaterFftSettings settings, Action? prepareTopologyRebuild = null)
    {
        settings = Clamp(settings);
        bool topology;
        int topologyEpoch;
        lock (_gate)
        {
            if (_disposed) return false;
            topology = settings.Resolution != _settings.Resolution || settings.CascadeCount != _settings.CascadeCount;
            bool spectrum = topology || settings.Seed != _settings.Seed || settings.Period0 != _settings.Period0 || settings.Period1 != _settings.Period1 || settings.Period2 != _settings.Period2 || settings.Period3 != _settings.Period3
                || !Mathf.IsEqualApprox(settings.WindSpeedMetersPerSecond, _settings.WindSpeedMetersPerSecond)
                || !Mathf.IsEqualApprox(settings.WindDirectionDegrees, _settings.WindDirectionDegrees);
            _settings = settings; _configurationVersion++; _topologyDirty |= topology;
            if (topology) _topologyEpoch++;
            if (spectrum) _spectrumDirtyMask = (1 << settings.CascadeCount) - 1;
            if (topology) { _ready = false; _status = "Rebuild queued"; }
            topologyEpoch = _topologyEpoch;
        }
        if (!topology) return false;

        // SetShaderParameter queues its renderer work before this callback's
        // CallOnRenderThread acknowledgement. RenderTick checks the latter, so
        // even an older queued tick cannot free the old RIDs early.
        prepareTopologyRebuild?.Invoke();
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            lock (_gate)
                if (!_disposed && topologyEpoch == _topologyEpoch)
                    _topologyPreparedEpoch = topologyEpoch;
        }));
        return true;
    }

    public void RequestUpdate(double simulationSeconds, int cascadeMask)
    {
        lock (_gate)
        {
            if (_disposed || _fatalUnsupported) return;
            _requestedTime = double.IsFinite(simulationSeconds) ? simulationSeconds : 0d;
            _requestedCascadeMask |= cascadeMask & ((1 << _settings.CascadeCount) - 1);
            if (_queued) return; // Merge into the one queued render callback; never queue catch-up work.
            _queued = true;
        }
        RenderingServer.CallOnRenderThread(Callable.From(RenderTick));
    }

    /// <summary>Legacy single-cascade request retained for existing diagnostics.</summary>
    public void RequestUpdate(double simulationSeconds, float deltaSeconds)
    {
        int mask;
        lock (_gate) { mask = 1 << _legacyNextCascade; _legacyNextCascade = (_legacyNextCascade + 1) % Math.Max(1, _settings.CascadeCount); }
        RequestUpdate(simulationSeconds, mask);
    }

    /// <summary>
    /// Test-only inspection path. The copied payload is one RGBA16F normal/foam
    /// layer (resolution * resolution * 8 bytes); gameplay never calls this.
    /// </summary>
    public void RequestReadbackForTests(Action<byte[]> callback, int layer = 0, int mipLevel = 0)
        => RequestTextureReadbackForTests(false, callback, layer, mipLevel);

    /// <summary>Test-only RGBA16F displacement readback. Values are (around, up, across, unused).</summary>
    public void RequestDisplacementReadbackForTests(Action<byte[]> callback, int layer = 0, int mipLevel = 0)
        => RequestTextureReadbackForTests(true, callback, layer, mipLevel);

    /// <summary>Bounded asynchronous point sample; no full texture readback.</summary>
    public bool RequestSurfaceOffsets(Vector2 uv, int cascade, float mipLevel, Vector2 period, float heightScale, float choppiness, Action<RingWaterFftSurfaceQueryResult?> callback)
        => RequestSurfaceOffsets(uv, cascade, mipLevel, period, heightScale, choppiness, 0.0f, 0.0f, callback);

    /// <summary>
    /// Bounded asynchronous point sample with the same distance fade as the
    /// near geometry. A non-positive fade distance disables that fade for
    /// diagnostic callers which only need raw displacement parity.
    /// </summary>
    public bool RequestSurfaceOffsets(Vector2 uv, int cascade, float mipLevel, Vector2 period, float heightScale, float choppiness, float geometryFadeEndMeters, float baseSurfaceToBodyVerticalMeters, Action<RingWaterFftSurfaceQueryResult?> callback, Vector2[]? cascadeUvs = null, Vector2[]? cascadePeriods = null, float[]? cascadeLods = null, int cascadeMask = 0, Texture2D? shorelineField = null, Vector2 shorelineUvOrigin = default, float shorelineInverseSpan = 0f, float shorelineFadeDistance = 10f, float shorelineChopFadeDistance = 20f, float shorelineShallowDepth = 3f, float deepWaterWaveDepth = 100f, int shorelineMode = 0)
    {
        ArgumentNullException.ThrowIfNull(callback);
        int generation, configuration, serial;
        lock (_gate) { if (_surfaceQueryPending || _disposed || !_ready || !_surfaceQueryBuffer.IsValid) return false; _surfaceQueryPending = true; generation = _generation; configuration = _configurationVersion; serial = ++_surfaceQuerySerial; }
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            try {
                bool stale;
                lock (_gate)
                {
                    stale = _disposed || generation != _generation || configuration != _configurationVersion || serial != _surfaceQuerySerial;
                    // A newer request owns this flag. Never clear its pending
                    // state while delivering an older request's null result.
                    if (stale && serial == _surfaceQuerySerial) _surfaceQueryPending = false;
                }
                if (stale) { Callable.From(() => callback(null)).CallDeferred(); return; }
                var pc = new uint[28];
                int selected = Mathf.Clamp(cascade, 0, 3);
                for (int layer = 0; layer < 4; ++layer)
                {
                    Vector2 layerUv = cascadeUvs is not null && layer < cascadeUvs.Length ? cascadeUvs[layer] : layer == selected ? uv : Vector2.Zero;
                    Vector2 layerPeriod = cascadePeriods is not null && layer < cascadePeriods.Length ? cascadePeriods[layer] : layer == selected ? period : Vector2.One;
                    int offset = layer * 4;
                    pc[offset] = F(layerUv.X); pc[offset + 1] = F(layerUv.Y);
                    pc[offset + 2] = F(Mathf.Max(.01f, layerPeriod.X)); pc[offset + 3] = F(Mathf.Max(.01f, layerPeriod.Y));
                    pc[16 + layer] = F(Mathf.Max(0, cascadeLods is not null && layer < cascadeLods.Length ? cascadeLods[layer] : mipLevel));
                }
                pc[20] = F(Mathf.Clamp(heightScale, 0, 10)); pc[21] = F(Mathf.Clamp(choppiness, 0, 3));
                pc[22] = F(float.IsFinite(geometryFadeEndMeters) ? Mathf.Max(0.0f, geometryFadeEndMeters) : 0.0f); pc[23] = F(float.IsFinite(baseSurfaceToBodyVerticalMeters) ? baseSurfaceToBodyVerticalMeters : 0.0f);
                pc[24] = I(cascadeMask != 0 ? cascadeMask & 15 : 1 << selected);
                int safeShoreMode = shorelineMode is 0 or 1 or 2 ? shorelineMode : 0;
                Rid shoreTexture = _shorelineFallbackTexture;
                if (safeShoreMode == 2 && shorelineField is not null && GodotObject.IsInstanceValid(shorelineField))
                {
                    Rid candidate = RenderingServer.TextureGetRdTexture(shorelineField.GetRid(), false);
                    if (candidate.IsValid) shoreTexture = candidate;
                    else safeShoreMode = 1;
                }
                else if (safeShoreMode == 2) safeShoreMode = 1;
                if (!_shorelineQuerySet.IsValid || _shorelineQueryTexture != shoreTexture)
                {
                    if (_shorelineQuerySet.IsValid) { _rd!.FreeRid(_shorelineQuerySet); _rids.Remove(_shorelineQuerySet); }
                    _shorelineQueryTexture = shoreTexture;
                    _shorelineQuerySet = Set(_surfaceQueryPipeline, 0, SamplerWithTexture(_surfaceSampler, _displacement, 0), Buffer(_surfaceQueryBuffer, 1), Buffer(_shorelineQueryBuffer, 2), SamplerWithTexture(_shorelineSampler, shoreTexture, 3));
                }
                // Keep the 32-byte shoreline ABI: mapping.w now carries the
                // deep-water threshold while mapping.z remains the shared UV scale.
                uint[] shoreData = { F(shorelineUvOrigin.X), F(shorelineUvOrigin.Y), F(shorelineInverseSpan), F(deepWaterWaveDepth), F(shorelineFadeDistance), F(shorelineChopFadeDistance), F(shorelineShallowDepth), F(safeShoreMode) };
                var shoreBytes = new byte[32]; System.Buffer.BlockCopy(shoreData, 0, shoreBytes, 0, shoreBytes.Length);
                _rd!.BufferUpdate(_shorelineQueryBuffer, 0, 32, shoreBytes);
                long list = _rd!.ComputeListBegin(); Bind(list, _surfaceQueryPipeline, _shorelineQuerySet); Push(list, pc);
                _rd.ComputeListDispatch(list, 3, 3, 1); _rd.ComputeListEnd();
                _rd.BufferGetDataAsync(_surfaceQueryBuffer, Callable.From((byte[] bytes) => { RingWaterFftSurfaceQueryResult? v = null; lock (_gate) { if (!_disposed && generation == _generation && configuration == _configurationVersion && serial == _surfaceQuerySerial && bytes.Length >= 144) { var h = new float[9]; var r = new float[9]; for (int i = 0; i < 9; i++) { h[i] = BitConverter.ToSingle(bytes, i * 16); r[i] = BitConverter.ToSingle(bytes, i * 16 + 4); } v = new(h, r); } if (serial == _surfaceQuerySerial) _surfaceQueryPending = false; } Callable.From(() => callback(v)).CallDeferred(); }));
            } catch { lock (_gate) if (serial == _surfaceQuerySerial) _surfaceQueryPending = false; Callable.From(() => callback(null)).CallDeferred(); }
        }));
        return true;
    }

    private void RequestTextureReadbackForTests(bool displacement, Action<byte[]> callback, int layer, int mipLevel)
    {
        ArgumentNullException.ThrowIfNull(callback);
        Rid texture; int generation; int arrayLayers; int mipCount; int resolution;
        lock (_gate)
        {
            if (_disposed || !_ready || !(displacement ? _displacement : _normal).IsValid) { Callable.From(() => callback(Array.Empty<byte>())).CallDeferred(); return; }
            texture = displacement ? _displacement : _normal; generation = _generation; arrayLayers = _settings.CascadeCount;
            mipCount = 1 + (int)Math.Log2(_settings.Resolution);
            resolution = _settings.Resolution;
        }
        int safeLayer = Mathf.Clamp(layer, 0, Math.Max(0, arrayLayers - 1));
        int safeMip = Mathf.Clamp(mipLevel, 0, Math.Max(0, mipCount - 1));
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            byte[] data = Array.Empty<byte>();
            lock (_gate)
            {
                if (!_disposed && generation == _generation && (displacement ? _displacement : _normal) == texture && _rd is not null)
                {
                    try
                    {
                        // TextureGetData on a parent array layer contains its
                        // complete mip payload. Slice it here rather than
                        // relying on backend-specific shared-slice readback.
                        byte[] payload = _rd.TextureGetData(texture, (uint)safeLayer);
                        int byteOffset = 0;
                        for (int mip = 0, size = resolution; mip < safeMip; mip++, size = Math.Max(1, size / 2))
                            byteOffset += size * size * 8;
                        int mipSize = Math.Max(1, resolution >> safeMip);
                        int byteCount = mipSize * mipSize * 8;
                        if (payload.Length >= byteOffset + byteCount)
                        {
                            data = new byte[byteCount];
                            System.Buffer.BlockCopy(payload, byteOffset, data, 0, byteCount);
                        }
                        else _status = "FFT readback payload omitted mip levels";
                    }
                    catch (Exception error) { _status = "FFT readback error: " + error.Message; }
                }
            }
            // Callable.CallDeferred moves test observation back out of the render callback.
            Callable.From(() => callback(data)).CallDeferred();
        }));
    }

    private void RenderTick()
    {
        RingWaterFftSettings settings; int generation; int configurationVersion, topologyEpoch; double time; float delta;
        bool buildPending = false;
        lock (_gate)
        {
            if (_disposed) return;
            settings = Clamp(_settings); generation = _generation; configurationVersion = _configurationVersion; topologyEpoch = _topologyEpoch; time = _requestedTime; delta = 0;
        }
        try
        {
            _rd ??= RenderingServer.GetRenderingDevice();
            if (_rd is null)
            {
                lock (_gate) _fatalUnsupported = true;
                SetStatus("Unsupported renderer (no main RenderingDevice)");
                return;
            }
            if (_topologyDirty || !_normal.IsValid)
            {
                lock (_gate)
                {
                    // The initial dirty state owns no published texture, so epoch
                    // zero can build immediately. Later topology epochs require
                    // a material-retirement acknowledgement. Recheck the current
                    // epoch so a queued old tick never frees for a newer topology.
                    if (_topologyDirty && (_topologyPreparedEpoch != topologyEpoch || _topologyEpoch != topologyEpoch))
                    {
                        _status = "Awaiting material retirement";
                        return;
                    }
                }
                lock (_gate) { _ready = false; _status = "Rebuilding FFT"; }
                // A settings change mid-build invalidates the partial resources; start over.
                if (_createStage > 0 && _createConfigurationVersion != configurationVersion) FreeRenderResources();
                if (_createStage == 0) { FreeRenderResources(); _createConfigurationVersion = configurationVersion; }
                if (!CreateResourcesStep(settings))
                {
                    // One GPU resource group per rendered frame keeps a topology rebuild from landing in a single frame.
                    buildPending = true;
                    lock (_gate) _status = $"Building FFT ({_createStage}/{CreateStageCount})";
                    return;
                }
                lock (_gate)
                {
                    if (_disposed || configurationVersion != _configurationVersion)
                    {
                        _topologyDirty = true;
                        FreeRenderResources();
                        return;
                    }
                    _generation++; generation = _generation; _topologyDirty = false;
                    _spectrumDirtyMask = (1 << settings.CascadeCount) - 1; _legacyNextCascade = 0;
                    _initializedCascadeMask = 0;
                    Array.Fill(_lastCascadeTimes, double.NaN);
                    Array.Fill(_cascadeUpdateRates, 0f);
                    _ready = false;
                    _status = "Initializing FFT";
                }
            }
            int cascadeMask;
            lock (_gate) { if (_disposed || generation != _generation || configurationVersion != _configurationVersion) return; cascadeMask = _requestedCascadeMask & ((1 << settings.CascadeCount) - 1); _requestedCascadeMask = 0; }
            // A topology rebuild must initialize every layer together. Afterwards the
            // controller supplies only cascades due this rendered frame.
            if (_initializedCascadeMask != (1 << settings.CascadeCount) - 1)
                cascadeMask |= ((1 << settings.CascadeCount) - 1) & ~_initializedCascadeMask;
            for (int cascade = 0; cascade < settings.CascadeCount; cascade++)
            {
                if ((cascadeMask & (1 << cascade)) == 0) continue;
                bool makeSpectrum; float foamDelta = delta; double previous;
                lock (_gate) { makeSpectrum = (_spectrumDirtyMask & (1 << cascade)) != 0; previous = _lastCascadeTimes[cascade]; if ((_initializedCascadeMask & (1 << cascade)) != 0 && double.IsFinite(previous)) foamDelta = Mathf.Clamp((float)(time - previous), 0f, .25f); }
                DispatchCascade(settings, cascade, (float)time, foamDelta, makeSpectrum);
                lock (_gate)
                {
                    if (_disposed || generation != _generation || configurationVersion != _configurationVersion) return;
                    if (double.IsFinite(previous) && time > previous) { float instantaneous = 1f / Mathf.Max(.001f, (float)(time - previous)); _cascadeUpdateRates[cascade] = _cascadeUpdateRates[cascade] <= 0 ? instantaneous : Mathf.Lerp(_cascadeUpdateRates[cascade], instantaneous, .18f); }
                    _lastCascadeTimes[cascade] = time;
                    if (makeSpectrum) _spectrumDirtyMask &= ~(1 << cascade);
                    _initializedCascadeMask |= 1 << cascade;
                    _completedUpdates++;
                }
            }
            lock (_gate)
            {
                _ready = _normalFoam is not null && _normalFoam.TextureRdRid.IsValid && _displacementTexture is not null && _displacementTexture.TextureRdRid.IsValid && _initializedCascadeMask == (1 << settings.CascadeCount) - 1;
                _status = _ready ? "Ready" : "Initializing FFT";
            }
        }
        catch (Exception error)
        {
            // Keep the analytic fallback stable after an initialization failure.
            // Toggling the feature on again creates a fresh provider for a retry.
            lock (_gate) _fatalUnsupported = true;
            SetStatus("FFT error: " + error.Message);
        }
        finally
        {
            // A main-thread request can arrive after this tick consumes its mask.
            // Preserve that bounded work in one follow-up callback rather than
            // leaving a merged mask stranded until another simulation tick.
            bool reschedule, continueBuild;
            lock (_gate)
            {
                _queued = false;
                continueBuild = buildPending && !_disposed && !_fatalUnsupported;
                reschedule = !continueBuild && !_disposed && !_fatalUnsupported && !_topologyDirty && _requestedCascadeMask != 0;
                if (reschedule || continueBuild) _queued = true;
            }
            if (reschedule) RenderingServer.CallOnRenderThread(Callable.From(RenderTick));
            // Hop through the main thread so the next stage runs on the next frame rather than in this render flush.
            else if (continueBuild) Callable.From(() => RenderingServer.CallOnRenderThread(Callable.From(RenderTick))).CallDeferred();
        }
    }

    private const int CreateStageCount = 17;
    private int _createStage, _createConfigurationVersion;

    /// <summary>Runs the next resource-creation stage (at most one texture, buffer group or pipeline); true once every stage has run.</summary>
    private bool CreateResourcesStep(RingWaterFftSettings s)
    {
        int n = s.Resolution, c = s.CascadeCount, stages = (int)Math.Log2(n);
        int publishedLayers = Math.Max(2, c);
        int mipCount = 1 + stages;
        switch (_createStage)
        {
            case 0:
                // The selected Stockham kernel needs n lanes, while the fixed transpose
                // kernel uses 32x32 lanes and 8 KiB shared memory. The unpack kernel is
                // smaller (512 lanes) but also uses 8 KiB of shared vec2 staging.
                if (_rd is null
                    || _rd.LimitGet(RenderingDevice.Limit.MaxComputeWorkgroupSizeX) < (uint)Math.Max(n, 32)
                    || _rd.LimitGet(RenderingDevice.Limit.MaxComputeWorkgroupSizeY) < 32u
                    || _rd.LimitGet(RenderingDevice.Limit.MaxComputeWorkgroupInvocations) < 1024u
                    || _rd.LimitGet(RenderingDevice.Limit.MaxComputeSharedMemorySize) < (uint)Math.Max(8448, n * 16)
                    || _rd.LimitGet(RenderingDevice.Limit.MaxTextureArrayLayers) < (uint)c)
                {
                    throw new NotSupportedException($"Renderer lacks workgroup/shared-memory support for {n}x{n} water FFT ({n * 16 / 1024} KiB shared required).");
                }
                _spectrum = Texture(RenderingDevice.DataFormat.R32G32B32A32Sfloat, n, c, RenderingDevice.TextureUsageBits.StorageBit, 16);
                break;
            case 1:
                // Both published arrays deliberately have two backing layers at minimum.
                // The FFT only writes the active cascade count; unused layers remain zero.
                _displacement = Texture(RenderingDevice.DataFormat.R16G16B16A16Sfloat, n, publishedLayers, RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanCopyFromBit, 8, mipCount);
                break;
            case 2:
                // Texture2DArrayRD rejects a one-layer backing texture in Godot 4.7.2.
                // The second output layer remains unused for a one-cascade simulation.
                _normal = Texture(RenderingDevice.DataFormat.R16G16B16A16Sfloat, n, publishedLayers, RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanCopyFromBit, 8, mipCount);
                break;
            case 3:
                _butterfly = Own(_rd!.StorageBufferCreate((uint)(stages * n * 16)));
                _fft = Own(_rd.StorageBufferCreate((uint)(c * n * n * 4 * 2 * 2 * 4)));
                _renderTexture = new Texture2DArrayRD { TextureRdRid = _normal };
                _normalFoam = _renderTexture;
                _renderDisplacementTexture = new Texture2DArrayRD { TextureRdRid = _displacement };
                _displacementTexture = _renderDisplacementTexture;
                break;
            case 4: _spectrumPipeline = Pipeline("spectrum_compute.glsl"); break;
            case 5: _butterflyPipeline = Pipeline("fft_butterfly.glsl"); break;
            case 6: _modulatePipeline = Pipeline("spectrum_modulate.glsl"); break;
            case 7: _fftPipeline = Pipeline($"fft_compute_{n}.glsl"); break;
            case 8: _transposePipeline = Pipeline("transpose.glsl"); break;
            case 9: _unpackPipeline = Pipeline("fft_unpack.glsl"); break;
            case 10: _mipmapPipeline = Pipeline("fft_mipmap.glsl"); break;
            case 11: _surfaceQueryPipeline = Pipeline("fft_surface_query.glsl"); break;
            case 12:
                // Explicitly expose the generated chain. Some drivers default an RD
                // sampler's maximum LOD to zero, which silently turns a requested
                // trilinear gameplay query into base-mip sampling.
                _surfaceSampler = Own(_rd!.SamplerCreate(new RDSamplerState { MagFilter = RenderingDevice.SamplerFilter.Linear, MinFilter = RenderingDevice.SamplerFilter.Linear, MipFilter = RenderingDevice.SamplerFilter.Linear, MinLod = 0, MaxLod = stages, RepeatU = RenderingDevice.SamplerRepeatMode.Repeat, RepeatV = RenderingDevice.SamplerRepeatMode.Repeat }));
                _surfaceQueryBuffer = Own(_rd.StorageBufferCreate(9 * 16));
                _shorelineQueryBuffer = Own(_rd.StorageBufferCreate(8 * 4));
                _shorelineSampler = Own(_rd.SamplerCreate(new RDSamplerState { MagFilter = RenderingDevice.SamplerFilter.Linear, MinFilter = RenderingDevice.SamplerFilter.Linear, MipFilter = RenderingDevice.SamplerFilter.Linear, RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge, RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge }));
                var shorelineFormat = new RDTextureFormat { TextureType = RenderingDevice.TextureType.Type2D, Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat, Width = 1, Height = 1, ArrayLayers = 1, Mipmaps = 1, UsageBits = RenderingDevice.TextureUsageBits.SamplingBit };
                var shorelineData = new Godot.Collections.Array<byte[]>(); shorelineData.Add(new byte[8]);
                _shorelineFallbackTexture = Own(_rd.TextureCreate(shorelineFormat, new RDTextureView(), shorelineData));
                break;
            case 13:
                _spectrumSet = Set(_spectrumPipeline, 0, Image(_spectrum, 0));
                _butterflySet = Set(_butterflyPipeline, 0, Buffer(_butterfly, 0));
                _fftSet = Set(_fftPipeline, 0, Buffer(_butterfly, 0), Buffer(_fft, 1));
                // Godot 4.7 validates storage-image read/write qualifiers per pipeline.
                _modulateSpectrumSet = Set(_modulatePipeline, 0, Image(_spectrum, 0));
                _modulateFftSet = Set(_modulatePipeline, 1, Buffer(_fft, 0));
                _unpackImageSet = Set(_unpackPipeline, 0, Image(_displacement, 0), Image(_normal, 1));
                _unpackFftSet = Set(_unpackPipeline, 1, Buffer(_fft, 0));
                _shorelineQueryTexture = _shorelineFallbackTexture;
                _surfaceQuerySet = _shorelineQuerySet = Set(_surfaceQueryPipeline, 0, SamplerWithTexture(_surfaceSampler, _displacement, 0), Buffer(_surfaceQueryBuffer, 1), Buffer(_shorelineQueryBuffer, 2), SamplerWithTexture(_shorelineSampler, _shorelineFallbackTexture, 3));
                break;
            case 14:
                for (int layer = 0; layer < c; layer++)
                for (int mip = 1, size = n / 2; mip < mipCount; mip++, size /= 2)
                {
                    Rid source = Own(_rd!.TextureCreateSharedFromSlice(new RDTextureView(), _normal, (uint)layer, (uint)(mip - 1), 1));
                    Rid destination = Own(_rd.TextureCreateSharedFromSlice(new RDTextureView(), _normal, (uint)layer, (uint)mip, 1));
                    _mipLevels.Add(new MipLevel(size, Set(_mipmapPipeline, 0, Image(source, 0), Image(destination, 1))));
                }
                break;
            case 15:
                for (int layer = 0; layer < c; layer++)
                for (int mip = 1, size = n / 2; mip < mipCount; mip++, size /= 2)
                {
                    Rid source = Own(_rd!.TextureCreateSharedFromSlice(new RDTextureView(), _displacement, (uint)layer, (uint)(mip - 1), 1));
                    Rid destination = Own(_rd.TextureCreateSharedFromSlice(new RDTextureView(), _displacement, (uint)layer, (uint)mip, 1));
                    _mipLevels.Add(new MipLevel(size, Set(_mipmapPipeline, 0, Image(source, 0), Image(destination, 1))));
                }
                break;
            default:
                long activePixels = (long)n * n * c;
                long publishedPixels = (long)n * n * publishedLayers;
                EstimatedBytes = activePixels * 16 + publishedPixels * 8 * 4 / 3
                    + publishedPixels * 8 * 4 / 3 + (long)c * n * n * 4 * 2 * 2 * 4 + (long)stages * n * 16 + 9 * 16;
                long list = _rd!.ComputeListBegin();
                Bind(list, _butterflyPipeline, _butterflySet); _rd.ComputeListDispatch(list, (uint)(n / 128), (uint)stages, 1); _rd.ComputeListEnd();
                _createStage = 0;
                return true;
        }
        _createStage++;
        return false;
    }

    private void DispatchCascade(RingWaterFftSettings s, int cascade, float time, float delta, bool spectrum)
    {
        using RingPerformanceGpu.GpuScope diagnosticScope = RingPerformanceGpu.BeginScope(RingPerformanceGpu.WaterFft);
        int n = s.Resolution; long list = _rd!.ComputeListBegin();
        if (spectrum)
        {
            Vector2 period = CascadePeriod(s, cascade);
            float fetch = Math.Max(1000f, period.X * period.Y * .5f);
            float alpha = .076f * Mathf.Pow(s.WindSpeedMetersPerSecond * s.WindSpeedMetersPerSecond / (fetch * 9.81f), .22f);
            float peak = 22f * Mathf.Pow(9.81f * 9.81f / (s.WindSpeedMetersPerSecond * fetch), 1f / 3f);
            Bind(list, _spectrumPipeline, _spectrumSet); Push(list, I(s.Seed + cascade * 7919), I(s.Seed * 31 + cascade * 104729), F(period.X), F(period.Y), F(alpha), F(peak), F(s.WindSpeedMetersPerSecond), F(Mathf.DegToRad(s.WindDirectionDegrees)), F(20), F(.8f), F(1), F(.2f), I(cascade)); _rd.ComputeListDispatch(list, (uint)(n / 16), (uint)(n / 16), 1); _rd.ComputeListAddBarrier(list);
        }
        Vector2 modulationPeriod = CascadePeriod(s, cascade);
        Bind(list, _modulatePipeline, _modulateSpectrumSet, _modulateFftSet); Push(list, F(modulationPeriod.X), F(modulationPeriod.Y), F(20), F(time), I(cascade)); _rd.ComputeListDispatch(list, (uint)(n / 16), (uint)(n / 16), 1); _rd.ComputeListAddBarrier(list);
        Bind(list, _fftPipeline, _fftSet); Push(list, I(cascade)); _rd.ComputeListDispatch(list, 1, (uint)n, 4); _rd.ComputeListAddBarrier(list);
        Bind(list, _transposePipeline, _fftSet); Push(list, I(cascade)); _rd.ComputeListDispatch(list, (uint)(n / 32), (uint)(n / 32), 4); _rd.ComputeListAddBarrier(list);
        Bind(list, _fftPipeline, _fftSet); Push(list, I(cascade)); _rd.ComputeListDispatch(list, 1, (uint)n, 4); _rd.ComputeListAddBarrier(list);
        Bind(list, _unpackPipeline, _unpackImageSet, _unpackFftSet); Push(list, I(cascade), F(s.Whitecap), F(delta * s.FoamAmount * 7.5f), F(delta * Math.Max(.5f, 10f - s.FoamAmount) * 1.15f)); _rd.ComputeListDispatch(list, (uint)(n / 16), (uint)(n / 16), 1); _rd.ComputeListAddBarrier(list);
        int mipOffset = cascade * (int)Math.Log2(n);
        for (int mip = 0; mip < (int)Math.Log2(n); mip++)
        {
            MipLevel level = _mipLevels[mipOffset + mip];
            Bind(list, _mipmapPipeline, level.Set); Push(list, I(level.Size * 2));
            _rd.ComputeListDispatch(list, (uint)((level.Size + 7) / 8), (uint)((level.Size + 7) / 8), 1);
            _rd.ComputeListAddBarrier(list);
        }
        int displacementMipOffset = s.CascadeCount * (int)Math.Log2(n) + mipOffset;
        for (int mip = 0; mip < (int)Math.Log2(n); mip++)
        {
            MipLevel level = _mipLevels[displacementMipOffset + mip];
            Bind(list, _mipmapPipeline, level.Set); Push(list, I(level.Size * 2));
            _rd.ComputeListDispatch(list, (uint)((level.Size + 7) / 8), (uint)((level.Size + 7) / 8), 1);
            _rd.ComputeListAddBarrier(list);
        }
        _rd.ComputeListEnd();
    }

    private Rid Texture(RenderingDevice.DataFormat format, int n, int layers, RenderingDevice.TextureUsageBits usage, int bytesPerPixel, int mipmaps = 1)
    {
        var f = new RDTextureFormat { TextureType = RenderingDevice.TextureType.Type2DArray, Format = format, Width = (uint)n, Height = (uint)n, ArrayLayers = (uint)layers, Mipmaps = (uint)mipmaps, UsageBits = usage };
        int bytesPerLayer = 0;
        for (int size = n, mip = 0; mip < mipmaps; mip++, size = Math.Max(1, size / 2)) bytesPerLayer += size * size * bytesPerPixel;
        var zero = new Godot.Collections.Array<byte[]>(); for (int i = 0; i < layers; i++) zero.Add(new byte[bytesPerLayer]);
        return Own(_rd!.TextureCreate(f, new RDTextureView(), zero));
    }
    private Rid Pipeline(string name) { var file = GD.Load<RDShaderFile>($"res://third_party/godot_ocean_waves/compute/{name}") ?? throw new InvalidOperationException(name); Rid shader = Own(_rd!.ShaderCreateFromSpirV(file.GetSpirV())); Rid pipeline = Own(_rd.ComputePipelineCreate(shader)); _pipelineShaders[pipeline] = shader; return pipeline; }
    private Rid Set(Rid pipeline, uint set, params RDUniform[] uniforms) { return Own(_rd!.UniformSetCreate(new Godot.Collections.Array<RDUniform>(uniforms), _pipelineShaders[pipeline], set)); }
    private static RDUniform Image(Rid rid, uint binding) { var u = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = (int)binding }; u.AddId(rid); return u; }
    private static RDUniform Buffer(Rid rid, uint binding) { var u = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = (int)binding }; u.AddId(rid); return u; }
    private static RDUniform SamplerWithTexture(Rid sampler, Rid texture, uint binding) { var u = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = (int)binding }; u.AddId(sampler); u.AddId(texture); return u; }
    private void Bind(long list, Rid pipeline, params Rid[] sets) { _rd!.ComputeListBindComputePipeline(list, pipeline); for (uint i = 0; i < sets.Length; i++) _rd.ComputeListBindUniformSet(list, sets[i], i); }
    private void Push(long list, params uint[] data)
    {
        var bytes = new byte[data.Length * 4];
        System.Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        _rd!.ComputeListSetPushConstant(list, bytes, (uint)bytes.Length);
    }
    private static uint I(int value) => unchecked((uint)value); private static uint F(float value) => BitConverter.SingleToUInt32Bits(value);
    private Rid Own(Rid rid)
    {
        if (!rid.IsValid) throw new InvalidOperationException("RenderingDevice resource creation failed.");
        _rids.Add(rid);
        return rid;
    }
    private void SetStatus(string value) { lock (_gate) { _ready = false; _status = value; } }
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; _ready = false; _normalFoam = null; _displacementTexture = null; _status = "Disposed"; } RenderingServer.CallOnRenderThread(Callable.From(FreeRenderResources)); GC.SuppressFinalize(this); }
    private void FreeRenderResources()
    {
        if (_rd is null) return;
        if (_renderTexture is not null) _renderTexture.TextureRdRid = default;
        _renderTexture = null;
        if (_renderDisplacementTexture is not null) _renderDisplacementTexture.TextureRdRid = default;
        _renderDisplacementTexture = null;
        _surfaceQueryPending = false; _surfaceQuerySerial++;
        for (int i = _rids.Count - 1; i >= 0; --i) if (_rids[i].IsValid) _rd.FreeRid(_rids[i]);
        _rids.Clear(); _mipLevels.Clear(); _pipelineShaders.Clear();
        _surfaceQuerySet = _shorelineQuerySet = _shorelineQueryTexture = default;
        _shorelineQueryBuffer = _shorelineFallbackTexture = _shorelineSampler = default;
        _spectrum = _normal = _displacement = _butterfly = _fft = default;
        _createStage = 0;
    }
    private static Vector2 CascadePeriod(in RingWaterFftSettings s, int cascade) => cascade switch { 0 => s.Period0, 1 => s.Period1, 2 => s.Period2.X > .01f ? s.Period2 : s.Period1 * 4.0f, _ => s.Period3.X > .01f ? s.Period3 : s.Period1 * 16.0f };
    private static RingWaterFftSettings Clamp(RingWaterFftSettings s) => s with { Resolution = s.Resolution is 128 or 256 or 512 or 1024 ? s.Resolution : 128, CascadeCount = Mathf.Clamp(s.CascadeCount, 1, 4), Period0 = Period(s.Period0, 12), Period1 = Period(s.Period1, 79), Period2 = Period(s.Period2, s.Period1.X * 4), Period3 = Period(s.Period3, s.Period1.X * 16), WindSpeedMetersPerSecond = float.IsFinite(s.WindSpeedMetersPerSecond) ? Mathf.Clamp(s.WindSpeedMetersPerSecond, .1f, 60) : 6, FoamAmount = float.IsFinite(s.FoamAmount) ? Mathf.Clamp(s.FoamAmount, 0, 10) : 3, Whitecap = float.IsFinite(s.Whitecap) ? Mathf.Clamp(s.Whitecap, 0, 2) : .5f, WindDirectionDegrees = float.IsFinite(s.WindDirectionDegrees) ? s.WindDirectionDegrees : 0 };
    private static Vector2 Period(Vector2 value, float fallback) => new(float.IsFinite(value.X) && value.X > .01f ? value.X : fallback, float.IsFinite(value.Y) && value.Y > .01f ? value.Y : fallback);
    private readonly record struct MipLevel(int Size, Rid Set);
}
