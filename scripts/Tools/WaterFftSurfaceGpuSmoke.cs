using Godot;
using System;
using System.Collections.Generic;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Small deterministic checks shared by the surface GPU smoke runner.  Keeping
/// interpolation here lets the runner compare a frozen CPU mip payload with the
/// 3x3 GPU query without reading a live full-resolution map during gameplay.
/// </summary>
public partial class WaterFftSurfaceGpuSmoke : Node
{
    private RingworldTechDemo.Voxel.LOD.RingWaterFftProvider? _provider;
    private ImageTexture? _shorelineFixture;
    private ShaderMaterial? _rebuildMaterial;
    private SubViewport? _rebuildViewport;
    private int _frame, _frozenGeneration, _rebuildGeneration, _boundMaterialFrames; private bool _pending, _frozen, _queried, _staleConfigureCallback, _disposeCallback;
    private string _lifecycleStage = "surface";
    private byte[]? _mip0, _layer1Mip0, _mip1, _mip2, _layer2Mip1, _layer2Mip2, _layer3Mip1, _layer3Mip2;
    private readonly float[][] _gpuLayerHeights = new float[4][];
    public bool Completed { get; private set; }
    public bool Passed { get; private set; }
    public override void _Ready()
    {
        _provider = new();
        var initial = new RingworldTechDemo.Voxel.LOD.RingWaterFftSettings(128, 4, 12017, new Vector2(12, 12), new Vector2(79, 79), 6, 20, 3, .5f, new Vector2(316, 316), new Vector2(1264, 1264));
        _provider.Configure(initial);
        // An unchanged configuration arriving before the initial render-thread
        // acknowledgement must not stall first resource creation.
        _provider.Configure(initial);
    }
    public override void _Process(double delta)
    {
        if (Completed || _provider is null) return;
        if (++_frame > 900) { Fail("timeout"); return; }
        if (_lifecycleStage != "surface") { ProcessLifecycle(); return; }
        if (!_frozen) _provider.RequestUpdate(_frame / 60d, .016f);
        if (!_provider.Ready || _pending) return;
        if (!_frozen)
        {
            _frozen = true; _frozenGeneration = _provider.Generation;
            if (_provider.DisplacementTexture is null) { Fail("ready provider did not publish displacement texture"); return; }
            Read(0, 0, b => _mip0 = b); return;
        }
        if (_provider.Generation != _frozenGeneration || !_provider.Ready || _provider.DisplacementTexture is null) { Fail("provider changed while surface snapshot was frozen"); return; }
        if (_mip0 is null) return;
        if (_layer1Mip0 is null) { Read(1, 0, b => _layer1Mip0 = b); return; }
        if (_mip1 is null) { Read(1, 1, b => _mip1 = b); return; }
        if (_mip2 is null) { Read(1, 2, b => _mip2 = b); return; }
        if (_layer2Mip1 is null) { Read(2, 1, b => _layer2Mip1 = b); return; }
        if (_layer2Mip2 is null) { Read(2, 2, b => _layer2Mip2 = b); return; }
        if (_layer3Mip1 is null) { Read(3, 1, b => _layer3Mip1 = b); return; }
        if (_layer3Mip2 is null) { Read(3, 2, b => _layer3Mip2 = b); return; }
        if (!_queried) { _queried = true; QueryFrozen(); return; }
    }
    private void Read(int layer, int mip, Action<byte[]> done) { _pending = true; _provider!.RequestDisplacementReadbackForTests(b => { _pending = false; done(b); }, layer, mip); }
    private void QueryFrozen()
    {
        if (!FiniteHalf(_mip0!) || !FiniteHalf(_mip1!) || !FiniteHalf(_mip2!)) { Fail("non-finite displacement mip"); return; }
        DisplacementStatistics stats = MeasureDisplacement(_mip0!), largeStats = MeasureDisplacement(_layer1Mip0!);
        if (!HasUsableHeightVariation(stats, 128 * 128) || !HasUsableHeightVariation(largeStats, 128 * 128))
        {
            Fail($"displacement lacks usable height variation: layer0={stats.Rms:0.000000} layer1={largeStats.Rms:0.000000}");
            return;
        }
        _pending = true;
        // This mirrors the compute shader: linear/trilinear sampling followed
        // by eight horizontal inversion iterations against the frozen texture.
        const float lod = 1.5f, chop = .75f;
        Vector2 queryUv = new(.375f, .625f), period = new(79, 79);
        _provider!.RequestSurfaceOffsets(queryUv, 1, lod, period, 0, chop, zero =>
        {
            if (zero is null || !Finite(zero.Value.Heights) || !AllNearZero(zero.Value.Heights, .00001f))
            {
                _pending = false; Fail("zero-height surface query returned displacement"); return;
            }
            _provider.RequestSurfaceOffsets(queryUv, 1, lod, period, 1, 0, direct =>
            {
                if (direct is null || !Finite(direct.Value.Heights) || !MatchesFrozenInversion(direct.Value.Heights, _mip1!, _mip2!, 64, 32, queryUv, period, lod, 0)) { _pending = false; Fail("zero-chop query disagrees with frozen mip"); return; }
                _provider.RequestSurfaceOffsets(queryUv, 1, lod, period, 1, chop, choppy =>
                {
                    if (choppy is null || !Finite(choppy.Value.Heights) || !Finite(choppy.Value.ResidualMeters) || !MatchesFrozenInversion(choppy.Value.Heights, _mip1!, _mip2!, 64, 32, queryUv, period, lod, chop) || !AnyDifferent(direct.Value.Heights, choppy.Value.Heights, .00001f) || Max(choppy.Value.ResidualMeters) > .03f) { _pending = false; Fail("invalid choppy surface query"); return; }
                    // The production overload includes the exact near-water
                    // geometry fade. Check a partial fade against the frozen
                    // CPU reference, then prove its fully faded-out result is
                    // identically zero before continuing lifecycle checks.
                    _provider.RequestSurfaceOffsets(queryUv, 1, lod, period, 1, chop, 10, 8, partial =>
                    {
                        if (partial is null || !Finite(partial.Value.Heights) || !Finite(partial.Value.ResidualMeters) || Max(partial.Value.ResidualMeters) > .03f || !MatchesFrozenInversion(partial.Value.Heights, _mip1!, _mip2!, 64, 32, queryUv, period, lod, chop, 10, 8) || !AnyDifferent(choppy.Value.Heights, partial.Value.Heights, .00001f)) { _pending = false; Fail("partial geometry fade query disagrees with frozen mip"); return; }
                        _provider.RequestSurfaceOffsets(queryUv, 1, lod, period, 1, chop, 10, 11, off =>
                        {
                            if (off is null || !Finite(off.Value.Heights) || !AllNearZero(off.Value.Heights, .00001f)) { _pending = false; Fail("fully faded geometry query was not zero"); return; }
                            _provider.RequestSurfaceOffsets(new(1.375f, .625f), 1, lod, period, 1, chop, b =>
                            {
                                _pending = false;
                                if (b is null || !Finite(b.Value.Heights) || MathF.Abs(choppy.Value.Heights[4] - b.Value.Heights[4]) > .01f) { Fail("periodic query mismatch"); return; }
                                QueryFrozenFourCascade(stats, largeStats, choppy.Value);
                            });
                        });
                    });
                });
            });
        });
    }
    private void QueryFrozenFourCascade(DisplacementStatistics small, DisplacementStatistics large, RingworldTechDemo.Voxel.LOD.RingWaterFftSurfaceQueryResult choppy)
    {
        Vector2[] periods = { new(12, 12), new(79, 79), new(316, 316), new(1264, 1264) };
        Vector2[] uvs = { new(.375f, .625f), new(.375f, .625f), new(.375f, .625f), new(.375f, .625f) };
        float[] lods = { 1.5f, 1.5f, 1.5f, 1.5f };
        byte[][] floors = { _mip1!, _mip1!, _mip1!, _mip1! }; byte[][] ceils = { _mip2!, _mip2!, _mip2!, _mip2! };
        floors[2] = _layer2Mip1!; ceils[2] = _layer2Mip2!; floors[3] = _layer3Mip1!; ceils[3] = _layer3Mip2!;
        QueryFrozenMultiStage(1, 0, "layer1_zero_chop", floors, ceils, uvs, periods, lods, 2, () =>
            QueryFrozenMultiStage(1, 0, "layer2_zero_chop", floors, ceils, uvs, periods, lods, 4, () =>
                QueryFrozenMultiStage(1, 0, "layer3_zero_chop", floors, ceils, uvs, periods, lods, 8, () =>
                    QueryFrozenMultiStage(1, 0, "sum_zero_chop", floors, ceils, uvs, periods, lods, 14, () => QueryFrozenFourCascadeChoppy(small, large, choppy, floors, ceils, uvs, periods, lods)))));
    }
    private void QueryFrozenMultiStage(float height, float chop, string stage, byte[][] floors, byte[][] ceils, Vector2[] uvs, Vector2[] periods, float[] lods, int mask, Action next)
    {
        _pending = true;
        if (!_provider!.RequestSurfaceOffsets(uvs[1], 1, 1.5f, periods[1], height, chop, 0, 0, result =>
        {
            _pending = false;
            if (result is null) { Fail($"{stage} null"); return; }
            if (!Finite(result.Value.Heights) || !Finite(result.Value.ResidualMeters)) { Fail($"{stage} non-finite query"); return; }
            if (mask is 2 or 4 or 8) _gpuLayerHeights[(int)MathF.Log2(mask)] = result.Value.Heights;
            if (mask == 14)
            {
                if (_gpuLayerHeights[1] is null || _gpuLayerHeights[2] is null || _gpuLayerHeights[3] is null) { Fail("missing one-bit GPU layer query"); return; }
                float sumError = 0; for (int i = 0; i < 9; ++i) sumError = MathF.Max(sumError, MathF.Abs(result.Value.Heights[i] - (_gpuLayerHeights[1][i] + _gpuLayerHeights[2][i] + _gpuLayerHeights[3][i])));
                if (sumError > .0002f) { Fail($"summed GPU query omitted or changed a layer: max_error={sumError:0.000000}"); return; }
            }
            var expected = new List<float>(9);
            bool parity = MatchesFrozenMultiCascadeInversion(result.Value.Heights, floors, ceils, new[] { 64, 64, 64, 64 }, new[] { 32, 32, 32, 32 }, uvs, periods, lods, mask, height, chop, expectedOut: expected);
            if (!parity || expected.Count != 9) { Fail($"{stage} invalid frozen reference or query outside 3cm budget"); return; }
            float error = 0; for (int i = 0; i < expected.Count; ++i) error = MathF.Max(error, MathF.Abs(expected[i] - result.Value.Heights[i]));
            GD.Print($"WATER_FFT_MULTI_QUERY_DIAG stage={stage} mask={mask} max_error={error:0.000000} expected={string.Join(',', expected)} actual={string.Join(',', result.Value.Heights)}");
            // Preserve the legacy one-layer (layer 1) .004m CPU comparison.
            // Wider-period layer filtering and the summed production query use
            // the established physical 3cm acceptance budget.
            float tolerance = mask == 2 ? .004f : .03f;
            if (error > tolerance) { Fail($"{stage} frozen-mip parity failed max_error={error:0.000000} tolerance={tolerance:0.000}"); return; }
            next();
        }, uvs, periods, lods, mask)) { _pending = false; Fail($"could not start {stage}"); }
    }
    private void QueryFrozenFourCascadeChoppy(DisplacementStatistics small, DisplacementStatistics large, RingworldTechDemo.Voxel.LOD.RingWaterFftSurfaceQueryResult choppy, byte[][] floors, byte[][] ceils, Vector2[] uvs, Vector2[] periods, float[] lods)
    {
        _pending = true;
        if (!_provider!.RequestSurfaceOffsets(uvs[1], 1, 1.5f, periods[1], 1, .75f, 0, 0, multi =>
        {
            _pending = false;
            if (multi is null) { Fail("four-cascade frozen query returned null"); return; }
            if (!Finite(multi.Value.Heights)) { Fail($"four-cascade frozen query returned non-finite heights={string.Join(',', multi.Value.Heights)}"); return; }
            if (!Finite(multi.Value.ResidualMeters)) { Fail($"four-cascade frozen query returned non-finite residuals={string.Join(',', multi.Value.ResidualMeters)}"); return; }
            float residual = Max(multi.Value.ResidualMeters);
            if (residual > .03f) { Fail($"four-cascade query residual {residual:0.000000} exceeds 0.03 heights={string.Join(',', multi.Value.Heights)}"); return; }
            var expected = new List<float>(9);
            bool parity = MatchesFrozenMultiCascadeInversion(multi.Value.Heights, floors, ceils, new[] { 64, 64, 64, 64 }, new[] { 32, 32, 32, 32 }, uvs, periods, lods, 14, 1, .75f, expectedOut: expected);
            if (!parity || expected.Count != 9) { Fail("four-cascade frozen reference invalid or query outside 3cm budget"); return; }
            float error = 0; for (int i = 0; i < expected.Count; i++) error = MathF.Max(error, MathF.Abs(expected[i] - multi.Value.Heights[i]));
            if (error > .03f) { Fail($"four-cascade frozen-mip CPU parity failed max_error={error:0.000000} expected={string.Join(',', expected)} actual={string.Join(',', multi.Value.Heights)} residual={residual:0.000000}"); return; }
            GD.Print($"WATER_FFT_MULTI_QUERY_PARITY max_error={error:0.000000}m budget=0.030000m residual={residual:0.000000}m");
            QueryShorelineFixture(small, large, multi.Value, uvs, periods, lods);
        }, uvs, periods, lods, 14)) { _pending = false; Fail("could not start four-cascade frozen query"); }
    }
    // Exercises the actual main-RD ImageTexture bridge.  A valid dry texel
    // pins the point-query surface, while a valid deep/offshore texel returns
    // the same value as the legacy (mode 0) request above.
    private void QueryShorelineFixture(DisplacementStatistics small, DisplacementStatistics large, RingworldTechDemo.Voxel.LOD.RingWaterFftSurfaceQueryResult legacy, Vector2[] uvs, Vector2[] periods, float[] lods)
    {
        SetShorelineFixture(0f, 0f, 1f);
        _pending = true;
        if (!_provider!.RequestSurfaceOffsets(uvs[1], 1, 1.5f, periods[1], 1, .75f, 0, 0, dry =>
        {
            _pending = false;
            if (dry is null || !AllNearZero(dry.Value.Heights, .0001f)) { Fail("shoreline GPU dry/contact fixture was not pinned"); return; }
            _pending = true;
            if (!_provider.RequestSurfaceOffsets(uvs[1], 1, 1.5f, periods[1], 1, 0f, 0, 0, legacyZero =>
            {
                _pending = false;
                if (legacyZero is null) { Fail("shoreline zero-chop baseline returned null"); return; }
                // The production curve is pinned through the 3m anchor and
                // reaches full amplitude at 100m. Midpoint depth is exactly
                // half-amplitude under smoothstep. Use a full shore-distance
                // factor so this fixture isolates the depth attenuation.
                SetShorelineFixture(51.5f, 20f, 1f);
                _pending = true;
                if (!_provider.RequestSurfaceOffsets(uvs[1], 1, 1.5f, periods[1], 1, 0f, 0, 0, shallow =>
                {
                    _pending = false;
                    if (shallow is null || !Scaled(legacyZero.Value.Heights, shallow.Value.Heights, .5f, .004f)) { Fail("shoreline zero-chop mid-depth attenuation was not half-height"); return; }
                    SetShorelineFixture(150f, 100f, 1f);
                    _pending = true;
                    if (!_provider.RequestSurfaceOffsets(uvs[1], 1, 1.5f, periods[1], 1, .75f, 0, 0, deep =>
                    {
                        _pending = false;
                        if (deep is null || !Finite(deep.Value.Heights) || !Near(legacy.Heights, deep.Value.Heights, .03f)) { Fail("shoreline GPU deep fixture changed legacy surface"); return; }
                        StartRebuildLifecycle(small, large, legacy);
                    }, uvs, periods, lods, 14, _shorelineFixture, new Vector2(.5f, .5f), 0f, 10f, 20f, 3f, 100f, 2)) { _pending = false; Fail("could not start shoreline deep query"); }
                }, uvs, periods, lods, 14, _shorelineFixture, new Vector2(.5f, .5f), 0f, 10f, 20f, 3f, 100f, 2)) { _pending = false; Fail("could not start shoreline shallow query"); }
            }, uvs, periods, lods, 14)) { _pending = false; Fail("could not start shoreline zero-chop baseline"); }
        }, uvs, periods, lods, 14, _shorelineFixture, new Vector2(.5f, .5f), 0f, 10f, 20f, 3f, 100f, 2)) { _pending = false; Fail("could not start shoreline dry query"); }
    }
    private void SetShorelineFixture(float depth, float distance, float valid)
    {
        byte[] bytes = new byte[8]; WriteHalf(bytes, 0, depth); WriteHalf(bytes, 2, distance); WriteHalf(bytes, 4, valid);
        Image image = Image.CreateFromData(1, 1, false, Image.Format.Rgbah, bytes);
        if (_shorelineFixture is null) _shorelineFixture = ImageTexture.CreateFromImage(image); else _shorelineFixture.Update(image);
    }
    private static void WriteHalf(byte[] bytes, int offset, float value) { ushort bits = BitConverter.HalfToUInt16Bits((Half)value); bytes[offset] = (byte)bits; bytes[offset + 1] = (byte)(bits >> 8); }
    private static bool Near(ReadOnlySpan<float> a, ReadOnlySpan<float> b, float tolerance) { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (!float.IsFinite(a[i]) || !float.IsFinite(b[i]) || MathF.Abs(a[i] - b[i]) > tolerance) return false; return true; }
    private static bool Scaled(ReadOnlySpan<float> baseline, ReadOnlySpan<float> actual, float scale, float tolerance) { if (baseline.Length != actual.Length) return false; for (int i = 0; i < baseline.Length; i++) if (!float.IsFinite(actual[i]) || MathF.Abs(actual[i] - baseline[i] * scale) > tolerance) return false; return true; }
    private void StartRebuildLifecycle(DisplacementStatistics small, DisplacementStatistics large, RingworldTechDemo.Voxel.LOD.RingWaterFftSurfaceQueryResult choppy)
    {
        if (_provider is null) { Fail("provider missing before stale-configure check"); return; }
        if (_provider.NormalFoamTexture is null || _provider.DisplacementTexture is null) { Fail("provider did not publish textures before material rebuild check"); return; }
        // Draw the production proxy shader with the live published wrappers for
        // two frames. The following stage must detach these exact bindings before
        // changing topology and retiring their RIDs.
        _rebuildMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://materials/ring_ocean_proxy.gdshader") };
        RingworldTechDemo.Voxel.LOD.RingWaterModel.ConfigureMaterial(_rebuildMaterial);
        _rebuildMaterial.SetShaderParameter("fft_ready", true);
        _rebuildMaterial.SetShaderParameter("fft_normal_foam", _provider.NormalFoamTexture);
        _rebuildMaterial.SetShaderParameter("fft_displacement", _provider.DisplacementTexture);
        _rebuildViewport = new SubViewport { Size = new Vector2I(32, 32), RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        var world = new Node3D();
        world.AddChild(new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(2, 2) }, MaterialOverride = _rebuildMaterial });
        world.AddChild(new Camera3D { Position = new Vector3(0, 0, 2), Current = true });
        _rebuildViewport.AddChild(world); AddChild(_rebuildViewport);
        _rebuildGeneration = _provider.Generation; _boundMaterialFrames = 0; _lifecycleStage = "draw_bound_material"; _frozen = false;
        GD.Print($"WATER_FFT_SURFACE_GPU_SMOKE=STATS layer0_rms={small.Rms:0.000000} layer1_79m_min={large.Min:0.000} max={large.Max:0.000} rms={large.Rms:0.000} residual={Max(choppy.ResidualMeters):0.000000}");
    }
    private void ProcessLifecycle()
    {
        if (_provider is null) return;
        _provider.RequestUpdate(_frame / 60d, .016f);
        if (_lifecycleStage == "draw_bound_material")
        {
            if (++_boundMaterialFrames < 2) return;
            if (_rebuildMaterial is null
                || _rebuildMaterial.GetShaderParameter("fft_normal_foam").AsGodotObject() is not Texture2DArrayRD normal || !normal.TextureRdRid.IsValid
                || _rebuildMaterial.GetShaderParameter("fft_displacement").AsGodotObject() is not Texture2DArrayRD displacement || !displacement.TextureRdRid.IsValid)
            { Fail("production material did not retain live FFT wrappers before rebuild"); return; }
            if (!_provider.RequestSurfaceOffsets(new(.1f, .2f), 1, 0, new(79, 79), 1, .5f, value => _staleConfigureCallback = value is null)) { Fail("could not start stale-configure query"); return; }
            _provider.Configure(new(256, 2, 12017, new Vector2(12, 12), new Vector2(79, 79), 6, 20, 3, .5f),
                RingworldTechDemo.Voxel.LOD.RingWaterModel.RetireFftTextureBindings);
            // A wind/spectrum-only update may arrive before the render-thread
            // retirement acknowledgement. It must not invalidate that ack.
            _provider.Configure(new(256, 2, 12018, new Vector2(12, 12), new Vector2(79, 79), 6, 20, 3, .5f));
            if (_rebuildMaterial.GetShaderParameter("fft_ready").AsBool()
                || _rebuildMaterial.GetShaderParameter("fft_normal_foam").AsGodotObject() is not null
                || _rebuildMaterial.GetShaderParameter("fft_displacement").AsGodotObject() is not null)
            { Fail("material FFT bindings were not retired before topology rebuild"); return; }
            _lifecycleStage = "rebuild_256";
            return;
        }
        if (_lifecycleStage == "rebuild_256")
        {
            if (!_staleConfigureCallback || !_provider.Ready || _provider.Generation <= _rebuildGeneration) return;
            _rebuildGeneration = _provider.Generation; _lifecycleStage = "rebuild_128";
            _provider.Configure(new(128, 2, 12017, new Vector2(12, 12), new Vector2(79, 79), 6, 20, 3, .5f)); return;
        }
        if (_lifecycleStage == "rebuild_128")
        {
            if (!_provider.Ready || _provider.Generation <= _rebuildGeneration) return;
            var dying = _provider; _provider = null; _lifecycleStage = "disposed";
            if (!dying.RequestSurfaceOffsets(new(.1f, .2f), 1, 0, new(79, 79), 1, .5f, value => { _disposeCallback = value is null; if (_disposeCallback) Pass(); })) { Fail("could not start dispose query"); return; }
            dying.Dispose(); return;
        }
    }
    private void Pass()
    {
        if (_lifecycleStage != "disposed" || !_disposeCallback) { Fail("dispose callback was not stale"); return; }
        Passed = Completed = true;
        GD.Print("WATER_FFT_SURFACE_GPU_SMOKE=PASS checks=four_cascade_frozen_mip_query_parity,layers_1_2_3,finite,zero_height,zero_chop_parity,choppy_variation,trilinear_inversion,partial_geometry_fade,fully_faded_zero,periodic,initial_unchanged_config,draw_bound_proxy_material,retire_fft_bindings,coalesced_spectrum_update,rebuild_256,rebuild_128,stale_configure,dispose");
    }
    private static bool FiniteHalf(byte[] bytes) { for (int i = 0; i + 1 < bytes.Length; i += 2) if (!float.IsFinite((float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bytes, i)))) return false; return bytes.Length > 0; }
    private static float Max(float[] values) { float m = 0; foreach (float v in values) m = MathF.Max(m, MathF.Abs(v)); return m; }
    public static bool AllNearZero(ReadOnlySpan<float> values, float tolerance) { foreach (float value in values) if (!float.IsFinite(value) || MathF.Abs(value) > tolerance) return false; return values.Length > 0; }
    public static bool AnyDifferent(ReadOnlySpan<float> left, ReadOnlySpan<float> right, float tolerance) { if (left.Length != right.Length) return false; for (int i = 0; i < left.Length; i++) if (MathF.Abs(left[i] - right[i]) > tolerance) return true; return false; }
    public static bool HasUsableHeightVariation(DisplacementStatistics stats, int samples) => stats.Samples == samples && float.IsFinite(stats.Min) && float.IsFinite(stats.Max) && float.IsFinite(stats.Rms) && stats.Max - stats.Min >= .0001f && stats.Rms >= .00001f;
    private void Fail(string message) { if (Completed) return; Completed = true; GD.PushError("WATER_FFT_SURFACE_GPU_SMOKE=FAIL " + message); _provider?.Dispose(); _provider = null; }
    public override void _ExitTree() { _provider?.Dispose(); _provider = null; }
    public readonly record struct DisplacementStatistics(float Min, float Max, float Rms, int Samples);
    /// <summary>Extracts Y from an RGBA16F displacement payload returned by the provider test hook.</summary>
    public static DisplacementStatistics MeasureDisplacement(byte[] payload)
    {
        if (payload is null || payload.Length < 8) return new(float.NaN, float.NaN, float.NaN, 0);
        float min = float.PositiveInfinity, max = float.NegativeInfinity; double squares = 0; int count = payload.Length / 8;
        for (int i = 0; i < count; i++) { float y = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(payload, i * 8 + 2)); if (!float.IsFinite(y)) return new(float.NaN, float.NaN, float.NaN, i); min = Math.Min(min, y); max = Math.Max(max, y); squares += y * y; }
        return new(min, max, (float)Math.Sqrt(squares / count), count);
    }
    /// <summary>CPU equivalent of fft_surface_query.glsl for a frozen two-mip payload.</summary>
    public static bool MatchesFrozenInversion(ReadOnlySpan<float> heights, byte[] mipFloor, byte[] mipCeil, int floorSize, int ceilSize, Vector2 uv, Vector2 period, float lod, float choppiness, float geometryFadeEnd = 0, float verticalToBody = 0)
    {
        if (heights.Length != 9 || !DecodeDisplacement(mipFloor, floorSize, out Vector3[] floor) || !DecodeDisplacement(mipCeil, ceilSize, out Vector3[] ceil)) return false;
        for (int y = 0; y < 3; y++)
        for (int x = 0; x < 3; x++)
        {
            int index = y * 3 + x;
            Vector2 horizontal = new Vector2(x, y) - Vector2.One;
            Vector2 source = horizontal;
            for (int iteration = 0; iteration < 8; iteration++)
            {
                Vector3 d = Trilinear(floor, floorSize, ceil, ceilSize, uv + source / period, lod);
                float fade = geometryFadeEnd <= 0 ? 1 : GeometryFade(MathF.Sqrt(source.LengthSquared() + verticalToBody * verticalToBody), geometryFadeEnd);
                source = horizontal - new Vector2(d.X, d.Z) * (choppiness * fade);
            }
            float finalFade = geometryFadeEnd <= 0 ? 1 : GeometryFade(MathF.Sqrt(source.LengthSquared() + verticalToBody * verticalToBody), geometryFadeEnd);
            float expected = Trilinear(floor, floorSize, ceil, ceilSize, uv + source / period, lod).Y * finalFade;
            if (!float.IsFinite(expected) || MathF.Abs(heights[index] - expected) > .004f) return false;
        }
        return true;
    }
    /// <summary>Frozen CPU reference for the packed four-cascade gameplay query.
    /// Multi-cascade geometry deliberately excludes layer zero when more than one
    /// layer is active, matching ring_fft_geometry_displacement.</summary>
    public static bool MatchesFrozenMultiCascadeInversion(ReadOnlySpan<float> heights, byte[][] floors, byte[][] ceils, int[] floorSizes, int[] ceilSizes, Vector2[] uvs, Vector2[] periods, float[] lods, int mask, float heightScale, float choppiness, float fadeEnd = 0, float verticalToBody = 0, List<float>? expectedOut = null)
    {
        if (heights.Length != 9 || floors.Length < 4 || ceils.Length < 4 || floorSizes.Length < 4 || ceilSizes.Length < 4 || uvs.Length < 4 || periods.Length < 4 || lods.Length < 4) return false;
        var floor = new Vector3[4][]; var ceil = new Vector3[4][];
        for (int layer = 0; layer < 4; layer++) if ((mask & (1 << layer)) != 0 && (!DecodeDisplacement(floors[layer], floorSizes[layer], out floor[layer]) || !DecodeDisplacement(ceils[layer], ceilSizes[layer], out ceil[layer]))) return false;
        bool matches = true;
        for (int y = 0; y < 3; y++) for (int x = 0; x < 3; x++)
        {
            Vector2 horizontal = new(x - 1, y - 1), source = horizontal;
            for (int iteration = 0; iteration < 8; iteration++) { Vector2 chop = Vector2.Zero; for (int layer = 0; layer < 4; layer++) if ((mask & (1 << layer)) != 0) { Vector3 d = Trilinear(floor[layer], floorSizes[layer], ceil[layer], ceilSizes[layer], uvs[layer] + source / periods[layer], lods[layer]); chop += new Vector2(d.X, d.Z); } float iterationFade = fadeEnd <= 0 ? 1 : GeometryFade(MathF.Sqrt(source.LengthSquared() + verticalToBody * verticalToBody), fadeEnd); source = horizontal - chop * (heightScale * choppiness * iterationFade); }
            float fade = fadeEnd <= 0 ? 1 : GeometryFade(MathF.Sqrt(source.LengthSquared() + verticalToBody * verticalToBody), fadeEnd);
            float expected = 0; for (int layer = 0; layer < 4; layer++) if ((mask & (1 << layer)) != 0) expected += Trilinear(floor[layer], floorSizes[layer], ceil[layer], ceilSizes[layer], uvs[layer] + source / periods[layer], lods[layer]).Y;
            expectedOut?.Add(expected * heightScale * fade);
            if (!float.IsFinite(expected) || MathF.Abs(heights[y * 3 + x] - expected * heightScale * fade) > .03f) matches = false;
        }
        return matches;
    }
    private static bool DecodeDisplacement(byte[] payload, int size, out Vector3[] values)
    {
        values = Array.Empty<Vector3>();
        if (payload.Length != size * size * 8) return false;
        values = new Vector3[size * size];
        for (int i = 0; i < values.Length; i++)
        {
            int offset = i * 8;
            values[i] = new((float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(payload, offset)), (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(payload, offset + 2)), (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(payload, offset + 4)));
            if (!float.IsFinite(values[i].X) || !float.IsFinite(values[i].Y) || !float.IsFinite(values[i].Z)) return false;
        }
        return true;
    }
    private static Vector3 Trilinear(ReadOnlySpan<Vector3> floor, int floorSize, ReadOnlySpan<Vector3> ceil, int ceilSize, Vector2 uv, float lod)
        => Bilinear(floor, floorSize, uv.X, uv.Y).Lerp(Bilinear(ceil, ceilSize, uv.X, uv.Y), Mathf.Clamp(lod - Mathf.Floor(lod), 0, 1));
    public static float Bilinear(ReadOnlySpan<float> heights, int size, float u, float v)
    {
        if (size < 1 || heights.Length < size * size) return float.NaN;
        float x = Mathf.PosMod(u, 1f) * size, y = Mathf.PosMod(v, 1f) * size;
        int x0 = Mathf.PosMod(Mathf.FloorToInt(x), size), y0 = Mathf.PosMod(Mathf.FloorToInt(y), size);
        int x1 = (x0 + 1) % size, y1 = (y0 + 1) % size;
        return Mathf.Lerp(Mathf.Lerp(heights[y0 * size + x0], heights[y0 * size + x1], x - Mathf.Floor(x)), Mathf.Lerp(heights[y1 * size + x0], heights[y1 * size + x1], x - Mathf.Floor(x)), y - Mathf.Floor(y));
    }
    // textureLod's linear sample is centered on texels, so normalized UV zero
    // lies halfway before texel zero rather than at its lower-left corner.
    private static Vector3 Bilinear(ReadOnlySpan<Vector3> values, int size, float u, float v)
    {
        float x = Mathf.PosMod(u, 1f) * size - .5f, y = Mathf.PosMod(v, 1f) * size - .5f;
        int xFloor = Mathf.FloorToInt(x), yFloor = Mathf.FloorToInt(y);
        int x0 = Mathf.PosMod(xFloor, size), y0 = Mathf.PosMod(yFloor, size);
        int x1 = (x0 + 1) % size, y1 = (y0 + 1) % size;
        return values[y0 * size + x0].Lerp(values[y0 * size + x1], x - xFloor)
            .Lerp(values[y1 * size + x0].Lerp(values[y1 * size + x1], x - xFloor), y - yFloor);
    }
    public static bool Finite(ReadOnlySpan<float> values) { foreach (float value in values) if (!float.IsFinite(value)) return false; return values.Length > 0; }
    public static float GeometryFade(float distance, float end) { end = Mathf.Max(.001f, end); return 1f - Mathf.SmoothStep(.65f * end, end, Mathf.Max(0, distance)); }
    private static float GeometryFade(Vector2 sourceUv, Vector2 centerUv, Vector2 period, float end, float verticalToBody)
    {
        if (end <= 0) return 1;
        Vector2 horizontal = (sourceUv - centerUv) * period;
        return GeometryFade(MathF.Sqrt(horizontal.LengthSquared() + verticalToBody * verticalToBody), end);
    }
}
