extends "res://tools/cloud_terrain_history_gpu_test.gd"

# Actual production shader: stationary sky rays alternate their jitter phase,
# then the density field abruptly clears without an opaque disocclusion.
func _run() -> void:
	_build_world()
	if failed: return
	opaque_plane.visible = false
	clouds.far_opaque_depth_texture = null
	clouds.clouds_density = 0.15
	clouds.accumulation_decay = 0.95
	clouds.diagnostic_menu_history_weights = true
	clouds.refresh_compute()
	await _draws(32)
	var weights := []; var raw := []; var retained := []
	for frame in 16:
		_set_dither(0.1 if frame % 2 == 0 else 0.9)
		await _draws(1)
		var diagnostic := await _read_cloud_output(1)
		var history := await _read_latest_history_color()
		weights.append(_mean_channel(diagnostic,0))
		raw.append(_mean_channel(diagnostic,3))
		retained.append(_mean_alpha(history))
	var stable := {"retained_weight_min":_minimum(weights),"raw_variance":_variance(raw),"retained_variance":_variance(retained)}
	_require(float(stable.retained_weight_min) >= 0.94,"ordinary sky jitter rejected coherent .95 history "+JSON.stringify(stable))
	_require(float(stable.raw_variance) > 0.0001,"sky phase sequence failed to exercise real sampling variance "+JSON.stringify(stable))
	_require(float(stable.retained_variance) < float(stable.raw_variance),"sky history failed to smooth jitter "+JSON.stringify(stable))
	if failed: return
	# Black fine-detail noise makes this power edit produce the same current
	# stochastic sample. The changed field must still retire old history once.
	var prior_alpha := float(raw[-1])
	clouds.clouds_detail_power += 0.25
	await _draws(1)
	var changed_power := await _read_cloud_output(1)
	_require(_mean_channel(changed_power,0) <= 0.10,"abrupt density power edit failed to reset history")
	_require(absf(_mean_channel(changed_power,3)-prior_alpha) < 0.00001,"power-change fixture did not preserve the coincident sample")
	if failed: return
	clouds.clouds_density = 0.0
	await _draws(1)
	var cleared_diagnostic := await _read_cloud_output(1)
	var cleared_history := await _read_latest_history_color()
	var cleared := {"retained_weight":_mean_channel(cleared_diagnostic,0),"raw_alpha":_mean_channel(cleared_diagnostic,3),"history_alpha":_mean_alpha(cleared_history)}
	_require(float(cleared.retained_weight) <= 0.10,"changed sky density retained incompatible history "+JSON.stringify(cleared))
	_require(float(cleared.history_alpha) <= 0.02,"cleared sky retained old cloud opacity "+JSON.stringify(cleared))
	if failed: return
	clouds.cloud_history_compatibility_enabled = false
	clouds.clouds_density = 0.15
	await _draws(1)
	var unchecked := await _read_cloud_output(1)
	_require(_mean_channel(unchecked,0) >= 0.94,"compatibility toggle-off retired requested history after density edit")
	clouds.request_history_reset()
	await _draws(1)
	var explicitly_reset := await _read_cloud_output(1)
	_require(_mean_channel(explicitly_reset,0) <= 0.10,"explicit reset was suppressed by compatibility toggle-off")
	if failed: return
	print("CLOUD_SKY_HISTORY_GPU_TEST=PASS stable=",JSON.stringify(stable)," cleared=",JSON.stringify(cleared))
	viewport_3d.queue_free(); await process_frame; await process_frame
	quit(0)
