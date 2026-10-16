using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Emitter relay grid phase G2 (docs/emitter-grid-climate-machines-spec.md G2/G4): the beam layer binds 600 links and a head
/// sprite MultiMesh, mirrors the endpoint math exactly, rebuilds absolutely and allocation free on every anchor change,
/// drives its fronts from the pure pulse clock, applies the saved settings and stays inert on worlds without emitter sites.
/// Expectations come from the live registry and clock; no golden values are used.
/// </summary>
public partial class EmitterGridLayerTest : RefCounted
{
    static void Expect(List<string> failures, bool ok, string message) { if (!ok) failures.Add(message); }

    public int Run(bool unused)
    {
        _ = unused;
        var failures = new List<string>();
        Node3D? fixture = null;
        try
        {
            SceneTree tree = (Engine.GetMainLoop() as SceneTree) ?? throw new InvalidOperationException("scene tree unavailable");
            using RingTerrainSampler sampler = EmitterSiteTestWorlds.CreateGen17(1337);
            using INaturalHeightSource source = EmitterSiteHeightSources.Create(sampler);
            EmitterSiteRegistry registry = EmitterSiteRegistry.GetOrBuild(EmitterSiteRecipe.Default(1337), source);
            EmitterGridTopology topology = EmitterGridTopology.Build(registry);

            fixture = new Node3D { Name = "EmitterGridFixture" };
            tree.Root.AddChild(fixture);
            var grid = new VoxelChunkGridManager
            {
                Name = "ChunkGridManager",
                BuildOnReady = false,
                DormantEditableBackend = true,
                EnableChunkStreaming = false,
                UseDeterministicTerrainGeneration = true,
                EnableCanonicalFloatingOriginAnchor = true,
                TerrainSettings = new VoxelTerrainSettings()
            };
            fixture.AddChild(grid);

            CheckInert(fixture, failures);

            var layer = new EmitterGridBeamLayer { Name = "EmitterGrid", AutoBind = false, LoadSavedDefaults = false };
            fixture.AddChild(layer);
            if (!layer.Bind(grid, topology)) { Console.Error.WriteLine("EMITTER_GRID_LAYER=FAIL bind"); return 1; }

            CheckStructure(layer, topology, failures);
            CheckMirror(layer, topology, grid, failures);
            CheckAnchorRebuilds(layer, grid, failures);
            CheckFronts(layer, topology, failures);
            CheckSettings(layer, failures);
            CheckVisibility(layer, failures);
            CheckHandoff(failures);
            CheckFarLayer(layer, topology, fixture, failures);
            CheckSources(failures);
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.ToString()); return 1; }
        finally
        {
            if (fixture != null && GodotObject.IsInstanceValid(fixture))
            {
                fixture.GetParent()?.RemoveChild(fixture);
                fixture.Free();
            }
        }
        if (failures.Count != 0) { Console.Error.WriteLine("EMITTER_GRID_LAYER=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("EMITTER_GRID_LAYER=PASS");
        return 0;
    }

    static void CheckInert(Node3D fixture, List<string> failures)
    {
        // No terrain sampler (and therefore no emitter sites): an auto-binding layer must stay unbound, hidden and child-free.
        var inert = new EmitterGridBeamLayer { Name = "InertGrid", AutoBind = true, LoadSavedDefaults = false };
        fixture.AddChild(inert);
        for (int i = 0; i < 3; i++) inert._Process(0.016);
        Expect(failures, !inert.IsBound && !inert.Visible && inert.GetChildCount() == 0 && inert.LinkInstances == null, "layer is not inert without emitter sites");
        Expect(failures, inert.DescribeStatus().Contains("waiting") || inert.DescribeStatus().Contains("unavailable"), "inert status text");
        Expect(failures, inert.LinkCount == 0 && inert.LiveFrontCount == 0, "inert layer reports links/fronts");
        inert.UpdateFronts(1000d); // must not throw while unbound
        fixture.RemoveChild(inert);
        inert.Free();
    }

    static void CheckStructure(EmitterGridBeamLayer layer, EmitterGridTopology topology, List<string> failures)
    {
        Expect(failures, layer.IsBound && layer.Visible && layer.LinkCount == 600 && topology.LinkCount == 600, $"links {layer.LinkCount}");
        if (layer.LinkInstances?.Multimesh is not { } links || layer.HeadInstances?.Multimesh is not { } heads) { failures.Add("missing multimeshes"); return; }
        Expect(failures, links.InstanceCount == 600 && links.UseCustomData && links.TransformFormat == MultiMesh.TransformFormatEnum.Transform3D, "link multimesh shape");
        Expect(failures, heads.InstanceCount == EmitterGridBeamLayer.MaxHeadSprites && heads.UseCustomData, "head multimesh shape");
        Expect(failures, layer.RibbonVertexCount == EmitterGridBeamLayer.RibbonSubSegments * 4 && links.Mesh is ArrayMesh { } mesh && mesh.GetSurfaceCount() == 1, "ribbon mesh shape");
        if (links.Mesh is ArrayMesh ribbon)
        {
            var arrays = ribbon.SurfaceGetArrays(0);
            var uv2 = (Vector2[])arrays[(int)Mesh.ArrayType.TexUV2];
            var uv = (Vector2[])arrays[(int)Mesh.ArrayType.TexUV];
            var index = (int[])arrays[(int)Mesh.ArrayType.Index];
            Expect(failures, uv.Length == 64 && uv2.Length == 64 && index.Length == 96, "ribbon attribute lengths");
            for (int s = 0; s < 16 && uv.Length == 64; s++)
                for (int c = 0; c < 4; c++)
                {
                    Vector2 a = uv[s * 4 + c], b = uv2[s * 4 + c];
                    if (Math.Abs(b.X - s / 16f) > 1e-6f || Math.Abs(b.Y - (s + 1) / 16f) > 1e-6f || Math.Abs(a.X - (c < 2 ? b.X : b.Y)) > 1e-6f || Math.Abs(a.Y - ((c & 1) == 0 ? -1f : 1f)) > 1e-6f)
                        failures.Add($"ribbon uv sub-segment {s} corner {c}");
                }
        }
        Expect(failures, layer.BeamMaterial?.Shader?.ResourcePath == EmitterGridBeamLayer.BeamShaderPath && layer.HeadMaterial?.Shader?.ResourcePath == EmitterGridBeamLayer.HeadShaderPath, "materials/shaders");
        Expect(failures, layer.CoreMaterial?.Shader?.ResourcePath == EmitterGridBeamLayer.CoreShaderPath, "core material/shader");
        Expect(failures, layer.CoreMaterial != null && layer.BeamMaterial!.NextPass == layer.CoreMaterial, "beam NextPass is not the core by default");
        Expect(failures, layer.BeamMaterial!.RenderPriority == EmitterGridBeamLayer.HaloRenderPriority && layer.HeadMaterial!.RenderPriority == EmitterGridBeamLayer.HaloRenderPriority
            && layer.CoreMaterial!.RenderPriority == EmitterGridBeamLayer.CoreRenderPriority && layer.CoreMaterial.RenderPriority > layer.BeamMaterial.RenderPriority, "render priorities");
        Expect(failures, layer.LinkInstances.Name == "GridLinks" && layer.HeadInstances.Name == "GridHeads", "child names");
        Expect(failures, layer.LinkInstances.MaterialOverride == layer.BeamMaterial && layer.LinkInstances.CastShadow == GeometryInstance3D.ShadowCastingSetting.Off, "link instance setup");
        // Custom data = (P0, P1, column, kind); readback is unavailable under the headless dummy renderer.
        Color c0 = links.GetInstanceCustomData(7);
        if (c0 != default && !c0.IsEqualApprox(new Color(0f, 0f, 0f, 1f)))
        {
            EmitterLink l = topology[7];
            Expect(failures, Math.Abs(c0.R - (float)l.P0) < 1f && Math.Abs(c0.G - (float)l.P1) < 1f && (int)c0.B == l.Column && (int)c0.A == (int)l.Kind, $"custom data {c0}");
        }
        else Console.WriteLine("EMITTER_GRID_LAYER rs_custom_readback=unavailable");
        Expect(failures, layer.Settings.GridEnabled && Math.Abs(layer.Clock!.PeriodSeconds - 300d) < 1e-9, "default clock");
    }

    static void CheckMirror(EmitterGridBeamLayer layer, EmitterGridTopology topology, VoxelChunkGridManager grid, List<string> failures)
    {
        RingLodGeometrySnapshot snapshot = grid.CaptureLodGeometrySnapshot();
        var a = new Vector3D[topology.LinkCount];
        var b = new Vector3D[topology.LinkCount];
        topology.FillWorldEndpoints(snapshot, a, b);
        double worstOrigin = 0, worstSpan = 0;
        for (int i = 0; i < topology.LinkCount; i++)
        {
            Transform3D t = layer.GetLinkTransform(i);
            worstOrigin = Math.Max(worstOrigin, t.Origin.DistanceTo(a[i].ToVector3()));
            worstSpan = Math.Max(worstSpan, t.Basis.X.DistanceTo((b[i] - a[i]).ToVector3()));
            if (a[i] != layer.GetWorldEndpointA(i) || b[i] != layer.GetWorldEndpointB(i)) { failures.Add($"endpoint cache {i}"); break; }
        }
        Expect(failures, worstOrigin <= 0.001 && worstSpan <= 0.001, $"mirror origin {worstOrigin} span {worstSpan}");
        Console.WriteLine($"EMITTER_GRID_LAYER mirror worst_origin={worstOrigin:E2} worst_span={worstSpan:E2}");
    }

    static void CheckAnchorRebuilds(EmitterGridBeamLayer layer, VoxelChunkGridManager grid, List<string> failures)
    {
        double circumference = grid.GetCanonicalRingCircumferenceMeters64();
        layer.Rebuild(grid.CaptureLodGeometrySnapshot()); // warm-up
        Vector3 before = layer.GetLinkTransform(0).Origin;
        int start = layer.RebuildCount, revision = layer.GeometryRevision;
        grid.SetFloatingOriginCanonicalAnchor(new RingCanonicalPosition(5_000_000d, 100d, 150_000d));
        Expect(failures, layer.RebuildCount == start + 1 && layer.GeometryRevision == revision + 1, $"anchor A rebuilds {layer.RebuildCount - start}");
        Expect(failures, layer.GetLinkTransform(0).Origin.DistanceTo(before) > 1000f, "anchor A did not move origins");
        int afterA = layer.RebuildCount;
        grid.SetFloatingOriginCanonicalAnchor(new RingCanonicalPosition(5_000_000d, 100d, 150_000d));
        Expect(failures, layer.RebuildCount == afterA, "same anchor rebuilt");
        grid.SetFloatingOriginCanonicalAnchor(new RingCanonicalPosition(circumference - 5d, 100d, 150_000d));
        Expect(failures, layer.RebuildCount == afterA + 1, "seam anchor rebuild");
        // Absolute rebuild: returning to a previous anchor reproduces the previous transforms exactly.
        grid.SetFloatingOriginCanonicalAnchor(new RingCanonicalPosition(5_000_000d, 100d, 150_000d));
        Transform3D again = layer.GetLinkTransform(0);
        grid.SetFloatingOriginCanonicalAnchor(new RingCanonicalPosition(1_234_567d, 90d, 120_000d));
        grid.SetFloatingOriginCanonicalAnchor(new RingCanonicalPosition(5_000_000d, 100d, 150_000d));
        Expect(failures, layer.GetLinkTransform(0) == again, "rebuild is not absolute (anchor round trip differs)");
        int afterRoundTrip = layer.RebuildCount;
        grid.ClearFloatingOriginCanonicalAnchor();
        Expect(failures, layer.RebuildCount == afterRoundTrip + 1, "clear rebuild");

        // Allocation and time budget: managed allocation across repeated rebuilds, steady state.
        RingLodGeometrySnapshot snapshot = grid.CaptureLodGeometrySnapshot();
        for (int i = 0; i < 10; i++) layer.Rebuild(snapshot);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        double worst = 0;
        for (int i = 0; i < 100; i++) { layer.Rebuild(snapshot); worst = Math.Max(worst, layer.LastRebuildMs); }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Console.WriteLine($"EMITTER_GRID_LAYER rebuild worst_ms={worst:F3} allocated_bytes_per_100={allocated}");
        Expect(failures, allocated == 0, $"rebuild allocated {allocated} bytes over 100 rebuilds");
        Expect(failures, worst < 5d, $"rebuild took {worst:F2} ms");
    }

    static void CheckFronts(EmitterGridBeamLayer layer, EmitterGridTopology topology, List<string> failures)
    {
        EmitterGridPulseClock clock = layer.Clock!;
        double c = clock.CircumferenceMeters;
        Span<EmitterPulseFront> buffer = stackalloc EmitterPulseFront[32];
        for (int step = 0; step < 40; step++)
        {
            double t = 12_345.678 + step * 7.31;
            layer.UpdateFronts(t);
            int expectedFronts = 0, expectedHeads = 0;
            for (int column = 0; column < 6; column++)
            {
                int n = clock.GetFronts(t, column, buffer);
                expectedFronts += n;
                for (int i = 0; i < n; i++) if (buffer[i].HeadQ >= 0d && buffer[i].HeadQ <= c) expectedHeads++;
            }
            if (layer.LiveFrontCount != Math.Min(expectedFronts, EmitterGridBeamLayer.MaxShaderFronts)) failures.Add($"fronts at {t}: {layer.LiveFrontCount} vs {expectedFronts}");
            if (layer.HeadSpriteCount != expectedHeads) failures.Add($"heads at {t}: {layer.HeadSpriteCount} vs {expectedHeads}");
            // Auto speed: exactly one head per chain is in flight.
            if (expectedHeads != 6) failures.Add($"auto speed head count {expectedHeads} at {t}");
            if (layer.LiveFrontCount > EmitterGridBeamLayer.MaxShaderFronts) failures.Add("front array overflow");
        }
        // Head position equals the chord interpolation at the head's p (independent linear scan).
        double tHead = 2_000.25;
        layer.UpdateFronts(tHead);
        int checkedHeads = 0;
        for (int column = 0; column < 6; column++)
        {
            int n = clock.GetFronts(tHead, column, buffer);
            for (int i = 0; i < n; i++)
            {
                double q = buffer[i].HeadQ;
                if (q < 0d || q > c) continue;
                double p = clock.Settings.Direction == EmitterPulseDirection.Prograde ? q : c - q;
                int link = -1;
                for (int k = 0; k < topology.LinksPerChain; k++)
                {
                    EmitterLink l = topology[column * topology.LinksPerChain + k];
                    if (p >= l.P0 && p <= l.P1) { link = l.Index; break; }
                }
                if (link < 0 || !layer.TryHeadLocal(column, q, out Vector3 local)) { failures.Add($"head col {column} q {q} has no link"); continue; }
                EmitterLink hl = topology[link];
                double f = (p - hl.P0) / (hl.P1 - hl.P0);
                Vector3 expected = (layer.GetWorldEndpointA(link) + (layer.GetWorldEndpointB(link) - layer.GetWorldEndpointA(link)) * f).ToVector3();
                if (local.DistanceTo(expected) > 0.01f + 1e-6f * expected.Length()) failures.Add($"head position col {column} off by {local.DistanceTo(expected)}");
                checkedHeads++;
            }
        }
        Expect(failures, checkedHeads == 6, $"checked heads {checkedHeads}");
        // Retrograde mirror: the head list is the same function of (p) with q = C - p.
        var retro = new GameplayEmitterGridSettings { PulseDirection = 1 };
        layer.Configure(retro);
        Expect(failures, layer.Clock!.Settings.Direction == EmitterPulseDirection.Retrograde, "retrograde direction not applied");
        layer.UpdateFronts(tHead);
        Expect(failures, layer.HeadSpriteCount == 6, "retrograde head count");
        layer.Configure(new GameplayEmitterGridSettings());
    }

    static void CheckSettings(EmitterGridBeamLayer layer, List<string> failures)
    {
        var s = new GameplayEmitterGridSettings
        {
            PulsePeriodSeconds = 120, PulseSpeedMode = 1, PulseSpeedKmPerSecond = 300, PulseTrailSeconds = 4, PulseWakeSeconds = 0, ColumnStaggerSeconds = 2,
            CoreWidthMeters = 80, HaloWidthMeters = 900, MinPixelWidth = 2.5f, FarCompensation = 0.3f, NightPeakIntensity = 2, NearLayerMaxKm = 400, BaseEmission = 9
        };
        layer.Configure(s);
        EmitterGridPulseClock clock = layer.Clock!;
        Expect(failures, Math.Abs(clock.PeriodSeconds - 120d) < 1e-9 && Math.Abs(clock.SpeedMetersPerSecond - 300_000d) < 1e-6 && !clock.Settings.AutoSpeed, "manual speed/period not applied");
        Expect(failures, clock.CutoffMeters >= clock.SpeedMetersPerSecond * 6d * 4d - 1d, "cut-off shorter than the trail with a zero wake");
        Vector4 widths = (Vector4)layer.BeamMaterial!.GetShaderParameter("beam_widths");
        Expect(failures, widths.IsEqualApprox(new Vector4(80f, 900f, 2.5f, 0.3f)), $"beam_widths {widths}");
        Vector4 visibility = (Vector4)layer.BeamMaterial.GetShaderParameter("eg_visibility");
        Expect(failures, Math.Abs(visibility.X - 2f) < 1e-6f, $"eg_visibility {visibility}");
        Expect(failures, Math.Abs((float)layer.BeamMaterial.GetShaderParameter("base_emission") - 9f) < 1e-6f, "base_emission");
        Expect(failures, layer.BeamMaterial.NextPass == layer.CoreMaterial, "DepthWriteCore default lost the core pass");
        foreach (ShaderMaterial? material in new[] { layer.BeamMaterial, layer.CoreMaterial })
            Expect(failures, material != null && Math.Abs((float)material.GetShaderParameter("world_scale") - 1f) < 1e-6f, "near world_scale");
        Expect(failures, Math.Abs((float)layer.HeadMaterial!.GetShaderParameter("world_scale") - 1f) < 1e-6f, "near head world_scale");
        layer.Configure(new GameplayEmitterGridSettings { DepthWriteCore = false });
        Expect(failures, layer.BeamMaterial.NextPass == null, "DepthWriteCore=false kept the core pass");
        layer.Configure(new GameplayEmitterGridSettings { CoreDepthThreshold = 0.8f, CoreDepthMinPixels = 6f });
        Expect(failures, layer.BeamMaterial.NextPass == layer.CoreMaterial, "DepthWriteCore=true did not restore the core pass");
        foreach (ShaderMaterial? material in new[] { layer.BeamMaterial, layer.CoreMaterial })
            Expect(failures, material != null && ((Vector2)material.GetShaderParameter("core_depth")).IsEqualApprox(new Vector2(0.8f, 6f)), "core_depth");
        layer.Configure(s);
        layer.UpdateFronts(5_000d);
        Expect(failures, layer.LiveFrontCount <= EmitterGridBeamLayer.MaxShaderFronts && layer.HeadSpriteCount <= EmitterGridBeamLayer.MaxHeadSprites, "slow-speed caps");
        // Settings persist through the defaults file and re-apply.
        var defaults = new GameplayEmitterGridDefaults("res://.godot/emitter_grid_layer_test_defaults.tres");
        defaults.Load();
        defaults.SetPulsePeriodSeconds(200);
        defaults.Apply(layer);
        Expect(failures, Math.Abs(layer.Clock!.PeriodSeconds - 200d) < 1e-9, "defaults Apply");
        layer.Configure(new GameplayEmitterGridSettings());
        Expect(failures, Math.Abs(layer.Clock!.PeriodSeconds - 300d) < 1e-9 && layer.Clock.Settings.AutoSpeed, "defaults restore");
    }

    static void CheckVisibility(EmitterGridBeamLayer layer, List<string> failures)
    {
        layer.SetUndergroundHidden(true);
        Expect(failures, !layer.Visible && layer.UndergroundHidden && layer.LinkInstances!.Multimesh.InstanceCount == 600, "underground hide");
        layer.SetUndergroundHidden(false);
        Expect(failures, layer.Visible, "underground restore");
        layer.Configure(new GameplayEmitterGridSettings { GridEnabled = false });
        Expect(failures, !layer.Visible, "disabled grid visible");
        layer.Configure(new GameplayEmitterGridSettings());
        Expect(failures, layer.Visible, "re-enabled grid hidden");
        layer.ShowAllLinks = true;
        Expect(failures, Math.Abs((float)layer.BeamMaterial!.GetShaderParameter("show_all") - 1f) < 1e-6f, "ShowAllLinks");
        layer.ShowAllLinks = false;
        double t = 3_000d;
        layer.FirePulseNow();
        Expect(failures, layer.Clock != null && layer.DescribeStatus().Contains("600 links"), "status line: " + layer.DescribeStatus());
        layer.PulseScrubSeconds = 0d;
        _ = t;
    }

    static void CheckHandoff(List<string> failures)
    {
        Vector2 off = EmitterGridBeamLayer.ResolveHandoffBand(false, true, true, true, 48000d, 100000d, 90d, 200d);
        Expect(failures, off.X == 1e12f && off.Y == 2e12f, $"inactive far layer band {off}");
        Vector2 clouds = EmitterGridBeamLayer.ResolveHandoffBand(true, true, true, true, 48000d, 100000d, 90d, 200d);
        Expect(failures, clouds.IsEqualApprox(new Vector2(48000f, 100000f)), $"cloud band {clouds}");
        Vector2 degenerate = EmitterGridBeamLayer.ResolveHandoffBand(true, true, true, true, 48000d, 10000d, 90d, 200d);
        Expect(failures, degenerate.IsEqualApprox(new Vector2(48000f, 48001f)), $"degenerate cloud band {degenerate}");
        Vector2 suspended = EmitterGridBeamLayer.ResolveHandoffBand(true, true, true, false, 48000d, 100000d, 90d, 200d);
        Expect(failures, suspended.IsEqualApprox(new Vector2(0f, 1f)), $"suspended near clouds band {suspended}");
        Vector2 unknown = EmitterGridBeamLayer.ResolveHandoffBand(true, true, false, true, 48000d, 100000d, 90d, 200d);
        Expect(failures, unknown.IsEqualApprox(new Vector2(90000f, 200000f)), $"unknown ownership band {unknown}");
        Vector2 manual = EmitterGridBeamLayer.ResolveHandoffBand(true, false, true, true, 48000d, 100000d, 90d, 200d);
        Expect(failures, manual.IsEqualApprox(new Vector2(90000f, 200000f)), $"manual band {manual}");
        Vector2 narrow = EmitterGridBeamLayer.ResolveHandoffBand(true, false, true, true, 48000d, 100000d, 100d, 100d);
        Expect(failures, narrow.IsEqualApprox(new Vector2(100000f, 101000f)), $"narrow manual band {narrow}");

        var band = new Vector2(120_000f, 172_000f);
        const float max = 700_000f, fade = 100_000f;
        for (float d = 0f; d <= 1_500_000f; d += 5_000f)
        {
            float nearBackdrop = EmitterGridBeamLayer.NearLayerWeight(d, true, max, fade, band);
            float nearSolid = EmitterGridBeamLayer.NearLayerWeight(d, false, max, fade, band);
            float far = EmitterGridBeamLayer.FarLayerWeight(d, max, fade, band);
            float nearFade = 1f - Smoothstep(max - fade, max, d);
            if (Math.Abs(nearBackdrop + far - 1f) > 1e-6f) failures.Add($"layer weights do not sum to 1 at {d}: {nearBackdrop} + {far}");
            if (Math.Abs(nearSolid - nearFade) > 1e-6f) failures.Add($"non-backdrop near weight at {d}: {nearSolid} vs {nearFade}");
            if (d <= 120_000f && (nearBackdrop != 1f || far != 0f)) failures.Add($"inside the band start at {d}: near {nearBackdrop} far {far}");
            if (d >= 172_000f && nearBackdrop > 1e-6f) failures.Add($"beyond the band end at {d}: near {nearBackdrop}");
        }
        Expect(failures, EmitterGridBeamLayer.FarLayerWeight(1_400_000f, max, fade, band) == 1f, "far layer does not own the distance");
    }

    static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    static void CheckFarLayer(EmitterGridBeamLayer layer, EmitterGridTopology topology, Node3D fixture, List<string> failures)
    {
        const float scale = 0.001f;
        const double width = 318000d;
        double circumference = topology.CircumferenceMeters, radius = circumference / Math.Tau;
        Vector3 Map(double around, double vertical, double across)
        {
            double wrapped = around % circumference;
            if (wrapped < 0d) wrapped += circumference;
            double angle = wrapped / radius, radial = radius - vertical;
            return new Vector3((float)(Math.Cos(angle) * radial * scale), (float)((-width * 0.5 + across) * scale), (float)(Math.Sin(angle) * radial * scale));
        }
        Expect(failures, layer.FarLayer == null && !layer.FarLayerActive, "far layer exists before binding");
        Expect(failures, !layer.BindFarLayer(Map, scale, _ => false) && layer.FarLayer == null, "failed attach left a far layer");
        Expect(failures, layer.BindFarLayer(Map, scale, node => { fixture.AddChild(node); return true; }), "BindFarLayer failed");
        EmitterGridFarLayer? far = layer.FarLayer;
        if (far == null) { failures.Add("no far layer"); return; }
        Expect(failures, layer.BindFarLayer(Map, scale, _ => false), "second BindFarLayer should be a no-op success");
        Expect(failures, far.LinkCount == 600 && far.LinkInstances?.Multimesh.InstanceCount == 600 && far.LinkInstances.Name == "FarGridLinks" && far.HeadInstances?.Name == "FarGridHeads"
            && far.HeadInstances.Multimesh.InstanceCount == EmitterGridBeamLayer.MaxHeadSprites, "far layer shape");
        double worstEnd = 0d, worstBasis = 0d;
        for (int i = 0; i < topology.LinkCount; i++)
        {
            EmitterLink link = topology[i];
            var (fa, fv, fc) = topology.RelayNode(link.FromSite);
            var (ta, tv, tc) = topology.RelayNode(link.ToSite);
            Vector3 a = Map(fa, fv, fc), b = Map(ta, tv, tc);
            if (far.GetScaledEndpointA(i) != a || far.GetScaledEndpointB(i) != b) { failures.Add($"far endpoint {i}"); break; }
            Transform3D t = far.GetLinkTransform(i);
            worstEnd = Math.Max(worstEnd, t.Origin.DistanceTo(a));
            worstBasis = Math.Max(worstBasis, t.Basis.X.DistanceTo(b - a));
        }
        Expect(failures, worstEnd == 0d && worstBasis == 0d, $"far link transforms off by {worstEnd} / {worstBasis}");
        ShaderMaterial? beam = far.BeamMaterial, head = far.HeadMaterial, nearBeam = layer.BeamMaterial;
        if (beam == null || head == null || nearBeam == null) { failures.Add("far materials missing"); return; }
        Expect(failures, beam.RenderPriority == EmitterGridFarLayer.FarRenderPriority && head.RenderPriority == EmitterGridFarLayer.FarRenderPriority
            && EmitterGridFarLayer.FarRenderPriority < 10, "far render priority");
        Expect(failures, beam.Shader?.ResourcePath == EmitterGridFarLayer.BeamShaderPath && head.Shader?.ResourcePath == EmitterGridFarLayer.HeadShaderPath, "far shader paths");
        foreach (ShaderMaterial material in new[] { beam, head })
        {
            Expect(failures, Math.Abs((float)material.GetShaderParameter("world_scale") - scale) < 1e-9f, "far world_scale");
            Expect(failures, ((Vector3)material.GetShaderParameter("ring_center")) == Vector3.Zero, "far ring_center");
            Expect(failures, ((Vector4)material.GetShaderParameter("eg_visibility")).IsEqualApprox((Vector4)nearBeam.GetShaderParameter("eg_visibility")), "far eg_visibility");
        }
        Expect(failures, ((Vector4)beam.GetShaderParameter("beam_widths")).IsEqualApprox((Vector4)nearBeam.GetShaderParameter("beam_widths")), "far beam_widths");
        Expect(failures, ((Vector2)beam.GetShaderParameter("core_depth")).IsEqualApprox((Vector2)nearBeam.GetShaderParameter("core_depth")), "far core_depth");
        Expect(failures, beam.NextPass == null && layer.Settings.DepthWriteCore && nearBeam.NextPass != null, "far beam material has a core pass");
        Expect(failures, layer.FarLayerActive && far.Visible, "far layer not active with defaults");

        // Heads: far sprite positions equal A_s + f (B_s - A_s) for the same link and fraction as the near layer.
        EmitterGridPulseClock clock = layer.Clock!;
        double c = clock.CircumferenceMeters, tHead = 2_000.25;
        layer.UpdateFronts(tHead);
        layer.SyncFarHeads();
        Span<EmitterPulseFront> buffer = stackalloc EmitterPulseFront[32];
        int index = 0;
        for (int column = 0; column < topology.Columns; column++)
        {
            int n = clock.GetFronts(tHead, column, buffer);
            for (int i = 0; i < n; i++)
            {
                double q = buffer[i].HeadQ;
                if (q < 0d || q > c) continue;
                if (!layer.TryHeadLocation(column, q, out int link, out double f)) { failures.Add($"far head col {column} has no link"); continue; }
                Vector3 expected = far.GetScaledEndpointA(link) + (far.GetScaledEndpointB(link) - far.GetScaledEndpointA(link)) * (float)f;
                if (index >= far.HeadCount || far.GetHeadPosition(index).DistanceTo(expected) > 1e-5f) failures.Add($"far head {index} position");
                index++;
            }
        }
        Expect(failures, index == 6 && far.HeadCount == 6 && far.HeadInstances!.Multimesh.VisibleInstanceCount == 6, $"far head count {index}/{far.HeadCount}");

        layer.Configure(new GameplayEmitterGridSettings { FarLayerEnabled = false });
        Expect(failures, !far.Visible && !layer.FarLayerActive, "far layer visible with FarLayerEnabled=false");
        string status = layer.DescribeStatus();
        Expect(failures, status.Contains("far layer off") && status.Contains("core depth on") && status.Contains("handoff"), "status line: " + status);
        layer.Configure(new GameplayEmitterGridSettings());
        Expect(failures, far.Visible && layer.FarLayerActive, "far layer not restored");
        layer.SetUndergroundHidden(true);
        Expect(failures, !far.Visible, "far layer visible while underground-hidden");
        layer.SetUndergroundHidden(false);
        Expect(failures, far.Visible, "far layer hidden after underground restore");
    }

    static void CheckSources(List<string> failures)
    {
        // Spec U2: the relay grid never reads the climate registry.
        foreach (string path in new[] { "res://scripts/poi/Emitters/EmitterGridBeamLayer.cs", "res://scripts/Terrain/Sites/EmitterGridTopology.cs", "res://scripts/Terrain/Sites/EmitterGridPulseClock.cs",
            "res://materials/ring_emitter_grid_beam.gdshader", "res://materials/ring_emitter_grid_head.gdshader", "res://materials/ring_emitter_grid_pulse.gdshaderinc",
            "res://scripts/poi/Emitters/EmitterGridFarLayer.cs", EmitterGridBeamLayer.CoreShaderPath, EmitterGridFarLayer.BeamShaderPath, EmitterGridFarLayer.HeadShaderPath,
            "res://materials/ring_emitter_grid_layers.gdshaderinc", "res://materials/ring_emitter_grid_beam_common.gdshaderinc", "res://materials/ring_emitter_grid_head_common.gdshaderinc" })
        {
            string full = ProjectSettings.GlobalizePath(path);
            if (!File.Exists(full)) { failures.Add("missing source " + path); continue; }
            if (File.ReadAllText(full).Contains("ClimateMachine", StringComparison.OrdinalIgnoreCase)) failures.Add("grid source references the climate registry: " + path);
        }
        foreach (string path in new[] { EmitterGridBeamLayer.BeamShaderPath, EmitterGridBeamLayer.HeadShaderPath, EmitterGridBeamLayer.CoreShaderPath,
            EmitterGridFarLayer.BeamShaderPath, EmitterGridFarLayer.HeadShaderPath, "res://tools/shaders/emitter_grid_intensity_parity.gdshader" })
        {
            Shader? shader = GD.Load<Shader>(path);
            if (shader == null) { failures.Add("shader did not load " + path); continue; }
            var uniforms = shader.GetShaderUniformList();
            Console.WriteLine($"EMITTER_GRID_LAYER shader {path} uniforms={uniforms.Count}");
            // Headless the language compiler still runs: a syntax or include error leaves the uniform list empty.
            if (uniforms.Count == 0) failures.Add("shader did not compile " + path);
            else if (!uniforms.Any(u => (string)u.AsGodotDictionary()["name"] == "eg_visibility")) failures.Add("shader lacks the pulse include uniforms " + path);
            else if (path.Contains("_beam", StringComparison.Ordinal) && !path.Contains("parity", StringComparison.Ordinal))
                foreach (string required in new[] { "core_depth", "eg_handoff" })
                    if (!uniforms.Any(u => (string)u.AsGodotDictionary()["name"] == required)) failures.Add($"beam shader lacks {required}: {path}");
        }
    }
}
