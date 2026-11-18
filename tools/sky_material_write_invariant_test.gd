extends SceneTree

# R1-6c: with the cadence on and clocks running, the ring sky material is written only on refresh frames
# (a write dirties the sky radiance map), and the uncached ring_radius_meters write does not happen per frame.
# Reads the DaySideAtmosphere counters (sky_material_write_count, sky_radiance_refresh_count, sky_material_write_frames).
const WORLD_SCENE: PackedScene = preload("res://scenes/VoxelRingIntegrationTest.tscn")
const WARMUP_SECONDS := 8.0
const RUN_SECONDS := 4.0
const MIN_FRAMES := 60

var _world: Node = null


func _initialize() -> void:
	_world = WORLD_SCENE.instantiate()
	root.add_child(_world)
	call_deferred("_run")


func _run() -> void:
	for i in 8:
		await process_frame
	var atmosphere := _world.get_node_or_null("DaySideAtmosphere")
	if atmosphere == null or atmosphere.get("ring_sky_material") == null:
		return _fail("DaySideAtmosphere or its ring sky material is missing")
	atmosphere.set("sky_radiance_cadence_enabled", true)
	# The scene settles for a few seconds after load (sky palette lerp, player landing): measure after that.
	var warm_start := Time.get_ticks_msec()
	while float(Time.get_ticks_msec() - warm_start) / 1000.0 < WARMUP_SECONDS:
		await process_frame
	var sun_start: Vector3 = (atmosphere.call("get_sky_live_inputs") as Dictionary).get("sun_direction", Vector3.ZERO)
	var writes_start := int(atmosphere.get("sky_material_write_count"))
	var write_frames_start := int(atmosphere.get("sky_material_write_frames"))
	var refresh_start := int(atmosphere.get("sky_radiance_refresh_count"))
	var frames := 0
	var frames_with_writes := 0
	var write_frames_without_epoch := 0
	var epoch_frames := 0
	var reasons := {}
	var last_writes := writes_start
	var last_refresh := refresh_start
	var started := Time.get_ticks_msec()
	while float(Time.get_ticks_msec() - started) / 1000.0 < RUN_SECONDS:
		await process_frame
		frames += 1
		var writes := int(atmosphere.get("sky_material_write_count"))
		var refresh := int(atmosphere.get("sky_radiance_refresh_count"))
		if writes != last_writes:
			frames_with_writes += 1
			if refresh == last_refresh:
				write_frames_without_epoch += 1
			if refresh != last_refresh and writes - last_writes != 1:
				return _fail("a refresh frame wrote %d times (the epoch alone expected)" % (writes - last_writes))
		if refresh != last_refresh:
			epoch_frames += 1
			var reason := str(atmosphere.get("sky_radiance_last_refresh_reason"))
			reasons[reason] = int(reasons.get(reason, 0)) + 1
		last_writes = writes
		last_refresh = refresh
	var elapsed := float(Time.get_ticks_msec() - started) / 1000.0
	var write_frames_counted := int(atmosphere.get("sky_material_write_frames")) - write_frames_start
	var refreshes := int(atmosphere.get("sky_radiance_refresh_count")) - refresh_start
	var sun_end: Vector3 = (atmosphere.call("get_sky_live_inputs") as Dictionary).get("sun_direction", Vector3.ZERO)
	var sun_moved := rad_to_deg(sun_start.angle_to(sun_end))
	var rate := float(frames_with_writes) / float(maxi(frames, 1))
	print("SKY_MATERIAL_WRITE_INVARIANT frames=%d seconds=%.2f fps=%.1f refreshes=%d refresh_per_frame=%.4f write_frames=%d write_frames_without_epoch=%d sun_moved_deg=%.3f" % [
			frames, elapsed, float(frames) / maxf(elapsed, 0.001), refreshes, float(refreshes) / float(maxi(frames, 1)), frames_with_writes, write_frames_without_epoch, sun_moved])
	print("SKY_MATERIAL_WRITE_INVARIANT reasons=%s" % str(reasons))
	if frames < MIN_FRAMES:
		return _fail("only %d frames in %.1f s" % [frames, elapsed])
	if refreshes <= 0:
		return _fail("the cadence never refreshed (age alone should refresh every 0.5 s)")
	if sun_moved < 0.01:
		return _fail("the sun did not move (%.4f deg): the clocks are not running" % sun_moved)
	if frames_with_writes != write_frames_counted:
		return _fail("write-frame counter %d disagrees with the observed %d" % [write_frames_counted, frames_with_writes])
	if epoch_frames != refreshes:
		return _fail("more than one epoch write per frame")
	if write_frames_without_epoch > 2:
		return _fail("%d frames wrote the sky material without a refresh epoch (static uniforms or ring_radius_meters per frame)" % write_frames_without_epoch)
	if rate > 0.5:
		return _fail("the sky material was written on %.0f%% of frames" % (rate * 100.0))
	print("SKY_MATERIAL_WRITE_INVARIANT=PASS")
	quit(0)


func _fail(message: String) -> void:
	push_error("SKY_MATERIAL_WRITE_INVARIANT=FAIL: " + message)
	quit(1)
