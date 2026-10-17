using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Celestial.Eclipse;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Underground;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Eclipse phase E4c (docs/eclipse-false-sun-spec.md E.1): the hub sun body proxy and the far-viewport sun colour mirror. The far suns are
/// bit-exactly white at hub weight 0 (every non-eclipse frame is unchanged) and the hub colour at weight 1; the body is inert and hidden
/// outside the hub light, follows the hub level (override) with the spec radius and emission, sits at half the camera far plane along the
/// direction to the hub point (cross-checked against the independent beam layer), keeps its angular size, survives a floating-origin rebase, honours
/// its setting switch, and round-trips through the underground participant. Expectations come from the live registry and topology; no goldens.
/// </summary>
public partial class EclipseHubBodyTest : RefCounted
{
    static void Expect(List<string> failures, bool ok, string message) { if (!ok) failures.Add(message); }
    static bool Near(double a, double b, double eps = 1e-6) => Math.Abs(a - b) <= eps;

    public int Run(bool unused)
    {
        _ = unused;
        var failures = new List<string>();
        Node3D? fixture = null;
        try
        {
            CheckFarSunColor(failures);
            CheckProfile(failures);

            SceneTree tree = (Engine.GetMainLoop() as SceneTree) ?? throw new InvalidOperationException("scene tree unavailable");
            using RingTerrainSampler sampler = EmitterSiteTestWorlds.CreateGen17(1337);
            using INaturalHeightSource source = EmitterSiteHeightSources.Create(sampler);
            EmitterSiteRegistry registry = EmitterSiteRegistry.GetOrBuild(EmitterSiteRecipe.Default(1337), source);
            EmitterGridTopology topology = EmitterGridTopology.Build(registry);
            fixture = new Node3D { Name = "EclipseHubBodyFixture" };
            tree.Root.AddChild(fixture);
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
            grid.ConfigureRuntimeRingTopology(EmitterSiteTestWorlds.Circumference / 32, EmitterSiteTestWorlds.Circumference / Math.Tau);
            var camera = new Camera3D { Name = "Camera", Current = true, Far = 1_200_000f };
            fixture.AddChild(camera);
            var gridLayer = new EmitterGridBeamLayer { Name = "EmitterGrid", AutoBind = false, LoadSavedDefaults = false };
            fixture.AddChild(gridLayer);
            if (!gridLayer.Bind(grid, topology)) { Console.Error.WriteLine("ECLIPSE_HUB_BODY=FAIL grid layer bind"); return 1; }
            var director = new EclipseEventDirector { Name = "EclipseEvent", AutoBind = false, LoadSavedDefaults = false };
            fixture.AddChild(director);
            var reference = new EclipseHubBeamLayer { Name = "ReferenceBeams", AutoBind = false };
            fixture.AddChild(reference);
            if (!reference.Bind(grid, topology)) { Console.Error.WriteLine("ECLIPSE_HUB_BODY=FAIL beam layer bind"); return 1; }
            var body = new EclipseHubBody { Name = "EclipseHubBody", AutoBind = false };
            fixture.AddChild(body);

            CheckInert(body, director, failures);
            CheckBody(body, director, camera, reference, failures);
            CheckRebase(body, director, grid, reference, failures);
            CheckSettings(body, director, failures);
            CheckUnderground(body, director, fixture, failures);
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.ToString()); return 1; }
        finally
        {
            EclipseHubLighting.Reset();
            if (fixture != null && GodotObject.IsInstanceValid(fixture))
            {
                fixture.GetParent()?.RemoveChild(fixture);
                fixture.Free();
            }
        }
        if (failures.Count != 0) { Console.Error.WriteLine("ECLIPSE_HUB_BODY=FAIL " + string.Join(" | ", failures.Take(25))); return 1; }
        Console.WriteLine("ECLIPSE_HUB_BODY=PASS");
        return 0;
    }

    // ---------------------------------------------------------------- far-viewport sun colour

    static void CheckFarSunColor(List<string> failures)
    {
        (float, float, float) hub = (0.92f, 0.97f, 1.0f);
        // Weight 0 (and anything that is not a positive finite weight) is exactly white, whatever the hub colour: every non-eclipse frame is unchanged.
        foreach (float weight in new[] { 0f, -0.0f, -1f, float.NaN, float.NegativeInfinity })
        {
            Color c = RingScaledFarViewport.ResolveFarSunColor(weight, hub);
            Expect(failures, c.R == 1f && c.G == 1f && c.B == 1f && c.A == 1f && c == Colors.White, $"far sun colour at hub weight {weight} is {c}, not exactly white");
        }
        Color full = RingScaledFarViewport.ResolveFarSunColor(1f, hub);
        Expect(failures, full.R == 0.92f && full.G == 0.97f && full.B == 1.0f && full.A == 1f, $"far sun colour at weight 1 is {full}");
        Color over = RingScaledFarViewport.ResolveFarSunColor(7f, hub);
        Expect(failures, over == full, "weights above 1 are not clamped");
        Color half = RingScaledFarViewport.ResolveFarSunColor(0.5f, hub);
        Expect(failures, Near(half.R, 0.96, 1e-6) && Near(half.G, 0.985, 1e-6) && Near(half.B, 1.0, 1e-6), $"far sun colour at weight 0.5 is {half}");
        Color tiny = RingScaledFarViewport.ResolveFarSunColor(1e-6f, hub);
        Expect(failures, tiny != Colors.White && Near(tiny.R, 1.0, 1e-6), "a tiny positive weight is not mirrored smoothly");

        // The static the director publishes: white until a colour is published, reset by Reset().
        EclipseHubLighting.Reset();
        Expect(failures, EclipseHubLighting.Color == (1f, 1f, 1f) && EclipseHubLighting.Weight == 0f, "EclipseHubLighting is not white / zero after Reset");
        EclipseHubLighting.SetColor(0.92f, 0.97f, 1f);
        Expect(failures, EclipseHubLighting.Color == (0.92f, 0.97f, 1f), "EclipseHubLighting colour did not store");
        EclipseHubLighting.SetColor(float.NaN, 5f, -2f);
        Expect(failures, EclipseHubLighting.Color == (1f, 1f, 0f), $"EclipseHubLighting colour clamps: {EclipseHubLighting.Color}");
        EclipseHubLighting.Reset();
        Expect(failures, EclipseHubLighting.Color == (1f, 1f, 1f), "Reset did not restore white");

        // A director at weight 0 publishes white even when the hub colour setting is not white; at the override (weight 1) it publishes the setting.
        var director = new EclipseEventDirector { Name = "FarSunDirector", AutoBind = false, LoadSavedDefaults = false };
        director.Engine.SetSettings(new EclipseEventSettings { HubLightRed = 0.5d, HubLightGreen = 0.6d, HubLightBlue = 0.7d });
        director.RefreshHubLight(0d);
        Expect(failures, EclipseHubLighting.Weight == 0f && RingScaledFarViewport.ResolveFarSunColor(EclipseHubLighting.Weight, EclipseHubLighting.Color) == Colors.White, "director at weight 0 changed the far sun colour");
        director.SetHubLevelOverride(1f);
        Color mirrored = RingScaledFarViewport.ResolveFarSunColor(EclipseHubLighting.Weight, EclipseHubLighting.Color);
        Expect(failures, EclipseHubLighting.Weight == 1f && Near(mirrored.R, 0.5, 1e-6) && Near(mirrored.G, 0.6, 1e-6) && Near(mirrored.B, 0.7, 1e-6), $"director override did not publish the hub colour: {mirrored}");
        director.SetHubLevelOverride(-1f);
        director.RefreshHubLight(0d);
        Expect(failures, EclipseHubLighting.Weight == 0f && RingScaledFarViewport.ResolveFarSunColor(EclipseHubLighting.Weight, EclipseHubLighting.Color) == Colors.White, "far sun colour did not return to exactly white");
        director.Free();
        EclipseHubLighting.Reset();
    }

    // ---------------------------------------------------------------- pure profile

    static void CheckProfile(List<string> failures)
    {
        var s = new EclipseHubBodySettings();
        Expect(failures, Near(EclipseHubBodyMath.CoreRadiusKm(s, 0d), 15d) && Near(EclipseHubBodyMath.CoreRadiusKm(s, 1d), 30d) && Near(EclipseHubBodyMath.CoreRadiusKm(s, 2.5d), 30d), "core radius 15 / 30 / 30 km at L 0 / 1 / 2.5");
        double kindleRadius = EclipseHubBodyMath.CoreRadiusKm(s, 0.1d);
        Expect(failures, Near(kindleRadius, 30d * (0.5d + 0.5d * Math.Sqrt(0.1d)), 1e-9) && kindleRadius > 19d && kindleRadius < 20d, $"kindle core radius {kindleRadius:F3} km");
        Expect(failures, Near(EclipseHubBodyMath.CoreEmission(s, 0.1d), 4d) && Near(EclipseHubBodyMath.CoreEmission(s, 1d), 40d) && Near(EclipseHubBodyMath.CoreEmission(s, 2.5d), 100d), "core emission 40 x L");
        Expect(failures, Near(EclipseHubBodyMath.HaloEmission(s, 1d), 0.6d) && Near(EclipseHubBodyMath.HaloEmission(s, 0d), 0d), "halo emission 0.6 x L");
        Expect(failures, Near(30d, 0.34d * Math.PI / 180d * 4_998_000d / 1000d, 0.5d), "30 km at the floor is about 0.34 degrees");
    }

    // ---------------------------------------------------------------- the node

    static void CheckInert(EclipseHubBody body, EclipseEventDirector director, List<string> failures)
    {
        for (int i = 0; i < 3; i++) body._Process(0.016);
        Expect(failures, body.Quad == null && body.GetChildCount() == 0 && body.DrawnLevel == 0f, "the body built a quad without a hub light");
        Expect(failures, body.DescribeStatus().Contains("hidden"), "idle status text");
        // Hub weight above 0 but level 0 (the Darkness beat): still nothing.
        director.SetHubLevelOverride(0f);
        body._Process(0.016);
        Expect(failures, body.Quad == null || !body.Quad.Visible, "the body is visible at hub level 0");
        director.SetHubLevelOverride(-1f);
        body._Process(0.016);
        Expect(failures, body.Quad == null || !body.Quad.Visible, "the body is visible with the override cleared");
    }

    static void CheckBody(EclipseHubBody body, EclipseEventDirector director, Camera3D camera, EclipseHubBeamLayer reference, List<string> failures)
    {
        camera.GlobalPosition = new Vector3(120f, 30f, -45f);
        director.SetHubLevelOverride(1f);
        body._Process(0.016);
        Expect(failures, body.Quad is { Visible: true } && body.Material != null && body.HubKnown && body.DrawnLevel == 1f, "the body is not drawn at hub level 1");
        if (body.Quad == null || body.Material == null) return;
        Expect(failures, body.Material.RenderPriority == EclipseHubBody.BodyRenderPriority && body.Material.Shader?.ResourcePath == EclipseHubBody.ShaderPath, "material priority / shader");

        // Placement: half the camera far plane along the direction to the hub point, which the independent beam layer also evaluates.
        Vector3 cam = camera.GlobalPosition;
        Vector3D toHub = reference.HubWorld - new Vector3D(cam.X, cam.Y, cam.Z);
        double distance = toHub.Length;
        Vector3 expectedDir = new((float)(toHub.X / distance), (float)(toHub.Y / distance), (float)(toHub.Z / distance));
        Expect(failures, Near(body.HubDistanceMeters, distance, 1e-3) && (body.HubWorld - reference.HubWorld).Length < 1e-6, $"hub distance {body.HubDistanceMeters:F3} vs {distance:F3}");
        Expect(failures, body.HubDirection.DistanceTo(expectedDir) < 1e-6f, $"hub direction {body.HubDirection} vs {expectedDir}");
        Expect(failures, Near(body.ProxyDistanceMeters, 600_000d, 1e-3) && body.Quad.GlobalPosition.DistanceTo(cam + expectedDir * 600_000f) < 0.5f, $"proxy placement {body.Quad.GlobalPosition}");
        Expect(failures, distance > 1000d, $"hub distance {distance:F0} m is not far away");

        // The profile at L = 1 reaches the material in proxy metres, preserving the angular size.
        double scale = body.ProxyDistanceMeters / distance;
        var s = director.Engine.Settings.HubBody;
        Expect(failures, Near((float)body.Material.GetShaderParameter("core_radius_m"), 30_000d * scale, 1e-3 * 30_000d * scale), $"core radius uniform {body.Material.GetShaderParameter("core_radius_m")}");
        Expect(failures, Near((float)body.Material.GetShaderParameter("halo_radius_m"), 400_000d * scale, 1e-3 * 400_000d * scale), "halo radius uniform");
        Expect(failures, Near((float)body.Material.GetShaderParameter("core_emission"), 40d, 1e-4) && Near((float)body.Material.GetShaderParameter("halo_emission"), 0.6d, 1e-5), "emission uniforms at L = 1");
        double angular = Math.Atan(30_000d / distance) * 180d / Math.PI;
        Expect(failures, Near(body.CoreAngularRadiusDegrees, angular, 1e-3), $"core angular radius {body.CoreAngularRadiusDegrees:F4} deg");
        float quadHalf = (float)body.Material.GetShaderParameter("quad_half_m");
        Expect(failures, quadHalf >= 5f * (float)(400_000d * scale) - 1e-3f, "the quad is narrower than five halo e-folds");

        // The kindle: level 0.1 gives emission 4 and the smaller core, still drawn.
        director.SetHubLevelOverride(0.1f);
        body._Process(0.016);
        Expect(failures, body.Quad.Visible && Near(body.DrawnLevel, 0.1d, 1e-6), "the kindle body is not drawn");
        Expect(failures, Near((float)body.Material.GetShaderParameter("core_emission"), 4d, 1e-4), "kindle core emission");
        double kindleKm = EclipseHubBodyMath.CoreRadiusKm(s, 0.1d);
        Expect(failures, Near((float)body.Material.GetShaderParameter("core_radius_m"), kindleKm * 1000d * scale, 1e-3 * kindleKm * 1000d * scale) && Near(body.CoreRadiusKm, kindleKm, 1e-6), "kindle core radius");

        // The overload swell (level 2.5): the core stops growing at L = 1; the emission and the halo carry the growth.
        director.SetHubLevelOverride(2.5f);
        body._Process(0.016);
        Expect(failures, Near(body.CoreRadiusKm, 30d) && Near((float)body.Material.GetShaderParameter("core_emission"), 100d, 1e-3) && Near((float)body.Material.GetShaderParameter("halo_emission"), 1.5d, 1e-4), "overload core / halo");

        // A different camera far moves the proxy (always half the far plane) without changing the angular size.
        camera.Far = 800_000f;
        director.SetHubLevelOverride(1f);
        body._Process(0.016);
        Expect(failures, Near(body.ProxyDistanceMeters, 400_000d, 1e-3) && Near(body.CoreAngularRadiusDegrees, angular, 1e-3), "camera far change");
        camera.Far = 1_200_000f;
        body._Process(0.016);

        // Level back to 0: hidden again, and the debug override cleared: hidden.
        director.SetHubLevelOverride(-1f);
        body._Process(0.016);
        Expect(failures, !body.Quad.Visible && body.DrawnLevel == 0f && body.DescribeStatus().Contains("hidden"), "the body stays visible after the override clears");
    }

    static void CheckRebase(EclipseHubBody body, EclipseEventDirector director, VoxelChunkGridManager grid, EclipseHubBeamLayer reference, List<string> failures)
    {
        director.SetHubLevelOverride(1f);
        body._Process(0.016);
        int rebuilds = body.HubRebuildCount;
        grid.SetFloatingOriginCanonicalAnchor(new RingCanonicalPosition(5_000_000d, 100d, 150_000d));
        Expect(failures, body.HubRebuildCount == rebuilds + 1, $"an anchor change rebuilt the hub point {body.HubRebuildCount - rebuilds} times");
        Expect(failures, (body.HubWorld - reference.HubWorld).Length < 1e-6, "after the rebase the hub point differs from the independent evaluation");
        grid.SetFloatingOriginCanonicalAnchor(new RingCanonicalPosition(5_000_000d, 100d, 150_000d));
        Expect(failures, body.HubRebuildCount == rebuilds + 1, "the same anchor rebuilt the hub point");
        grid.SetFloatingOriginCanonicalAnchor(new RingCanonicalPosition(grid.GetCanonicalRingCircumferenceMeters64() - 5d, 100d, 150_000d));
        Expect(failures, (body.HubWorld - reference.HubWorld).Length < 1e-6, "seam anchor: the hub point differs from the independent evaluation");
        grid.ClearFloatingOriginCanonicalAnchor();
        body._Process(0.016);
        Expect(failures, (body.HubWorld - reference.HubWorld).Length < 1e-6 && body.Quad is { Visible: true }, "cleared anchor");
        // Steady frames evaluate nothing.
        int steady = body.HubRebuildCount;
        for (int i = 0; i < 120; i++) body._Process(0.016);
        Expect(failures, body.HubRebuildCount == steady, $"steady frames rebuilt the hub point {body.HubRebuildCount - steady} times");
        director.SetHubLevelOverride(-1f);
        body._Process(0.016);
    }

    static void CheckSettings(EclipseHubBody body, EclipseEventDirector director, List<string> failures)
    {
        director.SetHubLevelOverride(1f);
        body._Process(0.016);
        Expect(failures, body.Quad is { Visible: true }, "settings: not drawn at the start");
        EclipseEventSettings settings = director.Engine.Settings;
        director.Engine.SetSettings(settings with { HubBody = settings.HubBody with { BodyEnabled = false } });
        body._Process(0.016);
        Expect(failures, body.Quad is { Visible: false }, "the body ignores its disable switch");
        director.Engine.SetSettings(settings with { HubBody = settings.HubBody with { CoreEmission = 25d, RadiusKm = 45d, HaloAmplitude = 1d, HaloRadiusKm = 500d, MinRadiusPixels = 3d } });
        body._Process(0.016);
        Expect(failures, body.Quad is { Visible: true } && Near((float)body.Material!.GetShaderParameter("core_emission"), 25d, 1e-4) && Near(body.CoreRadiusKm, 45d, 1e-6)
            && Near((float)body.Material.GetShaderParameter("halo_emission"), 1d, 1e-5) && Near((float)body.Material.GetShaderParameter("min_radius_px"), 3d, 1e-6), "new settings did not reach the material");
        // A hub body change does not touch the timeline.
        Expect(failures, director.Engine.Settings.Sanitized() == director.Engine.Settings, "settings are not sanitised");
        director.Engine.SetSettings(settings);
        director.SetHubLevelOverride(-1f);
        body._Process(0.016);
    }

    static void CheckUnderground(EclipseHubBody body, EclipseEventDirector director, Node3D fixture, List<string> failures)
    {
        var root = new Node { Name = "UndergroundRoot" };
        fixture.AddChild(root);
        // The participant binds by node name under the scene root; reparent a stand-in with the body's name.
        var stand = new EclipseHubBody { Name = "EclipseHubBody", AutoBind = false };
        root.AddChild(stand);
        List<UndergroundParticipant> participants = UndergroundParticipantFactory.CreateDefaults();
        UndergroundParticipant? participant = participants.FirstOrDefault(p => p.Name == "EclipseHubBody");
        Expect(failures, participant != null, "the hub body participant is not registered");
        if (participant == null) { root.Free(); return; }
        participant.Bind(root);
        Expect(failures, participant.Present && participant.Group == UndergroundGroups.FarTerrain, "participant not bound / group");
        string surface = participant.Describe();
        ulong[] ids = participant.RetainedInstanceIds();
        participant.Apply(new UndergroundParticipantState(true, false, false, 1f));
        Expect(failures, stand.UndergroundHold && !stand.UndergroundHidden, "hold state");
        participant.Apply(new UndergroundParticipantState(true, true, true, 1f));
        Expect(failures, stand.UndergroundHidden && stand.UndergroundHold, "hide state");
        participant.Apply(UndergroundParticipantState.Clear);
        Expect(failures, !stand.UndergroundHold && !stand.UndergroundHidden, "flags not cleared on exit");
        Expect(failures, participant.Describe() == surface && participant.RetainedInstanceIds().SequenceEqual(ids), $"describe / retained ids differ after the round trip: '{participant.Describe()}' vs '{surface}'");

        // A body that has been drawn is hidden when told so, and returns on exit without being rebuilt.
        director.SetHubLevelOverride(1f);
        body._Process(0.016);
        ulong quadId = body.Quad!.GetInstanceId();
        body.SetUndergroundHidden(true);
        body._Process(0.016);
        Expect(failures, !body.Quad.Visible, "the body draws while hidden underground");
        body.SetUndergroundHidden(false);
        body._Process(0.016);
        Expect(failures, body.Quad.Visible && body.Quad.GetInstanceId() == quadId, "the body did not return (or was rebuilt) after the underground exit");
        director.SetHubLevelOverride(-1f);
        body._Process(0.016);
        fixture.RemoveChild(root);
        root.Free();
    }
}
