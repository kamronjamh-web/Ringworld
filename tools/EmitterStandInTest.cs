using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// M1-4 far stand-ins (docs/emitter-poi-streaming-spec.md section B): instance counts and kind mapping, CPU mirror versus
/// the placement math, streamer flags, floating-origin rebuilds, the effective maximum distance, underground hiding and the
/// eclipse beam API. Expectations come from the live registry; no golden values are used.
/// </summary>
public partial class EmitterStandInTest : RefCounted
{
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
            var placements = new EmitterSitePlacementSource(registry);

            fixture = new Node3D { Name = "EmitterStandInFixture" };
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
            var layer = new EmitterStandInLayer { Name = "EmitterStandIns", AutoBind = false };
            fixture.AddChild(layer);
            if (!layer.Bind(grid, placements)) { Console.Error.WriteLine("EMITTER_FAR_STANDIN=FAIL bind"); return 1; }

            CheckCounts(layer, placements, failures);
            CheckMirror(layer, placements, grid, failures);
            CheckFlags(layer, placements, failures);
            CheckAnchorRebuilds(layer, placements, grid, failures);
            CheckEffectiveMax(failures);
            CheckVisibility(layer, failures);
            CheckBeams(layer, placements, grid, failures);
            if (GD.Load<Shader>(EmitterStandInLayer.ShaderPath) == null) failures.Add("shader resource did not load");
            if (layer.RegularMaterial?.Shader == null || layer.MainMaterial?.Shader == null) failures.Add("materials have no shader");
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
        if (failures.Count != 0) { Console.Error.WriteLine("EMITTER_FAR_STANDIN=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("EMITTER_FAR_STANDIN=PASS");
        return 0;
    }

    static void CheckCounts(EmitterStandInLayer layer, EmitterSitePlacementSource placements, List<string> failures)
    {
        if (placements.Count != 595) failures.Add($"placements {placements.Count}");
        if (layer.RegularInstanceCount != 594 || layer.MainInstanceCount != 1) failures.Add($"instances {layer.RegularInstanceCount}+{layer.MainInstanceCount}");
        if (layer.RegularInstanceCount + layer.MainInstanceCount != placements.Count) failures.Add("instance total != placements");
        if (layer.RegularInstances?.Multimesh is not { } regular || layer.MainInstances?.Multimesh is not { } main) { failures.Add("missing multimeshes"); return; }
        if (!regular.UseCustomData || !main.UseCustomData) failures.Add("custom data off");
        if (regular.Mesh?.ResourcePath != EmitterStandInLayer.RegularMeshPath) failures.Add("regular mesh path " + regular.Mesh?.ResourcePath);
        if (main.Mesh?.ResourcePath != EmitterStandInLayer.MainMeshPath) failures.Add("main mesh path " + main.Mesh?.ResourcePath);
        if (layer.MainSiteIndex != placements.Registry.MainIndex || layer.Count != placements.Count) failures.Add("beam source identity/count");
        if (layer.RegularInstances.Name != "RegularStandIns" || layer.MainInstances.Name != "MainStandIn") failures.Add("child names");
        for (int site = 0; site < placements.Count; site++)
        {
            bool isMain = placements.GetPlacement(site).Kind == SiteKind.Main;
            if (isMain != (site == placements.Registry.MainIndex)) failures.Add($"kind of site {site}");
        }
    }

    static void CheckMirror(EmitterStandInLayer layer, EmitterSitePlacementSource placements, VoxelChunkGridManager grid, List<string> failures)
    {
        RingLodGeometrySnapshot snapshot = grid.CaptureLodGeometrySnapshot();
        double worstOrigin = 0, worstBasis = 0, worstReadOrigin = 0;
        int readable = 0;
        for (int site = 0; site < placements.Count; site++)
        {
            Transform3D expected = placements.GetWorldTransform(snapshot, site);
            Transform3D actual = layer.GetInstanceTransform(site);
            worstOrigin = Math.Max(worstOrigin, actual.Origin.DistanceTo(expected.Origin));
            worstBasis = Math.Max(worstBasis, BasisDelta(actual.Basis, expected.Basis));
            Transform3D read = layer.ReadbackInstanceTransform(site);
            if (!read.IsEqualApprox(Transform3D.Identity))
            {
                readable++;
                worstReadOrigin = Math.Max(worstReadOrigin, read.Origin.DistanceTo(expected.Origin));
            }
        }
        if (worstOrigin > 0.001 || worstBasis > 1e-5) failures.Add($"mirror origin {worstOrigin} basis {worstBasis}");
        if (readable == 0) Console.WriteLine("EMITTER_FAR_STANDIN rs_readback=unavailable");
        else if (readable != placements.Count || worstReadOrigin > 0.001) failures.Add($"rs readback {readable}/{placements.Count} err {worstReadOrigin}");
        Console.WriteLine($"EMITTER_FAR_STANDIN mirror worst_origin={worstOrigin:E2} worst_basis={worstBasis:E2} readable={readable}");
    }

    static double BasisDelta(Basis a, Basis b)
    {
        double worst = 0;
        for (int row = 0; row < 3; row++)
            for (int column = 0; column < 3; column++)
                worst = Math.Max(worst, Math.Abs(a[row][column] - b[row][column]));
        return worst;
    }

    static void CheckFlags(EmitterStandInLayer layer, EmitterSitePlacementSource placements, List<string> failures)
    {
        int main = placements.Registry.MainIndex, regular = 7;
        Color Expect(bool presented, bool hidden) => new(presented ? 1f : 0f, hidden ? 1f : 0f, 0f, 0f);
        bool readbackWorks = false;
        foreach (int site in new[] { regular, main })
        {
            layer.SetSiteHidden(site, true);
            if (!layer.IsSiteHidden(site) || layer.IsSitePresented(site)) failures.Add($"hidden flag site {site}");
            layer.SetSitePresented(site, true);
            if (!layer.IsSitePresented(site)) failures.Add($"presented flag site {site}");
            Color data = layer.ReadbackInstanceCustomData(site);
            // The headless dummy renderer returns (0,0,0,1) for every instance.
            if (data != default && !data.IsEqualApprox(new Color(0f, 0f, 0f, 1f))) { readbackWorks = true; if (!data.IsEqualApprox(Expect(true, true))) failures.Add($"custom data {site} {data}"); }
        }
        if (layer.HiddenCount != 2 || layer.PresentedCount != 2) failures.Add($"counts {layer.HiddenCount}/{layer.PresentedCount}");
        layer.SetSiteHidden(regular, true); // idempotent
        if (layer.HiddenCount != 2) failures.Add("hidden count not idempotent");
        // Flags survive a rebuild and leave custom data alone.
        int revision = layer.GeometryRevision;
        layer.Rebuild(((VoxelChunkGridManager)layer.GetNode("../ChunkGridManager")).CaptureLodGeometrySnapshot());
        if (layer.GeometryRevision != revision + 1) failures.Add("rebuild did not bump revision");
        if (!layer.IsSiteHidden(regular) || !layer.IsSiteHidden(main) || !layer.IsSitePresented(regular) || !layer.IsSitePresented(main)) failures.Add("flags lost on rebuild");
        if (readbackWorks && !layer.ReadbackInstanceCustomData(regular).IsEqualApprox(Expect(true, true))) failures.Add("custom data changed by rebuild");
        layer.SetSitePresented(regular, false);
        layer.SetSitePresented(main, false);
        if (layer.PresentedCount != 0 || layer.IsSitePresented(regular)) failures.Add("presented not cleared");
        if (readbackWorks && !layer.ReadbackInstanceCustomData(regular).IsEqualApprox(Expect(false, true))) failures.Add("custom data after presented clear");
        layer.ClearHidden();
        if (layer.HiddenCount != 0 || layer.IsSiteHidden(regular) || layer.IsSiteHidden(main)) failures.Add("hidden not cleared");
        if (readbackWorks && !layer.ReadbackInstanceCustomData(main).IsEqualApprox(Expect(false, false))) failures.Add("custom data after clear");
        foreach (int bad in new[] { -1, placements.Count, int.MaxValue })
        {
            layer.SetSiteHidden(bad, true); layer.SetSitePresented(bad, true);
            if (layer.IsSiteHidden(bad) || layer.IsSitePresented(bad)) failures.Add($"out-of-range {bad} returned true");
        }
        if (layer.HiddenCount != 0 || layer.PresentedCount != 0) failures.Add("out-of-range changed counts");
        if (!readbackWorks) Console.WriteLine("EMITTER_FAR_STANDIN rs_custom_readback=unavailable");
    }

    static void CheckAnchorRebuilds(EmitterStandInLayer layer, EmitterSitePlacementSource placements, VoxelChunkGridManager grid, List<string> failures)
    {
        double circumference = grid.GetCanonicalRingCircumferenceMeters64();
        double worstMs = 0;
        void Verify(string label)
        {
            RingLodGeometrySnapshot snapshot = grid.CaptureLodGeometrySnapshot();
            double worstOrigin = 0;
            for (int site = 0; site < placements.Count; site++)
                worstOrigin = Math.Max(worstOrigin, layer.GetInstanceTransform(site).Origin.DistanceTo(placements.GetWorldTransform(snapshot, site).Origin));
            if (worstOrigin > 0.001) failures.Add($"{label}: mirror off by {worstOrigin}");
            worstMs = Math.Max(worstMs, layer.LastRebuildMs);
        }
        Vector3 Origin(int site) => layer.GetInstanceTransform(site).Origin;

        // Warm-up so JIT cost does not count against the rebuild budget.
        layer.Rebuild(grid.CaptureLodGeometrySnapshot());
        Vector3 beforeOrigin = Origin(0);
        int start = layer.RebuildCount;
        var anchorA = new RingCanonicalPosition(5_000_000d, 100d, 150_000d);
        grid.SetFloatingOriginCanonicalAnchor(anchorA);
        if (layer.RebuildCount != start + 1) failures.Add($"anchor A rebuilds {layer.RebuildCount - start}");
        Verify("anchor A");
        if (Origin(0).DistanceTo(beforeOrigin) < 1000d) failures.Add("anchor A did not move origins by more than 1 km");

        int afterA = layer.RebuildCount;
        grid.SetFloatingOriginCanonicalAnchor(anchorA);
        if (layer.RebuildCount != afterA) failures.Add("same anchor rebuilt");

        grid.SetFloatingOriginCanonicalAnchor(new RingCanonicalPosition(circumference - 5d, 100d, 150_000d));
        if (layer.RebuildCount != afterA + 1) failures.Add($"seam anchor rebuilds {layer.RebuildCount - afterA}");
        Verify("seam anchor");

        int afterSeam = layer.RebuildCount;
        grid.ClearFloatingOriginCanonicalAnchor();
        if (layer.RebuildCount != afterSeam + 1) failures.Add($"clear rebuilds {layer.RebuildCount - afterSeam}");
        Verify("clear");
        int afterClear = layer.RebuildCount;
        grid.ClearFloatingOriginCanonicalAnchor();
        if (layer.RebuildCount != afterClear) failures.Add("second clear rebuilt");

        Console.WriteLine($"EMITTER_FAR_STANDIN rebuild last_ms={layer.LastRebuildMs:F3} worst_ms={worstMs:F3}");
        if (worstMs > 5d) failures.Add($"rebuild took {worstMs:F2} ms");
    }

    static void CheckEffectiveMax(List<string> failures)
    {
        void Case(double requested, double? fade, double? far, double expected)
        {
            double actual = EmitterStandInLayer.ComputeEffectiveMaxDistance(requested, fade, far);
            if (Math.Abs(actual - expected) > 1e-6) failures.Add($"effective max ({requested},{fade},{far}) = {actual}, expected {expected}");
        }
        Case(700_000, 850_000, 1_200_000, 700_000);
        Case(850_000, 600_000, null, 600_000);
        Case(700_000, null, 300_000, 294_000);
        Case(700_000, null, null, 700_000);
    }

    static void CheckVisibility(EmitterStandInLayer layer, List<string> failures)
    {
        if (!layer.Visible) failures.Add("layer not visible after bind");
        int before = layer.RegularInstanceCount + layer.MainInstanceCount;
        layer.SetUndergroundHidden(true);
        if (layer.Visible || !layer.UndergroundHidden) failures.Add("underground hidden left layer visible");
        if (layer.RegularInstanceCount + layer.MainInstanceCount != before) failures.Add("underground hide changed instance count");
        layer.SetUndergroundHidden(false);
        if (!layer.Visible) failures.Add("underground restore did not show layer");
        layer.Enabled = false;
        if (layer.Visible) failures.Add("disabled layer visible");
        layer.Enabled = true;
        if (!layer.Visible) failures.Add("re-enabled layer hidden");
        layer.Configure(true, 500_000f, 80_000f, 2f, 9_000f, 20_000f);
        if (!layer.Visible || layer.MaxDistanceMeters != 500_000f) failures.Add("Configure did not apply");
        layer.Configure(true, EmitterStandInLayer.DefaultMaxDistanceMeters, EmitterStandInLayer.DefaultFadeMeters, EmitterStandInLayer.DefaultMinPixels,
            EmitterStandInLayer.DefaultRegularHandoffMeters, EmitterStandInLayer.DefaultMainHandoffMeters);
    }

    static void CheckBeams(EmitterStandInLayer layer, EmitterSitePlacementSource placements, VoxelChunkGridManager grid, List<string> failures)
    {
        IEmitterBeamSource beams = layer;
        if (beams.Count != 595) failures.Add($"beam count {beams.Count}");
        for (int site = 0; site < beams.Count; site++)
            if (beams.IsBeamActive(site)) { failures.Add($"beam {site} active by default"); break; }
        int events = 0;
        void Handler() => events++;
        beams.BeamsChanged += Handler;
        beams.SetBeamActive(12, true);
        if (!beams.IsBeamActive(12) || events != 1) failures.Add($"SetBeamActive active={beams.IsBeamActive(12)} events={events}");
        beams.SetBeamActive(12, true);
        if (events != 1) failures.Add("redundant SetBeamActive raised an event");
        RingLodGeometrySnapshot snapshot = grid.CaptureLodGeometrySnapshot();
        for (int site = 0; site < beams.Count; site += 37)
        {
            if (!beams.TryGetBeam(site, out EmitterBeamState beam)) { failures.Add($"TryGetBeam {site}"); continue; }
            if (beam.Geometry != placements.GetBeamGeometry(snapshot, site)) failures.Add($"beam geometry {site}");
            if (beam.SiteIndex != site || beam.Active != (site == 12)) failures.Add($"beam state {site}");
        }
        if (!beams.TryGetBeam(beams.MainSiteIndex, out EmitterBeamState main) || main.Kind != SiteKind.Main) failures.Add("main beam kind");
        // The dormant grid has its own ring dimensions, so the tilt is judged in a snapshot that matches the registry's world.
        double radius = EmitterSiteTestWorlds.Circumference / Math.Tau;
        EmitterSitePlacement mainPlacement = placements.GetPlacement(beams.MainSiteIndex);
        var worldSnapshot = new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero, radius, 321d, 50d, EmitterSiteTestWorlds.Circumference, -8000d, 8000d,
            new RingCanonicalPosition(mainPlacement.AroundMeters + 1500d, mainPlacement.PadHeightMeters + 20d, mainPlacement.AcrossMeters - 900d));
        double mainTilt = placements.GetBeamGeometry(worldSnapshot, beams.MainSiteIndex).TiltDegrees;
        Console.WriteLine($"EMITTER_FAR_STANDIN main beam tilt_deg={mainTilt:E3}");
        if (Math.Abs(mainTilt) >= 0.001) failures.Add($"main beam tilt {mainTilt}");
        if (beams.TryGetBeam(-1, out _) || beams.TryGetBeam(beams.Count, out _)) failures.Add("TryGetBeam accepted an out-of-range site");
        beams.SetAllBeamsActive(false);
        if (beams.IsBeamActive(12) || events != 2) failures.Add($"SetAllBeamsActive events={events}");
        int revision = beams.GeometryRevision;
        events = 0;
        layer.Rebuild(snapshot);
        if (beams.GeometryRevision != revision + 1 || events != 1) failures.Add("rebuild revision/event");
        beams.BeamsChanged -= Handler;
    }
}
