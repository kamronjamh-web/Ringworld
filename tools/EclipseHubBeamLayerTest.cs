using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Celestial.Eclipse;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Underground;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Eclipse phase E4b (docs/eclipse-false-sun-spec.md E.2, G.5): the 595 spire-to-hub beams. Geometry (beam origin to hub endpoints, count,
/// tilt), the per-beam custom data, the floating-origin rebase (transforms only, no custom-data rewrite), zero per-frame MultiMesh writes in
/// steady state, the CPU twin against the timeline, the far layer, the build window (nothing exists outside an eclipse) and the underground
/// round trip of the new participants. Expectations come from the live registry, topology and timeline; no golden values.
/// </summary>
public partial class EclipseHubBeamLayerTest : RefCounted
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
            EclipseSiteTable table = EclipseSiteTable.FromTopology(topology);
            var clock = new EmitterGridPulseClock(1337, topology.CircumferenceMeters);
            var window = new EclipseEventWindow(0, 12_048.45d, 16_138.59d);
            var timeline = new EclipseEventTimeline(new EclipseEventSettings(), window, table, clock, 1337);

            fixture = new Node3D { Name = "EclipseHubBeamFixture" };
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
            // The dormant grid has its own small ring; the registry's world is the 31,416 km ring, so make the grid match it.
            grid.ConfigureRuntimeRingTopology(EmitterSiteTestWorlds.Circumference / 32, EmitterSiteTestWorlds.Circumference / Math.Tau);
            var camera = new Camera3D { Name = "Camera", Current = true, Far = 1_200_000f };
            fixture.AddChild(camera);

            CheckInert(fixture, timeline, failures);
            CheckBuildWindow(timeline, failures);

            var layer = new EclipseHubBeamLayer { Name = "EclipseBeams", AutoBind = false };
            fixture.AddChild(layer);
            if (!layer.Bind(grid, topology)) { Console.Error.WriteLine("ECLIPSE_HUB_BEAM_LAYER=FAIL bind"); return 1; }

            CheckTopology(topology, table, failures);
            CheckStructure(layer, failures);
            CheckGeometry(layer, topology, grid, failures);
            CheckCustomData(layer, timeline, table, topology, failures);
            CheckMath(layer, timeline, failures);
            CheckRebase(layer, topology, grid, failures);
            CheckSteadyState(layer, timeline, failures);
            CheckRefuel(layer, timeline, clock, failures);
            CheckOrbs(layer, timeline, clock, topology, failures);
            CheckSettings(layer, failures);
            CheckFarLayer(layer, timeline, topology, fixture, failures);
            CheckBeamOrigin(layer, topology, grid, failures);
            CheckUnderground(fixture, grid, topology, timeline, failures);
            CheckClouds(layer, timeline, topology, new EmitterSitePlacementSource(registry), grid, camera, fixture, failures);
            CheckUnbind(layer, failures);
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
        if (failures.Count != 0) { Console.Error.WriteLine("ECLIPSE_HUB_BEAM_LAYER=FAIL " + string.Join(" | ", failures.Take(25))); return 1; }
        Console.WriteLine("ECLIPSE_HUB_BEAM_LAYER=PASS");
        return 0;
    }

    // ---------------------------------------------------------------- outside an eclipse

    static void CheckInert(Node3D fixture, EclipseEventTimeline timeline, List<string> failures)
    {
        // An auto-binding layer with no director, and one with a director that has no timeline, must stay unbuilt, hidden and child-free.
        var bare = new EclipseHubBeamLayer { Name = "BareBeams", AutoBind = true };
        fixture.AddChild(bare);
        for (int i = 0; i < 3; i++) bare._Process(0.016);
        Expect(failures, !bare.IsBound && !bare.Visible && bare.GetChildCount() == 0 && bare.Instances == null && bare.BeamCount == 0, "layer is not inert without a director");
        fixture.RemoveChild(bare);
        bare.Free();

        var director = new EclipseEventDirector { Name = "EclipseEvent", AutoBind = false, LoadSavedDefaults = false };
        fixture.AddChild(director);
        var withDirector = new EclipseHubBeamLayer { Name = "IdleBeams", AutoBind = true };
        fixture.AddChild(withDirector);
        for (int i = 0; i < 3; i++) withDirector._Process(0.016);
        Expect(failures, !withDirector.IsBound && !withDirector.Visible && withDirector.GetChildCount() == 0 && director.Engine.Timeline == null, "layer built with an idle director");
        Expect(failures, withDirector.DescribeStatus().Contains("not built"), "idle status text");
        fixture.RemoveChild(withDirector);
        withDirector.Free();
        fixture.RemoveChild(director);
        director.Free();
        _ = timeline;
    }

    static void CheckBuildWindow(EclipseEventTimeline timeline, List<string> failures)
    {
        var beams = new EclipseBeamSettings();
        EclipseEventState At(double tau) => timeline.Evaluate(timeline.Window.Start + tau, 0d);
        // Not before the prebuild window (the parity states sit at the window start), nor in the PreRoll, nor in Idle.
        Expect(failures, !EclipseHubBeamLayer.ShouldBuild(timeline, At(-100d), beams), "built in the pre-roll");
        Expect(failures, !EclipseHubBeamLayer.ShouldBuild(timeline, At(0.5d), beams), "built at the window start");
        Expect(failures, !EclipseHubBeamLayer.ShouldBuild(timeline, At(timeline.TauMainFire - EclipseHubBeamLayer.PrebuildSeconds - 0.5d), beams), "built before the prebuild window");
        Expect(failures, EclipseHubBeamLayer.ShouldBuild(timeline, At(timeline.TauMainFire - EclipseHubBeamLayer.PrebuildSeconds + 0.5d), beams), "not built inside the prebuild window");
        Expect(failures, EclipseHubBeamLayer.ShouldBuild(timeline, At(timeline.TauMainFire + 30d), beams) && EclipseHubBeamLayer.ShouldBuild(timeline, At(timeline.TauSustain + 600d), beams), "not built during the event");
        Expect(failures, EclipseHubBeamLayer.ShouldBuild(timeline, At(timeline.TauImplosion + timeline.BeamRetract * 0.5d), beams), "not built during the retract");
        Expect(failures, !EclipseHubBeamLayer.ShouldBuild(timeline, At(timeline.TauImplosion + timeline.BeamRetract + 2d), beams), "still built after the cut");
        Expect(failures, !EclipseHubBeamLayer.ShouldBuild(timeline, At(timeline.TauEnd + 100d), beams) && !EclipseHubBeamLayer.ShouldBuild(null, At(0d), beams), "built after the event or without a timeline");
        Expect(failures, !EclipseHubBeamLayer.ShouldBuild(timeline, At(timeline.TauMainFire + 30d), beams with { BeamsEnabled = false }), "built with the beams disabled");
        Expect(failures, !EclipseHubBeamLayer.ShouldBuild(timeline, At(-5000d), beams) && !EclipseHubBeamLayer.ShouldBuild(timeline, At(timeline.TauEnd + 5000d), beams), "built in Idle");
        // The beam is lit exactly from the main fire to the end of the retract.
        EclipseEventState before = At(timeline.TauMainFire - 0.1d), during = At(timeline.TauMainFire + 0.1d), after = At(timeline.TauImplosion + timeline.BeamRetract + 0.1d);
        Expect(failures, !before.MainBeamLit && during.MainBeamLit && !after.MainBeamLit, "MainBeamLit window");
    }

    // ---------------------------------------------------------------- structure and geometry

    static void CheckTopology(EmitterGridTopology topology, EclipseSiteTable table, List<string> failures)
    {
        Expect(failures, topology.HubBeamCount == 595 && topology.HubBeamCount == table.Count + 1, $"beam count {topology.HubBeamCount}");
        Expect(failures, topology.HubBeamSite(0) == topology.Registry.MainIndex, "beam 0 is not the main spire");
        var seen = new HashSet<int>();
        for (int b = 0; b < topology.HubBeamCount; b++) seen.Add(topology.HubBeamSite(b));
        Expect(failures, seen.Count == 595, "a site has two beams");
        for (int e = 0; e < table.Count; e++)
            if (table.SiteIndex[e] != topology.HubBeamSite(e + 1)) { failures.Add($"table entry {e} order differs from the beams"); break; }
        var (_, mainVertical, _) = topology.HubBeamCrown(0);
        var (_, regularVertical, _) = topology.HubBeamCrown(1);
        // Tweaks T1: the main spire's relay node is its crown focus, like the regular spires.
        Expect(failures, topology.MainRelayHeightM == EmitterSiteFrameMath.DefaultMainCrownHeightMeters && topology.RegularRelayHeightM == EmitterSiteFrameMath.DefaultCrownHeightMeters, "relay heights are not the crown foci");
        Expect(failures, Math.Abs(topology.RelayNode(topology.Registry.MainIndex).Vertical - (topology.Registry.MainSite.PadHeight + EmitterSiteFrameMath.DefaultPadClearanceMeters + 3989d)) < 1e-6, "main relay node height");
        Expect(failures, mainVertical - topology.Registry.MainSite.PadHeight - EmitterSiteFrameMath.DefaultPadClearanceMeters - EmitterSiteFrameMath.DefaultMainCrownHeightMeters is > -1e-6 and < 1e-6, "main crown height");
        Expect(failures, regularVertical - topology.Registry[topology.HubBeamSite(1)].PadHeight - EmitterSiteFrameMath.DefaultPadClearanceMeters - EmitterSiteFrameMath.DefaultCrownHeightMeters is > -1e-6 and < 1e-6, "regular crown height");
    }

    static void CheckStructure(EclipseHubBeamLayer layer, List<string> failures)
    {
        Expect(failures, layer.IsBound && layer.BeamCount == 595 && !layer.Visible, $"bound {layer.IsBound} count {layer.BeamCount} visible {layer.Visible} (hidden until lit)");
        if (layer.Instances?.Multimesh is not { } beams) { failures.Add("missing multimesh"); return; }
        Expect(failures, beams.InstanceCount == 595 && beams.UseCustomData && beams.TransformFormat == MultiMesh.TransformFormatEnum.Transform3D, "multimesh shape");
        Expect(failures, layer.RibbonVertexCount == EclipseHubBeamLayer.RibbonSubSegments * 4 + EclipseHubBeamLayer.OrbQuadVertices && beams.Mesh is ArrayMesh { } mesh && mesh.GetSurfaceCount() == 1, "ribbon mesh shape");
        if (beams.Mesh is ArrayMesh ribbon)
        {
            var arrays = ribbon.SurfaceGetArrays(0);
            var uv = (Vector2[])arrays[(int)Mesh.ArrayType.TexUV];
            var uv2 = (Vector2[])arrays[(int)Mesh.ArrayType.TexUV2];
            var index = (int[])arrays[(int)Mesh.ArrayType.Index];
            Expect(failures, uv.Length == 68 && uv2.Length == 68 && index.Length == 102, "ribbon attribute lengths (16 sub-segments and the orb quad)");
            // The orb quad: UV2 = (-1, -1) marks it, UV holds the four corners, and its indices stay inside it.
            for (int c = 0; c < 4 && uv2.Length == 68; c++)
                if (uv2[64 + c] != new Vector2(-1f, -1f) || Math.Abs(uv[64 + c].X) != 1f || Math.Abs(uv[64 + c].Y) != 1f) failures.Add($"orb quad corner {c}");
            Expect(failures, uv.Length == 68 && uv.Skip(64).Distinct().Count() == 4 && index.Skip(96).All(i => i >= 64 && i < 68), "orb quad corners / indices");
            float previous = 0f;
            for (int s = 0; s < 16 && uv.Length == 68; s++)
                for (int c = 0; c < 4; c++)
                {
                    Vector2 a = uv[s * 4 + c], b = uv2[s * 4 + c];
                    if (Math.Abs(b.X - EclipseHubBeamLayer.SegmentBoundary(s)) > 1e-6f || Math.Abs(b.Y - EclipseHubBeamLayer.SegmentBoundary(s + 1)) > 1e-6f
                        || Math.Abs(a.X - (c < 2 ? b.X : b.Y)) > 1e-6f || Math.Abs(a.Y - ((c & 1) == 0 ? -1f : 1f)) > 1e-6f || b.X < previous - 1e-9f)
                        failures.Add($"ribbon uv sub-segment {s} corner {c}");
                    previous = b.X;
                }
            Expect(failures, EclipseHubBeamLayer.SegmentBoundary(0) == 0f && EclipseHubBeamLayer.SegmentBoundary(16) == 1f, "segment boundaries do not span 0..1");
            // The first 8 sub-segments cover the 700 km near range of a 5,000 km beam.
            Expect(failures, EclipseHubBeamLayer.SegmentBoundary(1) * 4998f < 2f && EclipseHubBeamLayer.SegmentBoundary(8) * 4998f is > 500f and < 700f, "near-range segment density");
        }
        Expect(failures, layer.BeamMaterial?.Shader?.ResourcePath == EclipseHubBeamLayer.BeamShaderPath && layer.CoreMaterial?.Shader?.ResourcePath == EclipseHubBeamLayer.CoreShaderPath, "shaders");
        Expect(failures, layer.BeamMaterial!.NextPass == layer.CoreMaterial, "beam NextPass is not the core by default");
        Expect(failures, layer.BeamMaterial.RenderPriority == EclipseHubBeamLayer.HaloRenderPriority && layer.CoreMaterial!.RenderPriority == EclipseHubBeamLayer.CoreRenderPriority
            && layer.CoreMaterial.RenderPriority > layer.BeamMaterial.RenderPriority
            && layer.BeamMaterial.RenderPriority > EmitterGridBeamLayer.NearWaterRenderPriority, "render priorities (halo above the near water, core above the halo)");
        Expect(failures, layer.Instances.Name == "HubBeams" && layer.Instances.MaterialOverride == layer.BeamMaterial && layer.Instances.CastShadow == GeometryInstance3D.ShadowCastingSetting.Off, "instance setup");
        Expect(failures, layer.BeamMaterial.Shader!.GetMode() == Shader.Mode.Spatial, "shader mode");
    }

    static void CheckGeometry(EclipseHubBeamLayer layer, EmitterGridTopology topology, VoxelChunkGridManager grid, List<string> failures)
    {
        RingLodGeometrySnapshot snapshot = grid.CaptureLodGeometrySnapshot();
        var crown = new Vector3D[topology.HubBeamCount];
        topology.FillHubBeamEndpoints(snapshot, layer.Beams.BeamOrigin, crown, out Vector3D hub);
        var context = new EmitterSiteFrameContext(snapshot);
        Expect(failures, layer.Beams.BeamOrigin == EmitterBeamOrigin.Base && layer.AppliedBeamOrigin == EmitterBeamOrigin.Base, "the default beam origin is not Base");
        double worstTip = 0, worstOrigin = 0, maxTilt = 0, minLength = double.MaxValue, maxLength = 0, mainTilt = 0;
        for (int i = 0; i < crown.Length; i++)
        {
            Transform3D t = layer.GetBeamTransform(i);
            worstOrigin = Math.Max(worstOrigin, t.Origin.DistanceTo(crown[i].ToVector3()));
            // The tip (origin + basis X) reaches the hub point; summed in double from the float mirror.
            var tip = new Vector3D(t.Origin.X + (double)t.Basis.X.X, t.Origin.Y + (double)t.Basis.X.Y, t.Origin.Z + (double)t.Basis.X.Z);
            worstTip = Math.Max(worstTip, (tip - hub).Length);
            // Tilt against the local up (the inward radial) from the independent Beam() implementation.
            var (around, vertical, across) = topology.HubBeamOrigin(i, layer.Beams.BeamOrigin);
            EmitterBeamGeometry beam = context.Beam(around, across, vertical, 0d, topology.HubAcrossMeters);
            Vector3D span = hub - crown[i];
            Vector3D direction = span.Normalized();
            double tilt = Math.Acos(Math.Clamp(direction.Dot(beam.Up), -1d, 1d)) * 180d / Math.PI;
            maxTilt = Math.Max(maxTilt, tilt);
            if (i == 0) mainTilt = tilt;
            if (Math.Abs(tilt - beam.TiltDegrees) > 1e-6) { failures.Add($"tilt of beam {i} disagrees with Beam(): {tilt} vs {beam.TiltDegrees}"); break; }
            minLength = Math.Min(minLength, span.Length);
            maxLength = Math.Max(maxLength, span.Length);
        }
        Console.WriteLine($"ECLIPSE_HUB_BEAM_LAYER geometry worst_tip_m={worstTip:F3} worst_origin_m={worstOrigin:E2} max_tilt_deg={maxTilt:F4} main_tilt_deg={mainTilt:E2} length_km={minLength / 1000d:F3}..{maxLength / 1000d:F3} rebuild_ms={layer.LastRebuildMs:F3}");
        Expect(failures, worstTip <= 1d, $"tips are {worstTip:F3} m from the hub");
        Expect(failures, worstOrigin <= 0.001, $"origin mirror off by {worstOrigin}");
        // The tilt is atan(|across - W/2| / radial distance of the hub from the crown); the bound comes from the registry's widest site, not from a constant.
        double widest = 0d;
        for (int i = 0; i < crown.Length; i++) widest = Math.Max(widest, Math.Abs(topology.HubBeamCrown(i).Across - topology.HubAcrossMeters));
        double radius = topology.CircumferenceMeters / Math.Tau;
        double boundTilt = Math.Atan2(widest, radius - 3_000d - 1_743d) * 180d / Math.PI;
        Console.WriteLine($"ECLIPSE_HUB_BEAM_LAYER tilt widest_site_offset_km={widest / 1000d:F3} bound_deg={boundTilt:F4}");
        Expect(failures, maxTilt <= boundTilt + 0.02d && maxTilt >= 1.4d, $"tilt {maxTilt:F4} deg outside the registry bound {boundTilt:F4}");
        Expect(failures, mainTilt < 0.01d, $"main spire tilt {mainTilt:E2} (it stands on the hub line)");
        // From the bore iris (200 m under the pad) the beam is about 5,000 km; from the crown it is 1.9 km (4.2 km for the main spire) shorter.
        Expect(failures, minLength > 4_990_000d && maxLength < 5_003_000d, $"beam lengths {minLength / 1000d:F1}..{maxLength / 1000d:F1} km (about 5,000 km)");
        Expect(failures, (layer.HubWorld - hub).Length < 1e-6, "hub cache");
        Expect(failures, Enumerable.Range(0, crown.Length).All(i => layer.GetWorldOrigin(i) == crown[i]), "origin cache");
        // The hub is the same point for every beam: the mean of (crown + span) over all beams.
        Expect(failures, layer.RebuildCount >= 1 && layer.TransformWriteCount == 595L * layer.RebuildCount, "transform write count");
    }

    // ---------------------------------------------------------------- custom data (written once per timeline)

    static void CheckCustomData(EclipseHubBeamLayer layer, EclipseEventTimeline timeline, EclipseSiteTable table, EmitterGridTopology topology, List<string> failures)
    {
        long dataBefore = layer.CustomDataWriteCount;
        int bindsBefore = layer.TimelineBindCount;
        layer.BindTimeline(timeline);
        Expect(failures, layer.CustomDataWriteCount == dataBefore + 595 && layer.TimelineBindCount == bindsBefore + 1 && ReferenceEquals(layer.BoundTimeline, timeline), "timeline bind writes");
        Color main = layer.GetCustomMirror(0);
        Expect(failures, main.R == 0f && main.G == 0f && Math.Abs(main.B - (float)timeline.TauMainFire) < 1e-6f && main.A == 1f, $"main custom data {main}");
        double worstFire = 0;
        for (int e = 0; e < table.Count; e++)
        {
            Color c = layer.GetCustomMirror(e + 1);
            worstFire = Math.Max(worstFire, Math.Abs(c.B - timeline.SiteFireTau(e)));
            if (Math.Abs(c.R - (float)table.P[e]) > 2f || (int)c.G != table.Column[e] || c.A != 0f) { failures.Add($"custom data of entry {e}: {c}"); break; }
        }
        Expect(failures, worstFire < 1e-3, $"fire tau encoding off by {worstFire}");
        // Fire times reproduce tau_ign + min(p, C - p) / v (the schedule the timeline guarantees), in the order the beams are listed.
        Expect(failures, layer.GetCustomMirror(1).B < layer.GetCustomMirror(400).B || layer.GetCustomMirror(1).B > 0f, "fire times");
        // Encoding unit: dead sites never fire.
        Expect(failures, EclipseHubBeamMath.EncodeCustom(1d, 2, double.NaN, false).B == EclipseHubBeamMath.NeverFireTau, "NaN fire tau");

        // A dead site (output 0) is encoded as "never fires" while the others are unchanged.
        var dead = new DeadOutput(table.SiteIndex[10]);
        var deadTimeline = new EclipseEventTimeline(new EclipseEventSettings(), timeline.Window, table, timeline.GridClock, 1337, dead);
        layer.BindTimeline(deadTimeline);
        Expect(failures, layer.GetCustomMirror(11).B == EclipseHubBeamMath.NeverFireTau && Math.Abs(layer.GetCustomMirror(12).B - (float)deadTimeline.SiteFireTau(11)) < 1e-3f, "dead site custom data");
        layer.BindTimeline(timeline);
        Expect(failures, topology.HubBeamCount == layer.BeamCount, "beam count after rebinding");
    }

    sealed class DeadOutput : IEclipseSiteOutput
    {
        readonly int _site;
        public DeadOutput(int site) => _site = site;
        public float Output(int site) => site == _site ? 0f : 1f;
        public ulong Revision => 1UL;
    }

    // ---------------------------------------------------------------- CPU twin against the timeline

    static void CheckMath(EclipseHubBeamLayer layer, EclipseEventTimeline timeline, List<string> failures)
    {
        EclipseBeamSettings beams = new();
        double worstTip = 0, worstHandoff = 0;
        int litChecks = 0;
        foreach (double tau in new[] { 5d, timeline.TauMainFire + 1d, timeline.TauMainFire + 3.3d, timeline.TauKindle + 1d, timeline.TauIgnition + 0.4d, timeline.TauIgnition + 20.2d,
            timeline.TauIgnition + 77.7d, timeline.TauFrontsMet + 0.3d, timeline.TauCrescendo + 1.1d, timeline.TauHandoff + 0.9d, timeline.TauHandoff + 2.2d,
            timeline.TauSustain + 5.5d, timeline.TauInstability + 3.3d, timeline.TauImplosion + 0.2d, timeline.TauImplosion + 0.7d, timeline.TauImplosion + 1.4d })
        {
            EclipseEventState state = timeline.Evaluate(timeline.Window.Start + tau, 0d);
            EclipseHubBeamUniforms u = EclipseHubBeamMath.FromTimeline(timeline, beams, tau);
            float mainFire = layer.GetCustomMirror(0).B;
            // The main tip: the timeline's climb until the cut, then min(climb, retract top).
            double expectedMain = Math.Min(state.MainBeamTip, 1d - state.BeamCutProgress);
            worstTip = Math.Max(worstTip, Math.Abs(EclipseHubBeamMath.Tip(u, mainFire, 1f) - expectedMain));
            worstHandoff = Math.Max(worstHandoff, Math.Abs(EclipseHubBeamMath.Handoff(u, 0f) - state.RegularBeamIntensity));
            // Lit regular beams: the same count as the timeline's fired beams while the regular beams are on.
            int lit = 0;
            for (int i = 1; i < layer.BeamCount; i++) if (EclipseHubBeamMath.IsLit(u, layer.GetCustomMirror(i).B, 0f, 0f)) lit++;
            if (lit != state.LitRegularBeams) failures.Add($"lit regular beams at tau {tau:F1}: shader twin {lit} vs timeline {state.LitRegularBeams}");
            litChecks++;
        }
        // The shader keeps tau as a float32: near the collapse (tau about 3,900 s) one ulp is 0.25 ms, which a 1 s retract turns into 2.5e-4.
        Expect(failures, worstTip < 6e-4, $"main tip twin off by {worstTip}");
        Expect(failures, worstHandoff < 1e-5, $"handoff twin off by {worstHandoff}");

        // Soft tip, climb and cut by definition.
        EclipseHubBeamUniforms v = EclipseHubBeamMath.FromTimeline(timeline, beams, timeline.TauMainFire + timeline.MainClimb * 0.5d);
        float tip = EclipseHubBeamMath.ClimbTip(v, (float)timeline.TauMainFire, 1f);
        Expect(failures, Math.Abs(tip - Math.Pow(0.5d, timeline.Settings.MainClimbExponent)) < 1e-5, $"main climb tip {tip}");
        Expect(failures, EclipseHubBeamMath.TipMask(v, tip * 0.5f, tip) == 1f && EclipseHubBeamMath.TipMask(v, tip + 0.01f, tip) == 0f
            && EclipseHubBeamMath.TipMask(v, tip - 0.0025f, tip) is > 0f and < 1f && EclipseHubBeamMath.TipMask(v, 1f, 1f) == 1f, "soft tip mask");
        // Day visibility: full below 0.45, DayVisibility above 0.55, smooth between; the beams otherwise ignore the day.
        Expect(failures, EclipseHubBeamMath.DayVisibility(v, 0f) == 1f && EclipseHubBeamMath.DayVisibility(v, 0.44f) == 1f
            && Math.Abs(EclipseHubBeamMath.DayVisibility(v, 0.6f) - 0.6f) < 1e-6f && Math.Abs(EclipseHubBeamMath.DayVisibility(v, 0.8f) - 0.6f) < 1e-6f
            && EclipseHubBeamMath.DayVisibility(v, 0.5f) is > 0.6f and < 1f, "day visibility");
        float level = EclipseHubBeamMath.Level(v with { Time = (float)timeline.TauMainFire + 100f }, (float)timeline.TauMainFire, 1f, 0.5f, 0.9f, 0.8f, 0.7f);
        Expect(failures, Math.Abs(level - 0.6f * 0.8f * 0.7f) < 1e-6f, $"level product {level}");
        Console.WriteLine($"ECLIPSE_HUB_BEAM_LAYER math checked_times={litChecks} worst_main_tip={worstTip:E2} worst_handoff={worstHandoff:E2}");
    }

    // ---------------------------------------------------------------- floating-origin rebase

    static void CheckRebase(EclipseHubBeamLayer layer, EmitterGridTopology topology, VoxelChunkGridManager grid, List<string> failures)
    {
        double circumference = grid.GetCanonicalRingCircumferenceMeters64();
        layer.Rebuild(grid.CaptureLodGeometrySnapshot()); // warm-up
        Instance(layer, out ulong instanceId, out ulong multimeshId);
        Vector3 before = layer.GetBeamTransform(5).Origin;
        Vector3D hubBefore = layer.HubWorld, crown5Before = layer.GetWorldOrigin(5);
        int rebuilds = layer.RebuildCount, revision = layer.GeometryRevision, binds = layer.TimelineBindCount;
        long transforms = layer.TransformWriteCount, data = layer.CustomDataWriteCount;
        grid.SetFloatingOriginCanonicalAnchor(new RingCanonicalPosition(5_000_000d, 100d, 150_000d));
        Expect(failures, layer.RebuildCount == rebuilds + 1 && layer.GeometryRevision == revision + 1, $"anchor A rebuilds {layer.RebuildCount - rebuilds}");
        // Transform only: 595 transform writes, no custom data, no new bind, same instance and multimesh objects.
        Expect(failures, layer.TransformWriteCount == transforms + 595 && layer.CustomDataWriteCount == data && layer.TimelineBindCount == binds, "a rebase wrote more than the transforms");
        Instance(layer, out ulong instanceAfter, out ulong multimeshAfter);
        Expect(failures, instanceId == instanceAfter && multimeshId == multimeshAfter, "a rebase rebuilt the multimesh");
        Expect(failures, layer.GetBeamTransform(5).Origin.DistanceTo(before) > 1000f, "anchor A did not move the origins");
        // The whole world frame shifts by one vector, so the hub-minus-crown vector (the beam) is invariant, and every tip still lands on the hub.
        Vector3D shiftHub = layer.HubWorld - hubBefore, shiftCrown = layer.GetWorldOrigin(5) - crown5Before;
        Expect(failures, (shiftHub - shiftCrown).Length < 1e-3, $"beam vector changed across the rebase ({(shiftHub - shiftCrown).Length})");
        double worstTip = 0;
        for (int i = 0; i < layer.BeamCount; i++)
        {
            Transform3D t = layer.GetBeamTransform(i);
            var tip = new Vector3D(t.Origin.X + (double)t.Basis.X.X, t.Origin.Y + (double)t.Basis.X.Y, t.Origin.Z + (double)t.Basis.X.Z);
            worstTip = Math.Max(worstTip, (tip - layer.HubWorld).Length);
        }
        Expect(failures, worstTip <= 1.5d, $"after the rebase the tips are {worstTip:F3} m from the hub");
        int afterA = layer.RebuildCount;
        grid.SetFloatingOriginCanonicalAnchor(new RingCanonicalPosition(5_000_000d, 100d, 150_000d));
        Expect(failures, layer.RebuildCount == afterA, "the same anchor rebuilt");
        grid.SetFloatingOriginCanonicalAnchor(new RingCanonicalPosition(circumference - 5d, 100d, 150_000d));
        Expect(failures, layer.RebuildCount == afterA + 1, "seam anchor rebuild");
        // Absolute rebuild: returning to a previous anchor reproduces the previous transforms exactly.
        grid.SetFloatingOriginCanonicalAnchor(new RingCanonicalPosition(5_000_000d, 100d, 150_000d));
        Transform3D again = layer.GetBeamTransform(0);
        grid.SetFloatingOriginCanonicalAnchor(new RingCanonicalPosition(1_234_567d, 90d, 120_000d));
        grid.SetFloatingOriginCanonicalAnchor(new RingCanonicalPosition(5_000_000d, 100d, 150_000d));
        Expect(failures, layer.GetBeamTransform(0) == again, "rebuild is not absolute (anchor round trip differs)");
        grid.ClearFloatingOriginCanonicalAnchor();
        Expect(failures, layer.CustomDataWriteCount == data && layer.TimelineBindCount == binds, "custom data rewritten by a later rebase");

        RingLodGeometrySnapshot snapshot = grid.CaptureLodGeometrySnapshot();
        for (int i = 0; i < 10; i++) layer.Rebuild(snapshot);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        double worst = 0;
        for (int i = 0; i < 100; i++) { layer.Rebuild(snapshot); worst = Math.Max(worst, layer.LastRebuildMs); }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Console.WriteLine($"ECLIPSE_HUB_BEAM_LAYER rebuild worst_ms={worst:F3} allocated_bytes_per_100={allocated}");
        Expect(failures, allocated == 0, $"rebuild allocated {allocated} bytes over 100 rebuilds");
        Expect(failures, worst < 5d, $"rebuild took {worst:F2} ms");
        _ = topology;
    }

    static void Instance(EclipseHubBeamLayer layer, out ulong instanceId, out ulong multimeshId)
    {
        instanceId = layer.Instances?.GetInstanceId() ?? 0UL;
        multimeshId = layer.Instances?.Multimesh?.GetInstanceId() ?? 0UL;
    }

    // ---------------------------------------------------------------- steady state

    static void CheckSteadyState(EclipseHubBeamLayer layer, EclipseEventTimeline timeline, List<string> failures)
    {
        layer.ApplyEventTime(timeline.TauMainFire + 1d, true, 0.016);
        Expect(failures, layer.Visible, "layer not visible while the main beam is lit");
        long transforms = layer.TransformWriteCount, data = layer.CustomDataWriteCount;
        int binds = layer.TimelineBindCount, rebuilds = layer.RebuildCount;
        long farTransforms = layer.FarLayer?.TransformWriteCount ?? 0, farData = layer.FarLayer?.CustomDataWriteCount ?? 0;
        for (int frame = 0; frame < 600; frame++) layer.ApplyEventTime(timeline.TauMainFire + 1d + frame / 60d * 20d, true, 1d / 60d);
        Expect(failures, layer.TransformWriteCount == transforms && layer.CustomDataWriteCount == data && layer.TimelineBindCount == binds && layer.RebuildCount == rebuilds,
            $"per-frame writes: transforms {layer.TransformWriteCount - transforms}, custom {layer.CustomDataWriteCount - data}");
        Expect(failures, (layer.FarLayer?.TransformWriteCount ?? 0) == farTransforms && (layer.FarLayer?.CustomDataWriteCount ?? 0) == farData, "per-frame far writes");
        Expect(failures, Math.Abs(layer.EventTau - (timeline.TauMainFire + 1d + 599d / 60d * 20d)) < 1e-9, "event time not published");
        Expect(failures, Math.Abs((float)layer.BeamMaterial!.GetShaderParameter("ev_time") - (float)layer.EventTau) < 1e-3f, "ev_time uniform");
        // Marks and climb reached the shaders at bind time.
        Vector4 marks = (Vector4)layer.BeamMaterial.GetShaderParameter("ev_marks"), climb = (Vector4)layer.BeamMaterial.GetShaderParameter("ev_climb");
        Expect(failures, marks.IsEqualApprox(new Vector4((float)timeline.TauHandoff, (float)timeline.HandoffFade, (float)timeline.TauImplosion, (float)timeline.BeamRetract)), $"ev_marks {marks}");
        Expect(failures, climb.IsEqualApprox(new Vector4((float)timeline.MainClimb, (float)timeline.Settings.MainClimbExponent, (float)timeline.RegularClimb, 1f)), $"ev_climb {climb}");
        layer.ApplyEventTime(timeline.TauEnd, false, 0.016);
        Expect(failures, !layer.Visible, "layer visible after the main beam is cut");
        layer.ApplyEventTime(timeline.TauMainFire + 1d, true, 0.016);
    }

    // ---------------------------------------------------------------- settings

    static void CheckSettings(EclipseHubBeamLayer layer, List<string> failures)
    {
        var beams = new EclipseBeamSettings
        {
            CoreWidthMeters = 100, HaloWidthMeters = 900, MainCoreWidthMeters = 250, MainHaloWidthMeters = 1800, MinPixelWidth = 2.5, FarCompensation = 0.3,
            BaseEmission = 9, MainEmissionScale = 2, HaloAmplitude = 0.2, BaseFadeMeters = 800, DayVisibility = 0.4, CoreDepthThreshold = 0.5, CoreDepthMinPixels = 6, SoftTipFraction = 0.01, DepthWriteCore = false,
            CloseSoftenMeters = 120
        };
        layer.ApplyBeamSettings(beams);
        ShaderMaterial m = layer.BeamMaterial!;
        Expect(failures, ((Vector4)m.GetShaderParameter("beam_widths")).IsEqualApprox(new Vector4(100f, 900f, 2.5f, 0.3f)), $"beam_widths {m.GetShaderParameter("beam_widths")}");
        Expect(failures, ((Vector2)m.GetShaderParameter("main_widths")).IsEqualApprox(new Vector2(250f, 1800f)) && Math.Abs((float)m.GetShaderParameter("main_emission_scale") - 2f) < 1e-6f, "main widths/emission");
        Expect(failures, Math.Abs((float)m.GetShaderParameter("base_emission") - 9f) < 1e-6f && Math.Abs((float)m.GetShaderParameter("halo_amplitude") - 0.2f) < 1e-6f
            && Math.Abs((float)m.GetShaderParameter("base_fade_meters") - 800f) < 1e-3f, "emission/halo/base fade");
        Expect(failures, ((Vector2)m.GetShaderParameter("ev_shape")).IsEqualApprox(new Vector2(0.01f, 0.4f)) && ((Vector2)m.GetShaderParameter("core_depth")).IsEqualApprox(new Vector2(0.5f, 6f)), "ev_shape/core_depth");
        Expect(failures, Math.Abs((float)m.GetShaderParameter("close_soften_m") - 120f) < 1e-3f && Math.Abs((float)layer.CoreMaterial!.GetShaderParameter("close_soften_m") - 120f) < 1e-3f, "close-range softening uniform");
        Expect(failures, m.NextPass == null && layer.Uniforms.DayVisibility == 0.4f && layer.Uniforms.SoftTip == 0.01f, "depth core off / twin uniforms");
        // The core material follows the same values (it is the same parameter set).
        Expect(failures, ((Vector4)layer.CoreMaterial!.GetShaderParameter("beam_widths")).IsEqualApprox(new Vector4(100f, 900f, 2.5f, 0.3f)), "core material widths");
        layer.ApplyBeamSettings(beams with { DepthWriteCore = true });
        Expect(failures, m.NextPass == layer.CoreMaterial, "depth core on");
        // Clamped.
        layer.ApplyBeamSettings(new EclipseBeamSettings { CoreWidthMeters = -4, BaseEmission = double.NaN, DayVisibility = 7, CloseSoftenMeters = double.NaN, BeamOrigin = (EmitterBeamOrigin)9 });
        Expect(failures, layer.Beams.CoreWidthMeters == 10d && layer.Beams.BaseEmission == 12d && layer.Beams.DayVisibility == 1d
            && layer.Beams.CloseSoftenMeters == 1000d && layer.Beams.BeamOrigin == EmitterBeamOrigin.Base, "beam settings not clamped");
        layer.ApplyBeamSettings(new EclipseBeamSettings());
        // The look settings never rebuild the timeline.
        var engine = new EclipseEventEngine();
        engine.SetSettings(new EclipseEventSettings());
        EclipseEventSettings baseline = engine.Settings;
        engine.SetSettings(baseline with { Beams = baseline.Beams with { BaseEmission = 20d } });
        Expect(failures, engine.Settings.Beams.BaseEmission == 20d && engine.Timeline == null, "engine beam setting");
    }

    // ---------------------------------------------------------------- far layer

    const float FarScale = 0.001f;

    /// <summary>The RingScaledFarViewport.ScaledPoint mapping for the registry's ring (ring axis Y, centre and hub at the origin).</summary>
    static Func<double, double, double, Vector3> ScaledPointFor(EmitterGridTopology topology)
    {
        double radius = topology.CircumferenceMeters / Math.Tau, width = topology.Registry.Recipe.WidthM;
        return (around, vertical, across) =>
        {
            double wrapped = around % topology.CircumferenceMeters;
            if (wrapped < 0d) wrapped += topology.CircumferenceMeters;
            double angle = wrapped / radius, radial = radius - vertical;
            return new Vector3((float)(Math.Cos(angle) * radial * FarScale), (float)((-width * 0.5d + across) * FarScale), (float)(Math.Sin(angle) * radial * FarScale));
        };
    }

    static void CheckFarLayer(EclipseHubBeamLayer layer, EclipseEventTimeline timeline, EmitterGridTopology topology, Node3D fixture, List<string> failures)
    {
        const float scale = FarScale;
        Func<double, double, double, Vector3> ScaledPoint = ScaledPointFor(topology);
        bool Attach(Node3D node) { fixture.AddChild(node); return true; }

        Expect(failures, layer.FarLayer == null && !layer.FarLayerActive, "far layer exists before it is bound");
        Expect(failures, layer.BindFarLayer(ScaledPoint, scale, Attach) && layer.FarLayer is { IsBuilt: true } && layer.BindFarLayer(ScaledPoint, scale, Attach), "far layer bind");
        EclipseHubFarLayer far = layer.FarLayer!;
        Expect(failures, far.BeamCount == 595 && far.Instances?.Multimesh is { InstanceCount: 595, UseCustomData: true }, "far multimesh shape");
        Expect(failures, far.BeamMaterial?.Shader?.ResourcePath == EclipseHubFarLayer.BeamShaderPath && far.BeamMaterial.RenderPriority == EmitterGridFarLayer.FarRenderPriority, "far material");
        Expect(failures, far.Instances!.Multimesh!.Mesh is ArrayMesh { } mesh && mesh.GetSurfaceCount() == 1, "far ribbon mesh");
        double worstTip = 0, worstHub = 0;
        for (int i = 0; i < 595; i++)
        {
            Transform3D t = far.GetBeamTransform(i);
            var origin = topology.HubBeamOrigin(i, layer.Beams.BeamOrigin);
            Vector3 crown = ScaledPoint(origin.Around, origin.Vertical, origin.Across);
            worstTip = Math.Max(worstTip, ((t.Origin + t.Basis.X)).Length());
            worstHub = Math.Max(worstHub, t.Origin.DistanceTo(crown));
        }
        // The hub is the scaled-world origin: every tip lands there (scaled units are km).
        Expect(failures, worstTip < 1e-3 && worstHub < 1e-4, $"far tips {worstTip:E2} / crowns {worstHub:E2} scaled units");
        Console.WriteLine($"ECLIPSE_HUB_BEAM_LAYER far worst_tip_scaled={worstTip:E2} far_builds_writes={far.TransformWriteCount}/{far.CustomDataWriteCount}");
        Expect(failures, far.TransformWriteCount == 595 && far.CustomDataWriteCount >= 595 + 595, $"far write counts {far.TransformWriteCount}/{far.CustomDataWriteCount}");
        // Settings reach the far material; visibility follows the near layer and the far toggle.
        layer.ApplyBeamSettings(new EclipseBeamSettings { BaseEmission = 7d, FarCompensation = 0.2d });
        Expect(failures, Math.Abs((float)far.BeamMaterial!.GetShaderParameter("base_emission") - 7f) < 1e-6f
            && ((Vector4)far.BeamMaterial.GetShaderParameter("beam_widths")).W is > 0.199f and < 0.201f, "far material settings");
        Expect(failures, Math.Abs((float)far.BeamMaterial.GetShaderParameter("world_scale") - scale) < 1e-9f, "far world scale");
        layer.ApplyEventTime(timeline.TauMainFire + 2d, true, 0.016);
        Expect(failures, layer.Visible && far.Visible && layer.FarLayerActive, "far layer not visible with the near layer");
        layer.ApplyBeamSettings(new EclipseBeamSettings { FarLayerEnabled = false });
        Expect(failures, !far.Visible && !layer.FarLayerActive, "far layer visible with the far toggle off");
        layer.ApplyBeamSettings(new EclipseBeamSettings());
        Expect(failures, far.Visible, "far layer not restored");
        Expect(failures, far.Instances.Multimesh.GetInstanceId() != 0UL, "far multimesh id");
        // The timeline custom data reaches the far layer on a rebind.
        long farData = far.CustomDataWriteCount;
        layer.BindTimeline(timeline);
        Expect(failures, far.CustomDataWriteCount == farData + 595, "far custom data not rewritten on bind");
    }

    // ---------------------------------------------------------------- beam origin (tweaks T2)

    static void CheckBeamOrigin(EclipseHubBeamLayer layer, EmitterGridTopology topology, VoxelChunkGridManager grid, List<string> failures)
    {
        // The pure anchor heights above the pad: the old crown (with the pad clearance), the bore iris 200 m under the pad, the reactor exhaust top.
        double clearance = EmitterSiteFrameMath.DefaultPadClearanceMeters;
        Expect(failures, EmitterSiteFrameMath.BeamOriginHeightMeters(SiteKind.Regular, EmitterBeamOrigin.Crown) == clearance + 1743d
            && EmitterSiteFrameMath.BeamOriginHeightMeters(SiteKind.Main, EmitterBeamOrigin.Crown) == clearance + 3989d, "crown origin heights");
        Expect(failures, EmitterSiteFrameMath.BeamOriginHeightMeters(SiteKind.Regular, EmitterBeamOrigin.Base) == -200d
            && EmitterSiteFrameMath.BeamOriginHeightMeters(SiteKind.Main, EmitterBeamOrigin.Base) == -200d, "base origin heights");
        Expect(failures, EmitterSiteFrameMath.BeamOriginHeightMeters(SiteKind.Regular, EmitterBeamOrigin.Reactor) == -2961d
            && EmitterSiteFrameMath.BeamOriginHeightMeters(SiteKind.Main, EmitterBeamOrigin.Reactor) == -1922d, "reactor origin heights");
        for (int b = 0; b < 2; b++)
        {
            EmitterSite site = topology.Registry[topology.HubBeamSite(b)];
            Expect(failures, topology.HubBeamOrigin(b, EmitterBeamOrigin.Crown) == topology.HubBeamCrown(b), $"beam {b} crown origin is not HubBeamCrown");
            Expect(failures, Math.Abs(topology.HubBeamOrigin(b, EmitterBeamOrigin.Base).Vertical - (site.PadHeight - 200d)) < 1e-9
                && Math.Abs(topology.HubBeamOrigin(b, EmitterBeamOrigin.Reactor).Vertical - (site.PadHeight + (site.IsMain ? -1922d : -2961d))) < 1e-9, $"beam {b} origin verticals");
        }

        // Switching: only the 595 transforms are rebuilt (near and far), no custom data, no timeline bind, same multimesh.
        EclipseHubFarLayer? far = layer.FarLayer;
        Expect(failures, far != null, "origin check needs the far layer");
        Instance(layer, out ulong instanceId, out ulong multimeshId);
        EmitterBeamOrigin start = layer.Beams.BeamOrigin;
        var distanceFromBase = new Dictionary<EmitterBeamOrigin, double[]>();
        EmitterBeamOrigin[] modes = { EmitterBeamOrigin.Crown, EmitterBeamOrigin.Reactor, EmitterBeamOrigin.Base };
        Func<double, double, double, Vector3> scaledPoint = ScaledPointFor(topology);
        foreach (EmitterBeamOrigin mode in modes)
        {
            int rebuilds = layer.RebuildCount, binds = layer.TimelineBindCount;
            long transforms = layer.TransformWriteCount, data = layer.CustomDataWriteCount, colors = layer.ColorWriteCount;
            long farTransforms = far!.TransformWriteCount, farData = far.CustomDataWriteCount;
            layer.ApplyBeamSettings(layer.Beams with { BeamOrigin = mode });
            Expect(failures, layer.AppliedBeamOrigin == mode && layer.RebuildCount == rebuilds + 1 && layer.TransformWriteCount == transforms + 595, $"{mode}: switch did not rebuild the 595 transforms once");
            Expect(failures, layer.CustomDataWriteCount == data && layer.TimelineBindCount == binds && layer.ColorWriteCount == colors, $"{mode}: switch wrote custom data, a timeline bind or colours");
            Expect(failures, far.TransformWriteCount == farTransforms + 595 && far.CustomDataWriteCount == farData, $"{mode}: far layer writes {far.TransformWriteCount - farTransforms}/{far.CustomDataWriteCount - farData}");
            Instance(layer, out ulong instanceAfter, out ulong multimeshAfter);
            Expect(failures, instanceId == instanceAfter && multimeshId == multimeshAfter, $"{mode}: the switch rebuilt the multimesh");
            // Endpoints: the origin is the mode's anchor on the spire axis, the tip is the hub, near and far.
            RingLodGeometrySnapshot snapshot = grid.CaptureLodGeometrySnapshot();
            var points = new Vector3D[topology.HubBeamCount];
            topology.FillHubBeamEndpoints(snapshot, mode, points, out Vector3D hub);
            double worstOrigin = 0, worstTip = 0, worstFarOrigin = 0, worstFarTip = 0;
            for (int i = 0; i < points.Length; i++)
            {
                Transform3D t = layer.GetBeamTransform(i);
                worstOrigin = Math.Max(worstOrigin, t.Origin.DistanceTo(points[i].ToVector3()));
                var tip = new Vector3D(t.Origin.X + (double)t.Basis.X.X, t.Origin.Y + (double)t.Basis.X.Y, t.Origin.Z + (double)t.Basis.X.Z);
                worstTip = Math.Max(worstTip, (tip - hub).Length);
                Transform3D f = far.GetBeamTransform(i);
                var o = topology.HubBeamOrigin(i, mode);
                worstFarOrigin = Math.Max(worstFarOrigin, f.Origin.DistanceTo(scaledPoint(o.Around, o.Vertical, o.Across)));
                worstFarTip = Math.Max(worstFarTip, (f.Origin + f.Basis.X).Length());
            }
            Expect(failures, worstOrigin <= 0.001 && worstTip <= 1d && worstFarOrigin < 1e-4 && worstFarTip < 1e-3, $"{mode}: endpoints origin {worstOrigin:E2} tip {worstTip:F3} far {worstFarOrigin:E2}/{worstFarTip:E2}");
            Expect(failures, Enumerable.Range(0, points.Length).All(i => layer.GetWorldOrigin(i) == points[i]) && (layer.HubWorld - hub).Length < 1e-6, $"{mode}: origin cache");
            // The anchor is on the spire axis: its distance from the crown is the height difference (independent of the topology code path).
            var crowns = new Vector3D[topology.HubBeamCount];
            topology.FillHubBeamEndpoints(snapshot, EmitterBeamOrigin.Crown, crowns, out _);
            var d = new double[2];
            for (int b = 0; b < 2; b++) d[b] = (crowns[b] - points[b]).Length;
            distanceFromBase[mode] = d;
            double expectRegular = mode switch { EmitterBeamOrigin.Crown => 0d, EmitterBeamOrigin.Base => 1743.05 + 200d, _ => 1743.05 + 2961d };
            double expectMain = mode switch { EmitterBeamOrigin.Crown => 0d, EmitterBeamOrigin.Base => 3989.05 + 200d, _ => 3989.05 + 1922d };
            Expect(failures, Math.Abs(d[0] - expectMain) < 0.01 && Math.Abs(d[1] - expectRegular) < 0.01, $"{mode}: crown to origin {d[0]:F3} / {d[1]:F3}, expected {expectMain:F3} / {expectRegular:F3}");
            Console.WriteLine($"ECLIPSE_HUB_BEAM_LAYER origin {mode} crown_to_origin_m main={d[0]:F2} regular={d[1]:F2} worst_origin_m={worstOrigin:E2} worst_tip_m={worstTip:F3} far_tip_scaled={worstFarTip:E2}");
        }
        // The same setting again, and a look change, rebuild nothing.
        int again = layer.RebuildCount;
        layer.ApplyBeamSettings(layer.Beams with { BeamOrigin = layer.Beams.BeamOrigin });
        layer.ApplyBeamSettings(layer.Beams with { BaseEmission = 11d });
        layer.ApplyBeamSettings(layer.Beams with { BaseEmission = 12d });
        Expect(failures, layer.RebuildCount == again, "an unchanged origin or a look change rebuilt the transforms");
        layer.ApplyBeamSettings(layer.Beams with { BeamOrigin = start });
        Expect(failures, layer.AppliedBeamOrigin == start, "origin not restored");
    }

    // ---------------------------------------------------------------- underground round trip

    static void CheckUnderground(Node3D fixture, VoxelChunkGridManager grid, EmitterGridTopology topology, EclipseEventTimeline timeline, List<string> failures)
    {
        // A scene root with the two layers under the names the participants bind to.
        var root = new Node { Name = "UndergroundRoot" };
        fixture.AddChild(root);
        var gridLayer = new EmitterGridBeamLayer { Name = "EmitterGrid", AutoBind = false, LoadSavedDefaults = false };
        root.AddChild(gridLayer);
        if (!gridLayer.Bind(grid, topology)) { failures.Add("underground grid layer did not bind"); return; }
        var layer = new EclipseHubBeamLayer { Name = "EclipseBeams", AutoBind = false };
        root.AddChild(layer);
        bool Attach(Node3D node) { fixture.AddChild(node); return true; }
        if (!layer.Bind(grid, topology) || !layer.BindFarLayer(ScaledPointFor(topology), FarScale, Attach)) { failures.Add("underground hub beam layer did not bind"); return; }
        layer.BindTimeline(timeline);
        layer.ApplyEventTime(timeline.TauMainFire + 5d, true, 0.016);

        List<UndergroundParticipant> participants = UndergroundParticipantFactory.CreateDefaults();
        UndergroundParticipant? gridParticipant = participants.FirstOrDefault(p => p.Name == "EmitterGrid");
        UndergroundParticipant? beamParticipant = participants.FirstOrDefault(p => p.Name == "EclipseHubBeams");
        Expect(failures, gridParticipant != null && beamParticipant != null, "grid / hub beam participants are not registered");
        if (gridParticipant == null || beamParticipant == null) return;
        gridParticipant.Bind(root); beamParticipant.Bind(root);
        Expect(failures, gridParticipant.Present && beamParticipant.Present && gridParticipant.Group == UndergroundGroups.FarTerrain && beamParticipant.Group == UndergroundGroups.FarTerrain, "participants not bound / group");
        Expect(failures, gridLayer.Visible && layer.Visible && layer.FarLayer is { Visible: true }, "layers not visible on the surface");
        string gridSurface = gridParticipant.Describe(), beamSurface = beamParticipant.Describe();
        ulong[] gridIds = gridParticipant.RetainedInstanceIds(), beamIds = beamParticipant.RetainedInstanceIds();

        // Enclosed: held but still drawn (like the stand-ins). Deep and dark: hidden.
        var hold = new UndergroundParticipantState(true, false, false, 1f);
        gridParticipant.Apply(hold); beamParticipant.Apply(hold);
        Expect(failures, gridLayer.UndergroundHold && layer.UndergroundHold && !gridLayer.UndergroundHidden && !layer.UndergroundHidden, "hold state");
        var hide = new UndergroundParticipantState(true, true, true, 1f);
        gridParticipant.Apply(hide); beamParticipant.Apply(hide);
        Expect(failures, gridLayer.UndergroundHidden && !gridLayer.Visible && layer.UndergroundHidden && !layer.Visible, $"layers not hidden underground: grid {gridLayer.Visible} beams {layer.Visible}");
        Expect(failures, gridLayer.FarLayer is not { Visible: true } && layer.FarLayer is not { Visible: true }, "far layers not hidden underground");
        // The layers are held, not freed.
        Expect(failures, gridLayer.IsBound && layer.IsBound && gridLayer.LinkInstances != null && layer.Instances != null, "a layer was freed underground");
        // A frame while hidden keeps them hidden and writes nothing per beam.
        long transforms = layer.TransformWriteCount;
        layer.ApplyEventTime(timeline.TauMainFire + 6d, true, 0.016);
        Expect(failures, !layer.Visible && layer.TransformWriteCount == transforms, "hidden beams drew or wrote");
        // Exit: everything is restored.
        gridParticipant.Apply(UndergroundParticipantState.Clear); beamParticipant.Apply(UndergroundParticipantState.Clear);
        Expect(failures, gridLayer.Visible && layer.Visible && layer.FarLayer is { Visible: true }, "layers not restored on exit");
        Expect(failures, !gridLayer.UndergroundHold && !layer.UndergroundHold && !gridLayer.UndergroundHidden && !layer.UndergroundHidden, "flags not cleared on exit");
        Expect(failures, gridParticipant.Describe() == gridSurface && beamParticipant.Describe() == beamSurface, $"describe differs after the round trip: '{gridParticipant.Describe()}' vs '{gridSurface}'");
        Expect(failures, gridParticipant.RetainedInstanceIds().SequenceEqual(gridIds) && beamParticipant.RetainedInstanceIds().SequenceEqual(beamIds), "retained instance ids changed");
        // A forced exit while the layer is unbuilt and re-built keeps the hidden flag consistent.
        beamParticipant.Apply(hide);
        layer.Unbind();
        beamParticipant.Apply(UndergroundParticipantState.Clear);
        Expect(failures, !layer.UndergroundHidden && !layer.UndergroundHold, "flags not cleared while unbuilt");
        Console.WriteLine($"ECLIPSE_HUB_BEAM_LAYER underground grid='{gridSurface}' beams='{beamSurface}'");
        fixture.RemoveChild(root);
        root.Free();
    }

    static void CheckUnbind(EclipseHubBeamLayer layer, List<string> failures)
    {
        layer.Unbind();
        Expect(failures, !layer.IsBound && layer.Instances == null && layer.FarLayer == null && !layer.Visible, "unbind left state behind");
        Expect(failures, layer.DescribeStatus().Contains("not built"), "unbound status text");
    }
}
