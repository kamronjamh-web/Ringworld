using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace RingworldTechDemo.Tools;

/// <summary>
/// R1-6c: RingSkyRadianceCadence decisions (pure logic). Each trigger of design 4.2 fires past its threshold and stays quiet below it;
/// a still scene refreshes only by age (0.5 s); a moving sun refreshes at about the threshold rate.
/// Run: tools/sky_radiance_cadence_test.gd (headless). Prints SKY_RADIANCE_CADENCE=PASS.
/// </summary>
public partial class SkyRadianceCadenceTest : RefCounted
{
    private static RingSkyRadianceCadence.Inputs Base() => new()
    {
        Sun = new Vector3(0.3f, 0.8f, 0.2f).Normalized(), Hub = new Vector3(0f, 1f, 0f), Giant = new Vector3(0.5f, 0.1f, 0.8f).Normalized(),
        Up = new Vector3(0f, 1f, 0.01f).Normalized(), Height = 2.0, Across = 0.0, CameraPosition = new Vector3(1000f, 5000f, 200f),
        HazeSun = new Vector3(12f, 11f, 10f), HazeAmbientDay = new Vector3(0.26f, 0.34f, 0.49f), HazeAmbientNight = new Vector3(0.004f, 0.005f, 0.008f),
        AtmosphereFactor = 1.0, RingInteriorFactor = 0.0, EclipseCoverage = 0.0, EclipseTransmittance = 1.0, HubLevel = 0.0, StarVisibility = 0.0,
        HazeStorm = new Vector4(0f, 0.001f, 0.9f, 0.5f), StormLightStrength = 0.0, StormLightMega = new Vector3(1f, 0f, 0f),
        SkyTop = new Vector3(0.2f, 0.4f, 0.8f), SkyHorizon = new Vector3(0.6f, 0.7f, 0.9f), SunColor = new Vector3(1f, 0.95f, 0.9f), HubColor = new Vector3(0.5f, 0.6f, 0.8f),
        SpinRadians = 1.0, Revision = 5, StormRevision = 2, LutValid = true,
    };

    // Rotates about an axis perpendicular to v, so the direction moves by exactly `degrees`.
    private static Vector3 Rotated(Vector3 v, double degrees)
    {
        var axis = v.Cross(Vector3.Right);
        if (axis.LengthSquared() < 1e-4f) axis = v.Cross(Vector3.Forward);
        return new Basis(axis.Normalized(), Mathf.DegToRad((float)degrees)) * v;
    }

    public int Run(bool verbose = false)
    {
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        try
        {
            // Armed controller at t = 0 (the first Step refreshes); returns it with the baseline stored.
            RingSkyRadianceCadence Armed()
            {
                var c = new RingSkyRadianceCadence();
                Check(c.Step(Base(), 0.0) && c.LastReason == "first", "first frame refreshes");
                return c;
            }
            void Trigger(string name, Func<RingSkyRadianceCadence.Inputs, double, RingSkyRadianceCadence.Inputs> below, Func<RingSkyRadianceCadence.Inputs, double, RingSkyRadianceCadence.Inputs> above)
            {
                var c = Armed();
                if (below != null) Check(!c.Step(below(Base(), 0.0), 0.01), $"{name}: just below the threshold stays quiet (got {c.LastReason})");
                var d = Armed();
                bool fired = d.Step(above(Base(), 0.0), 0.01);
                Check(fired, $"{name}: just above the threshold refreshes");
            }

            Trigger("sun", (i, _) => { i.Sun = Rotated(i.Sun, 0.018); return i; }, (i, _) => { i.Sun = Rotated(i.Sun, 0.022); return i; });
            Trigger("hub", (i, _) => { i.Hub = Rotated(i.Hub, 0.018); return i; }, (i, _) => { i.Hub = Rotated(i.Hub, 0.022); return i; });
            Trigger("giant", (i, _) => { i.Giant = Rotated(i.Giant, 0.018); return i; }, (i, _) => { i.Giant = Rotated(i.Giant, 0.022); return i; });
            Trigger("up", (i, _) => { i.Up = Rotated(i.Up, 0.018); return i; }, (i, _) => { i.Up = Rotated(i.Up, 0.022); return i; });
            Trigger("height low", (i, _) => { i.Height += 0.9; return i; }, (i, _) => { i.Height += 1.1; return i; });
            Trigger("across", (i, _) => { i.Across += 95.0; return i; }, (i, _) => { i.Across += 105.0; return i; });
            Trigger("haze sun", (i, _) => { i.HazeSun *= 1.003f; return i; }, (i, _) => { i.HazeSun *= 1.005f; return i; });
            Trigger("haze ambient day", (i, _) => { i.HazeAmbientDay *= 1.003f; return i; }, (i, _) => { i.HazeAmbientDay *= 1.005f; return i; });
            Trigger("haze ambient night", (i, _) => { i.HazeAmbientNight *= 1.003f; return i; }, (i, _) => { i.HazeAmbientNight *= 1.005f; return i; });
            Trigger("atmosphere", (i, _) => { i.AtmosphereFactor -= 0.002; return i; }, (i, _) => { i.AtmosphereFactor -= 0.004; return i; });
            Trigger("interior", (i, _) => { i.RingInteriorFactor += 0.002; return i; }, (i, _) => { i.RingInteriorFactor += 0.004; return i; });
            Trigger("eclipse coverage", (i, _) => { i.EclipseCoverage += 0.002; return i; }, (i, _) => { i.EclipseCoverage += 0.004; return i; });
            Trigger("eclipse transmittance", (i, _) => { i.EclipseTransmittance -= 0.002; return i; }, (i, _) => { i.EclipseTransmittance -= 0.004; return i; });
            Trigger("hub level", (i, _) => { i.HubLevel += 0.002; return i; }, (i, _) => { i.HubLevel += 0.004; return i; });
            Trigger("star visibility", (i, _) => { i.StarVisibility += 0.002; return i; }, (i, _) => { i.StarVisibility += 0.004; return i; });
            Trigger("haze storm", (i, _) => { i.HazeStorm.X += 0.002f; return i; }, (i, _) => { i.HazeStorm.X += 0.004f; return i; });
            Trigger("haze storm w", (i, _) => { i.HazeStorm.W += 0.002f; return i; }, (i, _) => { i.HazeStorm.W += 0.004f; return i; });
            Trigger("storm light", (i, _) => { i.StormLightStrength += 0.002; return i; }, (i, _) => { i.StormLightStrength += 0.004; return i; });
            Trigger("storm mega x", (i, _) => { i.StormLightMega.X -= 0.002f; return i; }, (i, _) => { i.StormLightMega.X -= 0.004f; return i; });
            Trigger("storm mega y", (i, _) => { i.StormLightMega.Y += 0.002f; return i; }, (i, _) => { i.StormLightMega.Y += 0.004f; return i; });
            Trigger("sky top", (i, _) => { i.SkyTop *= 1.003f; return i; }, (i, _) => { i.SkyTop *= 1.005f; return i; });
            Trigger("sky horizon", (i, _) => { i.SkyHorizon *= 1.003f; return i; }, (i, _) => { i.SkyHorizon *= 1.005f; return i; });
            Trigger("sun colour", (i, _) => { i.SunColor *= 1.003f; return i; }, (i, _) => { i.SunColor *= 1.005f; return i; });
            Trigger("hub colour", (i, _) => { i.HubColor *= 1.003f; return i; }, (i, _) => { i.HubColor *= 1.005f; return i; });
            Trigger("spin", (i, _) => { i.SpinRadians += Mathf.DegToRad(0.18f); return i; }, (i, _) => { i.SpinRadians += Mathf.DegToRad(0.22f); return i; });
            Trigger("revision", null, (i, _) => { i.Revision++; return i; });
            Trigger("storm revision", null, (i, _) => { i.StormRevision++; return i; });
            Trigger("lut flag", null, (i, _) => { i.LutValid = false; return i; });
            Trigger("teleport", (i, _) => { i.CameraPosition += new Vector3(900f, 0f, 0f); return i; }, (i, _) => { i.CameraPosition += new Vector3(1100f, 0f, 0f); return i; });

            // Height is relative above 500 m: 0.2 % of 10 km is 20 m.
            var tall = Base(); tall.Height = 10000.0;
            var ct = new RingSkyRadianceCadence(); ct.Step(tall, 0.0);
            var t19 = tall; t19.Height += 19.0;
            var t21 = tall; t21.Height += 21.0;
            Check(!ct.Step(t19, 0.01), "height 10 km: 19 m stays quiet");
            Check(ct.Step(t21, 0.02), "height 10 km: 21 m refreshes");

            // Spin wraps across 2 pi without a spurious refresh.
            var wrap = Base(); wrap.SpinRadians = Mathf.Tau - 0.0005;
            var cw = new RingSkyRadianceCadence(); cw.Step(wrap, 0.0);
            wrap.SpinRadians = 0.0005;
            Check(!cw.Step(wrap, 0.01), "spin wrap across 2 pi is a 0.06 deg move, not a refresh");

            // Teleport is judged per frame, not against the last refresh: slow drift below 1 km per frame never counts.
            var cd = Armed();
            var drift = Base();
            bool anyTeleport = false;
            for (int f = 1; f <= 40; f++) { drift.CameraPosition += new Vector3(0f, 0f, 400f); cd.Step(drift, 0.001 * f); anyTeleport |= cd.LastReason == "teleport"; }
            Check(!anyTeleport, "400 m per frame never reads as a teleport");

            // Megastorm flash: any change refreshes, and every frame while it is above zero; back to zero settles.
            var cf = Armed();
            var flash = Base(); flash.StormLightMega.Z = 0.5f;
            Check(cf.Step(flash, 0.01), "flash onset refreshes");
            Check(cf.Step(flash, 0.02), "flash held above zero refreshes every frame");
            Check(cf.Step(flash, 0.03), "flash held above zero refreshes every frame (again)");
            flash.StormLightMega.Z = 0.0f;
            Check(cf.Step(flash, 0.04), "flash ending refreshes once");
            Check(!cf.Step(flash, 0.05), "flash at zero is quiet");

            // Age: a still scene refreshes every 0.5 s and not before.
            var ca = Armed();
            int refreshes = 0;
            for (int f = 1; f <= 600; f++) if (ca.Step(Base(), f * 0.01)) { refreshes++; Check(ca.LastReason == "age", "still scene refreshes by age only"); }
            Check(refreshes == 11 || refreshes == 12, $"still 6 s refreshes about every 0.5 s (got {refreshes})");
            Check(!Armed().Step(Base(), 0.49), "0.49 s: no age refresh");
            Check(Armed().Step(Base(), 0.5), "0.5 s: age refresh");

            // A refresh re-baselines: the same displaced input does not refresh twice, and the age restarts.
            var cr = Armed();
            var moved = Base(); moved.Sun = Rotated(moved.Sun, 0.05);
            Check(cr.Step(moved, 0.01), "moved sun refreshes");
            Check(!cr.Step(moved, 0.02), "same sun right after a refresh is quiet");
            Check(!cr.Step(moved, 0.505), "age restarted at the refresh (0.495 s after it is not due)");
            Check(cr.Step(moved, 0.52), "age refresh 0.51 s after the last refresh");

            // External refresh (a static sky uniform write dirtied the map): refreshes and restarts the age.
            var ce = Armed();
            Check(ce.Step(Base(), 0.3, true) && ce.LastReason == "external", "external write counts as a refresh");
            Check(!ce.Step(Base(), 0.7), "age restarted by the external refresh");

            // Reset forces a refresh (scene or sky material reload).
            var cz = Armed();
            cz.Reset();
            Check(cz.Step(Base(), 0.01) && cz.LastReason == "first", "reset refreshes on the next frame");

            // Standing at noon-like angular speed 0.08 deg/s at 144 fps: about 4 refreshes per second from the sun, never every frame.
            var cs = Armed();
            int sunRefreshes = 0;
            const int frames = 144 * 10;
            for (int f = 1; f <= frames; f++)
            {
                var i = Base(); i.Sun = Rotated(i.Sun, 0.08 * f / 144.0);
                if (cs.Step(i, f / 144.0)) sunRefreshes++;
            }
            Check(sunRefreshes >= 35 && sunRefreshes <= 45, $"sun at 0.08 deg/s: about 4 refreshes per second over 10 s (got {sunRefreshes})");
            Check(sunRefreshes < frames / 10, "refreshes stay a small fraction of the frames");

            // Packed adapter: a wrong-size array refreshes (fail safe), a right-size one decodes.
            var cp = new RingSkyRadianceCadence();
            Check(cp.Update(new double[3], 0, 0, true, 0.0, false), "short packed array refreshes (fail safe)");
            var packed = new double[RingSkyRadianceCadence.PackedSize];
            packed[0] = 0; packed[1] = 1; packed[2] = 0;
            Check(cp.Update(packed, 1, 1, true, 0.0, false) && !cp.Update(packed, 1, 1, true, 0.1, false), "packed array: first frame refreshes, unchanged frame does not");
            Check(cp.Update(packed, 2, 1, true, 0.11, false), "packed array: revision change refreshes");
            if (verbose) GD.Print($"sun refreshes {sunRefreshes} / {frames}");
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }
        if (failures.Count != 0) { Console.Error.WriteLine("SKY_RADIANCE_CADENCE=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("SKY_RADIANCE_CADENCE=PASS");
        return 0;
    }
}
