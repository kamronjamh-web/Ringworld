extends SceneTree

# State and continuity contracts on the production rain/snow publisher.
const WORLD := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const DRIVER := preload("res://tools/RainPrecipitationTestDriver.cs")
const ISOLATION := preload("res://scripts/Tools/MinimapWindContractTest.cs")
var world: Node
var local: Node
var particles: GPUParticles3D
var driver: RefCounted
var failed := false
var ids: Array = []

func _initialize() -> void:
    call_deferred("run")

func run() -> void:
    world = WORLD.instantiate()
    if "--rain-profiles-certified" in OS.get_cmdline_user_args():
        var launcher := preload("res://scenes/WorldSizeLauncher.tscn").instantiate()
        root.add_child(launcher)
        launcher.call("_on_certified_preset_pressed")
        launcher.call("_configure_game", world, 5000000.0)
        launcher.free()
    var directory := "res://.godot/rain_haze_state_%d_%d" % [OS.get_process_id(), Time.get_ticks_usec()]
    var isolation: Dictionary = ISOLATION.new().ExecuteFixture("isolation", world, directory)
    if not bool(isolation.get("pass", false)):
        push_error("RAIN_HAZE_STATE=FAIL preference isolation " + str(isolation)); quit(1); return
    root.add_child(world)
    for frame in range(8): await process_frame
    world.get_node("Explorer").set_physics_process(false)
    world.get_node("RingVolumetricClouds").set_process(false)
    world.get_node("SunshineCloudBridge").set_process(false)
    world.get_node("RingDayNightCycle").set("Paused", true)
    world.get_node("WeatherAmbience").set_process(false)
    local = world.get_node("LocalWeatherEffects")
    local.set_process(false)
    local.set("EnableLightning", false)
    particles = local.get_node("CameraLocalPrecipitation")
    driver = DRIVER.new()
    if not check(bool(driver.call("CheckProfileMath")), "profile math"): return
    target(0.0); step(220)
    ids = identities()
    if not rain_has_no_particles(): return
    if not check(not bool(haze().visible), "clear retains haze"): return
    var previous_opacity := 0.0
    var wetness_before := float(local.get("SurfaceWetness"))
    for amount: float in [.15, .5, 1.0]:
        target(amount); step(220)
        if not invariant() or not rain_has_no_particles(): return
        var d := diag()
        if not check(absf(float(d.shared_intensity) - amount) < .0001, "authoritative rain intensity changed meaning"): return
        if not check(absf(float(d.scattered_weight) + float(d.steady_weight) + float(d.heavy_weight) - 1.0) < .000001, "profile blend"): return
        var opacity := float(haze().opacity_100m)
        if not check(opacity > previous_opacity and opacity <= .9, "haze does not thicken with rainfall"): return
        previous_opacity = opacity
    if not check(float(local.get("SurfaceWetness")) > wetness_before, "haze removed liquid wetness"): return
    if not check(float(driver.call("HazeOpacity", 1.0, 3.0)) == 0.0, "near foreground is fogged"): return
    # Snow keeps the original stable pool, and the liquid-rain veil is hidden.
    target(.5, true); step(220)
    if not check(bool(diag().snow) and particles.emitting and particles.visible and particles.amount_ratio > 0 and particles.lifetime == 6.0 and not bool(haze().visible), "snow presentation"): return
    target(.5); step(1)
    if not rain_has_no_particles(): return
    step(220)
    if not invariant(): return
    var explorer := world.get_node("Explorer")
    driver.call("SetUnderwaterForFixture", explorer, true); step(1)
    if not check(not bool(haze().visible), "underwater rain veil"): return
    driver.call("SetUnderwaterForFixture", explorer, false)
    driver.call("SetShelterForFixture", world.get_node("WeatherAmbience"), true); step(1)
    if not check(not bool(haze().visible) and float(diag().shared_intensity) > .49, "shelter changed rain state or kept haze"): return
    driver.call("SetShelterForFixture", world.get_node("WeatherAmbience"), false)
    driver.call("SetHazeControl", "Enabled", false); step(1)
    if not check(not bool(haze().visible), "disabled haze"): return
    driver.call("SetHazeControl", "Enabled", true)
    driver.call("SetHazeControl", "Density", NAN)
    driver.call("SetHazeControl", "MaximumOpacity", INF); step(1)
    if not check(is_finite(float(haze().density)) and is_finite(float(haze().maximum_opacity)), "invalid haze reached shader"): return
    driver.call("SetHazeControl", "Density", .004)
    driver.call("SetHazeControl", "MaximumOpacity", .6)
    local.set("Enabled", false); step(220)
    if not rain_has_no_particles() or not check(not bool(haze().visible), "disabled weather retained haze"): return
    local.set("Enabled", true); target(.5); step(220)
    if not rain_has_no_particles(): return
    if not check(bool(haze().visible), "re-enabled rain did not recover haze"): return
    var camera := world.get_node("Explorer/Head/Camera3D")
    var old_mesh := camera.get_node("RainHaze")
    driver.call("SetHazeControl", "Tint", Color(.31, .37, .43))
    step(1)
    var pushes := int(haze().parameter_pushes)
    step(30)
    if not check(int(haze().parameter_pushes) == pushes, "steady haze resends unchanged uniforms"): return
    var replacement := preload("res://scripts/RingLocalWeatherEffects.cs").new()
    replacement.set("Enabled", true)
    replacement.set("EnableLightning", false)
    replacement.set("RainHazePreferencesPath", local.get("RainHazePreferencesPath"))
    local.name = "RetiredLocalWeatherEffects"
    replacement.name = "LocalWeatherEffects"
    world.add_child(replacement)
    replacement.set_process(false)
    if not check(not old_mesh.visible, "replacement did not hide paused old veil immediately"): return
    if not check(Color(driver.call("GetHazeControl", "Tint")).is_equal_approx(Color(.31, .37, .43)), "replacement discarded live unsaved haze controls"): return
    local.free()
    # Distant presentation must release its cached old owner and resolve the
    # canonical replacement, even while that replacement is process-paused.
    world.get_node("DistantRainCurtains").call("_Process", 1.0 / 60.0)
    await process_frame
    if not check(bool(replacement.call("GetRainHazeDiagnostics").active_owner), "old cleanup erased replacement owner"): return
    replacement.free()
    world.get_node("DistantRainCurtains").call("_Process", 1.0 / 60.0)
    if not check(int(world.get_node("DistantRainCurtains").get("VisibleCurtainCount")) == 0, "distant rain retained presentation without owner"): return
    await process_frame
    for child in camera.get_children():
        if not check(not (child is MeshInstance3D and child.material_override is ShaderMaterial and child.material_override.shader.resource_path == "res://materials/ring_rain_haze.gdshader"), "effects owner cleanup left camera haze"): return
    print("RAIN_PRECIPITATION_PROFILES_STATE=PASS checks=profile_math,no_rain_drops,density,foreground,snow,wetness,underwater,shelter,enabled,finite,lifecycle")
    world.queue_free()
    for frame in range(3): await process_frame
    quit(0)

func target(intensity: float, snow := false, wind := Vector2.ZERO) -> void:
    driver.call("SetIntensity", local, intensity, snow, wind)
func step(count: int) -> void:
    for frame in range(count): local.call("_Process", 1.0 / 60.0)
func diag() -> Dictionary:
    return local.call("GetPrecipitationDiagnostics")
func haze() -> Dictionary:
    return local.call("GetRainHazeDiagnostics")
func identities() -> Array:
    var d := diag()
    return [d.emitter_id, d.mesh_id, d.material_id, d.process_id]
func rain_has_no_particles() -> bool:
    return check(not particles.emitting and not particles.visible and particles.amount_ratio == 0.0 and float(diag().effective_count) == 0.0, "liquid rain retained visible particles")
func invariant() -> bool:
    var d := diag()
    return check(identities() == ids and particles.amount == 4096 and is_finite(float(d.shared_intensity)) and Vector2(d.streak_size).is_finite() and Vector3(d.gravity).is_finite(), "snow resource/finite contract")
func check(ok: bool, message: String) -> bool:
    if not ok and not failed:
        failed = true
        push_error("RAIN_PRECIPITATION_PROFILES_STATE=FAIL " + message)
        quit(1)
    return ok
