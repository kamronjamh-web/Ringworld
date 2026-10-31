extends SceneTree
# Headless: megastorm lightning channel segment selection / building / pulse limits. Run: godot --headless --path . --script res://tools/lightning_channel_segments_test.gd
# Prints LIGHTNING_CHANNEL_SEGMENTS_TEST=PASS.
const TEST := preload("res://tools/LightningChannelSegmentsTest.cs")

func _initialize() -> void:
	var code: int = TEST.new().call("Run")
	quit(code)
