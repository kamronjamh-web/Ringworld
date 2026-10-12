using System;
using System.Reflection;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

[GlobalClass]
public partial class WaterSharedWindSmoke : RefCounted
{
    public Godot.Collections.Dictionary Run()
    {
        try
        {
            var response = new RingWaterWindResponse();
            Check(response.Step(new(4, 0), 100, 8, true) == new Vector2(4, 0), "initial mean");
            Vector2 halfway = response.Step(new(28, 0), 108, 8, true);
            Check(Math.Abs(halfway.X - (4 + 24 * (1 - Math.Exp(-1)))) < .0001, "mean response time");
            Check(response.Step(new(60, 0), 108, 8, true) == halfway, "pause must freeze water response");
            Check(response.Step(new(0, 9), 20, 8, true) == new Vector2(0, 9), "rewind mean reset");
            Check(response.Step(new(-7, 0), 20, 8, false) == new Vector2(-7, 0), "manual direction/speed");
            Check(response.Step(new(float.NaN, float.PositiveInfinity), 21, 8, false) == Vector2.Zero, "nonfinite guard");
            response.MarkSpectrumUpdated(30);
            Check(!response.SpectrumUpdateDue(30.99, true) && response.SpectrumUpdateDue(31, true), "bounded mean spectrum cadence");
            Check(response.SpectrumUpdateDue(30, false), "manual controls should retain debounce only");
            var ramp = new RingWaterWindResponse();
            float published = 4;
            int configurations = 0;
            for (int frame = 0; frame <= 1200; frame++)
            {
                double clock = frame / 60d;
                var snapshot = new global::RingworldTechDemo.Weather.RingWindSnapshot(new Vector2(4 + (float)clock, 0),
                    new Vector2(frame % 2 == 0 ? 60 : 0, 0), 1, clock, 3, true);
                Vector2 filtered = ramp.Step(snapshot.MeanWind, snapshot.SimulationTime, 8, true);
                if (Math.Abs(filtered.X - published) > .35f && ramp.SpectrumUpdateDue(clock, true))
                {
                    ramp.MarkSpectrumUpdated(clock);
                    published = filtered.X;
                    configurations++;
                }
            }
            Check(configurations >= 12 && configurations <= 20, "continuous mean ramp starved or exceeded spectrum cadence");
            Check(RingWaterWindResponse.DirectionDifferenceDegrees(179, -179) == 2, "wrapped bearing");
            double time = 1_000_000_000;
            float before = RingWaterWindResponse.BoundedPhase(time, 1.35, -2.071);
            float after = RingWaterWindResponse.BoundedPhase(time + .016, 1.35, -2.071);
            Check(Math.Abs(Mathf.Wrap(after - before, -Mathf.Pi, Mathf.Pi) + .016 * 1.35 * 2.071) < .00001, "long running phase continuity");
            VerifyOwnerReplacement();
            VerifyObserverOverrideSource();
            VerifyMaterials(time);
            VerifyShaderRoutes();
            return new() { ["pass"] = true, ["detail"] = "mean_response,pause,rewind,manual,finite,cadence,bearing,long_phase,all_material_clocks,gust_shading_only" };
        }
        catch (Exception exception) { return new() { ["pass"] = false, ["detail"] = exception.Message }; }
        finally { RingWaterModel.ClearWindClock(); RingWaterModel.RefreshOceanMaterials(); }
    }

    private static void VerifyOwnerReplacement()
    {
        var parent = new Node();
        var oldOwner = new RingWaterFftController { Enabled = false, FollowWeatherWind = false, WindSpeedMetersPerSecond = 4 };
        var newOwner = new RingWaterFftController { Enabled = false, FollowWeatherWind = false, WindSpeedMetersPerSecond = 17, WindDirectionDegrees = 90 };
        using var material = new ShaderMaterial { Shader = GD.Load<Shader>("res://materials/ring_ocean_proxy.gdshader") };
        try
        {
            parent.AddChild(oldOwner);
            parent.AddChild(newOwner);
            RingWaterModel.ConfigureMaterial(material);
            oldOwner._Ready();
            oldOwner._Process(.25);
            Check(material.GetShaderParameter("water_mean_wind_direction").AsVector2().DistanceTo(Vector2.Right) < .0001, "first owner did not publish water wind");
            newOwner._Ready();
            newOwner._Process(.5);
            long pushes = RingWaterModel.OceanMaterialParameterPushes;
            oldOwner._Process(1);
            Check(RingWaterModel.OceanMaterialParameterPushes == pushes, "obsolete water owner advanced shared clock");
            Check(material.GetShaderParameter("water_mean_wind_direction").AsVector2().DistanceTo(Vector2.Down) < .0001, "obsolete owner overwrote replacement wind");
            oldOwner._ExitTree();
            Check(ReferenceEquals(RingWaterFftController.Instance, newOwner)
                && material.GetShaderParameter("water_wind_clock_enabled").AsBool(), "obsolete exit cleared replacement clock");
            newOwner._Process(.25);
            Check(RingWaterModel.OceanMaterialParameterPushes > pushes, "replacement owner stopped publishing clock");
            newOwner._ExitTree();
            Check(RingWaterFftController.Instance is null && !material.GetShaderParameter("water_wind_clock_enabled").AsBool(), "active owner cleanup retained clock");
        }
        finally { parent.Free(); RingWaterModel.ClearWindClock(); RingWaterModel.RefreshOceanMaterials(); }
    }

    private static void VerifyObserverOverrideSource()
    {
        var effects = new global::RingworldTechDemo.RingLocalWeatherEffects();
        var controller = new RingWaterFftController { FollowWeatherWind = true, WindSpeedMetersPerSecond = 7, WindDirectionDegrees = 90 };
        try
        {
            Type effectsType = effects.GetType();
            Type controllerType = controller.GetType();
            controllerType.GetField("_localWeatherEffects", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, effects);
            MethodInfo resolve = controllerType.GetMethod("ResolveWind", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Vector2 Resolved() => (Vector2)resolve.Invoke(controller, null)!;
            effectsType.GetProperty("HasCurrentWindSnapshot")!.SetValue(effects, true);
            effectsType.GetProperty("CurrentWindSnapshot")!.SetValue(effects,
                new global::RingworldTechDemo.Weather.RingWindSnapshot(new Vector2(27, 5), new Vector2(60, 60), 1, 100, 3, true));
            Check(Resolved() == new Vector2(27, 5), "external storm observer mean failed to reach FFT");
            controller.FollowWeatherWind = false;
            Check(Resolved().DistanceTo(new Vector2(0, 7)) < .0001, "saved manual FFT wind lost to observer override");
            controller.FollowWeatherWind = true;
            effectsType.GetProperty("CurrentWindSnapshot")!.SetValue(effects,
                new global::RingworldTechDemo.Weather.RingWindSnapshot(Vector2.Zero, Vector2.Zero, 0, 100, 1, false));
            Check(Resolved() == Vector2.Zero, "valid calm/disabled observer mean fell back to wind");
            effectsType.GetProperty("HasCurrentWindSnapshot")!.SetValue(effects, false);
            Check(Resolved().DistanceTo(new Vector2(0, 7)) < .0001, "uninitialized observer bypassed fallback");
        }
        finally { controller.Free(); effects.Free(); }
    }

    private static void VerifyMaterials(double time)
    {
        using var near = new ShaderMaterial { Shader = GD.Load<Shader>("res://materials/ring_ocean_proxy.gdshader") };
        using var far = new ShaderMaterial { Shader = GD.Load<Shader>("res://materials/ring_ocean_global.gdshader") };
        RingWaterModel.ConfigureMaterial(near);
        RingWaterModel.ConfigureMaterial(far, .001f);
        RingWaterModel.UpdateWindClock(time, new Vector2(0, 24));
        RingWaterModel.RefreshOceanMaterials();
        Check(near.GetShaderParameter("water_mean_wind_direction").AsVector2() == Vector2.Down, "actual analytic bearing");
        Check(near.GetShaderParameter("water_wind_clock_enabled").AsBool() && far.GetShaderParameter("water_wind_clock_enabled").AsBool(), "fallback/far clocks");
        Check(!far.GetShaderParameter("water_spatial_gust_enabled").AsBool(), "scaled-far must not sample local atlas");
        Vector3[] phases = near.GetShaderParameter("water_wave_phases").AsVector3Array();
        Check(phases.Length == 12 && Math.Abs(phases[0].Y - RingWaterWindResponse.BoundedPhase(time, RingWaterModel.WaveSpeed, .42)) < .00001, "lake kind clock");
        Check(Math.Abs(phases[0].Z - RingWaterWindResponse.BoundedPhase(time, RingWaterModel.WaveSpeed, .18)) < .00001, "river kind clock");
        long pushes = RingWaterModel.OceanMaterialParameterPushes;
        RingWaterModel.UpdateWindClock(time, new Vector2(0, 24));
        RingWaterModel.RefreshOceanMaterials();
        Check(RingWaterModel.OceanMaterialParameterPushes == pushes, "paused material clock pushed unchanged parameters");
        RingWaterModel.UpdateWindClock(time, new Vector2(0, 24), false);
        RingWaterModel.RefreshOceanMaterials();
        Check(!near.GetShaderParameter("water_spatial_gust_enabled").AsBool(), "manual FFT wind inherited weather gusts");
        Check(near.GetShaderParameter("water_mean_wind_direction").AsVector2() == Vector2.Down, "manual direction was replaced");
        Vector4 translation = far.GetShaderParameter("water_far_group_translation").AsVector4();
        Check(Math.Abs(translation.Y) <= 1.001f && Math.Abs(translation.W) <= 1.001f, "long clock group drift bounds");
        RingWaterModel.ClearWindClock();
        RingWaterModel.RefreshOceanMaterials();
        Check(!near.GetShaderParameter("water_wind_clock_enabled").AsBool()
            && !near.GetShaderParameter("water_spatial_gust_enabled").AsBool()
            && near.GetShaderParameter("water_mean_response").AsSingle() == 1, "owner cleanup left water wind response");
    }

    private static void VerifyShaderRoutes()
    {
        string proxy = FileAccess.GetFileAsString("res://materials/ring_ocean_proxy.gdshader");
        string common = FileAccess.GetFileAsString("res://materials/ring_water_common.gdshaderinc");
        string fft = FileAccess.GetFileAsString("res://materials/ring_water_fft.gdshaderinc");
        Check(proxy.Contains("ring_wind_field_enabled > 0.5") && proxy.Contains("water_mean_wind_speed > 0.01"), "disabled/calm gust guards");
        Check(proxy.Contains("ring_wind_gust_at_world(world_position, 0.0)"), "shared moving gust sampling");
        Check(proxy.Contains("fft_detail.z * water_gust_response") && proxy.Contains("filtered_gradient *= water_mean_response * water_gust_response"), "foam and analytic gust routes");
        string controller = FileAccess.GetFileAsString("res://scripts/Voxel/LOD/RingWaterFftController.cs");
        Check(controller.Contains("QueryWindSnapshot(_explorer.CanonicalPosition).MeanWind")
            && controller.Contains("_localWeatherEffects.CurrentWindSnapshot") && !controller.Contains("InstantWind"), "FFT followed instantaneous gust wind");
        Check(!fft.Contains("water_gust_response") && !fft.Contains("water_mean_response"), "gust changed query geometry");
        Check(common.Contains("ring_water_wave_direction") && common.Contains("kind_coordinate_scale"), "kind seam phase closure");
    }

    private static void Check(bool pass, string message) { if (!pass) throw new InvalidOperationException(message); }
}
