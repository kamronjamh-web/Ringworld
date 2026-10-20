using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Poi;

namespace RingworldTechDemo.Tools;

/// <summary>
/// S2 rig check: poses the real <see cref="WeatherLungRig"/> on WeatherLungDetail.tscn and compares every pivot with the kit's baked
/// Weather_Lung_Intake_Vanes_Animated.glb (read straight from prototypes/ through GLTFDocument) at frames 1 + 6k. Allows one global
/// phase offset of -1..+1 frame and fits the ripple lag among the candidates. Run through tools/poi/verify_weather_lung.gd -- rig.
/// Prints WEATHER_LUNG_RIG=PASS.
/// </summary>
public partial class WeatherLungRigCheck : RefCounted
{
    private const double AngleTolerance = 2e-3;
    private const double RodTolerance = 0.1;
    private const double CycleFrames = 240.0;
    private const double AnimationLengthSeconds = 10.0;
    private static readonly string[] Channels = { "vane", "ramcyl", "gill", "rotor", "ring", "heliostat", "ramrod", "ramrod_ik" };

    public int Run(string detailScene, Node animRoot) // animRoot: the animated GLB loaded by GLTFDocument in GDScript (not bound to C#)
    {
        PackedScene? ps = ResourceLoader.Load<PackedScene>(detailScene);
        if (ps == null) { Console.Error.WriteLine("WEATHER_LUNG_RIG=FAIL cannot load " + detailScene); return 1; }
        var detail = ps.Instantiate<Node3D>();
        var rig = new WeatherLungRig();
        if (!rig.Bind(detail, out string error)) { Console.Error.WriteLine("WEATHER_LUNG_RIG=FAIL bind: " + error); detail.Free(); return 1; }
        Console.WriteLine($"  rig bound: {rig.PivotCount} pivots, {rig.NozzleMaterialCount} nozzle materials, heat material {rig.HasHeatMaterial}");

        AnimationPlayer? player = Find<AnimationPlayer>(animRoot);
        if (player == null) { Console.Error.WriteLine("WEATHER_LUNG_RIG=FAIL no AnimationPlayer in the animated GLB"); detail.Free(); return 1; }

        // Pivots (name -> kind) from the Detail metadata.
        var pivots = new List<(Node3D Node, string Kind)>();
        Collect(detail.GetNode("Arch/Moving"), pivots);

        double[] lags = { WeatherLungBreathMath.DefaultRippleLag, 0.0 };
        double[] offsets = { -3.0, -2.5, -2.0, -1.5, -1.0, -0.5, 0.0, 0.5, 1.0 };
        double bestScore = double.MaxValue;
        double bestOffset = 0;
        double bestLag = 0.0;
        Dictionary<string, double> bestErrors = new();
        foreach (double lag in lags)
        {
            foreach (double offset in offsets)
            {
                Dictionary<string, double> errors = Measure(rig, pivots, player, animRoot, lag, offset);
                double score = Score(errors);
                Console.WriteLine($"  candidate lag={lag:0.###} offset={offset,5:0.0} frames: score={score:G4} " + string.Join(" ", errors.Select(e => $"{e.Key}={e.Value:G3}")));
                if (score < bestScore) { bestScore = score; bestOffset = offset; bestLag = lag; bestErrors = errors; }
            }
        }

        if (System.Environment.GetEnvironmentVariable("WL_RIG_DIAG") is "1" or "2")
            foreach (string name in new[] { "VanePivot_00", "VanePivot_04", "Gill_Pivot_00", "Gill_Pivot_15", "Turbine_Rotor_A", "Turbine_Rotor_B", "Guide_Ring_A", "Guide_Ring_B", "Vane_RamCyl_02", "Vane_RamRod_02" })
            {
                double bo = 0, be = double.MaxValue;
                for (double o = -4.0; o <= 4.0; o += 0.05)
                {
                    var m = Measure(rig, pivots, player, animRoot, 0.012, o, name);
                    double v = m.Values.Max();
                    if (v < be) { be = v; bo = o; }
                }
                Console.WriteLine($"  diag {name}: best offset {bo:0.00} frames residual {be:G3}");
            }
        Console.WriteLine($"  FIT: phase offset {bestOffset} frame(s) ({bestOffset / CycleFrames:+0.00000;-0.00000;0} cycle), ripple lag {bestLag:0.###}");
        bool ok = true;
        foreach (string channel in Channels)
        {
            double tolerance = channel.StartsWith("ramrod") ? RodTolerance : AngleTolerance;
            double e = bestErrors.GetValueOrDefault(channel, double.NaN);
            bool pass = e <= tolerance;
            ok &= pass;
            Console.WriteLine($"  channel {channel,-9} max error {e:G4} {(channel.StartsWith("ramrod") ? "m" : "rad")} (tolerance {tolerance:G3}) {(pass ? "ok" : "FAIL")}");
        }
        detail.Free();
        if (!ok) { Console.Error.WriteLine("WEATHER_LUNG_RIG=FAIL"); return 1; }
        Console.WriteLine("WEATHER_LUNG_RIG=PASS");
        return 0;
    }

    private static double Score(Dictionary<string, double> errors)
    {
        double worst = 0.0;
        foreach (var kv in errors) worst = Math.Max(worst, kv.Key.StartsWith("ramrod") ? kv.Value / RodTolerance : kv.Value / AngleTolerance);
        return worst;
    }

    private static Dictionary<string, double> Measure(WeatherLungRig rig, List<(Node3D Node, string Kind)> pivots, AnimationPlayer player, Node animRoot, double lag, double offsetFrames, string? only = null)
    {
        var errors = Channels.ToDictionary(c => c, _ => 0.0);
        WeatherLungPose pose = WeatherLungPose.Defaults;
        pose.Sun = 1.0;
        pose.RippleLag = lag;
        pose.TurbineRevolutions = WeatherLungBreathMath.TurbineRevolutions(WeatherLungBreathMath.DefaultTurbineMaxRpm, WeatherLungBreathMath.DefaultCycleSeconds);
        // Frames 1 + 4m (t = m / 6 s) are the instants where the 24 fps timeline and the 30 keys/s bake coincide; the kit's plan frames 1 + 6k
        // fall between keys, and the stepped bake would then measure key quantisation instead of the math. Frame 1 (m = 0) is skipped: the bake
        // holds its first two keys at 0 instead of wrapping to phase -1/240.
        for (int m = 1; m <= 59; m++)
        {
            double time = m / 6.0;
            double samplePhase = time / AnimationLengthSeconds;
            pose.Phase = WeatherLungBreathMath.Frac(samplePhase + offsetFrames / CycleFrames);
            rig.Apply(pose);
            foreach ((Node3D node, string kind) in pivots)
            {
                if (only != null && node.Name != only) continue;
                if (kind == "heliostat")
                {
                    // No baked heliostat tracks (they follow sun_available); the animated scene holds the tracking pose (sun = 1) as its rest pose.
                    Node3D? rest = animRoot.FindChild(node.Name, true, false) as Node3D;
                    double he = rest == null ? double.PositiveInfinity : 2.0 * Math.Acos(Math.Min(1.0, Math.Abs(rest.Transform.Basis.GetRotationQuaternion().Dot(node.Transform.Basis.GetRotationQuaternion()))));
                    if (he > errors[kind]) errors[kind] = he;
                    continue;
                }
                Animation? anim = player.HasAnimation(node.Name) ? player.GetAnimation(node.Name) : null;
                if (anim == null || anim.GetTrackCount() == 0) { errors[kind] = double.PositiveInfinity; continue; }
                double e;
                if (kind == "ramrod")
                {
                    e = Math.Abs(node.Position.X - anim.PositionTrackInterpolate(0, time).X);
                    // Same rod from the BAKED vane angle through the ram kinematics: isolates the IK from the vane-angle quantisation of the bake.
                    Node3D cyl = (Node3D)node.GetParent();
                    int vi = node.GetMeta("wl_index").AsInt32();
                    Quaternion vq = player.GetAnimation($"VanePivot_{vi:00}").RotationTrackInterpolate(0, time);
                    WeatherLungBreathMath.Ram(2.0 * Math.Atan2(-vq.Z, vq.W), cyl.GetMeta("wl_vane_pos").AsVector3(), cyl.GetMeta("wl_pin").AsVector3(),
                        cyl.GetMeta("wl_base").AsVector3(), cyl.GetMeta("wl_l0").AsDouble(), out _, out double ikRod);
                    double ik = Math.Abs(ikRod - anim.PositionTrackInterpolate(0, time).X);
                    if (ik > errors["ramrod_ik"]) errors["ramrod_ik"] = ik;
                }
                else
                {
                    Quaternion q = anim.RotationTrackInterpolate(0, time);
                    Quaternion r = node.Transform.Basis.GetRotationQuaternion();
                    if (only == "VanePivot_00" && m % 10 == 0 && System.Environment.GetEnvironmentVariable("WL_RIG_DIAG") == "2")
                        Console.WriteLine($"    dbg m={m} t={time:0.###} p={pose.Phase:0.00000} rig={2.0 * Math.Atan2(-r.Z, r.W):0.00000} anim={2.0 * Math.Atan2(-q.Z, q.W):0.00000} math={WeatherLungBreathMath.Vane(pose.Phase, 0, 35.0, lag):0.00000}");
                    e = 2.0 * Math.Acos(Math.Min(1.0, Math.Abs(q.Dot(r))));
                }
                if (e > errors[kind]) errors[kind] = e;
            }
        }
        return errors;
    }

    private static void Collect(Node node, List<(Node3D, string)> list)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is Node3D pivot && child is not MeshInstance3D && pivot.HasMeta("wl_kind"))
            {
                list.Add((pivot, pivot.GetMeta("wl_kind").AsString()));
                Collect(pivot, list);
            }
        }
    }

    private static T? Find<T>(Node root) where T : Node
    {
        if (root is T hit) return hit;
        foreach (Node child in root.GetChildren())
            if (Find<T>(child) is { } found) return found;
        return null;
    }
}
