extends SceneTree
# Headless: megastorm audio diagnostics (WO6c): gating, hum ramp, once-per-passage boom, roar, shelter, tremor identity, thunder delay, assets.
# Run: Godot --headless --path . --script res://tools/megastorm_audio_test.gd [-- --out <dir>]. Prints MEGASTORM_AUDIO=PASS.
const TEST := preload("res://tools/MegastormAudioTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var out := ProjectSettings.globalize_path("res://.godot/captures/megastorm/wo6c")
	var args := OS.get_cmdline_user_args()
	var i := args.find("--out")
	if i >= 0 and i + 1 < args.size():
		out = args[i + 1]
	var runner = TEST.new()
	var code: int = runner.Run(out)
	runner = null
	if code == 0:
		print("MEGASTORM_AUDIO_WRAPPER=PASS")
	else:
		push_error("MEGASTORM_AUDIO_WRAPPER=FAIL exit=%d" % code)
	quit(code)
