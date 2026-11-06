using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using RingworldTechDemo.ContainmentField;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Atmosphere projector streaming (docs/containment-field-design.md 6.1, 6.2): the two-slot pool driven through
/// <see cref="AtmosphereProjectorStreamer.BuildObservations"/> and <see cref="EmitterPoiStreamerCore"/> (wall top, habitat floor, walking the wall with hysteresis,
/// wrap, two slots and eviction, dwell), the scene contract of AtmosphereProjectorPOI.tscn and the stand-in transforms. No scene tree needed.
/// Run: Godot --headless --path . --script res://tools/atmosphere_projector_streamer_test.gd. Prints ATMOSPHERE_PROJECTOR_STREAMER=PASS.
/// </summary>
public partial class AtmosphereProjectorStreamerTest : RefCounted
{
    private const double W = 318_000.0, Top = 10_400.0, Load = 16_500.0, Margin = 2_000.0, Dwell = 1.0;

    private readonly List<string> _failures = new();
    private void Check(bool ok, string message) { if (!ok) _failures.Add(message); }

    private static RingLodGeometrySnapshot Snap(double circumference, double radius)
        => new(Transform3D.Identity, Vector3.Zero, Vector3.Zero, radius, 0.0, 0.0, circumference, -22_304.0, Top, null);

    private AtmosphereProjectorSites _sites = null!;
    private double _wallAcross0, _wallAcross1, _spacing, _circ;

    private static EmitterPoiStreamerCore MakeCore(double load)
        => new(new[] { SiteKind.Regular, SiteKind.Regular }, new EmitterPoiStreamerConfig(true, load, load, Margin, Dwell, 0d, 0d));

    private readonly List<EmitterPoiSlotEvent> _events = new();
    private readonly EmitterPoiObservation[] _observations = new EmitterPoiObservation[256];

    private void Tick(EmitterPoiStreamerCore core, in RingCanonicalPosition observer, double now)
    {
        int count = AtmosphereProjectorStreamer.BuildObservations(_sites, observer, core.Config.UnloadFor(SiteKind.Regular), _observations);
        _events.Clear();
        core.Update(now, new ReadOnlySpan<EmitterPoiObservation>(_observations, 0, count), _events);
    }

    private static List<int> Active(EmitterPoiStreamerCore core) => core.Slots.Where(s => s.Active).Select(s => s.Site).OrderBy(s => s).ToList();
    private static string Show(IEnumerable<int> sites) => "[" + string.Join(",", sites) + "]";

    public int Run()
    {
        try
        {
            _circ = CertifiedRingScale.EffectiveCircumferenceMeters;
            RingLodGeometrySnapshot snapshot = Snap(_circ, CertifiedRingScale.EffectiveRadiusMeters);
            _sites = AtmosphereProjectorSites.Build(snapshot, W, RimWallDescription.Current);
            _spacing = _sites.SpacingMeters;
            _wallAcross0 = _sites[0].AcrossMeters;
            _wallAcross1 = _sites[_sites.PerWall].AcrossMeters;
            Check(_sites.Count == 252, "252 sites");
            WallTop();
            HabitatFloor();
            WalkAlongWall();
            Wrap();
            TwoSlots();
            DwellTest();
            SceneContract();
            StandInTransforms(snapshot);
        }
        catch (Exception exception)
        {
            _failures.Add("exception: " + exception);
        }
        if (_failures.Count == 0)
        {
            GD.Print("ATMOSPHERE_PROJECTOR_STREAMER=PASS");
            return 0;
        }
        foreach (string failure in _failures) GD.PrintErr("ATMOSPHERE_PROJECTOR_STREAMER FAIL: " + failure);
        GD.Print("ATMOSPHERE_PROJECTOR_STREAMER=FAIL");
        return 1;
    }

    private void WallTop()
    {
        EmitterPoiStreamerCore core = MakeCore(Load);
        int k = 5, site = _sites.IndexOf(0, k), opposite = _sites.IndexOf(1, k);
        var observer = new RingCanonicalPosition(_sites[site].AroundMeters, Top, _wallAcross0);
        Tick(core, observer, 0d);
        Check(Active(core).SequenceEqual(new[] { site }), "wall top at site 5 side 0: exactly site 5 assigned, got " + Show(Active(core)));
        Check(core.FindSlotForSite(site) == 0, "site 5 takes slot 0");
        Check(core.FindSlotForSite(opposite) < 0, "the opposite-wall site is not assigned");
    }

    private void HabitatFloor()
    {
        int site = _sites.IndexOf(0, 5);
        foreach ((double across, bool expected) in new[] { (5_000d, true), (20_000d, false) })
        {
            EmitterPoiStreamerCore core = MakeCore(Load);
            Tick(core, new RingCanonicalPosition(_sites[site].AroundMeters, 0d, across), 0d);
            Check((core.FindSlotForSite(site) >= 0) == expected, $"habitat floor across {across}: site assigned should be {expected}, active {Show(Active(core))}");
        }
    }

    private void WalkAlongWall()
    {
        EmitterPoiStreamerCore core = MakeCore(Load);
        int a = _sites.IndexOf(0, 5), b = _sites.IndexOf(0, 6);
        double start = _sites[a].AroundMeters, end = _sites[b].AroundMeters;
        double now = 0d;
        bool released = false, nearMidEmpty = false, siteBAssigned = false;
        int maxActive = 0;
        for (double x = 0d; x <= end - start; x += 1000d, now += 0.5d)
        {
            var observer = new RingCanonicalPosition(start + x, Top, _wallAcross0);
            Tick(core, observer, now);
            double da = _sites.Distance(a, observer), db = _sites.Distance(b, observer);
            maxActive = Math.Max(maxActive, core.ActiveCount);
            bool aActive = core.FindSlotForSite(a) >= 0, bActive = core.FindSlotForSite(b) >= 0;
            if (now > 0d && !released)
            {
                if (da <= Load + Margin) Check(aActive, $"walk x={x}: site 5 must stay assigned inside the unload radius (distance {da:F0})");
                else { Check(!aActive, $"walk x={x}: site 5 must be released beyond the unload radius (distance {da:F0})"); released = true; }
            }
            if (da > Load + Margin) Check(!aActive, $"walk x={x}: site 5 stays released");
            if (da > Load + Margin && db > Load + Margin) { Check(core.ActiveCount == 0, $"walk x={x}: nothing assigned between the towers, got {Show(Active(core))}"); nearMidEmpty = true; }
            if (db < Load) { Check(bActive, $"walk x={x}: site 6 assigned within the load radius (distance {db:F0})"); siteBAssigned = true; }
            if (da < Load && x > 0d) Check(aActive, $"walk x={x}: site 5 assigned within the load radius");
        }
        Check(released, "walk: site 5 was eventually released");
        Check(nearMidEmpty, "walk: the midpoint region was observed empty");
        Check(siteBAssigned, "walk: site 6 was assigned");
        Check(maxActive <= 1, "walk: never more than one slot active, max " + maxActive);
    }

    private void Wrap()
    {
        EmitterPoiStreamerCore core = MakeCore(Load);
        var observer = new RingCanonicalPosition(_circ - 2_000d, Top, _wallAcross0);
        Tick(core, observer, 0d);
        int site0 = _sites.IndexOf(0, 0), site125 = _sites.IndexOf(0, 125);
        Check(core.FindSlotForSite(site0) >= 0, "wrap: site 0 assigned at around C - 2000");
        Check(core.FindSlotForSite(site125) < 0, "wrap: site 125 not assigned");
    }

    private void TwoSlots()
    {
        const double big = 300_000d;
        EmitterPoiStreamerCore core = MakeCore(big);
        int s5 = _sites.IndexOf(0, 5), s4 = _sites.IndexOf(0, 4), s6 = _sites.IndexOf(0, 6), s7 = _sites.IndexOf(0, 7);
        var at5 = new RingCanonicalPosition(_sites[s5].AroundMeters, Top, _wallAcross0);
        Tick(core, at5, 0d);
        List<int> active = Active(core);
        Check(active.Count == 2 && active.Contains(s5) && (active.Contains(s4) ^ active.Contains(s6)), "two slots: sites 5 and one of 4 / 6 assigned, got " + Show(active));
        // Move before the dwell elapses: the farthest out-of-unload slot is evicted for the new nearer site.
        var at7 = new RingCanonicalPosition(_sites[s7].AroundMeters, Top, _wallAcross0);
        Tick(core, at7, 0.2d);
        active = Active(core);
        Check(active.Count == 2, "eviction: both slots stay busy, got " + Show(active));
        Check(active.Contains(s7), "eviction: site 7 assigned by evicting an out-of-range slot, got " + Show(active));
        Check(!active.Contains(s5), "eviction: site 5 (out of the unload radius) evicted, got " + Show(active));
        Check(active.All(s => _sites.Distance(s, at7) < big + Margin), "eviction: every active site is inside the unload radius");
    }

    private void DwellTest()
    {
        EmitterPoiStreamerCore core = MakeCore(Load);
        int site = _sites.IndexOf(0, 5);
        Tick(core, new RingCanonicalPosition(_sites[site].AroundMeters, Top, _wallAcross0), 0d);
        Check(core.FindSlotForSite(site) >= 0, "dwell: assigned at t = 0");
        var far = new RingCanonicalPosition(_sites[site].AroundMeters + 100_000d, Top, _wallAcross0);
        Tick(core, far, 0.3d);
        Check(core.FindSlotForSite(site) >= 0, "dwell: still assigned at 0.3 s although out of the unload radius");
        Tick(core, far, 0.9d);
        Check(core.FindSlotForSite(site) >= 0, "dwell: still assigned at 0.9 s");
        Tick(core, far, 1.0d);
        Check(core.FindSlotForSite(site) < 0, "dwell: released once the 1 s dwell has elapsed");
    }

    private void SceneContract()
    {
        const string path = AtmosphereProjectorStreamer.ScenePathDefault;
        var scene = ResourceLoader.Load<PackedScene>(path);
        if (scene == null) { _failures.Add("cannot load " + path); return; }
        Node3D? root = scene.Instantiate() as Node3D;
        if (root == null) { _failures.Add("scene root is not a Node3D"); return; }
        try
        {
            Node? detail = root.GetNodeOrNull("Detail");
            Check(detail != null, "Detail node exists");
            if (detail != null)
            {
                var meshes = detail.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().ToList();
                Check(meshes.Count == 49, "Detail holds 49 MeshInstance3D, got " + meshes.Count);
                foreach (MeshInstance3D mesh in meshes)
                {
                    if (mesh.Mesh == null) { _failures.Add(mesh.Name + ": no mesh"); continue; }
                    for (int s = 0; s < mesh.Mesh.GetSurfaceCount(); s++)
                    {
                        Material? material = mesh.GetSurfaceOverrideMaterial(s) ?? mesh.MaterialOverride ?? mesh.Mesh.SurfaceGetMaterial(s);
                        Check(material is ShaderMaterial shaderMaterial && PoiMaterialParams.IsPoiShader(shaderMaterial), $"{mesh.Name} surface {s} is not a POI ShaderMaterial");
                    }
                }
            }
            var farProxy = root.GetNodeOrNull<MeshInstance3D>("FarProxy");
            Check(farProxy != null, "FarProxy exists");
            if (farProxy != null) Check(Math.Abs(farProxy.VisibilityRangeBegin - 14_000f) < 0.5f, "FarProxy visibility_range_begin is 14,000, got " + farProxy.VisibilityRangeBegin);
            Check(root.GetNodeOrNull("Collision") == null, "there is no Collision node");
            double end = AtmosphereProjectorStreamer.ReadDetailVisibleEnd(root);
            Check(Math.Abs(end - 16_000d) < 0.5d, "detail visible end is 16,000, got " + end.ToString(CultureInfo.InvariantCulture));
            double radius = AtmosphereProjectorStreamer.ComputeEffectiveLoadRadius(AtmosphereProjectorStreamer.DefaultLoadMeters, end, AtmosphereProjectorStandInLayer.DefaultHandoffMeters);
            Check(Math.Abs(radius - 16_500d) < 0.5d, "effective load radius with the defaults is 16,500, got " + radius.ToString(CultureInfo.InvariantCulture));
        }
        finally { root.Free(); }
    }

    private void StandInTransforms(in RingLodGeometrySnapshot snapshot)
    {
        var transforms = new Transform3D[_sites.Count];
        int count = _sites.FillWorldTransforms(snapshot, transforms);
        Check(count == 252, "FillWorldTransforms wrote 252, got " + count);
        double worst = 0d;
        for (int i = 0; i < transforms.Length; i++)
        {
            RimWallTopAnchor anchor = _sites[i];
            Vector3 expected = snapshot.CanonicalToWorld(anchor.AroundMeters, anchor.VerticalMeters, anchor.AcrossMeters);
            double tolerance = 1e-6 * expected.Length() + 0.01;
            double error = (transforms[i].Origin - expected).Length();
            worst = Math.Max(worst, error);
            if (error > tolerance) { _failures.Add($"site {i} origin is {error:F4} m off CanonicalToWorld (tolerance {tolerance:F4})"); break; }
        }
        GD.Print($"ATMOSPHERE_PROJECTOR_STREAMER worst stand-in origin error {worst:F4} m");
    }
}
