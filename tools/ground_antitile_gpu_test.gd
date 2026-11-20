extends "res://tools/grass_card_color_match_gpu_test.gd"
# Ground albedo anti-tiling GPU test (marker GROUND_ANTITILE): thin wrapper that runs grass_card_color_match_gpu_test.gd in its `tiling` mode.
# Orthographic top-down views over pure grass, anti-tiling on vs off: 5 m periodicity score of the ground, the ground with cards and the proxy terrain,
# the hex lattice lags, the mean colour, and the 1000 m periodicity of the shader. Windowed Vulkan only (do not pass --headless).
#   godot --path . --script res://tools/ground_antitile_gpu_test.gd [-- out=<dir>]


func _init() -> void:
	super._init()
	args["mode"] = "tiling"
	if not args.has("out"):
		args["cleanup"] = "1" # scratch images are deleted on a pass; pass out=<dir> to keep them
		out_dir = "res://.godot/ground_antitile_%d" % OS.get_process_id()
