extends Node3D

@onready var meadow = $Meadow
@onready var camera: Camera3D = $Camera3D
@onready var status: Label = $CanvasLayer/Status
var paused := false
var overview := false
var wind_mode := "breeze"
var wind_clock := 0.0

func _ready() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("GRASS_MEADOW_CAPTURE=FAIL RenderingDevice required")
		if "--grass-meadow-capture" in OS.get_cmdline_user_args(): get_tree().quit(1)
		return
	meadow.connect("MeadowReady", _on_meadow_ready)
	camera.look_at(Vector3(0, 0, 0), Vector3.UP)
	RenderingServer.global_shader_parameter_set("ring_local_wind_enabled", 1.0)
	RenderingServer.global_shader_parameter_set("ring_local_wind_center", meadow.WindCenter)
	RenderingServer.global_shader_parameter_set("ring_local_wind_axis", meadow.WindAxis)
	set_breeze()

func _process(delta: float) -> void:
	if not paused: wind_clock += delta
	publish_wind()
	status.text = "Meadow benchmark 32 x 32 m  |  %d blades  |  %d tiles\nTarget %.0f roots/m² before terrain/exclusion filters  |  %s%s\n[B] breeze  [G] gust  [C] calm  [P] freeze wind  [O] overview  [L] lighting" % [meadow.InstanceCount, meadow.TileCount, meadow.Density, wind_mode, " (frozen)" if paused else ""]

func _unhandled_input(event: InputEvent) -> void:
	if not event is InputEventKey or not event.is_pressed() or event.is_echo(): return
	if event.keycode == KEY_B: set_breeze()
	elif event.keycode == KEY_G: set_gust()
	elif event.keycode == KEY_C: set_calm()
	elif event.keycode == KEY_P: paused = not paused
	elif event.keycode == KEY_O: toggle_overview()
	elif event.keycode == KEY_L: $Sun.rotation_degrees = Vector3(-25, 145, 0) if $Sun.rotation_degrees.y < 0 else Vector3(-42, -35, 0)

func set_calm() -> void: wind_mode = "calm"
func set_breeze() -> void: wind_mode = "breeze"
func set_gust() -> void: wind_mode = "gust"
func publish_wind() -> void:
	RenderingServer.global_shader_parameter_set("ring_local_wind_enabled", 1.0)
	RenderingServer.global_shader_parameter_set("ring_local_wind_center", meadow.WindCenter)
	RenderingServer.global_shader_parameter_set("ring_local_wind_axis", meadow.WindAxis)
	if wind_mode == "calm":
		RenderingServer.global_shader_parameter_set("ring_local_wind_vector", Vector2.ZERO)
		RenderingServer.global_shader_parameter_set("ring_local_wind_wave", Vector4.ZERO)
		meadow.SetGrassGustWeatherForTests(Vector2.ZERO, wind_clock)
		return
	var strength := 4.0 if wind_mode == "breeze" else 18.0
	var wind := Vector2(strength, strength * .22)
	RenderingServer.global_shader_parameter_set("ring_local_wind_vector", wind)
	RenderingServer.global_shader_parameter_set("ring_local_wind_wave", Vector4(sin(wind_clock * .19), cos(wind_clock * .19), sin(wind_clock * .19 * .47), cos(wind_clock * .19 * .47)))
	# The fixture has no production LocalWeatherEffects node, so drive the same
	# simulation-clock gust API explicitly. A frozen benchmark leaves both inputs exact.
	meadow.SetGrassGustWeatherForTests(wind, wind_clock)
func toggle_overview() -> void:
	overview = not overview
	camera.position = Vector3(0, 25, 25) if overview else Vector3(-12, 1.6, 12)
	camera.look_at(Vector3(0, 0, 0), Vector3.UP)
func _on_meadow_ready() -> void:
	print("GRASS_MEADOW_INTERACTIVE=READY")
	if "--grass-meadow-capture" in OS.get_cmdline_user_args():
		capture_review()

func capture_review() -> void:
	var out := "res://artifacts/grass_meadow"
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(out))
	set_calm()
	paused = false
	await RenderingServer.frame_post_draw
	await RenderingServer.frame_post_draw
	var low := get_viewport().get_texture().get_image()
	if low == null or low.is_empty() or low.save_png(out.path_join("calm_ground.png")) != OK:
		push_error("GRASS_MEADOW_CAPTURE=FAIL calm capture")
		get_tree().quit(1)
		return
	# Hold the simulation clock still and A/B the same breeze frame. This proves
	# the generated texture field contributes independently of harmonic wind.
	set_breeze()
	paused = true
	publish_wind()
	await RenderingServer.frame_post_draw
	await RenderingServer.frame_post_draw
	var texture_on := get_viewport().get_texture().get_image()
	if texture_on == null or texture_on.is_empty() or texture_on.save_png(out.path_join("breeze_texture_on.png")) != OK:
		push_error("GRASS_MEADOW_CAPTURE=FAIL texture-on capture")
		get_tree().quit(1)
		return
	var layer = meadow.GetGrassLayerForTests()
	if layer == null or not layer.GrassGustTextureReady:
		push_error("GRASS_MEADOW_CAPTURE=FAIL gust texture was not ready")
		get_tree().quit(1)
		return
	layer.EnableGrassGustTexture = false
	publish_wind()
	await RenderingServer.frame_post_draw
	await RenderingServer.frame_post_draw
	var texture_off := get_viewport().get_texture().get_image()
	if texture_off == null or texture_off.is_empty() or texture_off.save_png(out.path_join("breeze_texture_off.png")) != OK:
		push_error("GRASS_MEADOW_CAPTURE=FAIL texture-off capture")
		get_tree().quit(1)
		return
	layer.EnableGrassGustTexture = true
	publish_wind()
	if image_mean_delta(texture_on, texture_off) <= 0.00005:
		push_error("GRASS_MEADOW_CAPTURE=FAIL frozen texture gust had no visible effect")
		get_tree().quit(1)
		return
	paused = false
	set_gust()
	await RenderingServer.frame_post_draw
	await RenderingServer.frame_post_draw
	var gust := get_viewport().get_texture().get_image()
	if gust == null or gust.is_empty() or gust.save_png(out.path_join("gust_ground.png")) != OK:
		push_error("GRASS_MEADOW_CAPTURE=FAIL gust capture")
		get_tree().quit(1)
		return
	paused = true
	await RenderingServer.frame_post_draw
	var frozen := get_viewport().get_texture().get_image()
	if image_mean_delta(gust, frozen) > 0.001:
		push_error("GRASS_MEADOW_CAPTURE=FAIL frozen gust changed")
		get_tree().quit(1)
		return
	paused = false
	for i in range(20): await RenderingServer.frame_post_draw
	var gust_later := get_viewport().get_texture().get_image()
	if image_mean_delta(gust, gust_later) <= 0.0001:
		push_error("GRASS_MEADOW_CAPTURE=FAIL gust did not animate")
		get_tree().quit(1)
		return
	$Sun.rotation_degrees = Vector3(-25, 145, 0)
	await RenderingServer.frame_post_draw
	await RenderingServer.frame_post_draw
	var backlight := get_viewport().get_texture().get_image()
	if backlight == null or backlight.is_empty() or backlight.save_png(out.path_join("gust_backlight.png")) != OK:
		push_error("GRASS_MEADOW_CAPTURE=FAIL backlight capture")
		get_tree().quit(1)
		return
	toggle_overview()
	await RenderingServer.frame_post_draw
	await RenderingServer.frame_post_draw
	var overview_image := get_viewport().get_texture().get_image()
	if overview_image == null or overview_image.is_empty() or overview_image.save_png(out.path_join("gust_overview.png")) != OK:
		push_error("GRASS_MEADOW_CAPTURE=FAIL overview capture")
		get_tree().quit(1)
		return
	if meadow.InstanceCount <= 0:
		push_error("GRASS_MEADOW_CAPTURE=FAIL no production blades")
		get_tree().quit(1)
		return
	print("GRASS_MEADOW_CAPTURE=PASS path=artifacts/grass_meadow tiles=%d blades=%d" % [meadow.TileCount, meadow.InstanceCount])
	get_tree().quit(0)

func image_mean_delta(a: Image, b: Image) -> float:
	if a == null or b == null: return INF
	var total := 0.0
	var count := 0
	# Ignore the upper-left HUD where changing counters/status may draw text.
	for y in range(90, a.get_height(), 8):
		for x in range(0, a.get_width(), 8): total += absf(a.get_pixel(x, y).get_luminance() - b.get_pixel(x, y).get_luminance()); count += 1
	return total / max(1, count)
