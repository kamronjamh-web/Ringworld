extends SceneTree
# Bakes the emitter underground cavern shells (M2-2) from the EmitterSiteUnderground SDF. Needs the C# assembly built.
#
#   godot --headless --path . --script res://tools/poi/build_emitter_underground.gd
#
# Writes res://assets/poi/underground/CavernShell_{Regular,Main}.res (ArrayMesh with LODs),
# CavernShell_{Regular,Main}_Collision.res (ConcavePolygonShape3D) and CavernRock.tres. Deterministic.

const BAKER := preload("res://tools/EmitterUndergroundBaker.cs")

func _init() -> void:
	var baker = BAKER.new()
	var code := int(baker.call("Bake"))
	quit(code)
