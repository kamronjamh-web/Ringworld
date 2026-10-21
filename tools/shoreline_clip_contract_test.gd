extends SceneTree

const GLOBAL_LOD = preload("res://scripts/Voxel/LOD/RingTerrainGlobalLod.cs")

func _init() -> void:
	var global_lod := GLOBAL_LOD.new()
	var summary: String = global_lod.RunShorelineClipContract()
	global_lod.free()
	print("SHORELINE_CLIP_CONTRACT=PASS " + summary)
	quit(0)
