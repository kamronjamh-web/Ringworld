using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Celestial.Eclipse;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Phase E4e of docs/eclipse-false-sun-spec.md (E.6, E.7): in-cloud diffusion of the near beams, the stand-in crown glow and the bore light boost.
/// Weather queries are counted (nearest fired spires within the near range, at most 30 per eclipse, none per frame after), the MultiMesh COLOR holds
/// (k, deck base, deck top, queried) and is written once per query; the crown glow lane is written 3 times per beam per eclipse (fire, hand-off, cut) and
/// never per frame; the bore plug light follows the beam's crown level; the CPU twin of the cloud maths is checked at its edges.
/// </summary>
public partial class EclipseHubBeamLayerTest
{
    static RingVolumetricCloudLayer.LocalWeatherState DeckWeather(float coverage = 0.8f, float baseM = 2000f, float topM = 6000f)
        => default(RingVolumetricCloudLayer.LocalWeatherState) with { CloudCoverage = coverage, CloudBaseAltitudeMeters = baseM, CloudTopAltitudeMeters = topM };

    static EclipseEventTimeline NewEclipse(EclipseEventTimeline template, int index)
        => new(new EclipseEventSettings(), new EclipseEventWindow(index, template.Window.Start, template.Window.End), template.Sites, template.GridClock, 1337);

    static void CheckClouds(EclipseHubBeamLayer layer, EclipseEventTimeline timeline, EmitterGridTopology topology, EmitterSitePlacementSource placements,
        VoxelChunkGridManager grid, Camera3D camera, Node3D fixture, List<string> failures)
    {
        // ---- the pure maths: deck mask, factor, core keep (CPU twin of ehb_cloud_*)
        Expect(failures, EclipseHubBeamMath.CloudMask(-500f, 1000f, 5000f) == 0f && EclipseHubBeamMath.CloudMask(3000f, 1000f, 5000f) == 1f && EclipseHubBeamMath.CloudMask(9000f, 1000f, 5000f) == 0f
            && Math.Abs(EclipseHubBeamMath.CloudMask(1000f, 1000f, 5000f) - 0.5f) < 1e-6f && Math.Abs(EclipseHubBeamMath.CloudMask(5000f, 1000f, 5000f) - 0.5f) < 1e-6f, "cloud mask edges");
        Expect(failures, EclipseHubBeamMath.CloudFactor(new Color(0.8f, 1000f, 5000f, 0f), 3000f) == 0f && EclipseHubBeamMath.CloudFactor(new Color(0f, 1000f, 5000f, 1f), 3000f) == 0f
            && EclipseHubBeamMath.CloudFactor(new Color(0.8f, 1000f, 5000f, 1f), 3000f, enabled: false) == 0f
            && Math.Abs(EclipseHubBeamMath.CloudFactor(new Color(0.8f, 1000f, 5000f, 1f), 3000f) - 0.8f) < 1e-6f, "cloud factor flags");
        Expect(failures, EclipseHubBeamMath.CloudCoreKeep(0f, 0.5f) == 1f && EclipseHubBeamMath.CloudCoreKeep(0.7f, 0.5f) == 0f && Math.Abs(EclipseHubBeamMath.CloudCoreKeep(0.5f, 0.5f) - 0.5f) < 1e-6f
            && EclipseHubBeamMath.CloudCoreKeep(1f, 1.5f) == 1f, "cloud core keep (a cut above 1 never fires)");
        Color encoded = EclipseHubBeamMath.EncodeCloud(1.7d, 2000d, 6000d, 1750d);
        Expect(failures, encoded.R == 1f && encoded.G == 250f && encoded.B == 4250f && encoded.A == 1f, $"cloud encoding {encoded}");

        // ---- queries: nearest fired spires within the near range, counted, none per frame afterwards
        layer.ApplyBeamSettings(new EclipseBeamSettings());
        layer.RefuelFrontClock = null;
        int queries = 0;
        layer.WeatherQueryOverride = (position, time) => { queries++; return DeckWeather(); };
        layer.CloudNearRangeOverrideMeters = null;
        layer.BindTimeline(NewEclipse(timeline, 11));
        EclipseEventTimeline eclipse = layer.BoundTimeline!;
        double tau = eclipse.TauHandoff - 2d;
        int focus = 60;
        Vector3D focusCrown = layer.GetWorldOrigin(focus);
        camera.GlobalPosition = new Vector3((float)focusCrown.X, (float)focusCrown.Y + 50f, (float)focusCrown.Z);
        long colorBefore = layer.ColorWriteCount;
        int[] expected = NearestFired(layer, eclipse, tau, camera.GlobalPosition, 700_000d, EclipseHubBeamLayer.MaxWeatherQueries);
        layer.ApplyEventTime(tau, true, 0.3d);
        Expect(failures, expected.Length > 0 && expected.Length <= EclipseHubBeamLayer.MaxWeatherQueries, $"expected near spire count {expected.Length}");
        Expect(failures, queries == expected.Length && layer.WeatherQueryCount == expected.Length, $"weather queries {queries}/{layer.WeatherQueryCount}, expected {expected.Length}");
        Expect(failures, layer.ColorWriteCount == colorBefore + expected.Length, $"color writes {layer.ColorWriteCount - colorBefore}");
        var queriedSet = new HashSet<int>();
        for (int i = 0; i < layer.BeamCount; i++) if (layer.GetCloudMirror(i).A > 0.5f) queriedSet.Add(i);
        Expect(failures, queriedSet.SetEquals(expected), "the queried beams are not the nearest fired spires within range");
        if (expected.Length > 0)
        {
            int b = expected[0];
            Color mirror = layer.GetCloudMirror(b);
            double originAltitude = topology.HubBeamOrigin(b, layer.Beams.BeamOrigin).Vertical;
            Expect(failures, Math.Abs(mirror.R - 0.8f) < 1e-6f && Math.Abs(mirror.G - (2000d - originAltitude)) < 0.5d && Math.Abs(mirror.B - (6000d - originAltitude)) < 0.5d && mirror.A == 1f, $"cloud colour {mirror}");
        }
        Console.WriteLine($"ECLIPSE_HUB_BEAM_LAYER cloud near_range queries={queries} (expected {expected.Length}, cap {EclipseHubBeamLayer.MaxWeatherQueries}) colour_writes={layer.ColorWriteCount - colorBefore} steady_frames_queries=0");
        // The spire's query uses the crown's around and across, at vertical 0.
        long steadyColor = layer.ColorWriteCount;
        int steadyQueries = queries;
        for (int frame = 0; frame < 240; frame++) layer.ApplyEventTime(tau + frame * 0.05d, true, 0.3d);
        Expect(failures, queries == steadyQueries && layer.ColorWriteCount == steadyColor, $"per-frame cloud work: queries +{queries - steadyQueries}, colour writes +{layer.ColorWriteCount - steadyColor}");

        // ---- a beam origin change re-encodes the queried decks relative to the new origin (one colour write each, no query, no custom data)
        {
            EmitterBeamOrigin startOrigin = layer.Beams.BeamOrigin;
            long data = layer.CustomDataWriteCount, colors = layer.ColorWriteCount;
            int binds = layer.TimelineBindCount;
            var mirrorsBefore = expected.ToDictionary(i => i, i => layer.GetCloudMirror(i));
            EmitterBeamOrigin other = startOrigin == EmitterBeamOrigin.Crown ? EmitterBeamOrigin.Base : EmitterBeamOrigin.Crown;
            layer.ApplyBeamSettings(layer.Beams with { BeamOrigin = other });
            Expect(failures, layer.ColorWriteCount == colors + expected.Length && queries == steadyQueries && layer.CustomDataWriteCount == data && layer.TimelineBindCount == binds,
                $"origin switch: colour writes +{layer.ColorWriteCount - colors} (expected {expected.Length}), queries +{queries - steadyQueries}, custom data +{layer.CustomDataWriteCount - data}");
            double worstShift = 0d, worstAbs = 0d;
            foreach (int b in expected)
            {
                Color now = layer.GetCloudMirror(b);
                double newAltitude = topology.HubBeamOrigin(b, other).Vertical, oldAltitude = topology.HubBeamOrigin(b, startOrigin).Vertical;
                worstAbs = Math.Max(worstAbs, Math.Max(Math.Abs(now.G - (2000d - newAltitude)), Math.Abs(now.B - (6000d - newAltitude))));
                worstShift = Math.Max(worstShift, Math.Abs((now.G - mirrorsBefore[b].G) - (oldAltitude - newAltitude)));
                if (now.R != 0.8f || now.A != 1f) failures.Add($"origin switch changed the coverage or flag of beam {b}: {now}");
            }
            Expect(failures, worstAbs < 0.5d && worstShift < 0.5d, $"origin switch re-encode: abs {worstAbs:F3} m, shift {worstShift:F3} m");
            Console.WriteLine($"ECLIPSE_HUB_BEAM_LAYER cloud origin_switch {startOrigin}->{other} re_encoded={expected.Length} worst_abs_m={worstAbs:F3} worst_shift_m={worstShift:F3}");
            layer.ApplyBeamSettings(layer.Beams with { BeamOrigin = startOrigin });
            double worstBack = 0d;
            foreach (int b in expected) worstBack = Math.Max(worstBack, Math.Abs(layer.GetCloudMirror(b).G - mirrorsBefore[b].G));
            Expect(failures, worstBack < 0.5d, $"origin round trip drift {worstBack:F3} m");
            steadyColor = layer.ColorWriteCount;
        }

        // ---- the cap: a huge near range never exceeds 30 queries per eclipse, nearest first
        EclipseEventTimeline second = NewEclipse(timeline, 12);
        layer.BindTimeline(second);
        Expect(failures, layer.WeatherQueryCount == 0 && queriedSet.All(i => layer.GetCloudMirror(i).A == 0f), "a new eclipse did not reset the cloud queries");
        queries = 0;
        layer.CloudNearRangeOverrideMeters = 1e9d;
        long colorBeforeCap = layer.ColorWriteCount;
        expected = NearestFired(layer, second, tau, camera.GlobalPosition, 1e9d, EclipseHubBeamLayer.MaxWeatherQueries);
        for (int frame = 0; frame < 20; frame++) layer.ApplyEventTime(tau, true, 0.3d);
        Expect(failures, queries == EclipseHubBeamLayer.MaxWeatherQueries && layer.WeatherQueryCount == EclipseHubBeamLayer.MaxWeatherQueries, $"cap: {queries} queries, expected {EclipseHubBeamLayer.MaxWeatherQueries}");
        Expect(failures, layer.ColorWriteCount == colorBeforeCap + EclipseHubBeamLayer.MaxWeatherQueries, $"cap colour writes {layer.ColorWriteCount - colorBeforeCap}");
        Console.WriteLine($"ECLIPSE_HUB_BEAM_LAYER cloud cap queries={queries} colour_writes={layer.ColorWriteCount - colorBeforeCap}");
        queriedSet.Clear();
        for (int i = 0; i < layer.BeamCount; i++) if (layer.GetCloudMirror(i).A > 0.5f) queriedSet.Add(i);
        Expect(failures, queriedSet.SetEquals(expected), "the capped queries are not the 30 nearest spires");

        // A timeline-setting change inside the same eclipse (same window index) neither resets nor repeats the queries.
        int before = queries;
        layer.BindTimeline(NewEclipse(timeline, 12));
        for (int frame = 0; frame < 10; frame++) layer.ApplyEventTime(tau, true, 0.3d);
        Expect(failures, queries == before && layer.WeatherQueryCount == EclipseHubBeamLayer.MaxWeatherQueries, "a rebind inside one eclipse repeated queries");

        // Before the fire nothing is queried, and a disabled feature queries nothing.
        layer.CloudNearRangeOverrideMeters = null;
        layer.BindTimeline(NewEclipse(timeline, 13));
        queries = 0;
        layer.ApplyEventTime(layer.BoundTimeline!.TauMainFire - 1d, true, 0.3d);
        Expect(failures, queries == 0, "queried before any spire fired");
        layer.ApplyBeamSettings(new EclipseBeamSettings { CloudDiffusionEnabled = false });
        Vector4 cloudParams = (Vector4)layer.BeamMaterial!.GetShaderParameter("cloud_params");
        Expect(failures, cloudParams.W == 0f && Math.Abs(cloudParams.X - 3f) < 1e-6f && Math.Abs(cloudParams.Y - 1.5f) < 1e-6f, $"cloud_params with the feature off {cloudParams}");
        for (int frame = 0; frame < 5; frame++) layer.ApplyEventTime(tau, true, 0.3d);
        Expect(failures, queries == 0, "queried with the feature disabled");
        layer.ApplyBeamSettings(new EclipseBeamSettings { CloudWidening = 5d, CloudHaloBoost = 2d, CloudCoreCut = 0.8d });
        cloudParams = (Vector4)layer.BeamMaterial.GetShaderParameter("cloud_params");
        Expect(failures, cloudParams.IsEqualApprox(new Vector4(5f, 0.8f, 2f, 1f)), $"cloud_params {cloudParams}");
        layer.ApplyBeamSettings(new EclipseBeamSettings());
        // The multimesh draws with colours (the shader reads COLOR) and the far layer carries none.
        Expect(failures, layer.Instances!.Multimesh.UseColors && (layer.FarLayer == null || !layer.FarLayer.Instances!.Multimesh.UseColors), "multimesh colour flags");
        layer.WeatherQueryOverride = null;

        CheckCrownGlowAndBore(layer, timeline, topology, placements, grid, camera, fixture, failures);
    }

    /// <summary>The nearest <paramref name="limit"/> fired beams within <paramref name="range"/> of <paramref name="from"/>, ordered by distance (independent of the layer's scan).</summary>
    static int[] NearestFired(EclipseHubBeamLayer layer, EclipseEventTimeline timeline, double tau, Vector3 from, double range, int limit)
    {
        var list = new List<(int Beam, double Distance)>();
        for (int i = 0; i < layer.BeamCount; i++)
        {
            float fire = layer.GetCustomMirror(i).B;
            if (fire >= 1e8f || tau <= fire) continue;
            Vector3D c = layer.GetWorldOrigin(i);
            double dx = c.X - from.X, dy = c.Y - from.Y, dz = c.Z - from.Z, d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (d < range) list.Add((i, d));
        }
        _ = timeline;
        return list.OrderBy(t => t.Distance).Take(limit).Select(t => t.Beam).ToArray();
    }

    static void CheckCrownGlowAndBore(EclipseHubBeamLayer layer, EclipseEventTimeline template, EmitterGridTopology topology, EmitterSitePlacementSource placements,
        VoxelChunkGridManager grid, Camera3D camera, Node3D fixture, List<string> failures)
    {
        var standIns = new EmitterStandInLayer { Name = "EmitterStandIns", AutoBind = false };
        fixture.AddChild(standIns);
        var bores = new EmitterBoreLayer { Name = "EmitterBores", AutoBind = false };
        fixture.AddChild(bores);
        try
        {
            if (!standIns.Bind(grid, placements) || !bores.Bind(grid, placements)) { failures.Add("stand-in or bore layer did not bind"); return; }
            layer.ResolveCollaborators();
            layer.ApplyBeamSettings(new EclipseBeamSettings());
            EclipseEventTimeline timeline = NewEclipse(template, 21);
            layer.BindTimeline(timeline);
            int alive = 0;
            for (int i = 0; i < layer.BeamCount; i++) if (layer.GetCustomMirror(i).B < 1e8f) alive++;

            // The stand-in mesh is Y-up and about the declared height, so the crown band is the top of the mesh.
            Aabb regular = standIns.RegularInstances!.Multimesh.Mesh.GetAabb(), main = standIns.MainInstances!.Multimesh.Mesh.GetAabb();
            Expect(failures, regular.Size.Y > regular.Size.X && regular.Size.Y > regular.Size.Z && main.Size.Y > main.Size.X && main.Size.Y > main.Size.Z, $"stand-in meshes are not Y-up {regular.Size} {main.Size}");
            Vector2 band = (Vector2)standIns.RegularMaterial!.GetShaderParameter("crown_glow_band");
            Expect(failures, band.X < band.Y && band.Y < regular.End.Y && band.Y > regular.End.Y - regular.Size.Y * 0.05f, $"crown glow band {band}");
            Expect(failures, standIns.CrownGlowWriteCount == 0, "stand-in crown glow written before the event");

            // ---- crown glow: 3 writes per beam per eclipse (fire, hand-off complete, cut), none per frame
            long standBefore = standIns.CrownGlowWriteCount, layerBefore = layer.CrownGlowWriteCount;
            double start = timeline.TauMainFire - 3d, end = timeline.TauImplosion + 3d;
            int frames = 0;
            double sampledFire = double.NaN;
            int probe = 120;
            double probeFire = layer.GetCustomMirror(probe).B;
            int probeSite = topology.HubBeamSite(probe), mainSite = topology.HubBeamSite(0);
            bool sawFire = false, sawEmber = false, sawMainEmber = false, sawOff = false;
            for (double t = start; t <= end; t += 0.25d, frames++)
            {
                layer.ApplyEventTime(t, t < timeline.TauImplosion + timeline.BeamRetract, 0.25d);
                if (!sawFire && t > probeFire + 0.3d && t < timeline.TauHandoff) { sawFire = standIns.GetSiteCrownGlow(probeSite) == EclipseHubBeamLayer.CrownGlowFire; sampledFire = t; }
                if (!sawEmber && t > timeline.TauHandoff + timeline.HandoffFade + 0.3d && t < timeline.TauImplosion)
                {
                    sawEmber = standIns.GetSiteCrownGlow(probeSite) == EclipseHubBeamLayer.CrownGlowEmberRegular;
                    sawMainEmber = standIns.GetSiteCrownGlow(mainSite) == EclipseHubBeamLayer.CrownGlowEmberMain;
                }
                if (!sawOff && t > timeline.TauImplosion + 0.3d) sawOff = standIns.GetSiteCrownGlow(probeSite) == 0f && standIns.GetSiteCrownGlow(mainSite) == 0f;
            }
            Expect(failures, standIns.CrownGlowWriteCount - standBefore == alive * 3L && layer.CrownGlowWriteCount - layerBefore == alive * 3L,
                $"crown glow writes {standIns.CrownGlowWriteCount - standBefore} / {layer.CrownGlowWriteCount - layerBefore}, expected {alive * 3}");
            Expect(failures, sawFire && sawEmber && sawMainEmber && sawOff, $"crown glow values fire={sawFire} (t {sampledFire:0.0}) ember={sawEmber} mainEmber={sawMainEmber} off={sawOff}");
            Console.WriteLine($"ECLIPSE_HUB_BEAM_LAYER glow writes stand_in={standIns.CrownGlowWriteCount - standBefore} layer={layer.CrownGlowWriteCount - layerBefore} expected={alive * 3} (alive {alive}) frames={frames}");
            for (int site = 0; site < placements.Count; site++)
                if (standIns.GetSiteCrownGlow(site) != 0f) { failures.Add($"site {site} still glowing after the cut"); break; }

            // Mid-event, steady frames write nothing (stand-in and layer), and the stand-in flags are untouched.
            standIns.SetSitePresented(probeSite, true);
            layer.ApplyEventTime(timeline.TauSustain + 5d, true, 0.016d);
            long standSteady = standIns.CrownGlowWriteCount, layerSteady = layer.CrownGlowWriteCount;
            for (int frame = 0; frame < 600; frame++) layer.ApplyEventTime(timeline.TauSustain + 5d + frame * 0.016d, true, 0.016d);
            Expect(failures, standIns.CrownGlowWriteCount == standSteady && layer.CrownGlowWriteCount == layerSteady, "per-frame crown glow writes");
            Expect(failures, standIns.IsSitePresented(probeSite) && !standIns.IsSiteHidden(probeSite), "crown glow disturbed the stand-in flags");
            standIns.SetSitePresented(probeSite, false);

            // A stand-in rebind mid-event (a fresh layer reallocates the glow lane): the next update restores the glow, steady frames still write nothing.
            {
                double tMid = timeline.TauSustain + 5d;
                float expected = standIns.GetSiteCrownGlow(probeSite);
                Expect(failures, expected > 0f, "rebind check: the probe spire is not glowing in Sustain");
                fixture.RemoveChild(standIns);
                standIns.Free();
                standIns = new EmitterStandInLayer { Name = "EmitterStandIns", AutoBind = false };
                fixture.AddChild(standIns);
                if (!standIns.Bind(grid, placements)) { failures.Add("rebound stand-in layer did not bind"); return; }
                Expect(failures, standIns.GetSiteCrownGlow(probeSite) == 0f, "rebound stand-in layer starts dark");
                layer.ResolveCollaborators();
                layer.ApplyEventTime(tMid + 0.016d, true, 0.016d);
                Expect(failures, standIns.GetSiteCrownGlow(probeSite) == expected && standIns.GetSiteCrownGlow(mainSite) == layer.GetCrownGlowWritten(0),
                    $"rebind did not restore the crown glow ({standIns.GetSiteCrownGlow(probeSite)} vs {expected})");
                long afterRebind = standIns.CrownGlowWriteCount, layerAfterRebind = layer.CrownGlowWriteCount;
                for (int frame = 0; frame < 120; frame++) layer.ApplyEventTime(tMid + 0.032d + frame * 0.016d, true, 0.016d);
                Expect(failures, standIns.CrownGlowWriteCount == afterRebind && layer.CrownGlowWriteCount == layerAfterRebind, "steady frames after a rebind wrote the crown glow");
                Console.WriteLine($"ECLIPSE_HUB_BEAM_LAYER rebind restored glow={standIns.GetSiteCrownGlow(probeSite):0.00} revision={standIns.BindRevision}");
            }

            // A backward jump resyncs to the state at that time (all dark before the main fire), and the switch clears the glow.
            layer.ApplyEventTime(timeline.TauSustain + 5d, true, 0.016d);
            bool glowing = false;
            for (int site = 0; site < placements.Count; site++) if (standIns.GetSiteCrownGlow(site) > 0f) glowing = true;
            Expect(failures, glowing, "no spire glows in Sustain");
            layer.ApplyEventTime(timeline.TauMainFire - 2d, true, 0.016d);
            glowing = false;
            for (int site = 0; site < placements.Count; site++) if (standIns.GetSiteCrownGlow(site) > 0f) glowing = true;
            Expect(failures, !glowing, "a backward jump did not resync the crown glow");
            layer.ApplyEventTime(timeline.TauSustain + 5d, true, 0.016d);
            layer.ApplyBeamSettings(new EclipseBeamSettings { CrownGlowEnabled = false });
            layer.ApplyEventTime(timeline.TauSustain + 5.1d, true, 0.016d);
            glowing = false;
            for (int site = 0; site < placements.Count; site++) if (standIns.GetSiteCrownGlow(site) > 0f) glowing = true;
            Expect(failures, !glowing, "disabling the crown glow left spires glowing");
            layer.ApplyBeamSettings(new EclipseBeamSettings { CrownGlowEmission = 7d });
            Expect(failures, Math.Abs(standIns.CrownGlowEmission - 7f) < 1e-6f && Math.Abs((float)standIns.RegularMaterial!.GetShaderParameter("crown_glow_emission") - 7f) < 1e-6f, "crown glow emission uniform");
            layer.ApplyBeamSettings(new EclipseBeamSettings());

            // ---- tweaks T3: a Sustain refuel orb does not relight its beam: the lit count stays at the main beam, and the status counts the orbs in flight
            layer.RefuelFrontClock = template.GridClock;
            int quiet = int.MaxValue, flashing = 0, orbsSeen = 0;
            for (double t = timeline.TauSustain + 60d; t < timeline.TauSustain + 400d; t += 0.25d)
            {
                layer.ApplyEventTime(t, true, 0.25d, timeline.Window.Start + t);
                int count = layer.LitBeamCount();
                quiet = Math.Min(quiet, count);
                flashing = Math.Max(flashing, count);
                orbsSeen = Math.Max(orbsSeen, layer.OrbsInFlight());
            }
            layer.RefuelFrontClock = null;
            layer.ApplyEventTime(timeline.TauSustain + 5d, true, 0.016d);
            Expect(failures, quiet == 1 && flashing == 1 && orbsSeen > 1, $"lit beam count in Sustain: min {quiet}, max {flashing} (the main beam only; an orb relights nothing), orbs in flight max {orbsSeen}");

            // ---- bore light: the plug light of the spire's slot follows the crown level of its beam
            EmitterSitePlacement site60 = placements.GetPlacement(probeSite);
            bores.Step(new RingCanonicalPosition(site60.AroundMeters + 500d, site60.PadHeightMeters + 1.7d, site60.AcrossMeters), null, consumersActive: true);
            int slot = bores.GetActiveSite(0) == probeSite ? 0 : bores.GetActiveSite(1) == probeSite ? 1 : -1;
            Expect(failures, slot >= 0, "the bore layer did not present the test spire");
            if (slot >= 0)
            {
                OmniLight3D light = bores.GetSlotLight(slot)!;
                layer.ApplyEventTime(probeFire - 1d, true, 0.016d);
                Expect(failures, Math.Abs(bores.GetSlotEventScale(slot) - 1f) < 1e-6f && Math.Abs(light.LightEnergy - 2f) < 1e-4f, $"bore boosted before the fire ({bores.GetSlotEventScale(slot)})");
                layer.ApplyEventTime(probeFire + 3d, true, 0.016d);
                Console.WriteLine($"ECLIPSE_HUB_BEAM_LAYER bore firing scale={bores.GetSlotEventScale(slot):0.00} energy={light.LightEnergy:0.00}");
                Expect(failures, Math.Abs(bores.GetSlotEventScale(slot) - 4f) < 1e-4f && Math.Abs(light.LightEnergy - 8f) < 1e-3f, $"bore not boosted while firing ({bores.GetSlotEventScale(slot)}, energy {light.LightEnergy})");
                bores.Configure(16000f, 3f);
                Expect(failures, Math.Abs(light.LightEnergy - 12f) < 1e-3f, "Configure dropped the event boost");
                bores.Configure(16000f, 2f);
                layer.ApplyBeamSettings(new EclipseBeamSettings { BoreLightBoost = 1d });
                layer.ApplyEventTime(probeFire + 3.1d, true, 0.016d);
                Expect(failures, Math.Abs(light.LightEnergy - 2f) < 1e-4f, "a boost of 1 changed the light");
                layer.ApplyBeamSettings(new EclipseBeamSettings());
                layer.ApplyEventTime(probeFire + 3.2d, true, 0.016d);
                layer.ApplyEventTime(timeline.TauHandoff + timeline.HandoffFade + 1d, true, 0.016d);
                Expect(failures, Math.Abs(bores.GetSlotEventScale(slot) - 1f) < 1e-4f && Math.Abs(light.LightEnergy - 2f) < 1e-4f, $"bore still boosted after the hand-off ({bores.GetSlotEventScale(slot)})");
                layer.ApplyEventTime(probeFire + 3.2d, true, 0.016d);
                Expect(failures, bores.GetSlotEventScale(slot) > 3.9f, "bore boost did not come back on a backward jump");
                // A slot reassignment resets the boost, and the layer's unbind restores it.
                bores.Step(new RingCanonicalPosition(site60.AroundMeters + 500d, site60.PadHeightMeters + 1.7d, site60.AcrossMeters), null, consumersActive: false);
                Expect(failures, bores.GetSlotEventScale(0) == 1f && bores.GetSlotEventScale(1) == 1f, "a released slot kept its boost");
                bores.Step(new RingCanonicalPosition(site60.AroundMeters + 500d, site60.PadHeightMeters + 1.7d, site60.AcrossMeters), null, consumersActive: true);
                layer.ApplyEventTime(probeFire + 3.2d, true, 0.016d);
                Expect(failures, bores.GetSlotEventScale(bores.GetActiveSite(0) == probeSite ? 0 : 1) > 3.9f, "boost not applied to the new slot");
            }
            layer.ApplyEventTime(timeline.TauSustain + 5d, true, 0.016d);
            layer.Unbind();
            Expect(failures, bores.GetSlotEventScale(0) == 1f && bores.GetSlotEventScale(1) == 1f, "unbind left the bore boosted");
            for (int site = 0; site < placements.Count; site++)
                if (standIns.GetSiteCrownGlow(site) != 0f) { failures.Add("unbind left a crown glowing"); break; }
        }
        finally
        {
            fixture.RemoveChild(standIns);
            standIns.Free();
            fixture.RemoveChild(bores);
            bores.Free();
        }
    }
}
