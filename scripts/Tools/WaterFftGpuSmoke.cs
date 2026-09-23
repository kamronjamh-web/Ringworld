using System;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

/// <summary>Finite GPU contract probe for the FFT provider; it owns no scene water state.</summary>
public partial class WaterFftGpuSmoke : Node
{
    private const int FramesPerStage = 18;
    private const int TimeoutFrames = 900;
    private RingWaterFftProvider? _provider;
    private int _frame, _stageFrames, _generation;
    private long _updatesAtStage;
    private byte[]? _first, _temporal, _mipBase, _mip1, _mip3, _mipLast;
    private float _foamBeforeDecay;
    private bool _readbackPending, _freezeUpdates;
    private string _stage = "new";

    public bool Completed { get; private set; }
    public bool Passed { get; private set; }
    public string LastFailure { get; private set; } = string.Empty;

    public override void _Ready()
    {
        if (RenderingServer.GetRenderingDevice() is null) Fail("unsupported GPU renderer (no main RenderingDevice)");
        else CreateProvider(new RingWaterFftSettings(128, 1, 12017, new Vector2(12, 12), new Vector2(48, 48), 6, 20, 3, .5f), "initial");
    }

    public override void _Process(double delta)
    {
        if (Completed) return;
        try { ProcessCore(delta); }
        catch (Exception error) { Fail(error.Message); }
    }

    private void ProcessCore(double delta)
    {
        if (Completed) return;
        if (++_frame > TimeoutFrames) { Fail("timeout at " + _stage); return; }
        if (_provider is null) { Fail("provider unexpectedly unavailable"); return; }
        if (!_freezeUpdates) _provider.RequestUpdate(_frame / 60d, Mathf.Clamp((float)delta, .001f, .05f));
        if (!_provider.Ready) return;
        if (_readbackPending) return;
        if (!_freezeUpdates && (++_stageFrames < FramesPerStage || _provider.CompletedUpdates <= _updatesAtStage)) return;

        switch (_stage)
        {
            case "initial": Request("initial_readback", bytes => _first = bytes); break;
            case "initial_readback":
                if (_first is null) return;
                ValidatePixels(_first, 128, "initial");
                _freezeUpdates = true; BeginStage("mip_base");
                break;
            case "mip_base": Request("mip_base_readback", bytes => _mipBase = bytes, 0, 0); break;
            case "mip_base_readback": Request("mip1_readback", bytes => _mip1 = bytes, 0, 1); break;
            case "mip1_readback": Request("mip3_readback", bytes => _mip3 = bytes, 0, 3); break;
            case "mip3_readback": Request("mip_last_readback", bytes => _mipLast = bytes, 0, 7); break;
            case "mip_last_readback":
                if (_mipBase is null || _mip1 is null || _mip3 is null || _mipLast is null) return;
                ValidateMipChain(_mipBase, _mip1, _mip3, _mipLast, 128);
                _freezeUpdates = false; BeginStage("temporal");
                break;
            case "temporal": Request("temporal_readback", bytes => _temporal = bytes); break;
            case "temporal_readback":
                if (_temporal is null) return;
                ValidatePixels(_temporal, 128, "temporal");
                if (!HasTemporalChange(_first!, _temporal)) { Fail("consecutive updates returned identical normal/foam data"); return; }
                Reconfigure(new RingWaterFftSettings(128, 1, 12017, new Vector2(12, 12), new Vector2(48, 48), 8, 45, 6, 2), "foam_growth");
                break;
            case "foam_growth":
                Request("foam_growth_readback", bytes =>
                {
                    ValidatePixels(bytes, 128, "foam_growth");
                    _foamBeforeDecay = SumFoam(bytes);
                    if (_foamBeforeDecay <= .0001f) throw new InvalidOperationException("high-whitecap foam never accumulated");
                });
                break;
            case "foam_growth_readback":
                // FoamAmount=0 disables growth while retaining the image, so the
                // upstream unpack pass can prove its persistent exponential decay.
                Reconfigure(new RingWaterFftSettings(128, 1, 12017, new Vector2(12, 12), new Vector2(48, 48), 8, 45, 0, 2), "foam_decay");
                break;
            case "foam_decay":
                Request("foam_decay_readback", bytes =>
                {
                    ValidatePixels(bytes, 128, "foam_decay");
                    float decayed = SumFoam(bytes);
                    if (decayed <= .0001f || decayed >= _foamBeforeDecay)
                        throw new InvalidOperationException($"foam did not persist and decay: before={_foamBeforeDecay:0.0000}, after={decayed:0.0000}");
                });
                break;
            case "foam_decay_readback":
                Reconfigure(new RingWaterFftSettings(256, 2, 12017, new Vector2(12, 12), new Vector2(48, 48), 8, 45, 3, .5f), "rebuild_256x2");
                break;
            case "rebuild_256x2":
                if (_provider.Generation <= _generation) return;
                Request("rebuild_256x2_readback", bytes => ValidatePixels(bytes, 256, "rebuild_256x2"));
                break;
            case "rebuild_256x2_readback":
                Request("second_layer_readback", bytes => ValidatePixels(bytes, 256, "second_layer"), 1);
                break;
            case "second_layer_readback": Reconfigure(new RingWaterFftSettings(512, 4, 12017, new Vector2(12, 12), new Vector2(48, 48), 8, 45, 3, .5f, new Vector2(192, 192), new Vector2(768, 768)), "rebuild_512x4"); break;
            case "rebuild_512x4": Request("layer3_512_readback", bytes => ValidatePixels(bytes, 512, "layer3_512"), 3); break;
            case "layer3_512_readback": Reconfigure(new RingWaterFftSettings(1024, 4, 12017, new Vector2(12, 12), new Vector2(48, 48), 8, 45, 3, .5f, new Vector2(192, 192), new Vector2(768, 768)), "rebuild_1024x4"); break;
            case "rebuild_1024x4": Request("layer3_1024_readback", bytes => ValidatePixels(bytes, 1024, "layer3_1024"), 3); break;
            case "layer3_1024_readback": Reconfigure(new RingWaterFftSettings(128, 1, 12017, new Vector2(12, 12), new Vector2(48, 48), 8, 45, 3, .5f), "rebuild_128x1"); break;
            case "rebuild_128x1":
                if (_provider.Generation <= _generation) return;
                _provider.Dispose(); _provider = null;
                CreateProvider(new RingWaterFftSettings(128, 1, 12017, new Vector2(12, 12), new Vector2(48, 48), 6, 20, 3, .5f), "recreated");
                break;
            case "recreated":
                if (_provider.Ready && _provider.NormalFoamTexture is not null)
                {
                    Passed = true; Completed = true;
                    GD.Print($"WATER_FFT_GPU_SMOKE=PASS seed=12017 resolution=128 cascades=1 generation={_provider.Generation} updates={_provider.CompletedUpdates} bytes={_provider.EstimatedBytes} checks=finite,nonconstant,foam_range,mip_mean,mip_energy,mip_foam,mip_persistence,foam_persistence,foam_decay,temporal,rebuild_256x2,rebuild_512x4_layer3,rebuild_1024x4_layer3,recreate");
                    _provider.Dispose(); _provider = null;
                }
                break;
        }
    }

    private void CreateProvider(RingWaterFftSettings settings, string stage)
    {
        _provider = new RingWaterFftProvider(); _provider.Configure(settings);
        _generation = _provider.Generation; BeginStage(stage);
    }
    private void Reconfigure(RingWaterFftSettings settings, string stage)
    {
        if (_provider is null) throw new InvalidOperationException("provider missing during reconfigure");
        _generation = _provider.Generation; _provider.Configure(settings); BeginStage(stage);
    }
    private void BeginStage(string stage) { _stage = stage; _stageFrames = 0; _updatesAtStage = _provider?.CompletedUpdates ?? 0; _readbackPending = false; }
    private void Request(string waitingStage, Action<byte[]> callback, int layer = 0, int mipLevel = 0)
    {
        if (_readbackPending || _provider is null || _provider.NormalFoamTexture is null) return;
        _readbackPending = true;
        _stage = waitingStage;
        _provider.RequestReadbackForTests(bytes => { try { callback(bytes); } catch (Exception error) { Fail(error.Message); } finally { _readbackPending = false; } }, layer, mipLevel);
    }
    private static void ValidatePixels(byte[] data, int resolution, string stage, bool requireNonconstant = true)
    {
        int expected = checked(resolution * resolution * 8);
        if (data.Length != expected) throw new InvalidOperationException($"{stage} readback size {data.Length}, expected {expected} RGBA16F bytes");
        float minimum = float.PositiveInfinity, maximum = float.NegativeInfinity;
        for (int offset = 0; offset < data.Length; offset += 2)
        {
            float value = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(data, offset));
            if (!float.IsFinite(value)) throw new InvalidOperationException($"{stage} contains non-finite half-float at byte {offset}");
            if (offset % 8 == 0)
            {
                minimum = MathF.Min(minimum, value);
                maximum = MathF.Max(maximum, value);
            }
        }
        if (requireNonconstant && maximum - minimum < .00001f) throw new InvalidOperationException($"{stage} normal/foam texture is constant");
        for (int offset = 6; offset < data.Length; offset += 8)
        {
            float foam = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(data, offset));
            if (foam < -.0001f || foam > 1.0001f) throw new InvalidOperationException($"{stage} foam is outside [0,1]: {foam}");
        }
    }
    private static void ValidateMipChain(byte[] baseMip, byte[] mip1, byte[] mip3, byte[] lastMip, int resolution)
    {
        ValidatePixels(baseMip, resolution, "mip_base");
        ValidatePixels(mip1, resolution >> 1, "mip1");
        ValidatePixels(mip3, resolution >> 3, "mip3");
        ValidatePixels(lastMip, 1, "mip_last", false);
        MipStats reference = MipStats.Read(baseMip);
        foreach ((string name, byte[] data) in new[] { ("mip1", mip1), ("mip3", mip3), ("mip_last", lastMip) })
        {
            MipStats current = MipStats.Read(data);
            AssertMean(name, "gradient_x", reference.GradientX, current.GradientX);
            AssertMean(name, "gradient_y", reference.GradientY, current.GradientY);
            AssertMean(name, "slope_energy", reference.SlopeEnergy, current.SlopeEnergy);
            AssertMean(name, "foam", reference.Foam, current.Foam);
            if (current.SlopeEnergy + .015f < current.GradientX * current.GradientX + current.GradientY * current.GradientY)
                throw new InvalidOperationException($"{name} slope energy is below filtered gradient energy");
        }
    }
    private static void AssertMean(string mip, string channel, float expected, float actual)
    {
        float tolerance = .004f + MathF.Abs(expected) * .035f;
        if (MathF.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException($"{mip} {channel} mean drifted: base={expected:0.000000}, mip={actual:0.000000}");
    }
    private readonly record struct MipStats(float GradientX, float GradientY, float SlopeEnergy, float Foam)
    {
        public static MipStats Read(byte[] data)
        {
            double x = 0, y = 0, energy = 0, foam = 0;
            int count = data.Length / 8;
            for (int offset = 0; offset < data.Length; offset += 8)
            {
                x += (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(data, offset));
                y += (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(data, offset + 2));
                energy += (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(data, offset + 4));
                foam += (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(data, offset + 6));
            }
            return new MipStats((float)(x / count), (float)(y / count), (float)(energy / count), (float)(foam / count));
        }
    }
    private static bool HasTemporalChange(byte[] first, byte[] second)
    {
        if (first.Length != second.Length) return true;
        for (int i = 0; i < first.Length; i += 32) if (first[i] != second[i] || first[i + 1] != second[i + 1]) return true;
        return false;
    }
    private static float SumFoam(byte[] data)
    {
        double sum = 0;
        for (int offset = 6; offset < data.Length; offset += 8)
            sum += (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(data, offset));
        return (float)sum;
    }
    private void Fail(string reason)
    {
        if (Completed) return;
        LastFailure = reason; Completed = true; Passed = false;
        GD.PushError("WATER_FFT_GPU_SMOKE=FAIL " + reason);
        _freezeUpdates = false; _provider?.Dispose(); _provider = null;
    }
    public override void _ExitTree() { _freezeUpdates = false; _provider?.Dispose(); _provider = null; }
}
