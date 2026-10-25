extends Node

## TEST ONLY. Entry point for tools/quality_preset_capture.gd in an EXPORTED build. Official templates disable --script and scene-path
## overrides, so the export preset "Windows Desktop Release (perf harness)" sets the qp_perf_harness feature, whose main_scene
## override in project.godot is this scene. Run: <export>\Ringworld.exe -- <capture args> (tools/quality_preset_capture.ps1 -ExportedBuild).
## Attaches the capture script (extends SceneTree) to the running SceneTree and starts it; the script then owns the run and quits.

const CAPTURE := preload("res://tools/quality_preset_capture.gd")


func _ready() -> void:
	var tree := get_tree()
	tree.set_script(CAPTURE)
	tree.call_deferred("_run")
