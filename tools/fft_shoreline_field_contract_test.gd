extends SceneTree

const SECTOR := preload("res://scripts/Voxel/LOD/RingTerrainProxySector.cs")

func _init() -> void:
	var sector := SECTOR.new()
	var passed := bool(sector.call("RunFftShorelineFieldContractTest"))
	var surface_query := FileAccess.get_file_as_string("res://third_party/godot_ocean_waves/compute/fft_surface_query.glsl")
	var provider := FileAccess.get_file_as_string("res://scripts/Voxel/LOD/RingWaterFftProvider.cs")
	var controller := FileAccess.get_file_as_string("res://scripts/Voxel/LOD/RingWaterFftController.cs")
	passed = passed \
		and surface_query.contains("vec2(shoreline.mapping.z)") \
		and surface_query.contains("shoreline.mapping.w") \
		and provider.contains("F(deepWaterWaveDepth)") \
		and controller.contains("DeepWaterWaveDepthMeters") \
		and controller.contains("latticeCenter, radius, DeepWaterWaveDepthMeters")
	sector.free()
	if not passed:
		push_error("FFT shoreline field contract failed")
		quit(1)
		return
	print("FFT shoreline field contract passed")
	quit(0)
