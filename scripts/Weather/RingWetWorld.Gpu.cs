using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Weather;

/// <summary>Render-thread glue of <see cref="RingWetWorld"/>: the wet map compute dispatch (scripts/render/ring_wet_map.glsl) and its two ping-pong RGBA16F textures.</summary>
public partial class RingWetWorld
{
    private const string ShaderPath = "res://scripts/render/ring_wet_map.glsl";
    private const int ParamsBytes = 208;
    private const int SystemsBytes = RainHistorySnapshot.MaxLags * RainHistorySnapshot.MaxSystemsPerLag * 32;

    private sealed class GpuLink
    {
        public RenderingDevice? Rd;
        public Rid Shader, Pipeline, NearestSampler, LinearSampler, ParamsBuffer, SystemsBuffer, DummyStorm;
        public readonly Rid[] Textures = new Rid[2];
        public readonly Dictionary<(int Ping, Rid Storm), Rid> Sets = new();
        public int Ping;
        public volatile bool Ready, Failed, Released, MapPublished;
        public string Error = "";
        public byte[]? Readback;
        public bool ReadbackRequested;
    }

    private GpuLink? _link;
    private Texture2Drd? _mapTexture;
    /// <summary>Raw bytes (RGBA16F, 256 x 256) of the newest dispatch when <see cref="RequestReadback"/> was set; tests only.</summary>
    public byte[]? ReadbackBytes => _link?.Readback;
    public bool GpuReady => _link is { Ready: true };
    public bool GpuFailed => _link is { Failed: true };
    public string GpuError => _link?.Error ?? "";
    public bool MapPublished => _link is { MapPublished: true };

    /// <summary>Asks the next dispatch to read the map back (a render-thread stall; tests and diagnostics only).</summary>
    public void RequestReadback() { if (_link != null) _link.ReadbackRequested = true; _readbackWanted = true; _lastDispatchSeconds = double.NegativeInfinity; }
    private bool _readbackWanted;

    /// <summary>Decoded RGBA floats of the last readback (index = (y * 256 + x) * 4), or null.</summary>
    public float[]? ReadbackFloats()
    {
        byte[]? bytes = ReadbackBytes;
        if (bytes == null || bytes.Length < WetMapFrame.Size * WetMapFrame.Size * 8) return null;
        var result = new float[WetMapFrame.Size * WetMapFrame.Size * 4];
        for (int i = 0; i < result.Length; i++) result[i] = (float)BitConverter.ToHalf(bytes, i * 2);
        return result;
    }

    private bool EnsureGpu()
    {
        if (_link is { Ready: true }) return true;
        if (_link is { Failed: true }) return false;
        if (_link != null) return false;
        if (RenderingServer.GetRenderingDevice() == null) return false;
        _mapTexture ??= new Texture2Drd();
        RenderingServer.GlobalShaderParameterSet(GlobalMap, _mapTexture);
        var link = new GpuLink();
        _link = link;
        RenderingServer.CallOnRenderThread(Callable.From(() => InitOnRenderThread(link)));
        return false;
    }

    private static void InitOnRenderThread(GpuLink link)
    {
        if (link.Released) return;
        try
        {
            RenderingDevice? rd = RenderingServer.GetRenderingDevice();
            if (rd == null) { link.Error = "no RenderingDevice"; link.Failed = true; return; }
            RDShaderFile? source = GD.Load<RDShaderFile>(ShaderPath);
            if (source == null) { link.Error = "wet map shader missing"; link.Failed = true; return; }
            link.Shader = rd.ShaderCreateFromSpirV(source.GetSpirV());
            if (!link.Shader.IsValid) { link.Error = "wet map shader compile failed: " + source.BaseError; link.Failed = true; return; }
            link.Pipeline = rd.ComputePipelineCreate(link.Shader);
            link.NearestSampler = rd.SamplerCreate(new RDSamplerState { MinFilter = RenderingDevice.SamplerFilter.Nearest, MagFilter = RenderingDevice.SamplerFilter.Nearest });
            link.LinearSampler = rd.SamplerCreate(new RDSamplerState { MinFilter = RenderingDevice.SamplerFilter.Linear, MagFilter = RenderingDevice.SamplerFilter.Linear });
            link.ParamsBuffer = rd.UniformBufferCreate(ParamsBytes);
            link.SystemsBuffer = rd.StorageBufferCreate(SystemsBytes);
            var format = new RDTextureFormat
            {
                Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat, Width = WetMapFrame.Size, Height = WetMapFrame.Size, Depth = 1, ArrayLayers = 1, Mipmaps = 1,
                TextureType = RenderingDevice.TextureType.Type2D,
                UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanCopyFromBit | RenderingDevice.TextureUsageBits.CanUpdateBit,
            };
            var zero = new Godot.Collections.Array<byte[]> { new byte[WetMapFrame.Size * WetMapFrame.Size * 8] };
            for (int i = 0; i < 2; i++) link.Textures[i] = rd.TextureCreate(format, new RDTextureView(), zero);
            var dummy = new RDTextureFormat
            {
                Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat, Width = 1, Height = 1, Depth = 1, ArrayLayers = 2, Mipmaps = 1,
                TextureType = RenderingDevice.TextureType.Type2DArray, UsageBits = RenderingDevice.TextureUsageBits.SamplingBit,
            };
            link.DummyStorm = rd.TextureCreate(dummy, new RDTextureView(), new Godot.Collections.Array<byte[]> { new byte[8], new byte[8] });
            link.Rd = rd;
            link.Ready = true;
        }
        catch (Exception error) { link.Error = error.Message; link.Failed = true; }
    }

    private void ReleaseGpu()
    {
        GpuLink? link = _link;
        _link = null;
        Texture2Drd? texture = _mapTexture;
        _mapTexture = null;
        HasPublishedFrame = false;
        if (link == null) return;
        link.Released = true;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            if (texture != null && IsInstanceValid(texture)) texture.TextureRdRid = default;
            RenderingDevice? rd = link.Rd;
            if (rd == null) return;
            foreach (Rid set in link.Sets.Values) if (set.IsValid && rd.UniformSetIsValid(set)) rd.FreeRid(set);
            foreach (Rid rid in new[] { link.Pipeline, link.Shader, link.NearestSampler, link.LinearSampler, link.ParamsBuffer, link.SystemsBuffer, link.Textures[0], link.Textures[1], link.DummyStorm })
                if (rid.IsValid) rd.FreeRid(rid);
        }));
    }

    private bool DispatchMap(in WetMapFrame frame, RingCanonicalPosition camera, double simTime, double now, bool moved, bool timeJump, bool wetPossible)
    {
        GpuLink? link = _link;
        if (link is not { Ready: true } || _grid == null) return false;
        RingWetWorldSettings settings = RingWetWorldSettings.Current;
        double circumference = frame.CircumferenceMeters;

        // History snapshot: the test override, else S3's observer snapshot (offsets are measured from the observer, which is the camera).
        RainHistorySnapshot? snapshot = _snapshotOverride;
        RingCanonicalPosition observer = _snapshotOverride != null ? _snapshotOverrideObserver : camera;
        if (snapshot == null && _weather != null && _weather.TryGetRainHistorySnapshot(out RainHistorySnapshot live)) snapshot = live;
        var systems = new byte[SystemsBytes];
        var counts = new int[12];
        var lags = new float[12];
        if (snapshot != null)
        {
            int lagCount = Math.Min(snapshot.LagCount, RainHistorySnapshot.MaxLags);
            for (int l = 0; l < lagCount; l++)
            {
                lags[l] = snapshot.LagSeconds[l];
                int count = Math.Min(snapshot.Counts[l], RainHistorySnapshot.MaxSystemsPerLag);
                counts[l] = count;
                for (int i = 0; i < count; i++)
                {
                    StormSystemGeometry g = snapshot.Systems[l * RainHistorySnapshot.MaxSystemsPerLag + i];
                    int o = (l * RainHistorySnapshot.MaxSystemsPerLag + i) * 32;
                    WriteFloats(systems, o, g.OffsetMeters.X, g.OffsetMeters.Y, g.MajorRadiusMeters, g.MinorRadiusMeters, g.OrientationCos, g.OrientationSin, g.Density, g.PrecipitationPotential);
                }
            }
        }

        (double centreAround, double centreAcross) = frame.TexelCentre(0, 0);
        float deltaAround = (float)WetMapFrame.AroundDelta(centreAround, observer.AroundMeters, circumference);
        float deltaAcross = (float)(centreAcross - observer.AcrossMeters);
        bool stormMap = false;
        Rid stormRid = default;
        Vector2 stormOriginMeters = Vector2.Zero; float stormTexel = 1000.0f, heavyStart = 0.6f; Vector2I used = Vector2I.One, capacity = new(1024, 512); int layer = 0; double stormCirc = circumference;
        if (!_forced && SunOverride < 0.0f && _bridge != null && _bridge.TryGetStormMapDescription(out stormRid, out stormOriginMeters, out stormTexel, out used, out capacity, out layer, out stormCirc, out heavyStart)) stormMap = true;
        float stormAround = 0.0f, stormAcross = 0.0f;
        if (stormMap)
        {
            stormAround = (float)WetMapFrame.Wrap(centreAround - stormOriginMeters.X, stormCirc > 1.0 ? stormCirc : circumference);
            stormAcross = (float)(centreAcross - stormOriginMeters.Y);
        }

        double dt = double.IsNegativeInfinity(_lastDispatchSeconds) ? 10.0 : Math.Clamp(now - _lastDispatchSeconds, 0.0, 10.0);
        bool prevValid = HasPublishedFrame && !timeJump && !_forced && !_forceWasOn
            && Math.Abs(WetMapFrame.AroundDelta(camera.AroundMeters, _lastDispatchCamera.AroundMeters, circumference)) < 2000.0 && Math.Abs(camera.AcrossMeters - _lastDispatchCamera.AcrossMeters) < 2000.0;
        float alpha = prevValid ? 1.0f - MathF.Exp(-(float)dt / 0.5f) : 1.0f;
        Vector2 shift = Vector2.Zero;
        if (prevValid)
            shift = new Vector2((float)Math.Round(WetMapFrame.AroundDelta(frame.CornerAround, PublishedFrame.CornerAround, circumference) / WetMapFrame.TexelMeters),
                (float)Math.Round((frame.CornerAcross - PublishedFrame.CornerAcross) / WetMapFrame.TexelMeters));
        bool snowing = _effects?.Snowing ?? false;
        float coverage = _weather?.Coverage ?? 0.46f;

        var parameters = new byte[ParamsBytes];
        WriteFloats(parameters, 0, deltaAround, deltaAcross, stormAround, stormAcross);
        WriteFloats(parameters, 16, stormTexel, layer, used.X, used.Y);
        WriteFloats(parameters, 32, capacity.X, capacity.Y, heavyStart, stormMap ? 1.0f : 0.0f);
        WriteFloats(parameters, 48, RingVolumetricCloudLayer.StormArrivalPrecipitationStart, coverage, alpha, snowing ? 1.0f : 0.0f);
        WriteFloats(parameters, 64, SunOverride >= 0.0f ? SunOverride : _cameraDay, _windMetersPerSecond, settings.DryingSpeed, _forced ? 1.0f : 0.0f);
        WriteFloats(parameters, 80, _forceValue.X, _forceValue.Y, _forceValue.Z, _forceValue.W);
        WriteFloats(parameters, 96, shift.X, shift.Y, prevValid ? 1.0f : 0.0f, 0.0f);
        for (int i = 0; i < 12; i++) BitConverter.TryWriteBytes(parameters.AsSpan(112 + i * 4), counts[i]);
        WriteFloats(parameters, 160, lags);

        bool readback = _readbackWanted;
        _readbackWanted = false;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            try
            {
                if (link.Released || link.Rd is not { } rd) return;
                rd.BufferUpdate(link.ParamsBuffer, 0, (uint)parameters.Length, parameters);
                rd.BufferUpdate(link.SystemsBuffer, 0, (uint)systems.Length, systems);
                Rid storm = stormMap && stormRid.IsValid ? stormRid : link.DummyStorm;
                int ping = link.Ping;
                if (!link.Sets.TryGetValue((ping, storm), out Rid set) || !rd.UniformSetIsValid(set))
                {
                    if (link.Sets.Count > 8) { foreach (Rid old in link.Sets.Values) if (old.IsValid && rd.UniformSetIsValid(old)) rd.FreeRid(old); link.Sets.Clear(); }
                    RDUniform U(RenderingDevice.UniformType type, int binding, params Rid[] ids) { var u = new RDUniform { UniformType = type, Binding = binding }; foreach (Rid id in ids) u.AddId(id); return u; }
                    set = rd.UniformSetCreate(new Godot.Collections.Array<RDUniform>
                    {
                        U(RenderingDevice.UniformType.Image, 0, link.Textures[ping]),
                        U(RenderingDevice.UniformType.SamplerWithTexture, 1, link.NearestSampler, link.Textures[1 - ping]),
                        U(RenderingDevice.UniformType.SamplerWithTexture, 2, link.LinearSampler, storm),
                        U(RenderingDevice.UniformType.UniformBuffer, 3, link.ParamsBuffer),
                        U(RenderingDevice.UniformType.StorageBuffer, 4, link.SystemsBuffer),
                    }, link.Shader, 0);
                    link.Sets[(ping, storm)] = set;
                }
                long list = rd.ComputeListBegin();
                rd.ComputeListBindComputePipeline(list, link.Pipeline);
                rd.ComputeListBindUniformSet(list, set, 0);
                rd.ComputeListDispatch(list, WetMapFrame.Size / 8, WetMapFrame.Size / 8, 1);
                rd.ComputeListEnd();
                if (_mapTexture != null && IsInstanceValid(_mapTexture)) _mapTexture.TextureRdRid = link.Textures[ping];
                if (readback || link.ReadbackRequested) { link.Readback = rd.TextureGetData(link.Textures[ping], 0); link.ReadbackRequested = false; }
                link.Ping = 1 - ping;
                link.MapPublished = true;
            }
            catch (Exception error) { link.Error = "dispatch failed: " + error.Message; link.Failed = true; }
        }));

        _forceWasOn = _forced;
        PublishedFrame = frame; HasPublishedFrame = true;
        _lastDispatchCamera = camera; _lastDispatchSeconds = now; _lastDispatchSimTime = simTime;
        DispatchCount++;
        return true;
    }

    private bool _forceWasOn;

    private static void WriteFloats(byte[] buffer, int offset, params float[] values)
    {
        for (int i = 0; i < values.Length; i++) BitConverter.TryWriteBytes(buffer.AsSpan(offset + i * 4), values[i]);
    }
}
