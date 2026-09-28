extends SceneTree

## Guards the fail-closed per-quad shoreline policy used by the 2m/4m/8m FFT
## geometry. Missing or mixed ocean-mask texels must never become displaced.
func _init() -> void:
	var helper := FileAccess.get_file_as_string("res://materials/ring_water_fft.gdshaderinc")
	var proxy := FileAccess.get_file_as_string("res://materials/ring_ocean_proxy.gdshader")
	var sector := FileAccess.get_file_as_string("res://scripts/Voxel/LOD/RingTerrainProxySector.cs").replace("\r\n", "\n")
	var passed := helper.contains("return distant_factors") \
		and helper.contains("vec2 ring_fft_shoreline_factors(vec2 canonical, vec2 distant_factors)") \
		and helper.contains("fft_shoreline_field_mode != 2") \
		and proxy.contains("instance uniform vec4 fft_distant_ocean_mask_rect") \
		and proxy.find("instance uniform vec4 coverage_atlas_rect") < proxy.find("instance uniform vec4 fft_distant_ocean_mask_rect") \
		and proxy.contains("filter_linear") \
		and proxy.contains("float depth = texture(fft_distant_ocean_mask_atlas, uv).r * 510.0") \
		and proxy.contains("vec2 vertex_texel = clamp(mesh_uv") \
		and proxy.contains("(tile_size - 1.0) + 0.5") \
		and proxy.contains("fft_deep_water_wave_depth_meters") \
		and sector.contains("InstallFftDistantOceanMask") \
		and sector.contains("_fftDistantOceanMaskSlots") \
		and sector.contains("_oceanReplacementDependents") \
		and sector.contains("NotifyOceanReplacementDependents") \
		and sector.contains("RefreshOceanReplacementFromFiner") \
		and sector.contains("InvalidateOceanReplacementFromFiner") \
		and sector.contains("dependent.InvalidateOceanReplacementFromFiner(this)") \
		and sector.contains("Reactivated cached water is a visible ownership change") \
		and sector.contains("UpdateSharedFftOceanCoverage();\n            NotifyOceanReplacementDependents();") \
		and sector.contains("mask == null || SampleSpacingMeters is not (2 or 4 or 8)") \
		and sector.contains("FftDeepWaterVertexDepths") \
		and sector.contains("TryResolveConservativeOceanVertexDepth") \
		and sector.contains("mask.ProxyQuadCells + 1") \
		and sector.contains("RunFftDistantOceanVertexFieldContract") \
		and not sector.contains("&& _oceanReplacementFinerRevision == _finerCoverageLevel._coverageRevision") \
		and not helper.contains("fft_distant_ocean_certified") \
		and not proxy.contains("fft_distant_ocean_certified")
	if not passed:
		push_error("WATER_FFT_DISTANT_OCEAN_CONTRACT_FAIL")
		quit(1)
		return
	print("WATER_FFT_DISTANT_OCEAN_CONTRACT_PASS")
	quit(0)
