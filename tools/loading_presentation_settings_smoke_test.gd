extends SceneTree

const SETTINGS_PROBE := preload("res://scripts/Tools/LoadingPresentationSettingsSmoke.cs")
const VIEW := preload("res://scripts/ui/ring_loading_screen.gd")
const PATH := "res://.godot/loading_presentation_settings_smoke.tres"

func _initialize() -> void:
	call_deferred("_run")

func _run() -> void:
	var probe: RefCounted = SETTINGS_PROBE.new()
	var result: Dictionary = probe.Run(PATH)
	probe = null
	if not bool(result.get("pass", false)):
		_fail(str(result.get("detail", "preferences failed")))
		return
	var layer := CanvasLayer.new()
	root.add_child(layer)
	var view = VIEW.new()
	view.configure({"world_title": "Fixture Ring", "seed": 42, "circumference_m": 2000000.0, "width_m": 100000.0, "spawn_around_m": 500000.0, "is_existing": true})
	layer.add_child(view)
	view.set_progress("terrain", "12 of 24 terrain regions", 0.4)
	await process_frame
	var progress_ok: bool = view.get("_progress") == 0.4 and view.get("_stage").text == "Shape terrain" and view.get("_detail").text == "12 of 24 terrain regions"
	var info_ok: bool = view.get("_title").text == "Fixture Ring" and view.get("_facts").text.contains("42") and view.get("_facts").text.contains("2.00 Mm")
	view.set_arrival_position(1500000.0, 75000.0)
	var arrival: bool = view.get("_hologram").beacon_turn == 0.75 and view.get("_hologram").beacon_across == 0.75
	view.set_progress("terrain", "Retained completed work", 0.2)
	var monotonic: bool = view.get("_progress") == 0.4
	view.show_failure("Fixture world was not available.")
	var failed: bool = view.get("_failed") and not view.get("_cancel").disabled and not view.get("_hologram").motion_enabled and not view.get("_hologram").is_processing()
	var signals := [0]
	view.cancel_requested.connect(func(): signals[0] += 1)
	view.get("_cancel").emit_signal("pressed")
	view.get("_cancel").emit_signal("pressed")
	var cancellation: bool = signals[0] == 1
	view.queue_free()
	await process_frame
	var completed = VIEW.new()
	layer.add_child(completed)
	completed.finish()
	var finish_ok: bool = completed.get("_finished") and completed.get("_progress") == 1.0 and completed.get("_cancel").disabled
	var finish_signals := [0]
	completed.cancel_requested.connect(func(): finish_signals[0] += 1)
	completed.get("_cancel").emit_signal("pressed")
	finish_ok = finish_ok and finish_signals[0] == 0
	layer.queue_free()
	await process_frame
	if not progress_ok or not info_ok or not arrival or not monotonic or not failed or not cancellation or not finish_ok:
		_fail("view progress=%s info=%s arrival=%s monotonic=%s failure=%s cancellation=%s finish=%s" % [progress_ok, info_ok, arrival, monotonic, failed, cancellation, finish_ok])
		return
	print("LOADING_PRESENTATION_SETTINGS_SMOKE_TEST_PASS %s view=true" % str(result.get("detail", "")))
	quit(0)

func _fail(message: String) -> void:
	push_error("LOADING_PRESENTATION_SETTINGS_SMOKE_TEST_FAIL " + message)
	quit(1)
