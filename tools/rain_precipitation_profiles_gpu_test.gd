extends SceneTree

# Render the actual owner-created haze in a controlled camera world. No drop
# material is shown or edited: the production publisher must keep its snow pool hidden.
const WORLD := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const DRIVER := preload("res://tools/RainPrecipitationTestDriver.cs")
const ISOLATION := preload("res://scripts/Tools/MinimapWindContractTest.cs")
const SIZE := Vector2i(640, 360)
var world: Node
var local: Node
var driver: RefCounted
var camera: Camera3D
var viewport: SubViewport
var rig: Node3D
var output := ""
var failed := false
var fixture_wind_anchor: Variant = null

func _initialize() -> void:
    if RenderingServer.get_rendering_device() == null:
        fail("Forward+ required"); return
    call_deferred("run")

func run() -> void:
    # A bounded startup/test watchdog also exits if an asynchronous script
    # exception aborts this coroutine before its normal quit path.
    create_timer(90.0).timeout.connect(func(): fail("fixture watchdog expired"))
    output = "res://.godot/rain_haze_gpu_%d_%d" % [OS.get_process_id(), Time.get_ticks_usec()]
    DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(output))
    world = WORLD.instantiate()
    if "--rain-profiles-certified" in OS.get_cmdline_user_args():
        var launcher := preload("res://scenes/WorldSizeLauncher.tscn").instantiate()
        root.add_child(launcher); launcher.call("_on_certified_preset_pressed")
        launcher.call("_configure_game", world, 5000000.0); launcher.free()
    var isolation: Dictionary = ISOLATION.new().ExecuteFixture("isolation", world, output + "/preferences")
    if not bool(isolation.get("pass", false)): fail("preference isolation " + str(isolation)); return
    root.add_child(world)
    for frame in range(8): await process_frame
    for name in ["Explorer", "RingVolumetricClouds", "SunshineCloudBridge", "DistantRainCurtains", "LocalVolumetricCloudRenderer", "WeatherAmbience", "TerrainMinimap"]:
        var node := world.get_node_or_null(name)
        if node != null: node.set_process(false); node.set_physics_process(false)
    world.get_node("RingDayNightCycle").set("Paused", true)
    local = world.get_node("LocalWeatherEffects")
    local.set_process(false); local.set("EnableLightning", false)
    driver = DRIVER.new()
    driver.call("SetShelterForFixture", world.get_node("WeatherAmbience"), false)
    driver.call("SetUnderwaterForFixture", world.get_node("Explorer"), false)
    driver.call("SetDaylightForFixture", world.get_node("RingDayNightCycle"), 1.0)
    setup_view()
    target(0.0)
    var baseline := await capture("clear")
    var previous_far := 0.0
    for intensity: float in [.15, .5, 1.0]:
        target(intensity)
        if not no_drops(): return
        var image := await capture("rain_%d" % int(intensity * 100))
        # The projected foreground spans x123..299. Keep the assertion well
        # inside its silhouette so sky haze cannot contaminate this budget.
        var near_delta := region_delta(baseline, image, Rect2i(145, 100, 115, 160))
        var far_delta := region_delta(baseline, image, Rect2i(420, 230, 120, 80))
        var sky_delta := region_delta(baseline, image, Rect2i(480, 30, 120, 70))
        print("RAIN_HAZE_DEPTH intensity=%.2f near_delta=%.6f far_delta=%.6f sky_delta=%.6f near_clear=%.3f near_before=%s near_after=%s" % [intensity, near_delta, far_delta, sky_delta, float(driver.call("GetHazeControl", "NearClearDistanceMeters")), str(baseline.get_pixel(200, 180)), str(image.get_pixel(200, 180))])
        if not require(near_delta < .001, "near opaque foreground was fogged: %.6f" % near_delta): return
        if not require(far_delta > previous_far + .002 and sky_delta > .005, "rain density did not visibly thicken distant/sky view"): return
        previous_far = far_delta
    var heavy := await capture("heavy_paused_a")
    driver.call("HoldRainCompositorForFixture", local, true)
    var fallback := await capture("heavy_fallback_parity")
    if not require(bool(local.call("GetRainHazeDiagnostics").fallback_visible), "parity fixture did not activate fallback"): return
    driver.call("HoldRainCompositorForFixture", local, false)
    var restored := await capture("heavy_compositor_restored")
    var parity := region_delta(heavy, fallback, Rect2i(Vector2i.ZERO, SIZE))
    print("RAIN_HAZE_COMPOSITOR_FALLBACK_PARITY delta=%.6f" % parity)
    if not require(parity < .003, "compositor/fallback tint, gust or depth mismatch: %.6f" % parity): return
    if not require(not bool(local.call("GetRainHazeDiagnostics").fallback_visible) and region_delta(heavy, restored, Rect2i(Vector2i.ZERO, SIZE)) < .000001, "compositor restore changed paused haze"): return
    var paused := await capture("heavy_paused_b")
    if not require(region_delta(heavy, paused, Rect2i(Vector2i.ZERO, SIZE)) < .000001, "paused shared clock moved haze"): return
    # Equivalent camera/geometry and gust-anchor translation preserves the veil.
    var translation := Vector3(17321, -811, 4013)
    var old_anchor: Vector3 = driver.call("PublishedWindAnchor", world)
    rig.position = translation
    fixture_wind_anchor = old_anchor + translation
    var rebased := await capture("heavy_rebased")
    if not require(region_delta(heavy, rebased, Rect2i(Vector2i.ZERO, SIZE)) < .001, "haze changed under equivalent rebase"): return
    rig.position = Vector3.ZERO
    fixture_wind_anchor = null
    driver.call("PublishHazeWindAnchorForFixture", world, old_anchor)
    driver.call("SetDaylightForFixture", world.get_node("RingDayNightCycle"), 0.0)
    step(1)
    var night := await capture("heavy_night")
    if not require(luma(night.get_pixel(540, 45)) < luma(heavy.get_pixel(540, 45)) * .5, "night haze glows with daytime tint"): return
    driver.call("SetDaylightForFixture", world.get_node("RingDayNightCycle"), 1.0)
    driver.call("SetHazeControl", "Enabled", false); step(1)
    var disabled := await capture("haze_disabled")
    if not require(region_delta(baseline, disabled, Rect2i(Vector2i.ZERO, SIZE)) < .000001, "disabled haze changed baseline view"): return
    driver.call("SetHazeControl", "Enabled", true)
    driver.call("SetUnderwaterForFixture", world.get_node("Explorer"), true); step(1)
    var underwater := await capture("underwater_haze_suppressed")
    if not require(region_delta(baseline, underwater, Rect2i(Vector2i.ZERO, SIZE)) < .000001, "underwater haze retained overlay"): return
    driver.call("SetUnderwaterForFixture", world.get_node("Explorer"), false)
    driver.call("SetShelterForFixture", world.get_node("WeatherAmbience"), true); step(1)
    var sheltered := await capture("sheltered_haze_suppressed")
    if not require(region_delta(baseline, sheltered, Rect2i(Vector2i.ZERO, SIZE)) < .000001, "sheltered view retained exterior rain"): return
    driver.call("SetShelterForFixture", world.get_node("WeatherAmbience"), false)
    target(.5, true)
    if not require(not bool(local.call("GetRainHazeDiagnostics").visible), "snow retained liquid-rain haze"): return
    target(.5)
    if not no_drops(): return
    print("RAIN_PRECIPITATION_PROFILES_GPU=PASS checks=no_drops,monotonic_density,near_depth,distant,sky,pause,rebase,night,enabled,underwater,shelter,snow output=" + output)
    world.queue_free(); viewport.queue_free()
    for frame in range(4): await process_frame
    quit(0)

func setup_view() -> void:
    viewport = SubViewport.new()
    viewport.size = SIZE; viewport.own_world_3d = true
    viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
    root.add_child(viewport)
    rig = Node3D.new(); viewport.add_child(rig)
    var environment := Environment.new()
    environment.background_mode = Environment.BG_COLOR
    environment.background_color = Color(.035, .05, .065)
    environment.tonemap_mode = Environment.TONE_MAPPER_LINEAR
    var owner := WorldEnvironment.new(); owner.environment = environment; rig.add_child(owner)
    camera = world.get_node("Explorer/Head/Camera3D")
    camera.reparent(rig, false); camera.transform = Transform3D.IDENTITY
    camera.fov = 80; camera.near = .1; camera.far = 2000; camera.make_current()
    add_quad(Vector3(-1.6, 0, -3), Vector2(2.3, 6), Color(.12, .13, .14))
    add_quad(Vector3(120, -80, -300), Vector2(300, 180), Color(.08, .09, .1))

func add_quad(position: Vector3, size: Vector2, color: Color) -> void:
    var mesh := MeshInstance3D.new()
    mesh.mesh = QuadMesh.new(); mesh.mesh.size = size
    var material := StandardMaterial3D.new()
    material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
    material.albedo_color = color; mesh.material_override = material
    mesh.position = position; rig.add_child(mesh)

func target(intensity: float, snow := false) -> void:
    driver.call("SetIntensity", local, intensity, snow, Vector2(8, -3)); step(220)
func step(count: int) -> void:
    for frame in range(count): local.call("_Process", 1.0 / 60.0)
func no_drops() -> bool:
    var particles := local.get_node("CameraLocalPrecipitation") as GPUParticles3D
    return require(not particles.emitting and not particles.visible and particles.amount_ratio == 0.0 and float(local.call("GetPrecipitationDiagnostics").effective_count) == 0.0, "rain retained visible drop pool")
func capture(label: String) -> Image:
    step(1)
    if fixture_wind_anchor != null: driver.call("PublishHazeWindAnchorForFixture", world, fixture_wind_anchor)
    for frame in range(6): await process_frame
    await RenderingServer.frame_post_draw
    var image := viewport.get_texture().get_image()
    if image.is_empty(): fail("empty capture " + label); return image
    image.save_png(output + "/" + label + ".png")
    return image
func luma(color: Color) -> float:
    return color.r * .2126 + color.g * .7152 + color.b * .0722
func region_delta(a: Image, b: Image, region: Rect2i) -> float:
    if a.is_empty() or b.is_empty(): return INF
    var total := 0.0; var count := 0
    for y in range(region.position.y, region.end.y, 4):
        for x in range(region.position.x, region.end.x, 4):
            total += absf(luma(a.get_pixel(x, y)) - luma(b.get_pixel(x, y))); count += 1
    return total / maxf(1.0, count)
func require(ok: bool, message: String) -> bool:
    if not ok: fail(message)
    return ok
func fail(message: String) -> void:
    if failed: return
    failed = true
    push_error("RAIN_PRECIPITATION_PROFILES_GPU=FAIL " + message)
    quit(1)
