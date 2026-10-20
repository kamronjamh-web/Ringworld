using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Atmosphere;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// RingPoiHaze registry (docs: assets/poi/README.md, POI architecture haze). Instantiates every POI scene, registers it and checks the
/// registered count against an independent material walk, the ray-frame publication, the POI haze toggle, de-duplication, pruning, the
/// per-field write gate, the stand-in frame binding, and that no POI mesh surface is still a StandardMaterial3D.
/// Run through tools/poi_haze_registry_test.gd. Prints POI_HAZE_REGISTRY=PASS.
/// </summary>
public partial class PoiHazeRegistryTest : RefCounted
{
    private const double RingRadius = 5_000_000.0;

    private static readonly (string Path, int Expected)[] Scenes =
    {
        ("res://assets/poi/beam_emitter/BeamEmitterPOI.tscn", 9),
        ("res://assets/poi/main_spire/MainSpirePOI.tscn", 11),
        ("res://assets/poi/reactor/ReactorPOI.tscn", 6),
        ("res://assets/poi/main_reactor/MainReactorPOI.tscn", 7),
    };

    public int Run(bool unused)
    {
        _ = unused;
        var failures = new List<string>();
        var instances = new List<Node>();
        Node3D? fixture = null;
        try
        {
            SceneTree tree = (Engine.GetMainLoop() as SceneTree) ?? throw new InvalidOperationException("scene tree unavailable");
            RingPoiHaze.Reset();
            RingPoiHaze.Enabled = true;
            RingPoiHaze.CloudShadowEnabled = false;
            fixture = new Node3D { Name = "PoiHazeRegistryFixture" };
            tree.Root.AddChild(fixture);

            // Scenes: registered count equals the distinct POI shader materials found by an independent walk.
            var allMaterials = new HashSet<ulong>();
            foreach ((string path, int expected) in Scenes)
            {
                var scene = ResourceLoader.Load<PackedScene>(path);
                if (scene == null) { failures.Add("cannot load " + path); continue; }
                Node root = scene.Instantiate();
                fixture.AddChild(root);
                instances.Add(root);
                HashSet<ulong> expectedIds = CollectPoiMaterials(root, failures, path);
                int registered = RingPoiHaze.RegisterTree(root, RingRadius);
                if (expectedIds.Count != expected) failures.Add($"{path}: {expectedIds.Count} distinct POI materials, expected {expected}");
                if (registered != expectedIds.Count) failures.Add($"{path}: registered {registered} != {expectedIds.Count}");
                if (RingPoiHaze.RegisterTree(root, RingRadius) != 0) failures.Add(path + ": re-registering added entries");
                allMaterials.UnionWith(expectedIds);
            }
            // Weather Lung: the POI root plus the Detail scene (attached by the streamer in slices).
            int wlTotal = 0;
            foreach (string path in new[] { "res://assets/poi/weather_lung/WeatherLungPOI.tscn", "res://assets/poi/weather_lung/WeatherLungDetail.tscn" })
            {
                var scene = ResourceLoader.Load<PackedScene>(path);
                if (scene == null) { failures.Add("cannot load " + path); continue; }
                Node root = scene.Instantiate();
                fixture.AddChild(root);
                instances.Add(root);
                HashSet<ulong> ids = CollectPoiMaterials(root, failures, path);
                RingPoiHaze.RegisterTree(root, RingRadius);
                allMaterials.UnionWith(ids);
                wlTotal += ids.Count;
            }
            if (wlTotal < 23) failures.Add($"Weather Lung registered {wlTotal} POI materials across POI+Detail, expected 23 distinct");
            if (RingPoiHaze.RegisteredPoiShaderCount != allMaterials.Count)
                failures.Add($"registered POI shader count {RingPoiHaze.RegisteredPoiShaderCount} != distinct {allMaterials.Count}");
            Console.WriteLine($"  registered {RingPoiHaze.RegisteredPoiShaderCount} POI materials ({allMaterials.Count} distinct across {instances.Count} scenes)");

            // Frame publication.
            var frame = new RingAtmosphereRayFrame(true, new Vector3(100f, 200f, 300f), 300f, Vector3.Right, Vector3.Up, 1000f, 2000f, 1f, Vector3.Left);
            RingPoiHaze.ApplyFrame(frame);
            List<ShaderMaterial> materials = RingPoiHaze.Snapshot().Where(PoiMaterialParams.IsPoiShader).ToList();
            foreach (ShaderMaterial material in materials)
            {
                if (!(bool)material.GetShaderParameter("ring_atmosphere_ray_frame_valid")) { failures.Add(material.ResourceName + ": frame not valid"); break; }
                if ((Vector3)material.GetShaderParameter("ring_atmosphere_camera_world_position") != frame.CameraWorld) { failures.Add(material.ResourceName + ": camera position"); break; }
                if (Math.Abs((double)material.GetShaderParameter("ring_radius_meters") - RingRadius) > 1.0) { failures.Add(material.ResourceName + ": ring_radius_meters"); break; }
            }

            // Toggle.
            RingPoiHaze.Enabled = false;
            if (materials.Any(m => (bool)m.GetShaderParameter(PoiMaterialParams.EnabledName))) failures.Add("Enabled=false left poi_haze_enabled true");
            RingPoiHaze.Enabled = true;
            if (materials.Any(m => !(bool)m.GetShaderParameter(PoiMaterialParams.EnabledName))) failures.Add("Enabled=true left poi_haze_enabled false");
            RingPoiHaze.CloudShadowEnabled = true;
            if (materials.Any(m => !(bool)m.GetShaderParameter(PoiMaterialParams.CloudShadowName))) failures.Add("CloudShadowEnabled=true did not reach every material");
            RingPoiHaze.CloudShadowEnabled = false;
            if (materials.Any(m => (bool)m.GetShaderParameter(PoiMaterialParams.CloudShadowName))) failures.Add("CloudShadowEnabled=false did not reach every material");

            // Write gate: only the camera moves, so two fields change (position and height).
            Span<long> before = stackalloc long[RingMaterialWriteCounters.SiteCount], after = stackalloc long[RingMaterialWriteCounters.SiteCount], textures = stackalloc long[RingMaterialWriteCounters.SiteCount];
            RingMaterialWriteCounters.Snapshot(before, textures);
            RingPoiHaze.ApplyFrame(frame with { CameraWorld = new Vector3(110f, 200f, 300f), CameraHeightMeters = 310f });
            RingMaterialWriteCounters.Snapshot(after, textures);
            long writes = after[(int)RingMaterialWriteCounters.Site.PoiAtmosphere] - before[(int)RingMaterialWriteCounters.Site.PoiAtmosphere];
            int registeredNow = RingPoiHaze.RegisteredCount;
            if (writes > 2L * registeredNow) failures.Add($"camera-only frame wrote {writes} fields for {registeredNow} materials (> 2 each)");
            if (writes < 1) failures.Add("camera-only frame wrote nothing");
            RingMaterialWriteCounters.Snapshot(before, textures);
            RingPoiHaze.ApplyFrame(frame with { CameraWorld = new Vector3(110f, 200f, 300f), CameraHeightMeters = 310f });
            RingMaterialWriteCounters.Snapshot(after, textures);
            if (after[(int)RingMaterialWriteCounters.Site.PoiAtmosphere] != before[(int)RingMaterialWriteCounters.Site.PoiAtmosphere]) failures.Add("an unchanged frame wrote parameters");

            // Pruning: a freed material leaves the registry.
            int countBefore = RingPoiHaze.RegisteredCount;
            var temporary = new ShaderMaterial { Shader = GD.Load<Shader>(PoiMaterialParams.ShaderPath) };
            if (!RingPoiHaze.RegisterMaterial(temporary, RingRadius)) failures.Add("temporary material was not registered");
            if (RingPoiHaze.RegisterMaterial(temporary, RingRadius)) failures.Add("duplicate RegisterMaterial added an entry");
            if (RingPoiHaze.RegisteredCount != countBefore + 1) failures.Add("registered count did not grow by one");
            temporary.Dispose();
            temporary = null;
            if (RingPoiHaze.RegisteredCount != countBefore) failures.Add($"freed material was not pruned ({RingPoiHaze.RegisteredCount} vs {countBefore})");

            // Audit: no StandardMaterial3D left on a POI mesh surface outside the allowlist.
            foreach (Node root in instances) AuditSurfaces(root, root, failures);

            // Stand-ins receive the frame after Bind.
            CheckStandIns(tree, fixture, failures);
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.ToString()); return 1; }
        finally
        {
            RingPoiHaze.Enabled = true;
            RingPoiHaze.CloudShadowEnabled = false;
            foreach (Node root in instances) if (GodotObject.IsInstanceValid(root)) { root.GetParent()?.RemoveChild(root); root.Free(); }
            if (fixture != null && GodotObject.IsInstanceValid(fixture)) { fixture.GetParent()?.RemoveChild(fixture); fixture.Free(); }
            RingPoiHaze.Reset();
        }
        if (failures.Count != 0) { Console.Error.WriteLine("POI_HAZE_REGISTRY=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("POI_HAZE_REGISTRY=PASS");
        return 0;
    }

    private static HashSet<ulong> CollectPoiMaterials(Node root, List<string> failures, string label)
    {
        var ids = new HashSet<ulong>();
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            Node node = stack.Pop();
            foreach (Node child in node.GetChildren()) stack.Push(child);
            if (node is GeometryInstance3D { MaterialOverride: ShaderMaterial overrideMaterial } && PoiMaterialParams.IsPoiShader(overrideMaterial)) ids.Add(overrideMaterial.GetInstanceId());
            Mesh? mesh = (node as MeshInstance3D)?.Mesh ?? (node as MultiMeshInstance3D)?.Multimesh?.Mesh;
            if (mesh == null) continue;
            for (int s = 0; s < mesh.GetSurfaceCount(); s++)
                if (mesh.SurfaceGetMaterial(s) is ShaderMaterial material && PoiMaterialParams.IsPoiShader(material)) ids.Add(material.GetInstanceId());
        }
        if (ids.Count == 0) failures.Add(label + ": no POI shader materials found");
        return ids;
    }

    private static void AuditSurfaces(Node root, Node node, List<string> failures)
    {
        foreach (Node child in node.GetChildren()) AuditSurfaces(root, child, failures);
        Mesh? mesh = (node as MeshInstance3D)?.Mesh;
        if (mesh == null) return;
        string path = root.GetPathTo(node).ToString();
        if (path.Contains("PlaceholderBeam", StringComparison.Ordinal) || path.Contains("Collision", StringComparison.Ordinal)) return;
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            Material? material = mesh.SurfaceGetMaterial(s);
            if (material is BaseMaterial3D) failures.Add($"{root.Name}/{path} surface {s} is still {material.GetType().Name} ({material.ResourceName})");
        }
    }

    private static void CheckStandIns(SceneTree tree, Node3D fixture, List<string> failures)
    {
        using RingTerrainSampler sampler = EmitterSiteTestWorlds.CreateGen17(1337);
        using INaturalHeightSource source = EmitterSiteHeightSources.Create(sampler);
        EmitterSiteRegistry registry = EmitterSiteRegistry.GetOrBuild(EmitterSiteRecipe.Default(1337), source);
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
        var camera = new Camera3D { Name = "Camera", Current = true };
        fixture.AddChild(camera);
        var layer = new EmitterStandInLayer { Name = "EmitterStandIns", AutoBind = false };
        fixture.AddChild(layer);
        int before = RingPoiHaze.RegisteredCount;
        if (!layer.Bind(grid, new EmitterSitePlacementSource(registry))) { failures.Add("stand-in bind failed"); return; }
        if (RingPoiHaze.RegisteredCount != before + 2) failures.Add($"stand-in Bind registered {RingPoiHaze.RegisteredCount - before} materials, expected 2");
        RingPoiHaze.Tick(grid, camera, Vector3.Up, true);
        foreach (ShaderMaterial? material in new[] { layer.RegularMaterial, layer.MainMaterial })
        {
            if (material == null) { failures.Add("stand-in material missing"); continue; }
            if (!(bool)material.GetShaderParameter("ring_atmosphere_ray_frame_valid")) failures.Add("stand-in material did not receive a valid ray frame");
        }
    }
}
