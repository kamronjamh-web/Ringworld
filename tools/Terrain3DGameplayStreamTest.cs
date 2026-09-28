using System;
using Godot;
using Dictionary = Godot.Collections.Dictionary;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo;

/// <summary>Asynchronous public-contract regression runner for gameplay surface preparation.</summary>
public partial class Terrain3DGameplayStreamTest : Node
{
    private RingTerrainSampler? _sampler;
    private RingTerrain3DGameplayStream? _stream;
    private RingSurfaceFrame _frame;
    private int _caseIndex;
    private int _phase;
    private int _checks;
    private long _initialHits;
    private float _expectedEdit;
    private ulong _deadlineUsec;
    public bool Finished { get; private set; }
    public bool Passed { get; private set; }
    public string Error { get; private set; } = string.Empty;

    public void Start() { if (!Finished && _sampler == null) { _deadlineUsec = Time.GetTicksUsec() + 90_000_000; VerifyCoverageTargetSelection(); BeginCase(); } }
    public bool IsFinished() => Finished;
    public bool IsPassed() => Passed;
    public override void _Process(double delta)
    {
        if (Finished) return;
        try
        {
            if (Time.GetTicksUsec() >= _deadlineUsec) throw new TimeoutException("stream test deadline");
            Step();
        }
        catch (Exception exception) { Complete(false, exception.Message); }
    }
    public override void _ExitTree() { if (!Finished) Complete(false, "runner removed before completion"); }

    private void BeginCase()
    {
        bool runtime = _caseIndex == 1;
        VoxelTerrainSettings settings = runtime
            ? new VoxelTerrainSettings
            {
                SurfaceGenerationRecipeJson = TerrainGenerationParameters.Default().CanonicalJson,
                SurfaceGenerationContractId = RingTerrainSampler.RuntimeSurfaceGenerationContractId,
                BedrockY = -8000, SeaLevelY = 0, EnableUnified3dDensityTerrain = false,
                EnableProceduralCaves = false, EnableNaturalOverhangs = false, EnableDesignedCaveDensityComposition = false
            }
            : new VoxelTerrainSettings();
        _sampler = new RingTerrainSampler(settings, true, CertifiedRingScale.AroundVoxelLength,
            CertifiedRingScale.EffectiveRadiusMeters, CertifiedRingScale.RingWidthMeters, runtime ? 8400 : 8000);
        float minimum = runtime ? -8000 : -8192;
        _stream = new RingTerrain3DGameplayStream(_sampler, minimum, 8000, CertifiedRingScale.RingWidthMeters, CertifiedRingScale.AroundVoxelLength);
        _stream.Configure(2, 8);
        _frame = new RingSurfaceFrame(0, 159000, 0, CertifiedRingScale.AroundVoxelLength, CertifiedRingScale.EffectiveRadiusMeters, 2, -1);
        _stream.Request(_frame, System.Array.Empty<RingSurfaceHeightEdit>(), 0, 1, 16);
        _phase = 0;
    }

    private void Step()
    {
        if (_stream == null || _sampler == null) { BeginCase(); return; }
        _stream.Poll();
        if (_stream.Error.Length > 0) throw new InvalidOperationException(_stream.Error);
        if (!_stream.TryTakeReady(out PreparedWindow prepared)) return;
        try
        {
            if (_phase == 0)
            {
                VerifyPrepared(prepared, _sampler, _frame, 0);
                Dictionary before = _stream.Diagnostics;
                _initialHits = before["cache_hits"].AsInt64();
                _stream.Request(new RingSurfaceFrame(256, 159000, 0, CertifiedRingScale.AroundVoxelLength,
                    CertifiedRingScale.EffectiveRadiusMeters, 2, -1), System.Array.Empty<RingSurfaceHeightEdit>(), 0, 2, 16);
                _phase = 1;
            }
            else if (_phase == 1)
            {
                Dictionary after = _stream.Diagnostics;
                Check(after["cache_hits"].AsInt64() - _initialHits == 6, "neighbor request reuses six cache tiles");
                Check(after["cache_bytes"].AsInt64() <= 8 * 1024 * 1024, "8 MiB cache remains bounded");
                _expectedEdit = prepared.Heights[128 * 384 + 128] + .25f;
                var frame = new RingSurfaceFrame(256, 159000, 0, CertifiedRingScale.AroundVoxelLength,
                    CertifiedRingScale.EffectiveRadiusMeters, 2, -1);
                _stream.Request(frame, new[] { new RingSurfaceHeightEdit(256, 159000, _expectedEdit) }, 7, 3, 16);
                _frame = frame; _phase = 2;
            }
            else if (_phase == 2)
            {
                Check(prepared.EditRevision == 7 && prepared.Revision == 3, "edited request publishes its revision once");
                Check(prepared.Heights[128 * 384 + 128] == _expectedEdit, "fractional edit applies without regenerating cached baseline");
                _stream.Configure(2, 1);
                Check(_stream.Diagnostics["cache_bytes"].AsInt64() <= 1024 * 1024, "1 MiB cache trims boundedly");
                RingSurfaceFrame forward = new(512, 159000, 0, CertifiedRingScale.AroundVoxelLength,
                    CertifiedRingScale.EffectiveRadiusMeters, 2, -1);
                RingSurfaceFrame reversed = new(256, 159000, 0, CertifiedRingScale.AroundVoxelLength,
                    CertifiedRingScale.EffectiveRadiusMeters, 2, 1);
                _stream.Request(forward, System.Array.Empty<RingSurfaceHeightEdit>(), 8, 4, 16);
                _stream.Request(reversed, System.Array.Empty<RingSurfaceHeightEdit>(), 9, 5, 16);
                _frame = reversed; _phase = 3;
            }
            else if (_phase == 3)
            {
                Check(prepared.Revision == 5 && prepared.EditRevision == 9, "rapid cancellation and reversal publish only latest request");
                long submitted = _stream.Diagnostics["submitted"].AsInt64();
                _stream.Poll();
                Check(_stream.Diagnostics["submitted"].AsInt64() == submitted, "completed requests do not schedule duplicate packing");
                Check(!_stream.TryTakeReady(out PreparedWindow duplicate), "continued polling does not repeat a result");
                duplicate?.Dispose();
                RequestAndAdvance(new RingSurfaceFrame(0, 192, 0, CertifiedRingScale.AroundVoxelLength,
                    CertifiedRingScale.EffectiveRadiusMeters, 2, -1), 10, 6, 4);
            }
            else if (_phase == 4)
            {
                VerifyPrepared(prepared, _sampler, _frame, 10);
                VerifyBoundedScheduler();
                RequestAndAdvance(new RingSurfaceFrame(0, CertifiedRingScale.RingWidthMeters - 194, 0, CertifiedRingScale.AroundVoxelLength,
                    CertifiedRingScale.EffectiveRadiusMeters, 2, -1), 11, 7, 5);
            }
            else if (_phase == 5)
            {
                VerifyPrepared(prepared, _sampler, _frame, 11);
                VerifyBoundedScheduler();
                RequestAndAdvance(new RingSurfaceFrame(CertifiedRingScale.AroundVoxelLength - 128, 159000, 0, CertifiedRingScale.AroundVoxelLength,
                    CertifiedRingScale.EffectiveRadiusMeters, 2, -1), 12, 8, 6);
            }
            else if (_phase == 6)
            {
                VerifyPrepared(prepared, _sampler, _frame, 12);
                VerifyBoundedScheduler();
                _stream.Request(new RingSurfaceFrame(CertifiedRingScale.AroundVoxelLength / 2, 80000, 0, CertifiedRingScale.AroundVoxelLength,
                    CertifiedRingScale.EffectiveRadiusMeters, 2, -1), System.Array.Empty<RingSurfaceHeightEdit>(), 13, 9, 16);
                _stream.Dispose(); _stream = null; _sampler.Dispose(); _sampler = null;
                _caseIndex++;
                if (_caseIndex >= 2) Complete(true, string.Empty); else BeginCase();
            }
        }
        finally { prepared.Dispose(); }
    }

    private void RequestAndAdvance(RingSurfaceFrame frame, ulong editRevision, long revision, int nextPhase)
    {
        _stream!.Request(frame, System.Array.Empty<RingSurfaceHeightEdit>(), editRevision, revision, 16);
        _frame = frame;
        _phase = nextPhase;
    }

    private void VerifyBoundedScheduler()
    {
        Dictionary state = _stream!.Diagnostics;
        Check(state["cache_bytes"].AsInt64() <= 1024 * 1024, "1 MiB cache stays bounded after a complete window");
        long pending = state["pending_jobs"].AsInt64();
        bool packing = state["packing"].AsBool();
        Check(pending + (packing ? 1 : 0) <= 2, "pending work including packer stays within configured worker cap");
        Check(state["highwater_jobs"].AsInt64() <= 2, "high-water job diagnostic includes the packer within configured cap");
    }

    private void VerifyCoverageTargetSelection()
    {
        var frame = new RingSurfaceFrame(256, 159000, 0, CertifiedRingScale.AroundVoxelLength,
            CertifiedRingScale.EffectiveRadiusMeters, 2, -1);
        foreach (double offset in new[] { -176.0, -128.0, 128.0, 176.0 })
        {
            var position = new RingCanonicalPosition(frame.AnchorAroundMeters + offset, 0, frame.AnchorAcrossMeters);
            RingCanonicalPosition target = RingTerrain3DGameplay.SelectCoverageTarget(frame, position, Vector3.Zero, 64, 64, false);
            Check(target.AroundMeters == frame.AnchorAroundMeters && target.AcrossMeters == frame.AnchorAcrossMeters,
                $"stationary offset {offset} retains the active stream anchor");
        }
        RingCanonicalPosition forward = RingTerrain3DGameplay.SelectCoverageTarget(frame,
            new RingCanonicalPosition(384, 0, frame.AnchorAcrossMeters), Vector3.Right * 12, 64, 64, false);
        Check(forward.AroundMeters == 512, "forward travel still prefetches the next around anchor");
        RingCanonicalPosition reverse = RingTerrain3DGameplay.SelectCoverageTarget(frame,
            new RingCanonicalPosition(128, 0, frame.AnchorAcrossMeters), Vector3.Left * 12, 64, 64, false);
        Check(reverse.AroundMeters == 0, "reversal still prefetches the prior around anchor");
        var unsupported = new RingCanonicalPosition(900, 5, 159120);
        RingCanonicalPosition direct = RingTerrain3DGameplay.SelectCoverageTarget(frame, unsupported, Vector3.Zero, 64, 64, true);
        Check(direct.Equals(unsupported), "unsupported collision position remains the direct recovery target");
    }

    private void VerifyPrepared(PreparedWindow prepared, RingTerrainSampler sampler, RingSurfaceFrame frame, ulong editRevision)
    {
        Check(prepared.EditRevision == editRevision, "prepared snapshot preserves edit revision");
        Check(prepared.Heights.Length == 384 * 384 && prepared.HeightImages.Count == 9 && prepared.BiomeImages.Count == 9, "nine ordered prepared tiles");
        foreach ((int x, int z) point in new[] { (0, 0), (383, 0), (0, 383), (383, 383), (192, 192) })
        {
            long around = RingSurfaceFrame.Wrap(frame.AnchorAroundMeters + (point.x - 128L) * 2, frame.CircumferenceMeters);
            int across = frame.AnchorAcrossMeters + frame.AcrossDirection * (point.z - 128) * 2;
            float expected = across < 0 || across >= CertifiedRingScale.RingWidthMeters
                ? (sampler.UsesRuntimeSurfaceGeneration ? -8000 : -8192)
                : sampler.UsesRuntimeSurfaceGeneration ? sampler.GetPresentationSurfaceHeight(around, across) : sampler.GetSmoothSurfaceHeight(around, across) + .5f;
            float actual = prepared.Heights[point.z * 384 + point.x];
            Check(actual == expected, "prepared heights preserve field sample ordering");
        }
        Image height = prepared.HeightImages[4];
        float packed = height.GetPixel(0, 0).R;
        Check(BitConverter.SingleToInt32Bits(packed) == BitConverter.SingleToInt32Bits(prepared.Heights[128 * 384 + 128] - frame.AnchorHeight), "RF height image preserves raw float bits");
        Image biome = prepared.BiomeImages[4];
        long centerAround = frame.AnchorAroundMeters; int centerAcross = frame.AnchorAcrossMeters;
        Color expectedBiome = sampler.GetBiomeTextureWeights(centerAround, centerAcross).ToVertexColor();
        using Image expectedImage = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
        expectedImage.SetPixel(0, 0, expectedBiome);
        byte[] expectedBytes = expectedImage.GetData();
        byte[] actualBytes = biome.GetData();
        Check(actualBytes[0] == expectedBytes[0] && actualBytes[1] == expectedBytes[1]
            && actualBytes[2] == expectedBytes[2] && actualBytes[3] == expectedBytes[3], "biome image uses Godot RGBA8 quantization");
        Check(prepared.Frame.AcrossDirection == -1 && prepared.Geometry != null, "reversed curved frame has gameplay collision geometry");
    }

    private void Check(bool condition, string label) { _checks++; if (!condition) throw new InvalidOperationException(label); }
    private void Complete(bool passed, string error)
    {
        _stream?.Dispose(); _stream = null; _sampler?.Dispose(); _sampler = null;
        Passed = passed; Error = error; Finished = true;
        GD.Print(passed ? $"TERRAIN3D_GAMEPLAY_STREAM=PASS checks={_checks}" : $"TERRAIN3D_GAMEPLAY_STREAM=FAIL checks={_checks}: {error}");
    }
}
