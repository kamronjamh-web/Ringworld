extends Node
## Native streamer is initialized before gameplay; preferences are applied by the tuning controller.
func _ready() -> void:
	if not Engine.has_singleton("TextureStreaming"):
		push_error("Ringworld requires Godot 4.8 dev7 for native texture streaming.")
		return
	var streamer: Object = Engine.get_singleton("TextureStreaming")
	streamer.call("set_memory_budget_mb_override", int(ProjectSettings.get_setting("rendering/textures/streaming/memory_budget_mb", 512)))
	streamer.call("set_min_lod_override", int(ProjectSettings.get_setting("rendering/textures/streaming/min_lod", 0)))
	streamer.call("set_max_lod_override", int(ProjectSettings.get_setting("rendering/textures/streaming/max_lod", 3)))
