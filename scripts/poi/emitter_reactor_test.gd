extends Node3D
## Standalone test scene for the beam emitter + reactor POI assets and the main spire + main reactor (not part of the ring world).
##
## Keys: 1-5 camera bookmarks, B toggle placeholder beam, O toggle original (unmerged) imported GLBs, M toggle regular / main pair.
## The camera swaps to a dark environment when below the ground so the reactor is not lit by the surface sky.
##
## Command-line (after `--`), used for measurement:
##   --bookmark=N        start at bookmark N (1-based)
##   --main              start with the main spire + main reactor pair (default: regular pair)
##   --original          use the unmerged imported GLBs instead of the merged scenes
##   --measure=FRAMES    after warm-up, average frame time over FRAMES frames, print stats and quit
##   --warmup=FRAMES     warm-up frames before measuring/capturing (default 60)
##   --capture=PATH      save a PNG after warm-up (and quit unless --measure given)

const EMITTER_ORIGINAL := "res://assets/poi/beam_emitter/Surface_Spire_and_Facility_Godot.glb"
const REACTOR_ORIGINAL := "res://assets/poi/reactor/Reactor_Architecture_Godot.glb"
const MAIN_SPIRE_ORIGINAL := "res://assets/poi/main_spire/Main_Spire_Outer_Ring_Godot.glb"
const MAIN_REACTOR_ORIGINAL := "res://assets/poi/main_reactor/Main_Reactor_Architecture.glb"
const DARK_ENV := "res://scenes/poi/EmitterReactorDarkEnvironment.tres"
const UNDERGROUND_Y := -30.0
const SUN_SHADOW_REGULAR := 700.0
const SUN_SHADOW_MAIN := 2000.0

@onready var _cam: Camera3D = $FlyCamera
@onready var _sun: DirectionalLight3D = $Sun

var _emitter: Node3D
var _reactor: Node3D
var _main := false

var _surface_env: Environment
var _dark_env: Environment
var _original := false

var _bookmark := 0
var _measure_frames := 0
var _warmup_frames := 60
var _capture_path := ""
var _frame := 0
var _times: PackedFloat64Array = PackedFloat64Array()
var _gpu_ms := 0.0
var _cpu_ms := 0.0

func _ready() -> void:
	_surface_env = ($WorldEnvironment as WorldEnvironment).environment
	_dark_env = load(DARK_ENV) as Environment
	_parse_args()
	RenderingServer.viewport_set_measure_render_time(get_viewport().get_viewport_rid(), true)
	_set_reactor_layers($Regular/ReactorPOI)
	_set_reactor_layers($Main/MainReactorPOI)
	_set_reactor_layers($Regular/ShaftLinerPlaceholder)
	_set_reactor_layers($Main/ShaftLinerPlaceholder)
	_select_pair(_main)
	(_cam as Object).call("goto_bookmark", maxi(_bookmark - 1, 0))

func _parse_args() -> void:
	for a in OS.get_cmdline_user_args():
		if a.begins_with("--bookmark="): _bookmark = int(a.get_slice("=", 1))
		elif a == "--main": _main = true
		elif a == "--original": _original = true
		elif a.begins_with("--measure="): _measure_frames = int(a.get_slice("=", 1))
		elif a.begins_with("--warmup="): _warmup_frames = int(a.get_slice("=", 1))
		elif a.begins_with("--capture="): _capture_path = a.get_slice("=", 1)

func _select_pair(main: bool) -> void:
	# Only one pair is visible at a time (both are centred on the origin).
	_main = main
	($Regular as Node3D).visible = not main
	($Main as Node3D).visible = main
	_emitter = $Main/MainSpirePOI if main else $Regular/BeamEmitterPOI
	_reactor = $Main/MainReactorPOI if main else $Regular/ReactorPOI
	_cam.bookmarks_path = ^"../Main/Bookmarks" if main else ^"../Regular/Bookmarks"
	_sun.directional_shadow_max_distance = SUN_SHADOW_MAIN if main else SUN_SHADOW_REGULAR
	_use_original(_original)

func _set_reactor_layers(n: Node) -> void:
	# Reactor geometry is on render layer 2 so the surface sun (cull mask 1) never lights it.
	if n is VisualInstance3D:
		(n as VisualInstance3D).layers = 2
	for c in n.get_children():
		_set_reactor_layers(c)

func _use_original(on: bool) -> void:
	_swap_detail(_emitter, MAIN_SPIRE_ORIGINAL if _main else EMITTER_ORIGINAL, on, false)
	_swap_detail(_reactor, MAIN_REACTOR_ORIGINAL if _main else REACTOR_ORIGINAL, on, true)
	_original = on

func _swap_detail(poi: Node3D, glb: String, original: bool, reactor_layers: bool) -> void:
	var detail := poi.get_node("Detail") as Node3D
	var existing := poi.get_node_or_null("OriginalImport")
	if existing:
		existing.queue_free()
		poi.remove_child(existing)
	detail.visible = not original
	if poi.has_node("FarProxy"):
		poi.get_node("FarProxy").visible = not original
	if original:
		var inst := (load(glb) as PackedScene).instantiate()
		inst.name = "OriginalImport"
		poi.add_child(inst)
		if reactor_layers:
			_set_reactor_layers(inst)

func _unhandled_key_input(event: InputEvent) -> void:
	if event is InputEventKey and event.pressed and not event.echo:
		match (event as InputEventKey).keycode:
			KEY_B:
				var beam := _emitter.get_node_or_null("PlaceholderBeam") as Node3D
				if beam: beam.visible = not beam.visible
			KEY_O:
				_use_original(not _original)
			KEY_M:
				_select_pair(not _main)
				(_cam as Object).call("goto_bookmark", 0)

func _process(_delta: float) -> void:
	var want_dark := _cam.global_position.y < UNDERGROUND_Y
	var env := _dark_env if want_dark else _surface_env
	if _cam.environment != env:
		_cam.environment = env
	_frame += 1
	if _measure_frames <= 0 and _capture_path == "":
		return
	if _frame <= _warmup_frames:
		return
	if _measure_frames > 0 and _times.size() < _measure_frames:
		_times.append(_delta)
		var vp := get_viewport().get_viewport_rid()
		_gpu_ms += RenderingServer.viewport_get_measured_render_time_gpu(vp)
		_cpu_ms += RenderingServer.viewport_get_measured_render_time_cpu(vp)
		return
	if _capture_path != "":
		var img := get_viewport().get_texture().get_image()
		img.save_png(_capture_path)
		print("captured ", _capture_path)
	if _measure_frames > 0:
		var sum := 0.0
		var worst := 0.0
		for t in _times:
			sum += t
			worst = maxf(worst, t)
		var avg := sum / _times.size()
		print("MEASURE pair=", "main" if _main else "regular", " variant=", "original" if _original else "merged", " bookmark=", _bookmark,
			" frames=", _times.size(), " avg_ms=", snappedf(avg * 1000.0, 0.01), " fps=", snappedf(1.0 / avg, 0.1), " worst_ms=", snappedf(worst * 1000.0, 0.01),
			" render_gpu_ms=", snappedf(_gpu_ms / _times.size(), 0.01), " render_cpu_ms=", snappedf(_cpu_ms / _times.size(), 0.01),
			" draw_calls=", RenderingServer.get_rendering_info(RenderingServer.RENDERING_INFO_TOTAL_DRAW_CALLS_IN_FRAME),
			" objects=", RenderingServer.get_rendering_info(RenderingServer.RENDERING_INFO_TOTAL_OBJECTS_IN_FRAME),
			" primitives=", RenderingServer.get_rendering_info(RenderingServer.RENDERING_INFO_TOTAL_PRIMITIVES_IN_FRAME))
	get_tree().quit()
