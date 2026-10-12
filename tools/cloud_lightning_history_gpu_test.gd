extends "res://tools/cloud_terrain_history_gpu_test.gd"

# Real production compute, light UBO, ping-pong history and published output.
# UPDATE_ONCE draws make each paired readback refer to one completed callback.
func _run() -> void:
	_build_world()
	if failed: return
	opaque_plane.visible = false
	clouds.far_opaque_depth_texture = null
	clouds.diagnostic_menu_history_weights = false
	clouds.ray_start_jitter_strength = 0.0
	clouds.clouds_density = 0.15
	clouds.directional_lights_data = [Vector4(0,0,-1,1),Vector4(1,1,1,1)]
	clouds.point_lights_data = []; clouds.lights_updated = true
	clouds.accumulation_decay = 0.95
	clouds.lightning_history_isolation_enabled = true
	clouds.refresh_compute()
	await _draws(96)
	var baseline := await _read_latest_history_color()
	clouds.point_lights_data = [Vector4(0,0,-2000,10000),Vector4(1,.8,.6,5)]
	clouds.lights_updated = true
	await _draws(3)
	var flash := await _read_cloud_output(1)
	var flash_persistent := await _read_latest_history_color()
	var temporal: Dictionary = clouds.get_temporal_history_diagnostics()
	_require(absf(float(temporal.last_packed_decay)-0.95) < 0.00001,"flash dropped requested coherent history")
	clouds.point_lights_data = []; clouds.lights_updated = true
	await _draws(2)
	var after := await _read_cloud_output(1)
	var after_persistent := await _read_latest_history_color()
	clouds.accumulation_decay = 0.0
	await _draws(2)
	var fresh := await _read_cloud_output(1)
	var metrics := {"flash_output":_rgb_delta(baseline,flash),"persistent_flash":_rgb_delta(baseline,flash_persistent),
		"persistent_after":_rgb_delta(baseline,after_persistent),"after_output":_rgb_delta(baseline,after),
		"persistent_vs_fresh":_rgb_delta(after_persistent,fresh)}
	_require(float(metrics.flash_output.mean) > 0.005,"point light was not visible in actual production output")
	for key in ["persistent_flash","persistent_after","after_output","persistent_vs_fresh"]:
		_require(float(metrics[key].mean) <= 0.002 and float(metrics[key].changed_fraction) <= 0.01,
			"transient entered persistent RGB "+key+" "+JSON.stringify(metrics))
	if failed: return
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT))
	for item in [["lightning_baseline",baseline],["lightning_output",flash],["lightning_persistent_flash",flash_persistent],
		["lightning_output_after",after],["lightning_persistent_after",after_persistent],["lightning_fresh",fresh]]:
		(item[1] as Image).save_png(ProjectSettings.globalize_path(OUTPUT.path_join(str(item[0])+".png")))
	print("CLOUD_LIGHTNING_HISTORY_GPU_TEST=PASS metrics=",JSON.stringify(metrics))
	viewport_3d.queue_free(); await process_frame; await process_frame
	quit(0)

func _rgb_delta(a: Image,b: Image) -> Dictionary:
	var sum := 0.0; var maximum := 0.0; var changed := 0
	for y in a.get_height():
		for x in a.get_width():
			var left := a.get_pixel(x,y); var right := b.get_pixel(x,y)
			var delta := absf(left.r-right.r)+absf(left.g-right.g)+absf(left.b-right.b)
			sum += delta; maximum = maxf(maximum,delta)
			if delta > 0.01: changed += 1
	var count := a.get_width()*a.get_height()
	return {"mean":sum/count,"maximum":maximum,"changed_fraction":float(changed)/count}
