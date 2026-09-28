extends SceneTree

const SURFACE_SCRIPT := preload("res://scripts/Terrain/Surface/RingTerrain3DSurface.cs")
const WINDOW := preload("res://scripts/Terrain/Generation/terrain_generation_window.gd")
var _staged_mode := OS.get_cmdline_user_args().has("--staged")
var _budgeted_mode := OS.get_cmdline_user_args().has("--budgeted")
var _output_directory := "res://.godot/terrain-generation-tests/prepared-visibility" + ("-budgeted" if _budgeted_mode else "-staged" if _staged_mode else "")
const HEIGHT := 123.375
var _checks := 0
var _failures := PackedStringArray()
var _captures := []
var _finished := false
var _completed := false

func _init() -> void:
	if DisplayServer.get_name() == "headless":
		print("TERRAIN_GENERATION_PREPARED_VISIBILITY=SKIP headless")
		quit(0)
		return
	call_deferred("_run")
	create_timer(60.0).timeout.connect(_timeout)

func _run() -> void:
	var environment := Environment.new()
	environment.background_mode = Environment.BGMode.BG_COLOR
	environment.background_color = Color(1, 0, 1)
	environment.ambient_light_source = Environment.AmbientSource.AMBIENT_SOURCE_COLOR
	environment.ambient_light_color = Color.WHITE
	environment.ambient_light_energy = 0.8
	root.add_child(WorldEnvironment.new())
	(root.get_child(root.get_child_count() - 1) as WorldEnvironment).environment = environment
	var light := DirectionalLight3D.new()
	light.rotation_degrees = Vector3(-55, 30, 0)
	light.light_energy = 1.3
	root.add_child(light)
	var camera := Camera3D.new()
	camera.current = true
	camera.near = .05
	camera.far = 2000
	root.add_child(camera)
	var surface = SURFACE_SCRIPT.new()
	root.add_child(surface)
	var heights := PackedFloat32Array()
	heights.resize(WINDOW.SIDE * WINDOW.SIDE)
	heights.fill(HEIGHT)
	var images = WINDOW.pack(heights, HEIGHT)
	_expect(images.size() == 9, "flat fixture packs nine RF images")
	_expect(surface.CommitPreparedWindow(heights, images, 0, 1000, HEIGHT, 1, 1, true), "prepared window commits")
	if not surface.IsReady:
		_fail("prepared surface not ready: " + surface.LastError)
		return
	for quarter in 4:
		var phi := float(quarter) * TAU * .25
		var tangent := Vector3(-sin(phi), 0, cos(phi))
		var inward := Vector3(-cos(phi), 0, -sin(phi))
		var world_frame := Transform3D(Basis(tangent, inward, Vector3.DOWN), Vector3(125, -75, 250))
		surface.SetWorldFrame(world_frame)
		if _staged_mode or _budgeted_mode:
			if _budgeted_mode:
				var geometry = preload("res://scripts/Terrain/Surface/RingTerrain3DCollisionGeometry.cs").new()
				_expect(geometry.Prepare(heights, quarter * 256, 1000, HEIGHT, 1, 16 if quarter % 2 == 0 else 32), "quarter %d projects private geometry" % quarter)
				_expect(surface.BeginBudgetedPreparedWindow(heights, images, geometry, quarter * 256, 1000, HEIGHT, 1, quarter + 2), "quarter %d begins budgeted stage" % quarter)
				var deadline := Time.get_ticks_msec() + 10000
				var preparation: Dictionary = {}
				while Time.get_ticks_msec() < deadline:
					preparation = surface.AdvanceBudgetedPreparation(1.0, .75)
					if not preparation.ok or preparation.ready: break
					await process_frame
				_expect(preparation.get("ready", false), "quarter %d completes budgeted stage" % quarter)
			else:
				_expect(surface.StagePreparedWindow(heights, images, quarter * 256, 1000, HEIGHT, 1, quarter + 2, true), "quarter %d stages" % quarter)
			var state: Dictionary = surface.GetPreparedDiagnostics()
			_expect(state.ready and state.revision == quarter + 1 and state.staged_ready, "quarter %d retains active while staged" % quarter)
			_expect(surface.ActivateStagedWindow(quarter + 2), "quarter %d activates" % quarter)
		var local_camera := Vector3(0, 10, 0)
		camera.global_position = world_frame * local_camera
		camera.look_at(world_frame * Vector3(20, 0, 0), world_frame.basis.y)
		surface.UpdateCamera(local_camera)
		await process_frame
		await RenderingServer.frame_post_draw
		var image := root.get_viewport().get_texture().get_image()
		var path := _output_directory + "/quarter_%d.png" % quarter
		DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(_output_directory))
		_expect(image.save_png(path) == OK, "quarter %d capture writes" % quarter)
		_expect(_has_ground_pixels(image), "quarter %d has nonbackground ground pixels" % quarter)
		_captures.append(path)
	surface.queue_free()
	camera.queue_free()
	light.queue_free()
	_completed = true
	_finish()

func _has_ground_pixels(image: Image) -> bool:
	for y_fraction in [0.5, 0.7]:
		var pixel := image.get_pixel(image.get_width() / 2, int(image.get_height() * y_fraction))
		if pixel.r > .5 and pixel.b > .5 and pixel.g < .1:
			return false
	return true

func _expect(condition: bool, label: String) -> void:
	_checks += 1
	if not condition:
		_failures.append(label)

func _timeout() -> void:
	if not _finished:
		_failures.append("watchdog expired")
		_finish()

func _fail(message: String) -> void:
	_failures.append(message)
	_finish()

func _finish() -> void:
	if _finished:
		return
	_finished = true
	if not _completed:
		_failures.append("test did not complete all stages")
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(_output_directory))
	var file := FileAccess.open(_output_directory + "/report.json", FileAccess.WRITE)
	if file != null:
		file.store_string(JSON.stringify({"checks": _checks, "completed": _completed, "staged": _staged_mode, "budgeted": _budgeted_mode, "captures": _captures, "failures": _failures}, "  "))
		file.close()
	else:
		_failures.append("could not write report")
	if _failures.is_empty() and _checks >= (30 if _budgeted_mode else 22 if _staged_mode else 10):
		print("TERRAIN_GENERATION_PREPARED_VISIBILITY=PASS ", JSON.stringify({"checks": _checks, "captures": _captures}))
		quit(0)
	else:
		push_error("TERRAIN_GENERATION_PREPARED_VISIBILITY=FAIL " + "; ".join(_failures))
		quit(1)
