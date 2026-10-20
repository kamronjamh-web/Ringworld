using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Dictionary = Godot.Collections.Dictionary;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// M1-8 bore liner, plug, aperture discard and hole-aware heights: A bore-floor and radius contract, B generated geometry,
/// C selector, D layer (slots, plug light, collision), E shader/project text checks. Prints EMITTER_BORE_APERTURE=PASS.
/// </summary>
public partial class EmitterBoreApertureTest : RefCounted
{
    private const double Eps = 1e-4;

    public int Run(bool unused)
    {
        _ = unused;
        var failures = new List<string>();
        Node3D? fixture = null;
        try
        {
            using RingTerrainSampler v5 = EmitterSiteTestWorlds.CreateGen17Sites(1337);
            EmitterSiteRegistry registry = v5.EmitterSites ?? throw new InvalidOperationException("v5 sampler has no registry");
            TestFloor(registry, failures);
            TestGeometry(failures);
            var placements = new EmitterSitePlacementSource(registry);
            TestSelector(placements, failures);
            SceneTree tree = (Engine.GetMainLoop() as SceneTree) ?? throw new InvalidOperationException("scene tree unavailable");
            fixture = new Node3D { Name = "EmitterBoreFixture" };
            tree.Root.AddChild(fixture);
            TestLayer(fixture, placements, failures);
            TestText(failures);
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.ToString()); return 1; }
        finally
        {
            if (fixture != null && GodotObject.IsInstanceValid(fixture))
            {
                fixture.GetParent()?.RemoveChild(fixture);
                fixture.Free();
            }
            EmitterBoreGeometry.ClearCache();
        }
        if (failures.Count != 0) { Console.Error.WriteLine("EMITTER_BORE_APERTURE=FAIL " + string.Join(" | ", failures.Take(25))); return 1; }
        Console.WriteLine("EMITTER_BORE_APERTURE=PASS");
        return 0;
    }

    static void Expect(List<string> failures, bool condition, string label) { if (!condition) failures.Add(label); }

    // ---- A: floor and radii -------------------------------------------------------------------------------------------------

    static void TestFloor(EmitterSiteRegistry registry, List<string> failures)
    {
        Expect(failures, !EmitterSiteApertures.TryGetBoreFloorAt(null, 0, 0, out float nullFloor) && nullFloor == 0f, "null registry has no bore floor");
        long circumference = (long)registry.Recipe.CircumferenceM;
        EmitterSite main = registry.MainSite;
        EmitterSite regular = Enumerable.Range(0, registry.Count).Select(i => registry[i]).First(s => s.Kind == SiteKind.Regular && s.Around > 100_000 && s.Around < circumference - 100_000);
        EmitterSite seam = Enumerable.Range(0, registry.Count).Select(i => registry[i]).Where(s => s.Kind == SiteKind.Regular)
            .OrderBy(s => Math.Min(s.Around, circumference - s.Around)).First();
        foreach (EmitterSite site in new[] { regular, main, seam })
        {
            string label = $"{site.Kind} #{site.Index}";
            float floor = site.PadHeight - 200f;
            float liner = EmitterSiteApertures.LinerRadius(site.Kind);
            double[] inside = { 0d, 50d, liner - 0.1d };
            double[] outside = { liner, liner + 1d, liner + 14d };
            foreach (double r in inside)
                foreach ((double da, double dc) in new[] { (r, 0d), (0d, r), (-r, 0d) })
                {
                    bool ok = EmitterSiteApertures.TryGetBoreFloorAt(registry, site.Around + da, site.Across + dc, out float h);
                    Expect(failures, ok && Math.Abs(h - floor) < 1e-3f, $"{label} r={r:0.0} inside the liner column reports the iris rim P-200 (ok={ok} h={h})");
                    // The same point shifted by a full circumference (seam wrap) must agree.
                    bool wrapped = EmitterSiteApertures.TryGetBoreFloorAt(registry, site.Around + da - circumference, site.Across + dc, out float hw);
                    Expect(failures, wrapped && Math.Abs(hw - floor) < 1e-3f, $"{label} r={r:0.0} wrapped inside gives P-200");
                }
            foreach (double r in outside)
                foreach ((double da, double dc) in new[] { (r, 0d), (0d, r), (-r, 0d) })
                    Expect(failures, !EmitterSiteApertures.TryGetBoreFloorAt(registry, site.Around + da, site.Across + dc, out _), $"{label} r={r:0.0} outside has no bore floor");
        }
        Expect(failures, !EmitterSiteApertures.TryGetBoreFloorAt(registry, 1_000_000, 100_000, out _), "a point far from every site has no bore floor");

        // Radius ordering: bore clear < liner < hole < discard <= POI ground coverage.
        const float poiCoverageRegular = 83f, poiCoverageMain = 158f;
        Expect(failures, 75f < EmitterSiteApertures.RegularLinerRadiusM && EmitterSiteApertures.RegularLinerRadiusM < EmitterSiteApertures.RegularHoleRadiusM
            && EmitterSiteApertures.RegularHoleRadiusM < EmitterSiteApertures.DiscardRadius(SiteKind.Regular) && EmitterSiteApertures.DiscardRadius(SiteKind.Regular) <= poiCoverageRegular, "regular radius ordering");
        Expect(failures, 150f < EmitterSiteApertures.MainLinerRadiusM && EmitterSiteApertures.MainLinerRadiusM < EmitterSiteApertures.MainHoleRadiusM
            && EmitterSiteApertures.MainHoleRadiusM < EmitterSiteApertures.DiscardRadius(SiteKind.Main) && EmitterSiteApertures.DiscardRadius(SiteKind.Main) <= poiCoverageMain, "main radius ordering");
        Expect(failures, EmitterSiteApertures.DiscardRadius(SiteKind.Regular) == 82f && EmitterSiteApertures.DiscardRadius(SiteKind.Main) == 157f, "discard radii are hole + 2");
    }

    // ---- B: geometry --------------------------------------------------------------------------------------------------------

    static void TestGeometry(List<string> failures)
    {
        foreach (SiteKind kind in new[] { SiteKind.Regular, SiteKind.Main })
        {
            string label = kind.ToString();
            float liner = EmitterSiteApertures.LinerRadius(kind), discard = EmitterSiteApertures.DiscardRadius(kind);
            EmitterBoreGeometry.BuildLiner(liner, out Vector3[] vertices, out Vector3[] normals, out int[] indices);
            Expect(failures, vertices.Length == 64 * 9 && indices.Length == 64 * 8 * 6, label + " liner is 64 x 8 segments");
            float minY = float.MaxValue, maxY = float.MinValue, worstRadius = 0f;
            bool inward = true;
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 v = vertices[i];
                worstRadius = Math.Max(worstRadius, Math.Abs(new Vector2(v.X, v.Z).Length() - liner));
                minY = Math.Min(minY, v.Y); maxY = Math.Max(maxY, v.Y);
                Vector3 radial = new Vector3(v.X, 0f, v.Z).Normalized();
                if (normals[i].Dot(radial) > -0.999f || Math.Abs(normals[i].Y) > Eps) inward = false;
            }
            Expect(failures, worstRadius < Eps, label + " liner radius exact");
            Expect(failures, Math.Abs(minY + 200f) < Eps && Math.Abs(maxY + 0.5f) < Eps, label + " liner spans y -200..-0.5");
            Expect(failures, inward, label + " liner normals face inward");

            // M2-3: the plug is open, so there is no plug disk builder any more; the iris rim is the inlay annulus checked below.

            EmitterBoreGeometry.BuildCap(liner, discard, out Vector3[] cap, out int[] capIndices);
            int n = EmitterBoreGeometry.RadialSegments;
            double outerExpected = discard / Math.Cos(Math.PI / n);
            bool innerOk = true, outerOk = true, planeOk = true, circumscribes = true;
            for (int c = 0; c < n; c++)
            {
                innerOk &= Math.Abs(new Vector2(cap[c].X, cap[c].Z).Length() - liner) < Eps;
                outerOk &= Math.Abs(new Vector2(cap[n + c].X, cap[n + c].Z).Length() - outerExpected) < 1e-3;
                planeOk &= Math.Abs(cap[c].Y + 0.5f) < Eps && Math.Abs(cap[n + c].Y + 0.5f) < Eps;
                Vector2 a = new(cap[n + c].X, cap[n + c].Z), b = new(cap[n + (c + 1) % n].X, cap[n + (c + 1) % n].Z);
                Vector2 mid = (a + b) * 0.5f;
                circumscribes &= mid.Length() >= discard - 1e-3f;
            }
            Expect(failures, innerOk && outerOk && planeOk, label + " cap inner/outer radii and plane");
            Expect(failures, circumscribes, label + " cap polygon circumscribes the discard circle");
            Expect(failures, capIndices.Length == n * 6, label + " cap triangle count");

            EmitterBoreGeometry.BuildInlay(EmitterBoreGeometry.InlayInner(kind), EmitterBoreGeometry.InlayOuter(kind), out Vector3[] inlay, out _);
            Expect(failures, inlay.All(v => Math.Abs(v.Y + 199.98f) < Eps), label + " inlay at -199.98");
            Expect(failures, kind == SiteKind.Main ? EmitterBoreGeometry.InlayInner(kind) == 140f && EmitterBoreGeometry.InlayOuter(kind) == 151f
                : EmitterBoreGeometry.InlayInner(kind) == 70f && EmitterBoreGeometry.InlayOuter(kind) == 76f, label + " iris rim radii");
            Expect(failures, Math.Abs(EmitterBoreGeometry.InlayOuter(kind) - liner) < Eps, label + " iris rim outer edge is the liner radius");
            Expect(failures, Math.Abs(inlay.Take(EmitterBoreGeometry.RadialSegments).Max(v => new Vector2(v.X, v.Z).Length()) - EmitterBoreGeometry.InlayInner(kind)) < Eps, label + " iris rim is an annulus (hollow centre)");

            EmitterBoreKindResources resources = EmitterBoreGeometry.Build(kind);
            Expect(failures, resources.LinerShape.BackfaceCollision, label + " liner collision is double sided");
            // Open iris: the body surface holds only the liner and rim cap vertices (no plug disk, no plug collision shape).
            var bodyVertices = (Vector3[])resources.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex];
            Expect(failures, bodyVertices.Length == vertices.Length + cap.Length, label + " body mesh has no plug disk");
            Expect(failures, !bodyVertices.Any(v => Math.Abs(v.Y + 200f) < Eps && new Vector2(v.X, v.Z).Length() < liner - 1f), label + " nothing spans the bore at -200");
            Expect(failures, resources.Mesh.GetSurfaceCount() == 2, label + " mesh has body and inlay surfaces");
            Expect(failures, resources.Dark is BaseMaterial3D { CullMode: BaseMaterial3D.CullModeEnum.Disabled }
                || resources.Dark is ShaderMaterial { Shader.ResourcePath: PoiMaterialParams.DoubleSidedShaderPath }, label + " dark material is double sided");
            Expect(failures, resources.LinerShape.GetFaces().Length == (indices.Length + capIndices.Length), label + " collision faces");
            Expect(failures, resources.LightRange == (kind == SiteKind.Main ? 420f : 260f), label + " light range");
        }
    }

    // ---- C: selector --------------------------------------------------------------------------------------------------------

    static RingCanonicalPosition At(EmitterSitePlacement p, double da, double dc) => new(p.AroundMeters + da, p.PadHeightMeters + 1.7, p.AcrossMeters + dc);

    static void TestSelector(EmitterSitePlacementSource placements, List<string> failures)
    {
        Span<int> results = stackalloc int[3];
        Expect(failures, EmitterBoreSelector.Select(null, new RingCanonicalPosition(0, 0, 0), null, 16000, results) == 0, "null placements select nothing");
        var regulars = Enumerable.Range(0, placements.Count).Where(i => placements.GetPlacement(i).Kind == SiteKind.Regular).Take(40).ToArray();
        foreach (int site in regulars.Concat(new[] { placements.Registry.MainIndex }))
        {
            EmitterSitePlacement p = placements.GetPlacement(site);
            int count = EmitterBoreSelector.Select(placements, At(p, 1200, 0), null, 16000, results);
            Expect(failures, count == 1 && results[0] == site, $"site {site} is selected from 1.2 km");
        }
        EmitterSitePlacement a = placements.GetPlacement(regulars[0]);
        EmitterSitePlacement other = placements.GetPlacement(regulars.First(i => Math.Abs(placements.GetPlacement(i).AroundMeters - a.AroundMeters) > 1_000_000));
        int two = EmitterBoreSelector.Select(placements, At(a, 300, 0), At(other, 300, 0), 16000, results);
        Expect(failures, two == 2 && results[0] == a.Index && results[1] == other.Index, "explorer first, then the camera's site");
        int same = EmitterBoreSelector.Select(placements, At(a, 300, 0), At(a, -200, 40), 16000, results);
        Expect(failures, same == 1, "explorer and camera at the same site select it once");
        Span<int> one = stackalloc int[1];
        Expect(failures, EmitterBoreSelector.Select(placements, At(a, 300, 0), At(other, 300, 0), 16000, one) == 1, "result span caps the selection");
        // Per-kind range scaling: range * HoleRadius / 80.
        Expect(failures, EmitterBoreSelector.Select(placements, At(a, 900, 0), null, 1000, results) == 1, "regular inside range");
        Expect(failures, EmitterBoreSelector.Select(placements, At(a, 1100, 0), null, 1000, results) == 0, "regular outside range");
        EmitterSitePlacement m = placements.GetPlacement(placements.Registry.MainIndex);
        Expect(failures, EmitterBoreSelector.Select(placements, At(m, 1900, 0), null, 1000, results) == 1, "main inside scaled range");
        Expect(failures, EmitterBoreSelector.Select(placements, At(m, 2000, 0), null, 1000, results) == 0, "main outside scaled range");
        // Stable under small motion.
        bool stable = true;
        for (int i = 0; i < 100; i++)
        {
            int count = EmitterBoreSelector.Select(placements, At(a, 5000 + Math.Sin(i) * 0.5, Math.Cos(i) * 0.5), null, 16000, results);
            stable &= count == 1 && results[0] == a.Index;
        }
        Expect(failures, stable, "selection is stable under small motion");
    }

    // ---- D: layer -----------------------------------------------------------------------------------------------------------

    static void TestLayer(Node3D fixture, EmitterSitePlacementSource placements, List<string> failures)
    {
        var root = new Node3D { Name = "BoreRig" };
        fixture.AddChild(root);
        var grid = new VoxelChunkGridManager
        {
            Name = "ChunkGridManager", BuildOnReady = false, DormantEditableBackend = true, EnableChunkStreaming = false,
            UseDeterministicTerrainGeneration = true, EnableCanonicalFloatingOriginAnchor = true, TerrainSettings = new VoxelTerrainSettings()
        };
        root.AddChild(grid);
        var layer = new EmitterBoreLayer { Name = "EmitterBores", AutoBind = false };
        root.AddChild(layer);
        layer.Bind(grid, placements);

        EmitterSitePlacement a = placements.GetPlacement(placements.GetPlacement(0).Kind == SiteKind.Regular ? 0 : 1);
        EmitterSitePlacement m = placements.GetPlacement(placements.Registry.MainIndex);
        layer.Step(At(a, 500, 0), null, consumersActive: false);
        Expect(failures, layer.ActiveBoreCount == 0 && !layer.IsSlotCollisionEnabled(0), "inactive layer presents nothing");
        layer.Step(At(a, 500, 0), null, consumersActive: true);
        Expect(failures, layer.ActiveBoreCount == 1 && layer.GetActiveSite(0) == a.Index, "active layer presents the nearest bore");
        Expect(failures, layer.IsSlotCollisionEnabled(0) && layer.GetSlotNode(0)!.Visible, "active slot has collision and is visible");
        OmniLight3D light = layer.GetSlotLight(0)!;
        Expect(failures, light.Visible && Math.Abs(light.OmniRange - 260f) < 1e-3 && !light.ShadowEnabled && Math.Abs(light.Position.Y + 185f) < 1e-3, "regular plug light range/position/shadow");
        Expect(failures, Math.Abs(light.LightEnergy - 2f) < 1e-3, "default plug light energy is 2");
        // Plug top lands at exactly P - 200 in world height (no pad clearance).
        RingLodGeometrySnapshot snapshot = grid.CaptureLodGeometrySnapshot();
        Transform3D flush = placements.GetWorldTransform(snapshot, a.Index, 0d), padded = placements.GetWorldTransform(snapshot, a.Index);
        Transform3D slotGlobal = layer.GetSlotNode(0)!.GlobalTransform;
        Expect(failures, slotGlobal.Origin.DistanceTo(flush.Origin) < 1e-2f, "bore origin is the pad datum (clearance 0)");
        Expect(failures, Math.Abs((padded.Origin - slotGlobal.Origin).Dot(slotGlobal.Basis.Y.Normalized()) - 0.05f) < 1e-2f, "bore origin sits 0.05 m below the padded POI origin");
        Vector3 plugWorld = slotGlobal * new Vector3(0f, -200f, 0f);
        Expect(failures, Math.Abs((plugWorld - flush.Origin).Dot(flush.Basis.Y.Normalized()) + 200f) < 1e-2f, "plug top is exactly 200 m below the pad datum");
        layer.Configure(16000f, 0f);
        Expect(failures, !layer.GetSlotLight(0)!.Visible, "plug light is hidden at energy 0");
        layer.Configure(16000f, 3f);
        Expect(failures, layer.GetSlotLight(0)!.Visible && Math.Abs(layer.GetSlotLight(0)!.LightEnergy - 3f) < 1e-3, "plug light follows the energy setting");
        // Stable slots and kind swap.
        layer.Step(At(a, 500.5, 0.5), null, true);
        Expect(failures, layer.GetActiveSite(0) == a.Index && layer.ActiveBoreCount == 1, "slot stays stable under small motion");
        layer.Step(At(a, 500, 0), At(m, 1000, 0), true);
        int mainSlot = layer.GetActiveSite(0) == m.Index ? 0 : layer.GetActiveSite(1) == m.Index ? 1 : -1;
        Expect(failures, layer.ActiveBoreCount == 2 && mainSlot >= 0 && layer.GetActiveSite(1 - mainSlot) == a.Index, "explorer and camera bores both present");
        if (mainSlot >= 0)
        {
            Expect(failures, Math.Abs(layer.GetSlotLight(mainSlot)!.OmniRange - 420f) < 1e-3, "main plug light range is 420");
            Expect(failures, layer.IsSlotCollisionEnabled(mainSlot), "main slot collision on");
        }
        layer.Step(At(a, 500, 0), null, true);
        Expect(failures, layer.ActiveBoreCount == 1 && layer.GetActiveSite(0) + layer.GetActiveSite(1) + 1 == a.Index, "camera bore releases when out of selection");
        layer.Step(At(a, 500, 0), null, false);
        Expect(failures, layer.ActiveBoreCount == 0 && !layer.IsSlotCollisionEnabled(0) && !layer.IsSlotCollisionEnabled(1) && !layer.GetSlotLight(0)!.Visible, "consumers off releases every bore");
        Dictionary nearest = layer.FindNearestBoreSite(m.AroundMeters + 100, m.AcrossMeters + 100);
        Expect(failures, nearest.Count > 0 && (int)nearest["site"] == m.Index && (double)nearest["liner_radius"] == 151.0 && (double)nearest["discard_radius"] == 157.0
            && Math.Abs((double)nearest["floor"] - (m.PadHeightMeters - 200d)) < 1e-3, "FindNearestBoreSite describes the main bore");
    }

    // ---- E: text ------------------------------------------------------------------------------------------------------------

    static void TestText(List<string> failures)
    {
        foreach (string shader in new[] { "ring_terrain_proxy", "ring_terrain_global", "ring_ocean_proxy", "ring_ocean_global", "ring_terrain_proxy_shadow" })
        {
            string text = FileAccess.GetFileAsString($"res://materials/{shader}.gdshader");
            Expect(failures, text.Contains("ring_surface_aperture.gdshaderinc") && text.Contains("ring_surface_aperture_discard("), shader + " includes and calls the aperture discard");
        }
        string project = FileAccess.GetFileAsString("res://project.godot");
        foreach (string name in new[] { "ring_surface_aperture_0", "ring_surface_aperture_axis_0", "ring_surface_aperture_1", "ring_surface_aperture_axis_1" })
            Expect(failures, project.Contains(name + "={"), "project.godot declares " + name);
        Expect(failures, !FileAccess.GetFileAsString("res://scripts/Voxel/LOD/RingScaledFarViewport.cs").Contains("surface_apertures_enabled"), "RingScaledFarViewport does not opt in");
        foreach (string path in new[] { "RingTerrainProxySector", "RingTerrainGlobalLod", "RingAnalyticGlobalLod" })
            Expect(failures, FileAccess.GetFileAsString($"res://scripts/Voxel/LOD/{path}.cs").Contains("\"surface_apertures_enabled\", true"), path + " opts in");
    }
}
