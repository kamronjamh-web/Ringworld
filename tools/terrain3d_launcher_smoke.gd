extends SceneTree

const LAUNCHER_SCENE := preload("res://scenes/WorldSizeLauncher.tscn")
const CERTIFIED_SETTINGS := {
	"radius_km": 5000.0,
	"ring_width_km": 318.0,
	"terrain_floor_y": -8192.0,
	"build_height_y": 8000.0,
	"structural_depth_km": 22.3,
	"terrain_seed": 1337.0
}

class SessionStub:
	extends Node
	var settings: Dictionary = {}
	func get_active_world_settings() -> Dictionary: return settings.duplicate(true)
	func consume_load_selected_world_request() -> bool: return false

var _real_session: Node
var _stub: SessionStub
var _launcher: Node
var _finished := false

func _initialize() -> void:
	call_deferred("run")

func run() -> void:
	var timeout := create_timer(15.0)
	timeout.timeout.connect(func(): _fail("launcher smoke timed out"))
	_real_session = root.get_node_or_null("WorldSession")
	if _real_session != null:
		_real_session.name = "WorldSessionOriginalForTerrain3DSmoke"
	_stub = SessionStub.new()
	_stub.name = "WorldSession"
	root.add_child(_stub)

	# Existing Certified metadata without a backend marker must retain SmoothTerrain.
	await _boot(CERTIFIED_SETTINGS.duplicate(true))
	var selector := _surface_backend_selector()
	if selector == null or selector.selected != 0:
		_fail("legacy Certified slot did not retain Smooth terrain")
		return
	await _dispose_launcher()

	# An explicit saved Terrain3D marker must survive the real launcher setup.
	var surface_settings := CERTIFIED_SETTINGS.duplicate(true)
	surface_settings["terrain_backend"] = "terrain3d_surface_v1"
	await _boot(surface_settings)
	selector = _surface_backend_selector()
	if selector == null or selector.selected != 1:
		_fail("saved Terrain3D backend marker was not restored")
		return

	# Mega forces SmoothTerrain and removes the Terrain3D choice.
	_launcher.call("_on_mega_ring_preset_pressed")
	await process_frame
	if selector.selected != 0 or not selector.disabled:
		_fail("Mega preset did not disable Terrain3D and select Smooth terrain")
		return
	await _dispose_launcher()

	# A player who explicitly chooses SmoothTerrain keeps it after a readout refresh.
	await _boot(CERTIFIED_SETTINGS.duplicate(true))
	selector = _surface_backend_selector()
	if selector == null:
		_fail("surface backend selector missing")
		return
	selector.select(0)
	selector.item_selected.emit(0)
	_launcher.call("_update_readout", 0.0)
	await process_frame
	if selector.selected != 0:
		_fail("explicit Smooth terrain choice was overwritten on refresh")
		return
	_pass()

func _boot(settings: Dictionary) -> void:
	_stub.settings = settings
	_launcher = LAUNCHER_SCENE.instantiate()
	root.add_child(_launcher)
	await process_frame
	await process_frame

func _surface_backend_selector() -> OptionButton:
	if _launcher == null: return null
	for node in _launcher.find_children("*", "OptionButton", true, false):
		var option := node as OptionButton
		if option != null and option.item_count >= 2 and option.get_item_text(1).begins_with("Terrain3D surface"):
			return option
	return null

func _dispose_launcher() -> void:
	if _launcher != null:
		_launcher.queue_free()
		_launcher = null
		await process_frame

func _pass() -> void:
	_finished = true
	_cleanup()
	print("TERRAIN3D_LAUNCHER=PASS")
	quit(0)

func _fail(reason: String) -> void:
	if _finished: return
	_finished = true
	push_error("TERRAIN3D_LAUNCHER=FAIL " + reason)
	_cleanup()
	quit(1)

func _cleanup() -> void:
	if _launcher != null: _launcher.queue_free()
	if _stub != null:
		if _stub.get_parent() != null: _stub.get_parent().remove_child(_stub)
		_stub.queue_free()
	if _real_session != null: _real_session.name = "WorldSession"
