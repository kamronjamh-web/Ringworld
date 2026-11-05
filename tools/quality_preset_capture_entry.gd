extends Node

## TEST ONLY. Entry point for tools/quality_preset_capture.gd in an EXPORTED build. Official templates disable --script and scene-path
## overrides, so the export preset "Windows Desktop Release (perf harness)" sets the qp_perf_harness feature, whose main_scene
## override in project.godot is this scene. Run: <export>\Ringworld.exe -- <capture args> (tools/quality_preset_capture.ps1 -ExportedBuild).
## Attaches the capture script (extends SceneTree) to the running SceneTree and starts it; the script then owns the run and quits.
## Pass --tree-asset-profile after -- to run imported_tree_asset_profile.gd from the same Release export.

const CAPTURE := preload("res://tools/quality_preset_capture.gd")
const TREE_ASSET_PROFILE := preload("res://tools/imported_tree_asset_profile.gd")


func _ready() -> void:
	var tree := get_tree()
	# set_script() runs the capture script's _init, which already defers _run; the explicit call below is only a fallback in case an
	# engine version stops doing that. _run ignores every call after the first, so the world is booted exactly once.
	var capture_script := TREE_ASSET_PROFILE if OS.get_cmdline_user_args().has("--tree-asset-profile") else CAPTURE
	tree.set_script(capture_script)
	tree.call_deferred("_run")
