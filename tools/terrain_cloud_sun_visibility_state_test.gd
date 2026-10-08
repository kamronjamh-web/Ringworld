extends SceneTree
var world: Node
var probe: RefCounted
var done := false
func _initialize() -> void:
    create_timer(240.0).timeout.connect(func(): _fail("watchdog expired"))
    call_deferred("_run")
func _run() -> void:
    if RenderingServer.get_rendering_device()==null:_fail("Forward+ required");return
    probe=load("res://tools/TerrainCloudSunVisibilityStateProbe.cs").new()
    if not _require(probe.SourceContract()):return
    world=load("res://scenes/VoxelRingIntegrationTest.tscn").instantiate()
    var launcher=load("res://scenes/WorldSizeLauncher.tscn").instantiate()
    root.add_child(launcher);launcher.call("_on_certified_preset_pressed");launcher.call("_configure_game",world,5000000.0);launcher.free()
    var path="res://.godot/terrain-cloud-state-%d" % OS.get_process_id()
    DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(path))
    if not _require(probe.Prepare(world,path)):return
    root.add_child(world)
    await process_frame
    probe.FreezePoseAndSun()
    var initial: Dictionary=probe.State()
    if not _require(initial):return
    if bool(initial.ready) or bool(initial.resourceReady):_fail("initial field was not neutral");return
    var first:=await _wait_ready(0)
    if done:return
    if not _require(probe.CheckFrame(false)):return
    if not _require(probe.CheckFrame(true)):return
    for i in 3:await process_frame
    var rebased: Dictionary=probe.State()
    if not _require(rebased):return
    if not bool(rebased.matches):_fail("resource did not follow ordinary rebase publication");return
    # Wait for another ordinary atlas publication at the same fixed lighting state.
    # Settle the one-shot initial readiness reset before recording renderer counters.
    for i in 6:await process_frame
    var stable_before: Dictionary=probe.State()
    var periodic:=await _wait_ready(int(stable_before.generation))
    if done:return
    for i in 3:await process_frame
    var stable_after: Dictionary=probe.State()
    if not _require(stable_after):return
    for key in ["zero_packed_history_callback_count", "accumulation_resource_rebuild_count"]:
        if int(stable_after.temporal[key]) != int(stable_before.temporal[key]):
            _fail("stable periodic field publication reset cloud history: "+key);return
    probe.FlipSun()
    for i in 3:await process_frame
    var stale: Dictionary=probe.State()
    if not _require(stale):return
    if bool(stale.ready) or bool(stale.resourceReady):_fail("stale sunlight atlas remained active");return
    var second:=await _wait_ready(int(first.generation))
    if done:return
    probe.Disable()
    for i in 3:await process_frame
    var disabled: Dictionary=probe.State()
    if not _require(disabled):return
    if bool(disabled.ready) or bool(disabled.resourceReady):_fail("disabled producer retained live resource");return
    probe.Enable()
    await _wait_ready(int(second.generation))
    if done:return
    probe.Release()
    for i in 3:await process_frame
    world.queue_free()
    for i in 3:await process_frame
    print("TERRAIN_CLOUD_SUN_VISIBILITY_STATE_TEST=PASS initial=",JSON.stringify(initial)," rebase=",JSON.stringify(rebased))
    done=true;quit(0)
func _wait_ready(after_generation: int)->Dictionary:
    while not done:
        var state: Dictionary=probe.State()
        if not _require(state):return {}
        if bool(state.ready) and int(state.generation)>after_generation:
            if not bool(state.resourceReady) or not bool(state.matches):
                # Bridge process may precede producer this frame; require next ordinary frames.
                await process_frame
                continue
            return state
        await process_frame
    return {}
func _require(result: Dictionary)->bool:
    if not bool(result.get("pass",false)):_fail(str(result));return false
    return true
func _fail(message: String)->void:
    if done:return
    done=true
    push_error("TERRAIN_CLOUD_SUN_VISIBILITY_STATE_TEST=FAIL "+message)
    if is_instance_valid(world):world.queue_free()
    quit(1)
