using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using GodotArray = Godot.Collections.Array<Godot.Image>;
using RingworldTechDemo.Terrain.Surface;

namespace RingworldTechDemo;

/// <summary>
/// Contract test for the Terrain3D surface follower fan-out (grass-cards plan step S1): registry forwarding and
/// dedupe on a dummy follower, then the same behaviour through a real prepared-window surface (staged windows never
/// drive followers; activation replays; a stationary camera writes nothing).
/// </summary>
public partial class TerrainSurfaceFollowerTest : Node
{
    private const int Window = 384;
    private const int Region = 128;
    private const float MaxHeight = 8.0f;

    private sealed class DummyFollower : IRingSurfaceFollower
    {
        public bool Alive = true;
        public int Writes;
        public readonly Dictionary<string, Variant> Values = new();
        public bool IsAlive => Alive;
        public void SetSurfaceParameter(StringName name, Variant value) { Writes++; Values[name.ToString()] = value; }
    }

    private int _checks;
    public bool Finished { get; private set; }
    public bool Passed { get; private set; }
    public string Error { get; private set; } = string.Empty;
    public bool IsFinished() => Finished;
    public bool IsPassed() => Passed;

    public void Start() => _ = RunAsync();

    private async Task RunAsync()
    {
        try
        {
            RunRegistryContract();
            await RunSurfaceContract();
            Passed = true;
            GD.Print($"TERRAIN_SURFACE_FOLLOWER_TEST=PASS checks={_checks}");
        }
        catch (Exception exception)
        {
            Error = exception.Message;
            GD.PushError("TERRAIN_SURFACE_FOLLOWER_TEST=FAIL " + exception.Message);
        }
        Finished = true;
    }

    private void Expect(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private void RunRegistryContract()
    {
        var registry = new RingSurfaceFollowerRegistry();
        var current = new Dictionary<StringName, Variant>
        {
            ["a"] = 1.0f, ["b"] = new Vector3(1, 2, 3), ["unsubscribed"] = 9.0f,
        };
        var follower = new DummyFollower();
        registry.Register(follower, new[] { (StringName)"a", (StringName)"b", (StringName)"missing" }, current);
        Expect(follower.Writes == 2 && registry.WriteCount == 2, $"register must replay existing subscribed values only, wrote {follower.Writes}");
        Expect(!follower.Values.ContainsKey("unsubscribed") && !follower.Values.ContainsKey("missing"), "unsubscribed/unknown names must not be written");

        registry.Forward("a", 1.0f);
        registry.Forward("b", new Vector3(1, 2, 3));
        registry.Forward("unsubscribed", 2.0f);
        Expect(follower.Writes == 2, "unchanged or unsubscribed values must not be written");
        registry.Forward("a", 2.0f);
        Expect(follower.Writes == 3 && (float)follower.Values["a"] == 2.0f, "changed value must be forwarded once");
        registry.Forward("a", 2.0f);
        Expect(follower.Writes == 3, "repeat of the last forwarded value must be skipped");

        current["a"] = 3.0f;
        registry.Replay(current);
        Expect(follower.Writes == 4, "replay writes only the subscribed value that changed");
        registry.Replay(current);
        Expect(follower.Writes == 4, "idle replay writes nothing");

        var second = new DummyFollower();
        registry.Register(second, new[] { (StringName)"a" }, current);
        registry.Forward("a", 4.0f);
        Expect(follower.Writes == 5 && second.Writes == 2, "followers are tracked independently");

        Expect(registry.Unregister(second) && !registry.Unregister(second), "unregister removes once");
        registry.Forward("a", 5.0f);
        Expect(second.Writes == 2, "an unregistered follower receives nothing");
        follower.Alive = false;
        registry.Forward("a", 6.0f);
        Expect(registry.Count == 0 && follower.Writes == 6, "a freed follower is pruned without a write");
    }

    private async Task NextFrame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    private static float[] FlatHeights() => new float[Window * Window];

    private static (GodotArray heights, GodotArray biomes) FlatImages()
    {
        var heights = new GodotArray();
        var biomes = new GodotArray();
        for (int i = 0; i < 9; i++)
        {
            heights.Add(Image.CreateEmpty(Region, Region, false, Image.Format.Rf));
            biomes.Add(Image.CreateEmpty(Region, Region, false, Image.Format.Rgba8));
        }
        return (heights, biomes);
    }

    private async Task StageWindow(RingTerrain3DSurface surface, long revision, Vector3 pose)
    {
        var geometry = new RingTerrain3DCollisionGeometry();
        Expect(geometry.PrepareGameplay(FlatHeights(), 0, 159000, 0.0f, -1, 0.0f, MaxHeight, 32), "prepare collision geometry");
        (GodotArray heights, GodotArray biomes) = FlatImages();
        Expect(surface.BeginGameplayPreparedWindow(FlatHeights(), heights, biomes, geometry, 0, 159000, 0.0f, -1, revision, 0.0f, MaxHeight), "begin prepared window");
        bool ready = false;
        for (int frame = 0; frame < 480 && !ready; frame++)
        {
            var state = surface.AdvanceBudgetedPreparation(0.5, 0.5);
            Expect(state["ok"].AsBool(), "prepared state invalid");
            ready = state["ready"].AsBool();
            if (!ready) { surface.UpdateCamera(pose); await NextFrame(); }
        }
        Expect(ready, "prepared window did not become ready");
    }

    private async Task RunSurfaceContract()
    {
        var sun = new DirectionalLight3D { Name = "Sun" };
        AddChild(sun);
        var surface = new RingTerrain3DSurface();
        AddChild(surface);
        await NextFrame();

        var follower = new DummyFollower();
        var names = new HashSet<StringName>
        {
            "sun_direction", "night_tint", "shadow_floor", "ring_atmosphere_camera_world", "ring_atmosphere_center_world",
            "ring_atmosphere_day_factor", "ring_terrain_shadow_anchor_uv", "biome_texture_scale", "biome_grass_albedo",
            "ring_radius_meters", "never_set_parameter",
        };
        surface.RegisterSurfaceFollower(follower, names);
        Expect(follower.Writes == 0, "nothing to replay before the surface owns a material");

        // First window: the facade has no active child yet, so nothing is forwarded until activation replays it.
        await StageWindow(surface, 1, new Vector3(0, 2, 0));
        Expect(follower.Writes == 0, "a staged window must not drive followers before activation");
        Expect(surface.ActivateStagedWindow(1), "activate first window");
        Expect(follower.Writes > 0, "activation must replay the active window's parameters");
        foreach (string expected in new[] { "sun_direction", "night_tint", "shadow_floor", "ring_atmosphere_center_world", "ring_terrain_shadow_anchor_uv", "biome_texture_scale", "biome_grass_albedo", "ring_radius_meters" })
            Expect(follower.Values.ContainsKey(expected), $"follower missed {expected}");
        Expect(!follower.Values.ContainsKey("never_set_parameter"), "a name the surface never sets must not appear");

        // Stationary camera: repeated frames write nothing once the first update has landed.
        Vector3 pose = new(3, 2, 4);
        for (int frame = 0; frame < 4; frame++) { surface.UpdateCamera(pose); await NextFrame(); }
        long idleBefore = surface.FollowerWriteCount;
        int dummyBefore = follower.Writes;
        for (int frame = 0; frame < 30; frame++) { surface.UpdateCamera(pose); await NextFrame(); }
        Expect(surface.FollowerWriteCount == idleBefore && follower.Writes == dummyBefore,
            $"a stationary camera must write 0 follower parameters, wrote {follower.Writes - dummyBefore}");

        // A moved camera forwards exactly the changed camera-dependent parameters.
        Vector3 moved = new(9, 2, -5);
        surface.UpdateCamera(moved);
        Expect(follower.Writes > dummyBefore, "a moved camera must forward");
        Expect(follower.Values["ring_atmosphere_camera_world"].AsVector3().IsEqualApprox(moved), "forwarded camera position must match the surface's");
        int movedWrites = follower.Writes;
        surface.UpdateCamera(moved);
        Expect(follower.Writes == movedWrites, "the repeated pose must not write again");

        // A second window is staged while the camera is stationary: its own setup must not touch the follower.
        await StageWindow(surface, 2, moved);
        int stagedWrites = follower.Writes;
        for (int frame = 0; frame < 6; frame++) { surface.UpdateCamera(moved); await NextFrame(); }
        Expect(follower.Writes == stagedWrites, $"staging/idle frames wrote {follower.Writes - stagedWrites} follower parameters");
        Expect(surface.ActivateStagedWindow(2), "activate second window");
        // Activation may rewrite a camera-derived value that differs by float round-off; it must then settle to idle.
        for (int frame = 0; frame < 3; frame++) { surface.UpdateCamera(moved); await NextFrame(); }
        int settledWrites = follower.Writes;
        for (int frame = 0; frame < 20; frame++) { surface.UpdateCamera(moved); await NextFrame(); }
        Expect(follower.Writes == settledWrites, $"after re-targeting, stationary frames wrote {follower.Writes - settledWrites} follower parameters");
        GD.Print($"TERRAIN_SURFACE_FOLLOWER_TEST window_switch_writes={settledWrites - stagedWrites}");

        Expect(surface.UnregisterSurfaceFollower(follower), "unregister follower");
        int finalWrites = follower.Writes;
        surface.UpdateCamera(new Vector3(40, 2, 40));
        await NextFrame();
        Expect(follower.Writes == finalWrites, "an unregistered follower must not be written");
        Expect(surface.FollowerCount == 0, "registry must be empty");

        surface.QueueFree();
        sun.QueueFree();
        await NextFrame();
    }
}
