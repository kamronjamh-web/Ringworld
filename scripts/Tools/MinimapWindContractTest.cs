using System;
using System.Reflection;
using Godot;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Weather;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

/// <summary>CPU projection contract; rendered/input coverage runs in the real map scene.</summary>
[GlobalClass]
public partial class MinimapWindContractTest : RefCounted
{
    public Godot.Collections.Dictionary ExecuteFixture(string operation, Node? world = null, string directory = "")
    {
        try
        {
            bool passed;
            switch (operation)
            {
                case "projection": passed = RunContract(); break;
                case "isolation": InstallIsolatedPreferences(world!, directory); passed = true; break;
                case "live": passed = RunLiveSource(world!); break;
                default: throw new ArgumentException("Unknown fixture operation " + operation);
            }
            return new() { ["pass"] = passed, ["detail"] = operation };
        }
        catch (Exception error)
        {
            return new() { ["pass"] = false, ["detail"] = error.ToString() };
        }
    }

    public void InstallIsolatedPreferences(Node world, string directory)
    {
        Require(directory.StartsWith("res://.godot/", StringComparison.Ordinal) && !directory.Contains(".."), "Fixture preferences must stay inside generated .godot directory.");
        var drawer = world.GetNode<RingLiveTuningDrawer>("LiveTuningDrawer");
        drawer.InstallAntiAliasingDefaults(directory + "/aa.tres");
        drawer.InstallMotionDefaults(directory + "/motion.tres");
        drawer.InstallWaterDefaults(directory + "/water.tres");
        drawer.InstallWindDefaults(directory + "/wind.tres");
        drawer.InstallRainHazeDefaults(directory + "/rain-haze.tres");
        world.GetNode<RingLocalWeatherEffects>("LocalWeatherEffects").RainHazePreferencesPath = directory + "/rain-haze.tres";
        var weather = world.GetNode<RingVolumetricCloudLayer>("RingVolumetricClouds");
        weather.Set("WindPreferencesPath", directory + "/wind.tres");
        weather.Set("LegacyGrassPreferencesPath", directory + "/legacy-grass.tres");
        drawer.InstallGrassDefaults(directory + "/grass.tres");
        drawer.InstallVegetationDefaults(directory + "/vegetation.tres");
        drawer.InstallShadowDefaults(directory + "/shadows.tres");
        drawer.InstallSurfaceTerrainDefaults(directory + "/surface.tres");
        world.GetNode<RingGraphicsQualityController>("GraphicsQuality").InstallAtmosphereDefaults(directory + "/atmosphere.tres");
        var pause = world.GetNode<RingLodPauseMenu>("LodPauseMenu");
        pause.InstallRenderResolutionDefaults(directory + "/resolution.tres");
        // These older owners have no install hook. Change only their path before Ready.
        var coverage = PrivateField(pause, "_proxyCoverageCullingDefaults");
        SetPrivateField(coverage, "_pathOverride", directory + "/coverage.tres");
        var clouds = world.GetNode<RingSunshineCloudBridge>("SunshineCloudBridge").GameplayCloudDefaults;
        SetPrivateField(clouds, "_pathOverride", directory + "/clouds.tres");
        world.GetNodeOrNull<Node>("WorldSaveManager")?.Free();
    }

    public bool RunLiveSource(Node world)
    {
        var map = world.GetNode<RingTerrainMinimap>("TerrainMinimap");
        var local = world.GetNode<RingLocalWeatherEffects>("LocalWeatherEffects");
        var weather = world.GetNode<RingVolumetricCloudLayer>("RingVolumetricClouds");
        var explorer = world.GetNode<RingVoxelExplorerController>("Explorer");
        var settings = RingWindSettings.Current;
        settings.Enabled = true;
        settings.RadarWindArrowEnabled = true;
        settings.RadarWindCalmThreshold = 0.1f;
        weather.Enabled = true;
        local.Enabled = true;
        local.SetProcess(false);
        explorer.SetPhysicsProcess(false);
        explorer.SetPresentationView(90.0f, -20.0f);
        var arrow = (RingMinimapWindArrow)PrivateField(map, "_windArrow");
        var camera = world.GetNode<Camera3D>("Explorer/Head/Camera3D");
        var grid = world.GetNode<RingworldTechDemo.Voxel.Phase2.VoxelChunkGridManager>("ChunkGridManager");
        var position = explorer.CanonicalPosition;
        Vector3 tangent = (grid.CanonicalRingPositionToWorld(position with { AroundMeters = position.AroundMeters + 100 })
            - grid.CanonicalRingPositionToWorld(position with { AroundMeters = position.AroundMeters - 100 })).Normalized();
        Vector3 across = (grid.GlobalBasis.Orthonormalized() * Vector3.Up).Normalized();
        Vector2 fixedWind = new(12, -8);
        Publish(local, fixedWind);
        Require(local.CurrentWindSnapshot.Enabled && local.CurrentWindSnapshot.InstantWind.IsEqualApprox(fixedWind), "Real weather must publish requested instantaneous wind.");
        map.SetDetailsExpanded(true);
        foreach (float yaw in new[] { 0f, 90f, 180f, 270f, 359f, 1f, -1f, 361f })
        {
            Vector2? previousMapOrientation = null;
            foreach (bool heading in new[] { false, true })
            {
                map.HeadingUp = heading;
                explorer.SetPresentationView(yaw, -20.0f);
                map._Process(0.0);
                // Independent world-vector reference uses the actual character body/head yaw basis.
                Basis facing = explorer.GlobalBasis * new Basis(Vector3.Up, explorer.SavedYaw);
                Vector3 worldWind = tangent * fixedWind.X + across * fixedWind.Y;
                Vector2 expected = new(worldWind.Dot(facing.X), worldWind.Dot(facing.Z));
                expected = expected.Normalized();
                Require(arrow.HasDirection && arrow.FacingRelativeDirection.Dot(expected) > 0.999f,
                    "Fixed cached wind needle must follow actual character facing, including yaw wrap.");
                if (previousMapOrientation.HasValue)
                    Require(arrow.FacingRelativeDirection.IsEqualApprox(previousMapOrientation.Value), "Map orientation must not turn the character-relative compass.");
                previousMapOrientation = arrow.FacingRelativeDirection;
                foreach (float pitch in new[] { -89f, 0f, 89f })
                {
                    explorer.SetPresentationView(yaw, pitch);
                    camera.Rotation = new Vector3(camera.Rotation.X, camera.Rotation.Y, .7f);
                    map._Process(0.0);
                    Require(arrow.FacingRelativeDirection.IsEqualApprox(previousMapOrientation.Value), "Camera pitch/roll must not turn the yaw-only compass.");
                    camera.Rotation = Vector3.Zero;
                }
            }
        }
        explorer.SetPresentationView(90, -20);
        foreach (Vector2 wind in new[] { new Vector2(12, 0), new Vector2(0, -8), new Vector2(-9, 7) })
        {
            Publish(local, wind);
            map._Process(0.0);
            Basis facing = explorer.GlobalBasis * new Basis(Vector3.Up, explorer.SavedYaw);
            Vector3 worldWind = tangent * wind.X + across * wind.Y;
            Vector2 expected = new(worldWind.Dot(facing.X), worldWind.Dot(facing.Z));
            Require(local.CurrentWindSnapshot.InstantWind.IsEqualApprox(wind) && arrow.HasDirection,
                "Changing cached live weather must update the compass needle.");
            Require(arrow.FacingRelativeDirection.Dot(expected.Normalized()) > .999f,
                "Changing cached live wind must update its destination relative to facing.");
        }
        Publish(local, Vector2.Zero);
        map._Process(0.0);
        Require(!arrow.HasDirection, "Explicit calm cached snapshot must suppress arrow.");
        settings.Enabled = false;
        Publish(local, new Vector2(12, 0));
        map._Process(0.0);
        Require(!local.CurrentWindSnapshot.Enabled && !arrow.HasDirection, "Disabled shared weather must suppress arrow.");
        settings.Enabled = true;
        Publish(local, new Vector2(12, -8));
        settings.RadarWindArrowEnabled = false;
        map._Process(0.0);
        Require(!arrow.HasDirection, "Live arrow preference must suppress arrow.");
        settings.RadarWindArrowEnabled = true;
        map._Process(0.0);
        Require(arrow.HasDirection, "Reenabled live preference must restore cached arrow.");
        return true;
    }

    public bool SetCaptureFacing(Node world, float yawDegrees, bool headingUp)
    {
        var map = world.GetNode<RingTerrainMinimap>("TerrainMinimap");
        world.GetNode<RingVoxelExplorerController>("Explorer").SetPresentationView(yawDegrees, -20);
        map.HeadingUp = headingUp;
        map.SetDetailsExpanded(true);
        map._Process(0.0);
        return ((RingMinimapWindArrow)PrivateField(map, "_windArrow")).HasDirection;
    }

    private static void Publish(RingLocalWeatherEffects local, Vector2 wind)
    {
        local.SetExternalWeatherOverride(true, default(RingVolumetricCloudLayer.LocalWeatherState) with { WindMetersPerSecond = wind });
        local._Process(0.0);
    }

    private static object PrivateField(object owner, string name) => owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
        ?? throw new InvalidOperationException("Missing fixture field " + name);

    private static void SetPrivateField(object owner, string name, object value)
    {
        var field = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing fixture field " + name);
        field.SetValue(owner, value);
    }

    public bool RunContract()
    {
        Vector2 wind = new(10, 0);
        Vector2 front = RingMinimapWindArrow.ResolveFacingRelativeWind(wind, 0);
        Vector2 right = RingMinimapWindArrow.ResolveFacingRelativeWind(wind, Mathf.Pi * .5f);
        Vector2 back = RingMinimapWindArrow.ResolveFacingRelativeWind(wind, Mathf.Pi);
        Require(front.Y < 0 && Mathf.Abs(front.X) < .001f, "Around destination must be forward at yaw zero.");
        Require(right.X > 0 && Mathf.Abs(right.Y) < .001f, "Around destination must be right when facing positive across.");
        Require(back.Y > 0 && Mathf.Abs(back.X) < .001f, "Around destination must be behind when facing negative around.");
        Vector2 beforeWrap = RingMinimapWindArrow.ResolveFacingRelativeWind(wind, Mathf.DegToRad(359)).Normalized();
        Vector2 afterWrap = RingMinimapWindArrow.ResolveFacingRelativeWind(wind, Mathf.DegToRad(1)).Normalized();
        Require(beforeWrap.Dot(afterWrap) > .999f, "Yaw wrap must remain continuous.");
        using var arrow = new RingMinimapWindArrow();
        arrow.SetDirection(front, true);
        Require(arrow.HasDirection && arrow.ScreenDirection.Y < 0, "Live wind must display a forward needle.");
        arrow.SetDirection(right, true);
        Require(arrow.ScreenDirection.X > 0, "Fixed wind must change needle orientation with facing.");
        arrow.SetDirection(Vector2.Zero, true);
        Require(!arrow.HasDirection, "Calm must have no needle.");
        arrow.SetDirection(front, false);
        Require(!arrow.HasDirection, "Unavailable wind must have no needle.");
        arrow.SetDirection(new Vector2(float.NaN, 1), true);
        Require(!arrow.HasDirection, "Nonfinite wind must have no needle.");
        return true;
    }

    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
