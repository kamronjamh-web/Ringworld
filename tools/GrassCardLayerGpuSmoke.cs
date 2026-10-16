using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tests;

/// <summary>Windowed smoke of the production Cards layer (grass-cards plan step S5): the layer builds real card tiles on the
/// procedural ring terrain, the card shader compiles and draws them, and the legacy no-follower fallback is active. Run via
/// tools/grass_card_layer_gpu_smoke.gd (windowed, not headless).</summary>
public partial class GrassCardLayerGpuSmoke : Node
{
    [Signal] public delegate void CompletedEventHandler(bool passed);
    private const long Circumference = 981_748L * 32L;
    private const int Width = 318_000;
    public string OutputDirectory { get; set; } = "";

    public override async void _Ready()
    {
        try { EmitSignal(SignalName.Completed, await RunAsync()); }
        catch (Exception exception) { GD.PushError("GRASS_CARD_LAYER_GPU=FAIL " + exception); EmitSignal(SignalName.Completed, false); }
    }

    private async Task<bool> RunAsync()
    {
        var settings = (VoxelTerrainSettings)GD.Load<VoxelTerrainSettings>("res://resources/voxel/voxel_terrain_settings_ring_integration.tres").Duplicate(true);
        using var sampler = new RingTerrainSampler(settings, true, Circumference, 5_000_000.0, Width, 8000);
        await Task.Run(() => RingGrassCardAssets.GetBlocking(RingGrassCardAtlasParameters.VariantD));
        var clean = new RingLodSurfaceEditSnapshot(1, Circumference, new Dictionary<RingTerrainColumnCoordinate, int>());
        RingCanonicalPosition home = new(186_000.0 + 24, 0, 141_000.0 + 24);
        var geometry = new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero, Circumference / Math.Tau, 0, 0, Circumference, -1000, 1000, home);
        var camera = new Camera3D { Current = true, Far = 800, Fov = 70 };
        AddChild(camera);
        var sun = new DirectionalLight3D { LightEnergy = 1.2f };
        AddChild(sun);
        var layer = new RingStandingGrassLayer { GrassRenderStyle = RingStandingGrassLayer.GrassRenderStyleKind.Cards, GrassCardReachMeters = 150,
            MaximumTileBuildsInFlight = 4, MaximumTileUploadsPerFrame = 8, MaximumTileUploadMilliseconds = 20 };
        AddChild(layer);
        layer.InitializeForTests();
        ulong deadline = Time.GetTicksMsec() + 120_000;
        for (int i = 0; ; i++)
        {
            layer.StepForTests(sampler, clean, home, geometry);
            if (i > 30 && layer.PendingGrassTileBuildCount == 0 && layer.PendingGrassUploadCount == 0) break;
            if (Time.GetTicksMsec() > deadline) throw new TimeoutException("cards layer did not settle");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        var audits = layer.CaptureTileAuditsForTests().Where(a => a.RenderInstanceId != 0 && a.InstanceCount > 400).OrderByDescending(a => a.InstanceCount).ToArray();
        GD.Print($"GRASS_CARD_LAYER_GPU tiles={layer.ResidentGrassTileCount} instances={layer.ResidentGrassInstanceCount} rendered={layer.RenderedGrassTileCount} follower={layer.CardMaterialFollowsSurface} gpu='{layer.GpuCullingStatus}'");
        bool ok = audits.Length > 0 && layer.RenderedGrassTileCount > 0 && !layer.CardMaterialFollowsSurface;
        if (audits.Length == 0) { GD.PushError("no dense tile to look at"); return false; }
        MultiMeshInstance3D renderer = GodotObject.InstanceFromId(audits[0].RenderInstanceId) as MultiMeshInstance3D ?? throw new InvalidOperationException("renderer missing");
        Basis tile = renderer.GlobalTransform.Basis;
        Vector3 up = tile.Y.Normalized(), around = tile.Z.Normalized();
        sun.GlobalTransform = new Transform3D(Basis.LookingAt(-up, around), up * 100f);
        layer.CardMaterialForTests!.SetShaderParameter("sun_direction", up);
        camera.GlobalTransform = new Transform3D(Basis.LookingAt(around - up * .35f, up), renderer.GlobalPosition + up * 1.7f);
        for (int i = 0; i < 20; i++)
        {
            layer.StepForTests(sampler, clean, home, geometry);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        await RenderingServer.Singleton.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image image = GetViewport().GetTexture().GetImage();
        double r = 0, g = 0, b = 0; int lit = 0, total = image.GetWidth() * image.GetHeight();
        for (int y = 0; y < image.GetHeight(); y++) for (int x = 0; x < image.GetWidth(); x++)
        {
            Color c = image.GetPixel(x, y);
            r += c.R; g += c.G; b += c.B;
            if (c.R + c.G + c.B > .06f) lit++;
        }
        GD.Print($"GRASS_CARD_LAYER_GPU image={image.GetWidth()}x{image.GetHeight()} mean=({r / total:F3},{g / total:F3},{b / total:F3}) lit_fraction={(double)lit / total:F3}");
        if (OutputDirectory.Length > 0) image.SavePng(OutputDirectory.TrimEnd('/') + "/layer_cards.png");
        ok &= lit > total / 10;
        async Task Frames(int n) { for (int i = 0; i < n; i++) { layer.StepForTests(sampler, clean, home, geometry); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); } }
        async Task<Image> Capture()
        {
            await Frames(10);
            await RenderingServer.Singleton.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            return GetViewport().GetTexture().GetImage();
        }
        // ---- GPU compute card mode (S6 B2) ----
        ok &= layer.GpuCullingStatus.StartsWith("Cards: GPU") && layer.GpuCullingBatchCount > 0;
        GD.Print($"GRASS_CARD_LAYER_GPU_VRAM instances={layer.ResidentGrassInstanceCount} cpu_mirror_bytes={layer.ResidentGrassInstanceBytes} gpu_total_bytes={layer.GpuCullingBytes} "
            + $"shared_source_bytes={layer.GpuCullingSharedSourceBytes} output_bytes={layer.GpuCullingOutputBytes} batches={layer.GpuCullingBatchCount} sources={layer.GpuCullingSharedSourceCount}");
        // Per-tile terrain uv uniform survives on GPU-culled outputs and is canonical (no seam).
        int uvChecked = 0, uvBad = 0;
        foreach (var audit in layer.CapturePresentationAuditsForTests())
        {
            if (audit.Intermediate || audit.GpuFrameWorld == null) continue;
            var tileRenderer = GodotObject.InstanceFromId(audit.RendererId) as MultiMeshInstance3D;
            RingGrassTileBounds bounds = RingGrassCoordinates.Bounds(audit.Around, audit.Across, 32, Circumference, Width);
            Vector4 expected = RingStandingGrassLayer.CardTerrainOriginUv(bounds, Circumference / Math.Tau, Width, Circumference, home.AroundMeters);
            Variant actual = tileRenderer?.GetInstanceShaderParameter("grass_terrain_origin_uv") ?? default;
            uvChecked++;
            if (actual.VariantType != Variant.Type.Vector4 || !actual.AsVector4().IsEqualApprox(expected)) uvBad++;
        }
        GD.Print($"GRASS_CARD_LAYER_GPU origin_uv gpu_tiles={uvChecked} bad={uvBad}");
        ok &= uvChecked > 0 && uvBad == 0;
        // Accepted-set parity of one dense tile (frustum off, so only the curve is compared).
        layer.GpuFrustumCulling = false;
        await Frames(20);
        {
            var tileAudit = audits[0];
            var pa = layer.CapturePresentationAuditsForTests().FirstOrDefault(a => !a.Intermediate && a.Around == tileAudit.Around && a.Across == tileAudit.Across);
            RingGrassCardComputeParameters card = layer.CardComputeFrame();
            var done = new TaskCompletionSource<(uint[], float[])>();
            bool requested = pa.GpuFrameWorld != null && layer.RequestGpuGrassReadbackForTests(tileAudit.Around, tileAudit.Across, false, (c, r) => done.TrySetResult((c, r)));
            float[] src = Array.Empty<float>();
            layer.TryGetGpuGrassSourceForTests(tileAudit.Around, tileAudit.Across, out src);
            for (int i = 0; requested && i < 300 && !done.Task.IsCompleted; i++) await Frames(1);
            bool parityOk = requested && done.Task.IsCompleted;
            const int S = RingGrassTileData.FloatsPerInstance;
            int mismatches = 0, ambiguous = 0, accepted = 0, rows = src.Length / S;
            if (parityOk)
            {
                (uint[] commands, float[] output) = await done.Task;
                uint count = commands.Length >= 2 ? commands[1] : 0;
                Transform3D l2w = pa.GpuFrameWorld!.Value;
                Vector3 cam = camera.GlobalPosition;
                var gpuAccepted = new bool[rows];
                int cursor = 0;
                for (int o = 0; o < (int)count && parityOk; o++)
                {
                    bool found = false;
                    for (; cursor < rows && !found; cursor++)
                    {
                        bool same = true;
                        for (int k = 0; k < S && same; k++) same = src[cursor * S + k] == output[o * S + k];
                        if (same) { gpuAccepted[cursor] = true; found = true; }
                    }
                    parityOk &= found;
                }
                for (int i = 0; i < rows; i++)
                {
                    float x = src[i * S + 16];
                    if (float.IsNaN(x)) continue;
                    Vector3 world = l2w.Basis * new Vector3(src[i * S + 3], src[i * S + 7], src[i * S + 11]) + l2w.Origin;
                    float distance = world.DistanceTo(cam);
                    bool expect = RingGrassCardComputeParameters.ReferenceAccepts(x, distance, card);
                    if (gpuAccepted[i]) accepted++;
                    if (expect == gpuAccepted[i]) continue;
                    if (RingGrassCardComputeParameters.ReferenceAmbiguous(x, distance, card)) ambiguous++; else mismatches++;
                }
                parityOk &= mismatches == 0 && ambiguous <= Math.Max(1, rows / 1000);
            }
            GD.Print($"GRASS_CARD_LAYER_GPU tile accepted-set parity tile=({tileAudit.Around},{tileAudit.Across}) rows={rows} gpu_accepted={accepted} mismatches={mismatches} ambiguous={ambiguous} ok={parityOk}");
            ok &= parityOk;
        }
        layer.GpuFrustumCulling = true;
        await Frames(20);
        // Image parity, GPU vs the CPU MultiMesh reference. Fixed camera; baseline noise = two identical GPU captures.
        static (double Mean, double Over) Diff(Image a, Image b)
        {
            double sum = 0; int over = 0, n = a.GetWidth() * a.GetHeight();
            for (int y = 0; y < a.GetHeight(); y++) for (int x = 0; x < a.GetWidth(); x++)
            {
                Color ca = a.GetPixel(x, y), cb = b.GetPixel(x, y);
                float dr = MathF.Abs(ca.R - cb.R), dg = MathF.Abs(ca.G - cb.G), db = MathF.Abs(ca.B - cb.B);
                sum += dr + dg + db;
                if (MathF.Max(dr, MathF.Max(dg, db)) > 8f / 255f) over++;
            }
            return (sum / (3.0 * n) * 255.0, (double)over / n);
        }
        Image gpuA = await Capture(), gpuB = await Capture();
        (double baseMean, double baseOver) = Diff(gpuA, gpuB);
        layer.UseGpuCulling = false;
        await Frames(60);
        bool cpuStatus = layer.GpuCullingStatus == "Cards: CPU MultiMesh" && layer.GpuCullingBatchCount == 0;
        Image cpuImage = await Capture();
        (double parMean, double parOver) = Diff(gpuA, cpuImage);
        if (OutputDirectory.Length > 0) { gpuA.SavePng(OutputDirectory.TrimEnd('/') + "/layer_cards_gpu.png"); cpuImage.SavePng(OutputDirectory.TrimEnd('/') + "/layer_cards_cpu.png"); }
        bool imageOk = cpuStatus && parMean <= .5 + baseMean && parOver <= .002 + baseOver;
        GD.Print($"GRASS_CARD_LAYER_GPU image parity gpu_vs_cpu mean_abs={parMean:F4}/255 over8={parOver:P3} baseline mean={baseMean:F4} over8={baseOver:P3} cpu_status={cpuStatus} ok={imageOk}");
        ok &= imageOk;
        layer.UseGpuCulling = true;
        await Frames(80);
        bool backOn = layer.GpuCullingStatus.StartsWith("Cards: GPU") && layer.GpuCullingBatchCount > 0;
        GD.Print($"GRASS_CARD_LAYER_GPU toggled back status='{layer.GpuCullingStatus}' batches={layer.GpuCullingBatchCount} ok={backOn}");
        ok &= backOn;
        // Idle 10 s (GPU mode), then a +-0.75 m oscillation of camera and focus: zero builds/uploads/registration churn.
        async Task<string> Idle(string name, float amplitude, bool strictCompute, bool gate = true)
        {
            Vector3 origin = camera.GlobalPosition;
            // The first oscillation cycle may do work (tier lookahead, as in the streaming hysteresis test); later cycles must not.
            if (amplitude > 0)
                for (int i = 0; i < 90; i++)
                {
                    float warm = amplitude * (((i / 15) & 1) == 0 ? 1f : -1f);
                    camera.GlobalPosition = origin + new Vector3(warm, 0, 0);
                    layer.StepForTests(sampler, clean, home.AddLocal(new Vector3(warm, 0, 0)), geometry);
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                }
            int builds = layer.TotalGrassBuildCount; long uploads = layer.TotalGrassUploadCount;
            long completed = layer.GpuCullingCompletedUpdates, dispatches = layer.GpuDispatchCountForTests, paramUploads = layer.GpuParameterUploadsForTests, writes = layer.PresentationParameterWrites;
            int batches = layer.GpuCullingBatchCount, batchChanges = 0, groupsMin = layer.AdaptiveGrassGroupCount, groupsMax = groupsMin;
            ulong start = Time.GetTicksMsec(); int frame = 0;
            while (Time.GetTicksMsec() - start < 10_000)
            {
                float offset = amplitude * (((frame++ / 15) & 1) == 0 ? 1f : -1f);
                camera.GlobalPosition = origin + new Vector3(offset, 0, 0);
                layer.StepForTests(sampler, clean, home.AddLocal(new Vector3(offset, 0, 0)), geometry);
                if (layer.GpuCullingBatchCount != batches) { batchChanges++; batches = layer.GpuCullingBatchCount; }
                groupsMin = Math.Min(groupsMin, layer.AdaptiveGrassGroupCount); groupsMax = Math.Max(groupsMax, layer.AdaptiveGrassGroupCount);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            camera.GlobalPosition = origin;
            bool pass = layer.TotalGrassBuildCount == builds && layer.TotalGrassUploadCount == uploads && batchChanges == 0
                && (!strictCompute || (layer.GpuCullingCompletedUpdates == completed && layer.GpuDispatchCountForTests == dispatches
                    && layer.GpuParameterUploadsForTests == paramUploads && layer.PresentationParameterWrites == writes));
            if (gate) ok &= pass;
            return $"GRASS_CARD_LAYER_GPU idle[{name}] groups={groupsMin}..{groupsMax} gate={gate} builds+{layer.TotalGrassBuildCount - builds} uploads+{layer.TotalGrassUploadCount - uploads} batch_changes={batchChanges} "
                + $"completed+{layer.GpuCullingCompletedUpdates - completed} dispatches+{layer.GpuDispatchCountForTests - dispatches} param_uploads+{layer.GpuParameterUploadsForTests - paramUploads} presentation_writes+{layer.PresentationParameterWrites - writes} ok={pass}";
        }
        GD.Print(await Idle("stationary", 0f, true));
        GD.Print(await Idle("oscillate_0.75m", .75f, false));
        await Frames(20);
        // Frustum retire / re-upload cycle: look away, tiles behind the camera retire their renderers; look back, they re-upload.
        int Renderers() => layer.CaptureTileAuditsForTests().Count(a => a.RenderInstanceId != 0);
        int renderersBefore = Renderers(); long uploadsBefore = layer.TotalGrassUploadCount;
        Transform3D lookBack = camera.GlobalTransform;
        camera.GlobalTransform = new Transform3D(Basis.LookingAt(-around - up * .35f, up), lookBack.Origin);
        await Frames(15);
        int renderersAway = Renderers();
        camera.GlobalTransform = lookBack;
        await Frames(40);
        int renderersBack = Renderers(); long uploadsDelta = layer.TotalGrassUploadCount - uploadsBefore;
        GD.Print($"GRASS_CARD_LAYER_GPU frustum renderers before={renderersBefore} away={renderersAway} back={renderersBack} reuploads={uploadsDelta}");
        ok &= renderersAway < renderersBefore && renderersBack >= renderersBefore * 9 / 10 && uploadsDelta > 0;
        // Underground hold: hidden, no work, tiles retained, instant resume.
        int tilesHeld = layer.ResidentGrassTileCount; int buildsHeld = layer.TotalGrassBuildCount;
        var idsHeld = layer.CaptureTileAuditsForTests().Where(a => a.RenderInstanceId != 0).Select(a => a.RenderInstanceId).ToHashSet();
        layer.UndergroundHold = true;
        for (int i = 0; i < 5; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        bool hidden = !layer.Visible && layer.ResidentGrassTileCount == tilesHeld && layer.TotalGrassBuildCount == buildsHeld;
        layer.UndergroundHold = false;
        for (int i = 0; i < 5; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var idsResumed = layer.CaptureTileAuditsForTests().Where(a => a.RenderInstanceId != 0).Select(a => a.RenderInstanceId).ToHashSet();
        bool resumed = layer.Visible && idsResumed.SetEquals(idsHeld) && layer.TotalGrassBuildCount == buildsHeld;
        GD.Print($"GRASS_CARD_LAYER_GPU underground hold hidden={hidden} resumed={resumed}");
        ok &= hidden && resumed;
        // ---- Adaptive grouping for Cards (S6 C): reach 400 so complete groups can form beyond the effective start ----
        layer.GrassCardReachMeters = 400;
        async Task Settle(string what, Func<bool> done, int milliseconds)
        {
            ulong until = Time.GetTicksMsec() + (ulong)milliseconds;
            long lastCompleted = -1; int stable = 0;
            for (int i = 0; ; i++)
            {
                layer.StepForTests(sampler, clean, home, geometry);
                // Backend idle: budgeted frames are skipped once every batch has had its pass (the last-frame deferred count is stale then).
                long completed = layer.GpuCullingCompletedUpdates;
                stable = completed == lastCompleted ? stable + 1 : 0; lastCompleted = completed;
                if (i > 20 && stable >= 30 && done()) return;
                if (Time.GetTicksMsec() > until) throw new TimeoutException(what + $" did not settle: builds={layer.PendingGrassTileBuildCount} uploads={layer.PendingGrassUploadCount} deferred={layer.GpuCullingDeferredBatchCount} groupsSettled={layer.IsAdaptiveGrassGroupingSettled} groups={layer.AdaptiveGrassGroupCount} tiles={layer.ResidentGrassTileCount} status={layer.AdaptiveGrassGroupingStatus} start={layer.EffectiveGrassGroupingStartMeters}");
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }
        bool Quiet() => layer.PendingGrassTileBuildCount == 0 && layer.PendingGrassUploadCount == 0;
        await Settle("reach 400 grouped", () => Quiet() && layer.IsAdaptiveGrassGroupingSettled && layer.AdaptiveGrassGroupCount > 0, 300_000);
        int groupedBatches = layer.GpuCullingBatchCount, groupCount = layer.AdaptiveGrassGroupCount;
        long groupedBytes = layer.GpuCullingBytes, groupedSource = layer.GpuCullingSharedSourceBytes, groupedOutput = layer.GpuCullingOutputBytes;
        int groupedResidentInstances = layer.ResidentGrassInstanceCount; long groupedMirror = layer.ResidentGrassInstanceBytes;
        string groupedStatus = layer.GpuCullingStatus;
        bool ownershipOk = true;
        try { layer.AssertAdaptiveGrassOwnershipForTests(); } catch (Exception exception) { ownershipOk = false; GD.PushError(exception.Message); }
        GD.Print($"GRASS_CARD_LAYER_GPU grouping reach=400 groups={groupCount} (2x2={layer.AdaptiveGrassLevel1GroupCount} 4x4={layer.AdaptiveGrassLevel2GroupCount}) grouped_sources={layer.AdaptiveGrassGroupedSourceCount} "
            + $"effective_start={layer.EffectiveGrassGroupingStartMeters:F1}m far_start={layer.EffectiveGrassGroupingFarStartMeters:F1}m tiles={layer.ResidentGrassTileCount} status='{groupedStatus}' ownership_ok={ownershipOk}");
        ok &= ownershipOk && groupCount > 0 && groupedStatus.StartsWith("Cards: GPU");
        GD.Print($"GRASS_CARD_LAYER_GPU_VRAM reach=400 grouped batches={groupedBatches} resident_instances={groupedResidentInstances} cpu_mirror_bytes={groupedMirror} gpu_total_bytes={groupedBytes} shared_source_bytes={groupedSource} output_bytes={groupedOutput}");
        // Image parity, grouped vs ungrouped, same pose.
        Image groupedA = await Capture();
        layer.EnableAdaptiveGrassGrouping = false;
        await Settle("reach 400 ungrouped", () => Quiet() && layer.AdaptiveGrassGroupCount == 0 && layer.IsAdaptiveGrassGroupingSettled, 300_000);
        int ungroupedBatches = layer.GpuCullingBatchCount;
        GD.Print($"GRASS_CARD_LAYER_GPU_VRAM reach=400 ungrouped batches={ungroupedBatches} resident_instances={layer.ResidentGrassInstanceCount} cpu_mirror_bytes={layer.ResidentGrassInstanceBytes} gpu_total_bytes={layer.GpuCullingBytes} "
            + $"shared_source_bytes={layer.GpuCullingSharedSourceBytes} output_bytes={layer.GpuCullingOutputBytes}");
        GD.Print(await Idle("ungrouped_oscillate_0.75m", .75f, false, false));
        Image ungroupedA = await Capture();
        (double gMean, double gOver) = Diff(groupedA, ungroupedA);
        Image ungroupedB = await Capture();
        (double nMean, double nOver) = Diff(ungroupedA, ungroupedB);
        if (OutputDirectory.Length > 0) { groupedA.SavePng(OutputDirectory.TrimEnd('/') + "/layer_cards_grouped.png"); ungroupedA.SavePng(OutputDirectory.TrimEnd('/') + "/layer_cards_ungrouped.png"); }
        bool groupImageOk = gMean <= .5 + nMean && gOver <= .002 + nOver;
        GD.Print($"GRASS_CARD_LAYER_GPU grouping image parity grouped_vs_ungrouped mean_abs={gMean:F4}/255 over8={gOver:P3} baseline mean={nMean:F4} over8={nOver:P3} ok={groupImageOk}");
        // Cards never defers a batch: the effective budget covers every registered batch (ungrouped here, the larger count).
        bool budgetOk = layer.EffectiveGpuCullingBatchesPerFrame >= ungroupedBatches && layer.GpuCullingDeferredBatchCount == 0;
        GD.Print($"GRASS_CARD_LAYER_GPU grouping batch relief reach=400 batches grouped={groupedBatches} ungrouped={ungroupedBatches} (setting {layer.MaximumGpuCullingBatchesPerFrame}/frame, effective {layer.EffectiveGpuCullingBatchesPerFrame}, deferred {layer.GpuCullingDeferredBatchCount}) ok={budgetOk}");
        ok &= groupImageOk && groupedBatches < ungroupedBatches && budgetOk;
        layer.EnableAdaptiveGrassGrouping = true;
        await Settle("reach 400 regrouped", () => Quiet() && layer.IsAdaptiveGrassGroupingSettled && layer.AdaptiveGrassGroupCount > 0, 300_000);
        bool regrouped = layer.AdaptiveGrassGroupCount == groupCount && layer.GpuCullingBatchCount == groupedBatches;
        GD.Print($"GRASS_CARD_LAYER_GPU grouping re-enabled groups={layer.AdaptiveGrassGroupCount} batches={layer.GpuCullingBatchCount} same_as_before={regrouped}");
        ok &= regrouped;
        // CPU fallback stays ungrouped.
        layer.UseGpuCulling = false;
        await Frames(80);
        bool cpuUngrouped = layer.AdaptiveGrassGroupCount == 0 && layer.GpuCullingStatus == "Cards: CPU MultiMesh";
        GD.Print($"GRASS_CARD_LAYER_GPU grouping cpu fallback groups={layer.AdaptiveGrassGroupCount} status='{layer.GpuCullingStatus}' ok={cpuUngrouped}");
        ok &= cpuUngrouped;
        layer.UseGpuCulling = true;
        await Settle("reach 400 regrouped after fallback", () => Quiet() && layer.IsAdaptiveGrassGroupingSettled && layer.AdaptiveGrassGroupCount > 0, 300_000);
        // Idle with groups settled.
        GD.Print((await Idle("grouped_stationary", 0f, true)).Replace("idle[", "idle[g_"));
        GD.Print((await Idle("grouped_oscillate_0.75m", .75f, false)).Replace("idle[", "idle[g_"));
        // Frustum retire / re-upload with groups: look away, leaf outputs behind the camera retire; look back, they return.
        // (The registered batch count is not monotone here: new groups form on the side that turns into view.)
        {
            int leavesBefore = Renderers(), batchesBefore = layer.GpuCullingBatchCount; Transform3D pose = camera.GlobalTransform;
            camera.GlobalTransform = new Transform3D(Basis.LookingAt(-around - up * .35f, up), pose.Origin);
            await Frames(40);
            int leavesAway = Renderers();
            camera.GlobalTransform = pose;
            await Settle("grouped look back", () => Quiet() && Renderers() >= leavesBefore * 9 / 10 && layer.IsAdaptiveGrassGroupingSettled, 120_000);
            int leavesBack = Renderers(), batchesBack = layer.GpuCullingBatchCount;
            bool auditOk = true;
            try { layer.AssertAdaptiveGrassOwnershipForTests(); } catch (Exception exception) { auditOk = false; GD.PushError(exception.Message); }
            bool frustumOk = leavesAway != leavesBefore && leavesBack >= leavesBefore * 9 / 10 && Math.Abs(batchesBack - batchesBefore) <= Math.Max(4, batchesBefore / 10) && auditOk;
            GD.Print($"GRASS_CARD_LAYER_GPU grouped frustum leaf renderers before={leavesBefore} away={leavesAway} back={leavesBack} batches before={batchesBefore} back={batchesBack} ownership_ok={auditOk} ok={frustumOk}");
            ok &= frustumOk;
        }
        // Underground hold with groups: hidden, nothing rebuilt, groups and batches retained, instant resume.
        {
            int tilesG = layer.ResidentGrassTileCount, buildsG = layer.TotalGrassBuildCount, groupsG = layer.AdaptiveGrassGroupCount, batchesG = layer.GpuCullingBatchCount;
            layer.UndergroundHold = true;
            for (int i = 0; i < 5; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            bool hiddenG = !layer.Visible && layer.ResidentGrassTileCount == tilesG && layer.TotalGrassBuildCount == buildsG && layer.AdaptiveGrassGroupCount == groupsG;
            layer.UndergroundHold = false;
            await Settle("grouped underground resume", () => Quiet() && layer.GpuCullingBatchCount == batchesG, 60_000);
            bool resumedG = layer.Visible && layer.TotalGrassBuildCount == buildsG && layer.AdaptiveGrassGroupCount == groupsG;
            GD.Print($"GRASS_CARD_LAYER_GPU grouped underground hold hidden={hiddenG} resumed={resumedG} groups={layer.AdaptiveGrassGroupCount}");
            ok &= hiddenG && resumedG;
        }
        GD.Print(ok ? "GRASS_CARD_LAYER_GPU=PASS" : "GRASS_CARD_LAYER_GPU=FAIL");
        layer.QueueFree();
        return ok;
    }
}
