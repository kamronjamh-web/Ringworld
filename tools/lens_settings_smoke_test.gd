extends SceneTree
# Settings smoke for the Sun group of the Atmosphere tab (LF-1): Sun glare + Lens flare. Headless.
# Run: Godot --headless --path . --script res://tools/lens_settings_smoke_test.gd   Prints LENS_SETTINGS_SMOKE=PASS.
const SMOKE := preload("res://tools/LensSettingsSmoke.cs")
const DRAWER := preload("res://scripts/ui/RingLiveTuningDrawer.cs")
const GRAPHICS := preload("res://scripts/Voxel/LOD/RingGraphicsQualityController.cs")
const DAY_NIGHT := preload("res://scripts/RingDayNightCycle.cs")
const SUN_GLARE := preload("res://scripts/Atmosphere/RingSunGlare.cs")
var DIR := "res://.godot/lens_settings_smoke_%d" % OS.get_process_id()
var _fixture: Node3D

func _initialize() -> void:
	call_deferred("_run")

func _run() -> void:
	var smoke = SMOKE.new()
	var result := str(smoke.Run(DIR + "/settings.tres"))
	if result != "PASS": return _fail(result)
	# Drawer rows: present under their keys with the documented ranges, driven through the real signal, reaching the owner.
	_fixture = Node3D.new()
	var sun := DirectionalLight3D.new(); sun.name = "StarLight"
	var cycle := DAY_NIGHT.new(); cycle.name = "RingDayNightCycle"; cycle.process_mode = Node.PROCESS_MODE_DISABLED
	var drawer = DRAWER.new(); drawer.name = "LiveTuningDrawer"
	drawer.call("InstallAntiAliasingDefaults", DIR + "/aa.tres")
	drawer.call("InstallShadowDefaults", DIR + "/shadows.tres")
	drawer.call("InstallGrassDefaults", DIR + "/grass.tres")
	drawer.call("InstallWaterDefaults", DIR + "/water.tres")
	drawer.call("InstallMotionDefaults", DIR + "/motion.tres")
	drawer.call("InstallContainmentFieldDefaults", DIR + "/containment.tres")
	var graphics = GRAPHICS.new(); graphics.name = "GraphicsQuality"; graphics.set("DefaultPreset", 3)
	var owner_node = SUN_GLARE.new(); owner_node.name = "SunGlare"
	for n in [sun, cycle, drawer, graphics, owner_node]: _fixture.add_child(n)
	root.add_child(_fixture)
	for i in 4: await process_frame
	graphics.call("InstallAtmosphereDefaults", DIR + "/atmosphere.tres")
	drawer.call("SetBenchmarkDrawerOpen", true)
	await create_timer(0.3).timeout
	var glare := drawer.find_child("AtmosphereSunGlare", true, false) as HSlider
	var flare := drawer.find_child("AtmosphereLensFlare", true, false) as HSlider
	if glare == null or flare == null: return _fail("Sun glare / Lens flare rows missing")
	if glare.min_value != 0.0 or glare.max_value != 1.0 or not is_equal_approx(glare.step, 0.01) or not is_equal_approx(glare.value, 1.0): return _fail("Sun glare slider range or default")
	if flare.min_value != 0.0 or flare.max_value != 1.0 or not is_equal_approx(flare.step, 0.01) or not is_equal_approx(flare.value, 0.0): return _fail("Lens flare slider range or default")
	var tip := (glare.get_parent() as Control).tooltip_text
	if not tip.begins_with("Veiling glare around the sun"): return _fail("Sun glare tooltip: " + tip)
	glare.value = 0.35
	flare.value = 0.6
	await process_frame
	var statics: Vector2 = smoke.ReadStatics()
	if not is_equal_approx(statics.x, 0.35) or not is_equal_approx(statics.y, 0.6): return _fail("slider signals did not reach RingSunGlare: " + str(statics))
	drawer.call("FlushAtmosphereDefaults")
	if not FileAccess.file_exists(DIR + "/atmosphere.tres"): return _fail("atmosphere profile was not saved")
	var saved := FileAccess.get_file_as_string(DIR + "/atmosphere.tres")
	if not saved.contains("SunGlare = 0.35") or not saved.contains("LensFlare = 0.6"): return _fail("saved profile lacks the rows: " + saved)
	_pass()

func _pass() -> void:
	print("LENS_SETTINGS_SMOKE=PASS")
	_finish(0)

func _fail(reason: String) -> void:
	push_error("LENS_SETTINGS_SMOKE=FAIL " + reason)
	_finish(1)

func _finish(code: int) -> void:
	if _fixture != null and is_instance_valid(_fixture): _fixture.free()
	var abs_dir := ProjectSettings.globalize_path(DIR)
	if DirAccess.dir_exists_absolute(abs_dir):
		for f in DirAccess.get_files_at(abs_dir): DirAccess.remove_absolute(abs_dir + "/" + f)
		DirAccess.remove_absolute(abs_dir)
	call_deferred("quit", code)
