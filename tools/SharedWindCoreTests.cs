using System;
using Godot;
using RingworldTechDemo.Weather;
using RingworldTechDemo.Voxel.Core;
namespace RingworldTechDemo.Tools;
public partial class SharedWindCoreTests : RefCounted
{
    public Godot.Collections.Dictionary RunIntegration(Node world)
    {
        var weather = world.GetNode<RingVolumetricCloudLayer>("RingVolumetricClouds");
        var explorer = world.GetNode<RingworldTechDemo.Voxel.Integration.RingVoxelExplorerController>("Explorer");
        var local = world.GetNode<RingLocalWeatherEffects>("LocalWeatherEffects");
        var position = explorer.CanonicalPosition;
        weather.Enabled = true;
        local.Enabled = true;
        local.SetProcess(false);
        weather.CloudSimulationTimeSeconds = 1000;
        var baseline = weather.QueryWindSnapshot(position);
        var state = new RingVolumetricCloudLayer.LocalWeatherState(-9991, RingVolumetricCloudLayer.WeatherSystemKind.MajorFront, 1, 10000, new Vector2((float)position.AroundMeters, (float)position.AcrossMeters), new Vector2(10000, 10000), 0, 1, 1, Vector2.Zero, CloudFormationProfile.Cumulonimbus, 1000, 5000) { InheritWind = true };
        weather.RegisterManualWeather("wind-core", state);
        var rising = weather.QueryWindSnapshotAtTime(position, 1000);
        var strong = weather.QueryWindSnapshotAtTime(position, 1040);
        weather.WindSettings.StormRiseSeconds = .5f;
        var fastRise = weather.QueryWindSnapshotAtTime(position, 1005);
        weather.WindSettings.StormRiseSeconds = 20;
        var slowRise = weather.QueryWindSnapshotAtTime(position, 1005);
        if (fastRise.MeanWind.Length() <= slowRise.MeanWind.Length() + 3)
            throw new Exception("Storm rise control did not change buildup");
        weather.WindSettings.StormRiseSeconds = 5;
        if (strong.MeanWind.Length() < 20 || strong.MeanWind.Length() <= rising.MeanWind.Length() + 2) throw new Exception($"Spawned storm did not rise: {rising.MeanWind} -> {strong.MeanWind}");
        if (Math.Abs(strong.MeanWind.Angle() - weather.WeatherWindMetersPerSecond.Angle()) < .1) throw new Exception("Storm direction did not change");
        weather.CloudSimulationTimeSeconds = 1040;
        weather.UnregisterManualWeather("wind-core");
        var pastInherited = weather.QueryLocalWeatherAtTime(position, 1020);
        if (pastInherited.SystemId != state.SystemId || !pastInherited.InheritWind)
            throw new Exception("Historical inherited storm lost source metadata after removal");
        var decaying = weather.QueryWindSnapshotAtTime(position, 1040);
        var next = weather.QueryWindSnapshotAtTime(position, 1040.016);
        var recovered = weather.QueryWindSnapshotAtTime(position, 1160);
        weather.WindSettings.StormDecaySeconds = 1;
        var fastDecay = weather.QueryWindSnapshotAtTime(position, 1050);
        weather.WindSettings.StormDecaySeconds = 60;
        var slowDecay = weather.QueryWindSnapshotAtTime(position, 1050);
        if (slowDecay.MeanWind.Length() <= fastDecay.MeanWind.Length() + 3)
            throw new Exception("Storm decay control did not change recovery");
        weather.WindSettings.StormDecaySeconds = 18;
        if (decaying.MeanWind.DistanceTo(strong.MeanWind) > .01f) throw new Exception($"Deletion jumped wind at unchanged clock: {strong.MeanWind} -> {decaying.MeanWind}");
        if (next.MeanWind.DistanceTo(decaying.MeanWind) > .1f || next.MeanWind.Length() > decaying.MeanWind.Length() + .001f) throw new Exception("First decay frame was discontinuous or increased");
        if (decaying.MeanWind.Length() <= recovered.MeanWind.Length() + 2) throw new Exception($"Storm deletion failed decay: {decaying.MeanWind} -> {recovered.MeanWind}");
        weather.RegisterManualWeather("wind-core", state with { InheritWind = false });
        var calm = weather.QueryWindSnapshotAtTime(position, 1040);
        if (calm.MeanWind != Vector2.Zero || calm.InstantWind != Vector2.Zero) throw new Exception("Explicit manual calm was replaced by inheritance");
        weather.RegisterManualWeather("wind-core", state with { InheritWind = false, WindMetersPerSecond = new Vector2(7, -2) });
        var edited = weather.QueryWindSnapshotAtTime(position, 1040);
        if (edited.MeanWind != new Vector2(7, -2)) throw new Exception("Same-count manual revision left stale cached wind");
        local.SetExternalWeatherOverride(true, state with { InheritWind = false, WindMetersPerSecond = new Vector2(9, 3) });
        local._Process(0);
        if (local.CurrentWindSnapshot.InstantWind != new Vector2(9, 3) || local.PublishedVegetationWind != new Vector2(9, 3)) throw new Exception("External resolved wind was multiplied twice");
        weather.CloudSimulationTimeSeconds = 1041;
        weather.UnregisterManualWeather("wind-core");
        var pastExplicit = weather.QueryWindSnapshotAtTime(position, 1040.5);
        var pastExplicitMetadata = weather.QueryLocalWeatherAtTime(position, 1040.5);
        if (pastExplicit.MeanWind != new Vector2(7, -2) || pastExplicitMetadata.SystemId != state.SystemId || pastExplicitMetadata.InheritWind)
            throw new Exception("Historical explicit vector lost metadata or exact wind after removal");
        weather.CloudSimulationTimeSeconds = 1042;
        weather.RegisterManualWeather("wind-calm-history", state with { InheritWind = false, WindMetersPerSecond = Vector2.Zero });
        weather.CloudSimulationTimeSeconds = 1043;
        weather.UnregisterManualWeather("wind-calm-history");
        var pastCalm = weather.QueryWindSnapshotAtTime(position, 1042.5);
        if (pastCalm.MeanWind != Vector2.Zero || pastCalm.InstantWind != Vector2.Zero)
            throw new Exception("Historical explicit calm was replaced after removal");
        weather.CloudSimulationTimeSeconds = 1200;
        local.SetExternalWeatherOverride(true, state with { InheritWind = true, WindMetersPerSecond = new Vector2(28, 0) });
        local._Process(0);
        var externalStart = local.CurrentWindSnapshot;
        local._Process(.016);
        if (local.CurrentWindSnapshot != externalStart) throw new Exception("Paused external inheritance changed snapshot");
        weather.CloudSimulationTimeSeconds = 1205;
        local._Process(0);
        var externalRise = local.CurrentWindSnapshot;
        if (externalRise.MeanWind.Length() <= externalStart.MeanWind.Length() + 3 || externalRise.MeanWind.Length() >= 28)
            throw new Exception("Intentional external storm did not rise smoothly");
        local.SetExternalWeatherOverride(false);
        local._Process(0);
        if (local.CurrentWindSnapshot.MeanWind.DistanceTo(externalRise.MeanWind) > .001)
            throw new Exception("External removal jumped at unchanged clock");
        weather.CloudSimulationTimeSeconds = 1210;
        local._Process(0);
        if (local.CurrentWindSnapshot.MeanWind.Length() >= externalRise.MeanWind.Length())
            throw new Exception("External storm did not recover");
        weather.CloudSimulationTimeSeconds = 1200;
        local._Process(0);
        if (!local.CurrentWindSnapshot.MeanWind.IsFinite()) throw new Exception("External rewind poisoned snapshot");
        local.SetExternalWeatherOverride(true, state with { InheritWind = false, WindMetersPerSecond = new Vector2(200, 200) });
        local._Process(0);
        if (Math.Abs(local.CurrentWindSnapshot.InstantWind.Length() - 80) > .001 || local.CurrentWindSnapshot.InstantWind != local.PublishedVegetationWind)
            throw new Exception("Extreme resolved wind did not match bounded publisher");
        local.SetExternalWeatherOverride(false);
        var stormMean = strong.MeanWind;
        weather.CaptureWindSnapshot(position, state with { WindMetersPerSecond = stormMean }, 2000);
        var gustStart = weather.SharedWindField.OffsetMeters;
        weather.CaptureWindSnapshot(position, state with { WindMetersPerSecond = stormMean }, 2001);
        var gustEnd = weather.SharedWindField.OffsetMeters;
        float Delta(float start, float end, double period) => (float)((end - start + period * 1.5) % period - period * .5);
        var moved = new Vector2(Delta(gustStart.X, gustEnd.X, weather.SharedWindField.AroundPeriodMeters), Delta(gustStart.Y, gustEnd.Y, weather.SharedWindField.AcrossPeriodMeters));
        if (moved.DistanceTo(stormMean * weather.WindSettings.GustTravelMultiplier) > .002)
            throw new Exception($"Storm gust patches did not move with resolved mean: {moved} vs {stormMean}");
        if (weather.SharedWindField.VelocityMetersPerSecond.DistanceTo(stormMean) > .001)
            throw new Exception("Published lag velocity did not follow storm mean direction");
        weather.SetProcess(false);
        world.SetProcess(false);
        return new() { ["passed"] = true, ["spawn_wind"] = strong.MeanWind, ["decay_wind"] = decaying.MeanWind, ["recovered_wind"] = recovered.MeanWind, ["baseline_wind"] = baseline.MeanWind, ["external_rise"] = externalRise.MeanWind };
    }
    public Godot.Collections.Dictionary ConfigureProductionProbe(Node world, int stage)
    {
        var weather = world.GetNode<RingVolumetricCloudLayer>("RingVolumetricClouds");
        var explorer = world.GetNode<RingworldTechDemo.Voxel.Integration.RingVoxelExplorerController>("Explorer");
        var grid = world.GetNode<RingworldTechDemo.Voxel.Phase2.VoxelChunkGridManager>("ChunkGridManager");
        var geometry = grid.CaptureLodGeometrySnapshot();
        var point = explorer.CanonicalPosition;
        if (stage == 4) point = point with { AroundMeters = geometry.CircumferenceMeters - 10 };
        var anchor = stage == 3 ? point with { AroundMeters = point.AroundMeters + 120, AcrossMeters = point.AcrossMeters + 90 } : point;
        if (stage == 1)
        {
            var transform = geometry.GridGlobalTransform;
            transform.Origin += new Vector3(10000, -2300, 15000);
            geometry = geometry with { GridGlobalTransform = transform };
        }
        if (stage == 2)
        {
            var transform = geometry.GridGlobalTransform;
            transform.Basis = new Basis(Vector3.Up, .73f) * transform.Basis.Scaled(new Vector3(1.2f, .8f, 1.1f));
            geometry = geometry with { GridGlobalTransform = transform };
        }
        weather.SharedWindField.Configure(geometry.CircumferenceMeters, new Vector2(12, 3), 1200, weather.WindSettings);
        weather.SharedWindField.Publish(geometry, anchor, new Vector2(12, 3), new Vector2(12, 3), weather.WindSettings, true);
        var points = new Godot.Collections.Array<Vector3>();
        var gusts = new Godot.Collections.Array<float>();
        foreach (var canonical in new[] { point, point with { AroundMeters = point.AroundMeters + 20 }, point with { AcrossMeters = point.AcrossMeters + 35 }, point with { AroundMeters = point.AroundMeters - 20, AcrossMeters = point.AcrossMeters - 25 } })
        {
            points.Add(geometry.CanonicalToWorld(canonical.AroundMeters, canonical.VerticalMeters, canonical.AcrossMeters));
            gusts.Add(weather.SharedWindField.Sample(canonical, weather.WindSettings));
        }
        return new() { ["points"] = points, ["gusts"] = gusts };
    }
    public Godot.Collections.Dictionary ConfigureProductionTravelProbe(Node world, bool later, bool opposite)
    {
        var weather = world.GetNode<RingVolumetricCloudLayer>("RingVolumetricClouds");
        var explorer = world.GetNode<RingworldTechDemo.Voxel.Integration.RingVoxelExplorerController>("Explorer");
        var grid = world.GetNode<RingworldTechDemo.Voxel.Phase2.VoxelChunkGridManager>("ChunkGridManager");
        var geometry = grid.CaptureLodGeometrySnapshot();
        var anchor = explorer.CanonicalPosition;
        Vector2 wind = new(13, 5);
        weather.SharedWindField.Configure(geometry.CircumferenceMeters, wind, later ? 3002 : 3000, weather.WindSettings);
        weather.SharedWindField.Publish(geometry, anchor, wind, wind, weather.WindSettings, true);
        var points = new Godot.Collections.Array<Vector3>();
        var gusts = new Godot.Collections.Array<float>();
        float shift = later ? (opposite ? -2 : 2) * weather.WindSettings.GustTravelMultiplier : 0;
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                var point = anchor with { AroundMeters = anchor.AroundMeters + x * 25 + wind.X * shift, AcrossMeters = anchor.AcrossMeters + y * 25 + wind.Y * shift };
                points.Add(geometry.CanonicalToWorld(point.AroundMeters, point.VerticalMeters, point.AcrossMeters));
                gusts.Add(weather.SharedWindField.Sample(point, weather.WindSettings));
            }
        }
        return new() { ["points"] = points, ["gusts"] = gusts, ["atlas_sha256"] = weather.SharedWindField.AtlasFingerprint };
    }

    public Godot.Collections.Dictionary Run()
    {
        var settings = new RingWindSettings();
        var field = new RingWindField();
        var p = new RingCanonicalPosition(120, 0, 310);
        Vector2 wind = new(2.2f, .7f);
        field.Configure(6000, wind, 100, settings);
        float original = field.Sample(p, settings);
        float seam = field.Sample(p with { AroundMeters = p.AroundMeters + 6000 }, settings);
        if (Math.Abs(original - seam) > .00001) throw new Exception("Canonical ring seam changed gust");
        field.Configure(6000, wind, 100, settings);
        if (field.Sample(p, settings) != original) throw new Exception("Paused clock changed gust");
        var offset = field.OffsetMeters;
        field.Configure(6000, new Vector2(20, 0), 100, settings);
        if (field.OffsetMeters != offset) throw new Exception("Wind edit jumped gust phase at paused clock");
        field.Configure(6000, wind, 99, settings);
        var fresh = new RingWindField();
        fresh.Configure(6000, wind, 99, settings);
        if (Math.Abs(field.Sample(p, settings) - fresh.Sample(p, settings)) > .00001) throw new Exception("Rewind failed deterministic rebaseline");
        settings.GustTravelMultiplier = float.NaN;
        field.Configure(6000, wind, 100, settings);
        if (!field.OffsetMeters.IsFinite() || !float.IsFinite(field.SampleAtTime(p, wind, 100, 6000, settings)))
            throw new Exception("Invalid travel multiplier poisoned shared field");
        settings.GustTravelMultiplier = 1;
        float before = field.SampleAtTime(p, wind, 1e12, 6000, settings);
        if (!float.IsFinite(before) || before < 0 || before > 4) throw new Exception("Long clock gust invalid");
        field.SetSeed(42);
        float other = field.SampleAtTime(p, wind, 100, 6000, settings);
        if (Math.Abs(other - original) < .001) throw new Exception("Terrain seed did not alter gust field");
        field.SetSeed(1337);
        fresh.SetSeed(1337);
        if (field.AtlasFingerprint != fresh.AtlasFingerprint)
            throw new Exception("Identical seed produced different atlas bytes");
        string fingerprint = field.AtlasFingerprint;
        fresh.SetSeed(42);
        if (fingerprint == fresh.AtlasFingerprint)
            throw new Exception("Different seeds produced identical atlas bytes");
        Vector2 travelWind = new(13, 5);
        field.Configure(6000, travelWind, 200, settings);
        var points = new System.Collections.Generic.List<RingCanonicalPosition>();
        var beforeValues = new System.Collections.Generic.List<float>();
        float minimum = float.PositiveInfinity, maximum = float.NegativeInfinity;
        for (int y = 0; y < 12; y++)
        {
            for (int x = 0; x < 12; x++)
            {
                var point = new RingCanonicalPosition(50 + x * 10, 0, 200 + y * 10);
                float sample = field.Sample(point, settings);
                points.Add(point);
                beforeValues.Add(sample);
                minimum = Math.Min(minimum, sample);
                maximum = Math.Max(maximum, sample);
            }
        }
        if (maximum - minimum < .35f)
            throw new Exception($"Nearby Perlin patches lacked simultaneous spatial variation: {minimum}..{maximum}");
        field.Configure(6000, travelWind, 202, settings);
        float maxAdvectiveError = 0, oppositeError = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var downwind = points[i] with { AroundMeters = points[i].AroundMeters + travelWind.X * 2, AcrossMeters = points[i].AcrossMeters + travelWind.Y * 2 };
            var upwind = points[i] with { AroundMeters = points[i].AroundMeters - travelWind.X * 2, AcrossMeters = points[i].AcrossMeters - travelWind.Y * 2 };
            maxAdvectiveError = Math.Max(maxAdvectiveError, Math.Abs(field.Sample(downwind, settings) - beforeValues[i]));
            oppositeError += Math.Abs(field.Sample(upwind, settings) - beforeValues[i]);
        }
        oppositeError /= points.Count;
        if (maxAdvectiveError > .0001f || oppositeError < .08f)
            throw new Exception($"Downwind propagation was wrong or spatially uniform: downwind error={maxAdvectiveError}, opposite error={oppositeError}");
        return new() { ["passed"] = true, ["seam_error"] = Math.Abs(original - seam), ["seed_delta"] = Math.Abs(other - original), ["long_clock_gust"] = before, ["spatial_spread"] = maximum - minimum, ["downwind_error"] = maxAdvectiveError, ["opposite_error"] = oppositeError, ["atlas_sha256"] = fingerprint };
    }
}
