using Godot;
using RingworldTechDemo.Loading;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Shared world-test helper (WO6 of docs/test-suite-tooling-design.md): the certified-preset world boot used by the cloud_layers / storm_profile_map
/// fixtures, and AwaitSettled, which replaces fixed settle timers (create_timer(20.0)) with real readiness signals.
/// GDScript cannot await a Task, so AwaitSettled is signal based:
///   helper.AwaitSettled(world, 40.0); var ok = await helper.Settled
/// </summary>
public partial class RingTestWorld : RefCounted
{
    private const int StableFramesRequired = 30;
    private const ulong StableFrameMaxMsec = 50;
    private const long MacroMapBuildsRequired = 2;

    [Signal] public delegate void SettledEventHandler(bool ok);

    public string ErrorText { get; private set; } = "";
    /// <summary>Why the last AwaitSettled timed out (which conditions were still false), or "settled in N.N s".</summary>
    public string LastReport { get; private set; } = "";

    /// <summary>
    /// Instantiates VoxelRingIntegrationTest, applies the certified preset through WorldSizeLauncher, runs far.Prepare and adds the world to the tree.
    /// The caller awaits one process_frame afterwards. Returns null (ErrorText set) on failure.
    /// </summary>
    public Node? BootCertified(SceneTree tree, GodotObject farCapture, string prepDirectory, double worldSizeMeters)
    {
        var far = (FarRingGameplayCapture)farCapture;
        var world = GD.Load<PackedScene>("res://scenes/VoxelRingIntegrationTest.tscn").Instantiate();
        var launcher = GD.Load<PackedScene>("res://scenes/WorldSizeLauncher.tscn").Instantiate();
        tree.Root.AddChild(launcher);
        launcher.Call("_on_certified_preset_pressed");
        launcher.Call("_configure_game", world, worldSizeMeters);
        launcher.Free();
        if (!far.Prepare(world, prepDirectory))
        {
            ErrorText = far.ErrorText;
            world.Free();
            return null;
        }
        tree.Root.AddChild(world);
        return world;
    }

    /// <summary>
    /// Emits Settled(true) when, on 30 consecutive frames under 50 ms: the terrain chunk queues are empty, startup shader warmup is complete and the
    /// Sunshine macro map has published at least two builds. Emits Settled(false) after maxSeconds (real time); LastReport says what was missing.
    /// </summary>
    public async void AwaitSettled(Node world, double maxSeconds)
    {
        SceneTree tree = world.GetTree();
        var grid = world.GetNodeOrNull<VoxelChunkGridManager>("ChunkGridManager");
        var bridge = world.GetNodeOrNull<RingSunshineCloudBridge>("SunshineCloudBridge");
        ulong start = Time.GetTicksMsec();
        ulong last = start;
        int stable = 0;
        bool terrainIdle = false, warmed = false, mapBuilt = false;
        while (true)
        {
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            ulong now = Time.GetTicksMsec();
            ulong frame = now - last;
            last = now;
            terrainIdle = grid == null || (grid.PendingChunkLoadCount == 0 && grid.PendingChunkRebuildCount == 0);
            warmed = RingStartupWarmup.IsComplete;
            mapBuilt = bridge == null || bridge.MacroMapBuildsCompleted >= MacroMapBuildsRequired;
            stable = frame < StableFrameMaxMsec && terrainIdle && warmed && mapBuilt ? stable + 1 : 0;
            double elapsed = (now - start) / 1000.0;
            if (stable >= StableFramesRequired)
            {
                LastReport = $"settled in {elapsed:0.0} s";
                EmitSignal(SignalName.Settled, true);
                return;
            }
            if (elapsed >= maxSeconds)
            {
                LastReport = $"timeout after {elapsed:0.0} s: terrainIdle={terrainIdle} warmed={warmed} macroMap={mapBuilt} stableFrames={stable}";
                EmitSignal(SignalName.Settled, false);
                return;
            }
        }
    }
}
