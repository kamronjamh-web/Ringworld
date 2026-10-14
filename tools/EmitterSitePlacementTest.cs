using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Godot;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// M1-3 placement adapter and frame math (docs/emitter-poi-streaming-spec.md D.1-a, b, c): entry bearing, up vector,
/// canonical round trip, rebase invariance, Terrain3D frame agreement, hub-converging beams and the batch fill.
/// Expectations come from the live registry; no registry identities or golden values are used.
/// </summary>
public partial class EmitterSitePlacementTest : RefCounted
{
    const double Circumference = EmitterSiteTestWorlds.Circumference;
    const double Width = EmitterSiteTestWorlds.Width;
    static readonly double Radius = Circumference / Math.Tau;
    const double VerticalBase = 50d, AcrossBase = 321d;
    const double Clearance = EmitterSiteFrameMath.DefaultPadClearanceMeters;

    static readonly Transform3D Grid = new(Basis.FromEuler(new Vector3(0.3f, 0.7f, -0.2f)), new Vector3(1000f, -50f, 20f));

    static RingLodGeometrySnapshot Snapshot(double anchorAround, double anchorVertical, double anchorAcross, bool identityGrid = false)
        => new(identityGrid ? Transform3D.Identity : Grid, identityGrid ? Vector3.Zero : new Vector3(3f, -2f, 1f),
            identityGrid ? Vector3.Zero : new Vector3(123.5f, -45.25f, 67f), Radius, AcrossBase, VerticalBase, Circumference, -8000d, 8000d,
            new RingCanonicalPosition(anchorAround, anchorVertical, anchorAcross));

    public int Run(bool unused)
    {
        var failures = new List<string>();
        try
        {
            string scene = File.ReadAllText(ProjectSettings.GlobalizePath("res://assets/poi/beam_emitter/BeamEmitterPOI.tscn"));
            if (!EmitterPoiMarkers.TryParse(scene, out EmitterPoiMarkers markers)) { Console.Error.WriteLine("EMITTER_SITE_PLACEMENT=FAIL marker parse"); return 1; }
            double markerDeg = Math.Atan2(markers.EntryApproach.Z, markers.EntryApproach.X) * 180d / Math.PI;
            if (Math.Abs(markerDeg - EmitterSitePlacementSource.AssetEntryBearingDegrees) > 0.01) failures.Add($"marker angle {markerDeg}");
            Console.WriteLine($"EMITTER_SITE_PLACEMENT markers crown={markers.BeamCrownFocus} origin={markers.BeamOrigin} shaft={markers.ShaftBottom} entry_deg={markerDeg:F4}");

            CheckMainMarkers(failures);
            foreach (bool gen17 in new[] { true, false })
            {
                string name = gen17 ? "gen17_v4" : "gen16_certified";
                using RingTerrainSampler sampler = gen17 ? EmitterSiteTestWorlds.CreateGen17(1337) : EmitterSiteTestWorlds.CreateGen16(1337);
                using INaturalHeightSource source = EmitterSiteHeightSources.Create(sampler);
                EmitterSiteRegistry registry = EmitterSiteRegistry.GetOrBuild(EmitterSiteRecipe.Default(1337), source);
                var placements = new EmitterSitePlacementSource(registry);
                Check(name, placements, markers, failures);
            }
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.ToString()); return 1; }
        if (failures.Count != 0) { Console.Error.WriteLine("EMITTER_SITE_PLACEMENT=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("EMITTER_SITE_PLACEMENT=PASS");
        return 0;
    }

    static void Check(string name, EmitterSitePlacementSource src, EmitterPoiMarkers markers, List<string> failures)
    {
        if (src.Count != 595) failures.Add($"{name}: count {src.Count}");
        if (src.GetPlacement(src.Count - 1).Kind != SiteKind.Main || src.Registry.MainIndex != src.Count - 1) failures.Add($"{name}: last placement is not the main site");
        int mainCount = 0;
        for (int i = 0; i < src.Count; i++) if (src.GetPlacement(i).Kind == SiteKind.Main) mainCount++;
        if (mainCount != 1) failures.Add($"{name}: {mainCount} main placements");
        if (src.GetPlacement(src.Count - 1).AcrossMeters != src.WidthMeters * 0.5d) failures.Add($"{name}: main placement not on the centre line");
        double worstEntry = 0, worstUp = 0, worstRound = 0, worstDet = 0, worstOrtho = 0, worstYaw = 0;
        for (int i = 0; i < src.Count; i++)
        {
            EmitterSitePlacement p = src.GetPlacement(i);
            EmitterSite site = src.Registry[i];
            worstYaw = Math.Max(worstYaw, Math.Abs(((p.YawDegrees - site.YawDegrees) % 360d + 540d) % 360d - 180d));
            // Per-site anchor near the site (a few km away, like a player standing in the area).
            RingLodGeometrySnapshot snap = Snapshot(p.AroundMeters + 1500d, p.PadHeightMeters + 20d, p.AcrossMeters - 900d);
            Transform3D t = src.GetWorldTransform(snap, i);

            // Independent canonical directions in world space from double-precision local differences.
            (double ax0, double ay0, double az0) = snap.CanonicalToAbsoluteLocal(p.AroundMeters - 0.5, p.PadHeightMeters, p.AcrossMeters);
            (double ax1, double ay1, double az1) = snap.CanonicalToAbsoluteLocal(p.AroundMeters + 0.5, p.PadHeightMeters, p.AcrossMeters);
            Vector3 aroundDir = (snap.GridGlobalTransform.Basis * new Vector3((float)(ax1 - ax0), (float)(ay1 - ay0), (float)(az1 - az0))).Normalized();
            Vector3 acrossDir = snap.GridGlobalTransform.Basis * Vector3.Up; // local Y = across
            Vector3 entry = t.Basis * markers.EntryApproach;
            double bearing = Math.Atan2(entry.Dot(acrossDir), entry.Dot(aroundDir)) * 180d / Math.PI;
            double diff = Math.Abs(((bearing - p.BearingDegrees) % 360d + 540d) % 360d - 180d);
            worstEntry = Math.Max(worstEntry, diff);
            if (diff > 0.01) failures.Add($"{name}: site {i} entry bearing {bearing:F4} != {p.BearingDegrees:F4}");

            // Up points at the ring axis (hub line at this site's across).
            (double sx, double sy, double sz) = snap.CanonicalToAbsoluteLocal(p.AroundMeters, p.PadHeightMeters + Clearance, p.AcrossMeters);
            (double hx, double hy, double hz) = snap.CanonicalToAbsoluteLocal(p.AroundMeters, Radius - VerticalBase, p.AcrossMeters);
            Vector3 toAxis = (snap.GridGlobalTransform.Basis * new Vector3((float)(hx - sx), (float)(hy - sy), (float)(hz - sz))).Normalized();
            double upAngle = Math.Atan2(t.Basis.Y.Cross(toAxis).Length(), t.Basis.Y.Dot(toAxis));
            worstUp = Math.Max(worstUp, upAngle);
            if (upAngle > 1e-5) failures.Add($"{name}: site {i} up angle {upAngle}");

            // det +1, orthonormal.
            worstDet = Math.Max(worstDet, Math.Abs(t.Basis.Determinant() - 1d));
            worstOrtho = Math.Max(worstOrtho, Math.Max(Math.Abs(t.Basis.X.Length() - 1d), Math.Max(Math.Abs(t.Basis.Y.Dot(t.Basis.X)), Math.Abs(t.Basis.Z.Dot(t.Basis.X)))));

            // Position equals CanonicalToWorld and round-trips through the inverse mapping.
            Vector3 expected = snap.CanonicalToWorld(p.AroundMeters, p.PadHeightMeters + Clearance, p.AcrossMeters);
            if (t.Origin.DistanceTo(expected) > 0.001f) failures.Add($"{name}: site {i} origin differs from CanonicalToWorld by {t.Origin.DistanceTo(expected)}");
            (double ra, double rv, double rx) = WorldToCanonical(snap, t.Origin);
            double da = Math.Abs(WrapDelta(ra - p.AroundMeters)), dv = Math.Abs(rv - (p.PadHeightMeters + Clearance)), dx = Math.Abs(rx - p.AcrossMeters);
            double round = Math.Max(da, Math.Max(dv, dx));
            worstRound = Math.Max(worstRound, round);
            if (round > 0.01) failures.Add($"{name}: site {i} round trip {round}");
        }
        if (worstYaw > 1e-3) failures.Add($"{name}: placement yaw vs registry YawDegrees {worstYaw}");
        if (worstDet > 1e-5 || worstOrtho > 1e-5) failures.Add($"{name}: basis det/ortho {worstDet}/{worstOrtho}");
        Console.WriteLine($"EMITTER_SITE_PLACEMENT {name} entry_bearing_worst_deg={worstEntry:F5} up_worst_deg={worstUp * 180d / Math.PI:F6} roundtrip_worst_m={worstRound:F5} det_worst={worstDet:E2} ortho_worst={worstOrtho:E2} yaw_vs_registry_deg={worstYaw:E2}");

        CheckRebase(name, src, failures);
        CheckTerrain3D(name, src, failures);
        CheckBeams(name, src, markers, failures);
        CheckBatch(name, src, failures);
    }

    static double WrapDelta(double d) { d %= Circumference; if (d > Circumference / 2) d -= Circumference; else if (d < -Circumference / 2) d += Circumference; return d; }

    /// <summary>Inverse of the snapshot mapping (double): world -> relative local -> absolute local -> canonical.</summary>
    static (double Around, double Vertical, double Across) WorldToCanonical(RingLodGeometrySnapshot g, Vector3 world)
    {
        Vector3 origin = g.GridGlobalTransform.Origin - g.FloatingOriginWorldOffset;
        Vector3 rel = g.GridGlobalTransform.Basis.Transposed() * (world - origin);
        (double ax, double ay, double az) = g.CanonicalToAbsoluteLocal(g.FloatingOriginCanonicalAnchor!.Value.AroundMeters, g.FloatingOriginCanonicalAnchor.Value.VerticalMeters, g.FloatingOriginCanonicalAnchor.Value.AcrossMeters);
        double x = rel.X + ax - g.RingCenterLocalOffset.X, y = rel.Y + ay - g.RingCenterLocalOffset.Y, z = rel.Z + az - g.RingCenterLocalOffset.Z;
        double around = Math.Atan2(z, x) * g.RingRadiusMeters;
        if (around < 0) around += g.CircumferenceMeters;
        double radial = Math.Sqrt(x * x + z * z);
        return (around, g.RingRadiusMeters - g.RingVerticalBaseOffsetMeters - radial, y - g.RingAcrossBaseOffsetMeters);
    }

    static double AngleBetween(Basis a, Basis b)
    {
        Quaternion q = (a.Inverse() * b).GetRotationQuaternion();
        return 2d * Math.Atan2(new Vector3(q.X, q.Y, q.Z).Length(), Math.Abs(q.W)) * 180d / Math.PI;
    }

    static void CheckRebase(string name, EmitterSitePlacementSource src, List<string> failures)
    {
        // Sites: nearest to the seam on both sides, nearest to 30,000 km of accumulated travel, plus an evenly spread set.
        var ids = new SortedSet<int>();
        ids.Add(Enumerable.Range(0, src.Count).OrderBy(i => src.GetPlacement(i).AroundMeters).First());
        ids.Add(Enumerable.Range(0, src.Count).OrderByDescending(i => src.GetPlacement(i).AroundMeters).First());
        const double Travelled = 30_000_000d;
        ids.Add(Enumerable.Range(0, src.Count).OrderBy(i => Math.Abs(WrapDelta(src.GetPlacement(i).AroundMeters - Travelled))).First());
        for (int i = 3; i < src.Count; i += 61) ids.Add(i);
        ids.Add(src.Count - 1); // main site
        double worstPos = 0, worstRot = 0; int cases = 0;
        foreach (int id in ids)
        {
            EmitterSitePlacement p = src.GetPlacement(id);
            double a = p.AroundMeters, v = p.PadHeightMeters, x = p.AcrossMeters;
            var anchors = new List<(double, double, double)>
            {
                (a, v, x), (a + 3000d, v + 5d, x + 700d), (a - 18_000d, v, x - 9000d), (a + 21_000d, v + 200d, x + 12_000d),
                (a + Circumference, v, x), (a - Circumference + 500d, v, x), (a + 955d * Circumference, v, x - 40d),
                (a - 3d * Circumference + 11d, v + 1d, x + 8d), (a + 2d * Circumference - 7d, v - 1d, x),
            };
            // Anchors at the ring seam itself and at the unwrapped 30,000 km position nearest this site.
            if (Math.Min(a, Circumference - a) < 40_000d) { anchors.Add((0d, v, x)); anchors.Add((Circumference - 2d, v, x)); anchors.Add((-1d, v, x + 3d)); }
            double nearTravel = Travelled + WrapDelta(a - Travelled);
            if (Math.Abs(WrapDelta(a - Travelled)) < 30_000d) anchors.Add((nearTravel + 150d, v, x));

            (double a, double v, double x) refCanon = (a + 300d, v + 10d, x - 200d);
            Vector3? basePos = null; Basis? baseRot = null;
            foreach ((double aa, double av, double ax) in anchors)
            {
                RingLodGeometrySnapshot snap = Snapshot(aa, av, ax);
                Transform3D t = src.GetWorldTransform(snap, id);
                Vector3 reference = snap.CanonicalToWorld(refCanon.a, refCanon.v, refCanon.x);
                Basis refBasis = EmitterSiteFrameMath.ComputeSurfaceBasis(snap, refCanon.a);
                Vector3 relPos = t.Basis.Inverse() * (reference - t.Origin);
                Basis relRot = refBasis.Inverse() * t.Basis;
                if (basePos == null) { basePos = relPos; baseRot = relRot; continue; }
                double dp = relPos.DistanceTo(basePos.Value), dr = AngleBetween(baseRot!.Value, relRot);
                worstPos = Math.Max(worstPos, dp); worstRot = Math.Max(worstRot, dr); cases++;
                if (dp > 0.01) failures.Add($"{name}: rebase site {id} (A={a:F0}) anchor ({aa:F0},{av:F0},{ax:F0}) relative position drift {dp:F4} m");
                if (dr > 0.001) failures.Add($"{name}: rebase site {id} anchor ({aa:F0}) relative rotation drift {dr:F6} deg");
            }
        }
        Console.WriteLine($"EMITTER_SITE_PLACEMENT {name} rebase sites={ids.Count} cases={cases} worst_rel_pos_m={worstPos:F5} worst_rel_rot_deg={worstRot:E2}");
    }

    static void CheckTerrain3D(string name, EmitterSitePlacementSource src, List<string> failures)
    {
        // Terrain3D window code: RingTerrain3DGameplay.BuildFrame/BuildWorldFrame (private; mirrored here) then
        // worldFrame * RingSurfaceFrame.MapPosition(...). Frames anchored at the site and 400 m away along the ring.
        double worstCanonical = 0, worstFlat = 0, worstBasis = 0, worstOrigin = 0; int points = 0;
        foreach (int id in new[] { 0, 111, 300, 593, 594, Enumerable.Range(0, src.Count).OrderBy(i => src.GetPlacement(i).AroundMeters).First() })
        {
            EmitterSitePlacement p = src.GetPlacement(id);
            foreach (long frameOffset in new long[] { 0, 400 })
            {
                long anchorA = (long)p.AroundMeters + frameOffset; int anchorX = (int)p.AcrossMeters; float anchorH = (float)p.PadHeightMeters;
                RingLodGeometrySnapshot snap = Snapshot(anchorA, anchorH, anchorX);
                var frame = new RingSurfaceFrame(anchorA, anchorX, anchorH, (long)Circumference, Radius, 2, -1);
                Transform3D worldFrame = BuildWorldFrame(snap, frame);
                Transform3D site = src.GetWorldTransform(snap, id);

                Vector3 terrainOrigin = worldFrame * frame.MapPosition(p.AroundMeters, p.AcrossMeters, p.PadHeightMeters + Clearance);
                double o = terrainOrigin.DistanceTo(site.Origin); worstOrigin = Math.Max(worstOrigin, o);
                if (o > 0.01) failures.Add($"{name}: site {id} frame+{frameOffset} origin differs from Terrain3D by {o:F4} m");
                if (frameOffset == 0)
                {
                    double ab = AngleBetween(worldFrame.Basis * new Basis(Vector3.Up, (float)p.YawRadians), site.Basis);
                    worstBasis = Math.Max(worstBasis, ab);
                    if (ab > 0.001) failures.Add($"{name}: site {id} basis differs from Terrain3D*yaw by {ab:F6} deg");
                }
                Basis yaw = new((Vector3.Up), (float)p.YawRadians);
                foreach (double r in new[] { 0d, 200d, 350d })
                    for (int k = 0; k < 8; k++)
                    {
                        double az = k * Math.PI / 4d;
                        double dAround = r * Math.Cos(az), dAcross = r * Math.Sin(az);
                        double canonA = p.AroundMeters + dAround, canonX = p.AcrossMeters + dAcross, canonV = p.PadHeightMeters + Clearance;
                        Vector3 terrain = worldFrame * frame.MapPosition(canonA, canonX, canonV);
                        Vector3 exact = snap.CanonicalToWorld(canonA, canonV, canonX);
                        double d1 = terrain.DistanceTo(exact); worstCanonical = Math.Max(worstCanonical, d1);
                        if (d1 > 0.01) failures.Add($"{name}: site {id} Terrain3D vs CanonicalToWorld {d1:F4} m at r={r}");
                        // Flat asset-local point whose yaw-rotated offset is (dAround, -dAcross) (spec A.2 teleport convention).
                        Vector3 local = yaw.Inverse() * new Vector3((float)dAround, 0f, (float)-dAcross);
                        Vector3 viaSite = site * local;
                        double d2 = terrain.DistanceTo(viaSite); worstFlat = Math.Max(worstFlat, d2);
                        // Flat asset plane vs curved ring: sagitta r^2/2R plus the arc-length scale at pad height, r*P/R.
                        double allowed = 0.01 + r * r / (2d * Radius) + r * p.PadHeightMeters / Radius;
                        if (d2 > allowed) failures.Add($"{name}: site {id} flat asset point vs Terrain3D {d2:F4} m at r={r}");
                        points++;
                    }
            }
        }
        Console.WriteLine($"EMITTER_SITE_PLACEMENT {name} terrain3d points={points} origin_worst_m={worstOrigin:F5} canonical_point_worst_m={worstCanonical:F5} flat_asset_point_worst_m={worstFlat:F5} basis_worst_deg={worstBasis:E2}");
    }

    /// <summary>Mirror of RingTerrain3DGameplay.BuildWorldFrame (scripts/Terrain/Surface/RingTerrain3DGameplay.cs:383).</summary>
    static Transform3D BuildWorldFrame(RingLodGeometrySnapshot geometry, RingSurfaceFrame frame)
    {
        double phi = frame.AnchorAroundMeters / geometry.RingRadiusMeters;
        Basis gridBasis = geometry.GridGlobalTransform.Basis;
        Vector3 tangent = (gridBasis * new Vector3((float)-Math.Sin(phi), 0, (float)Math.Cos(phi))).Normalized();
        Vector3 inward = (gridBasis * new Vector3((float)-Math.Cos(phi), 0, (float)-Math.Sin(phi))).Normalized();
        return new Transform3D(new Basis(tangent, inward, (gridBasis * Vector3.Down).Normalized()),
            geometry.CanonicalToWorld(frame.AnchorAroundMeters, frame.AnchorHeight, frame.AnchorAcrossMeters));
    }

    static void CheckBeams(string name, EmitterSitePlacementSource src, EmitterPoiMarkers markers, List<string> failures)
    {
        double hubAcross = src.WidthMeters * 0.5;
        double mainTiltMax = 0d;
        if (markers.BeamCrownFocus.Y != EmitterSiteFrameMath.DefaultCrownHeightMeters) failures.Add($"{name}: regular crown marker {markers.BeamCrownFocus.Y} != {EmitterSiteFrameMath.DefaultCrownHeightMeters}");
        if (markers.BeamCrownFocus.X != 0f || markers.BeamCrownFocus.Z != 0f || markers.BeamOrigin != Vector3.Zero) failures.Add($"{name}: markers not on the spire axis");
        double worstMiss = 0, worstTilt = 0, maxTilt = 0, worstOrigin = 0, worstAnalytic = 0, worstUp = 0;
        for (int i = 0; i < src.Count; i++)
        {
            EmitterSitePlacement p = src.GetPlacement(i);
            double crown = src.GetCrownHeightMeters(i);
            RingLodGeometrySnapshot snap = Snapshot(p.AroundMeters - 2500d, p.PadHeightMeters + 15d, p.AcrossMeters + 1100d);
            EmitterBeamGeometry beam = EmitterSiteFrameMath.GetBeamGeometry(snap, p, hubAcross, crown);

            // Independent hub position in anchor-relative world space (double): axis point at radial distance 0.
            var anchor = snap.FloatingOriginCanonicalAnchor!.Value;
            (double hx, double hy, double hz) = snap.CanonicalToAbsoluteLocal(0d, Radius - VerticalBase, hubAcross);
            (double qx, double qy, double qz) = snap.CanonicalToAbsoluteLocal(anchor.AroundMeters, anchor.VerticalMeters, anchor.AcrossMeters);
            Vector3 gx = snap.GridGlobalTransform.Basis.X, gy = snap.GridGlobalTransform.Basis.Y, gz = snap.GridGlobalTransform.Basis.Z;
            Vector3D hub = new(gx.X * (hx - qx) + gy.X * (hy - qy) + gz.X * (hz - qz) + (snap.GridGlobalTransform.Origin - snap.FloatingOriginWorldOffset).X,
                gx.Y * (hx - qx) + gy.Y * (hy - qy) + gz.Y * (hz - qz) + (snap.GridGlobalTransform.Origin - snap.FloatingOriginWorldOffset).Y,
                gx.Z * (hx - qx) + gy.Z * (hy - qy) + gz.Z * (hz - qz) + (snap.GridGlobalTransform.Origin - snap.FloatingOriginWorldOffset).Z);
            Vector3D origin = new(beam.OriginWorld.X, beam.OriginWorld.Y, beam.OriginWorld.Z);
            Vector3D toHub = hub - origin;
            double along = toHub.Dot(beam.Direction);
            double miss = (toHub - beam.Direction * along).Length;
            worstMiss = Math.Max(worstMiss, miss);
            if (along <= 0 || miss > 0.05) failures.Add($"{name}: site {i} beam misses the hub by {miss:F4} m (along {along:F0})");
            if (Math.Abs(beam.Direction.Length - 1d) > 1e-12) failures.Add($"{name}: site {i} beam direction not unit");

            Transform3D site = EmitterSiteFrameMath.ComputeSiteTransform(snap, p);
            double originErr = (site * new Vector3(0f, (float)crown, 0f)).DistanceTo(beam.OriginWorld);
            worstOrigin = Math.Max(worstOrigin, originErr);
            if (originErr > 0.01) failures.Add($"{name}: site {i} beam origin vs site*BeamCrownFocus {originErr:F4} m");
            var siteUp = new Vector3D(site.Basis.Y.X, site.Basis.Y.Y, site.Basis.Y.Z);
            double upErr = Math.Atan2(new Vector3D(beam.Up.Y * siteUp.Z - beam.Up.Z * siteUp.Y, beam.Up.Z * siteUp.X - beam.Up.X * siteUp.Z, beam.Up.X * siteUp.Y - beam.Up.Y * siteUp.X).Length, beam.Up.Dot(siteUp)) * 180d / Math.PI;
            worstUp = Math.Max(worstUp, upErr);

            double analytic = EmitterSiteFrameMath.GetBeamTiltDegrees(p.AcrossMeters, hubAcross, Radius, VerticalBase, p.PadHeightMeters + Clearance + crown);
            worstAnalytic = Math.Max(worstAnalytic, Math.Abs(analytic - beam.TiltDegrees));
            maxTilt = Math.Max(maxTilt, beam.TiltDegrees);
            if (beam.TiltDegrees > 1.6) failures.Add($"{name}: site {i} tilt {beam.TiltDegrees:F4} deg > 1.6");
            if (p.Kind == SiteKind.Main)
            {
                mainTiltMax = Math.Max(mainTiltMax, beam.TiltDegrees);
                double axisAngle = Math.Atan2(new Vector3D(beam.Direction.Y * beam.Up.Z - beam.Direction.Z * beam.Up.Y, beam.Direction.Z * beam.Up.X - beam.Direction.X * beam.Up.Z, beam.Direction.X * beam.Up.Y - beam.Direction.Y * beam.Up.X).Length, beam.Direction.Dot(beam.Up)) * 180d / Math.PI;
                if (beam.TiltDegrees >= 0.01 || analytic >= 0.01 || axisAngle >= 0.01) failures.Add($"{name}: main beam tilt {beam.TiltDegrees:E3} / analytic {analytic:E3} / axis angle {axisAngle:E3} deg is not ~0");
                if (crown != EmitterSiteRecipe.Default(1337).MainCrownHeightM) failures.Add($"{name}: main crown {crown}");
            }
            else if (p.AcrossMeters != hubAcross && beam.TiltDegrees <= 0d) failures.Add($"{name}: regular site {i} unexpectedly has zero tilt");
            worstTilt = Math.Max(worstTilt, beam.TiltDegrees);
        }
        if (worstAnalytic > 1e-6) failures.Add($"{name}: tilt vs analytic {worstAnalytic}");
        if (worstUp > 1e-4) failures.Add($"{name}: beam up vs site basis {worstUp} deg");
        Console.WriteLine($"EMITTER_SITE_PLACEMENT {name} main beam tilt_deg={mainTiltMax:E3} crown_m={src.GetCrownHeightMeters(src.Count - 1)} (marker source: {MainCrownSource})");
        Console.WriteLine($"EMITTER_SITE_PLACEMENT {name} beams hub_miss_worst_m={worstMiss:F5} max_tilt_deg={maxTilt:F4} tilt_vs_analytic_worst_deg={worstAnalytic:E2} origin_vs_site_crown_worst_m={worstOrigin:F5}");
    }

    static void CheckBatch(string name, EmitterSitePlacementSource src, List<string> failures)
    {
        var buffer = new Transform3D[src.Count];
        RingLodGeometrySnapshot snap = Snapshot(src.GetPlacement(5).AroundMeters + 100d, 30d, src.GetPlacement(5).AcrossMeters);
        src.FillWorldTransforms(snap, buffer);
        double worstSingle = 0, worstSnap = 0;
        for (int i = 0; i < src.Count; i++)
        {
            Transform3D single = src.GetWorldTransform(snap, i);
            worstSingle = Math.Max(worstSingle, Math.Max(single.Origin.DistanceTo(buffer[i].Origin), (single.Basis.X - buffer[i].Basis.X).Length() + (single.Basis.Y - buffer[i].Basis.Y).Length() + (single.Basis.Z - buffer[i].Basis.Z).Length()));
            EmitterSitePlacement p = src.GetPlacement(i);
            worstSnap = Math.Max(worstSnap, buffer[i].Origin.DistanceTo(snap.CanonicalToWorld(p.AroundMeters, p.PadHeightMeters + Clearance, p.AcrossMeters)));
        }
        if (worstSingle > 1e-4) failures.Add($"{name}: batch differs from single by {worstSingle}");
        // Anchor-relative world positions of far sites (up to ~15,000 km away) lose float precision by design; only near ones are checked.
        double nearWorst = 0;
        for (int i = 0; i < src.Count; i++)
        {
            EmitterSitePlacement p = src.GetPlacement(i);
            if (buffer[i].Origin.Length() > 20_000f) continue;
            nearWorst = Math.Max(nearWorst, buffer[i].Origin.DistanceTo(snap.CanonicalToWorld(p.AroundMeters, p.PadHeightMeters + Clearance, p.AcrossMeters)));
        }
        if (nearWorst > 0.005) failures.Add($"{name}: batch origin vs CanonicalToWorld {nearWorst}");

        // Cost and allocation.
        for (int i = 0; i < 50; i++) src.FillWorldTransforms(snap, buffer);
        const int Iterations = 400;
        var times = new double[Iterations];
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            src.FillWorldTransforms(snap, buffer);
            times[i] = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        Array.Sort(times);
        if (allocated != 0) failures.Add($"{name}: batch allocated {allocated} bytes");
        Console.WriteLine($"EMITTER_SITE_PLACEMENT {name} batch count={src.Count} median_us={times[Iterations / 2]:F1} p95_us={times[Iterations * 95 / 100]:F1} min_us={times[0]:F1} allocated_bytes={allocated} worst_vs_single={worstSingle:E2} worst_vs_canonical_to_world_m={worstSnap:F1} near_worst_m={nearWorst:E2} ({(IsOptimized() ? "optimized" : "debug")} assembly)");
    }

    static string MainCrownSource = "recipe crown; no MainSpirePOI.tscn found";

    /// <summary>If a main spire POI scene exists, its BeamCrownFocus/BeamOrigin markers must agree with the recipe crown (3,989 m, on axis).</summary>
    static void CheckMainMarkers(List<string> failures)
    {
        foreach (string candidate in new[] { "res://assets/poi/main_spire/MainSpirePOI.tscn", "res://scenes/poi/MainSpirePOI.tscn", "res://assets/poi/beam_emitter/MainSpirePOI.tscn" })
        {
            string path = ProjectSettings.GlobalizePath(candidate);
            if (!File.Exists(path)) continue;
            if (!EmitterPoiMarkers.TryParse(File.ReadAllText(path), out EmitterPoiMarkers m)) { failures.Add("main POI markers not parseable: " + candidate); return; }
            MainCrownSource = candidate + " BeamCrownFocus";
            if (Math.Abs(m.BeamCrownFocus.Y - EmitterSiteRecipe.Default(1337).MainCrownHeightM) > 0.5 || m.BeamCrownFocus.X != 0f || m.BeamCrownFocus.Z != 0f)
                failures.Add($"main POI BeamCrownFocus {m.BeamCrownFocus} != (0, {EmitterSiteRecipe.Default(1337).MainCrownHeightM}, 0)");
            double entry = Math.Atan2(m.EntryApproach.Z, m.EntryApproach.X) * 180d / Math.PI;
            if (Math.Abs(entry - EmitterSitePlacementSource.AssetEntryBearingDegrees) > 0.05) failures.Add($"main POI EntryApproach angle {entry}");
            return;
        }
    }

    static bool IsOptimized()
    {
        foreach (object attr in typeof(EmitterSitePlacementTest).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false))
            if (attr is DebuggableAttribute d) return !d.IsJITOptimizerDisabled;
        return true;
    }
}
