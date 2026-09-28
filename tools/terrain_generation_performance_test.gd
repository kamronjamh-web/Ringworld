extends SceneTree

# Isolated M4 benchmark: no player preferences, world saves, or production scene state.
const SURFACE := preload("res://scripts/Terrain/Surface/RingTerrain3DSurface.cs")
const WINDOW := preload("res://scripts/Terrain/Generation/terrain_generation_window.gd")
const STREAM := preload("res://scripts/Terrain/Generation/terrain_generation_stream.gd")
const RECIPE := preload("res://scripts/Terrain/Generation/terrain_generation_recipe.gd")
const REPORT := "res://.godot/terrain-generation-tests/m4-performance-baseline.json"
const INCREMENTAL_REPORT := "res://.godot/terrain-generation-tests/m4-performance-incremental.json"
const SIDE := 384
var _failures := PackedStringArray()
var _report := {"isolated_environment": "Dedicated scheduler and transient surface instances; no user preferences, world saves, or production scene state."}
var _done := false
var _completed := false
var _report_path := REPORT

func _init() -> void:
	call_deferred("_run")
	create_timer(180.0).timeout.connect(func(): if not _done: _failures.append("180 second watchdog expired"); _finish())

func _run() -> void:
	var baseline := OS.get_cmdline_user_args().has("--baseline")
	var incremental := OS.get_cmdline_user_args().has("--incremental")
	if baseline == incremental:
		_failures.append("pass exactly one of --baseline or --incremental")
		_finish()
		return
	_report_path = INCREMENTAL_REPORT if incremental else REPORT
	await process_frame
	await process_frame
	_report["system"] = {"processor": OS.get_processor_name(), "video_adapter": RenderingServer.get_video_adapter_name(), "logical_cpus": OS.get_processor_count(), "driver": RenderingServer.get_current_rendering_driver_name(), "viewport_resolution": Vector2i(root.get_viewport().get_visible_rect().size), "gpu_timestamps": "unavailable"}
	_report["cpu"] = []
	for workers in [2, 4]: _report.cpu.append(await _cpu_phase(workers))
	if incremental:
		_report["native"] = await _incremental_phase()
	else:
		_report["native"] = await _native_phase()
	_completed = true
	_finish()

func _cpu_phase(workers: int) -> Dictionary:
	var stream = STREAM.new()
	stream.configure(workers, 8)
	var recipe: Resource = RECIPE.new()
	var latencies := []; var samples := []; var packs := []; var polls := []; var projections := []; var revision := 0
	var raw: Array[Dictionary] = []
	for index in 7:
		var anchor: int = [0,256,512,768,512,256,0][index]
		revision += 1
		var started := Time.get_ticks_usec()
		var accepted: bool = stream.request_window(recipe, anchor, 159000, 1, revision, 1, 1, revision, {})
		if not accepted: _failures.append("cpu request rejected workers=%d anchor=%d" % [workers, anchor]); continue
		var max_poll := 0.0; var payload := {}; var deadline := Time.get_ticks_msec()+30000
		while payload.is_empty() and Time.get_ticks_msec()<deadline:
			var before := Time.get_ticks_usec(); payload = stream.poll(); max_poll=maxf(max_poll,float(Time.get_ticks_usec()-before)/1000.0)
			if payload.is_empty(): await process_frame
		if payload.is_empty() or not bool(payload.get("ok",false)) or int(payload.get("revision",-1))!=revision: _failures.append("cpu payload invalid workers=%d revision=%d" % [workers,revision])
		latencies.append(float(Time.get_ticks_usec()-started)/1000.0); samples.append(float(payload.get("sampling_ms",0))); packs.append(float(payload.get("packing_ms",0))); polls.append(max_poll)
		projections.append(float(payload.get("projection_ms", 0)))
		raw.append({"phase": "cold_initial" if index == 0 else "cold_recenter" if index < 4 else "warm_revisit", "around": anchor, "latency_wall_ms": latencies.back(), "sampling_accumulated_ms": samples.back(), "packing_ms": packs.back(), "projection_ms": projections.back(), "cache_hits": payload.get("cache_hits", 0)})
	var diagnostics: Dictionary = stream.get_diagnostics()
	stream.shutdown()
	if int(diagnostics.get("tasks_submitted",0)) != int(stream.get_diagnostics().get("tasks_joined",0)): _failures.append("cpu tasks were not joined workers=%d" % workers)
	if int(diagnostics.get("high_water",0))>workers or int(diagnostics.get("cache_peak_pending",0))>9 or int(diagnostics.get("cache_bytes",0))>8*1024*1024: _failures.append("cpu scheduler bounds exceeded workers=%d" % workers)
	return {"workers":workers,"cache_mib":8,"raw":raw,"latency_wall_ms":_stats(latencies),"sampling_accumulated_ms":_stats(samples),"packing_ms":_stats(packs),"projection_ms":_stats(projections),"poll_max_ms":_stats(polls),"queue_high_water":diagnostics.get("high_water",0),"cache_bytes":diagnostics.get("cache_bytes",0),"cache_hits":diagnostics.get("cache_hits",0),"diagnostics":stream.get_diagnostics()}

func _native_phase() -> Dictionary:
	var heights := PackedFloat32Array(); heights.resize(SIDE*SIDE)
	for z in SIDE:
		for x in SIDE: heights[z*SIDE+x] = 100.125 + x*.03125 + z*.015625
	var images: Array[Image] = WINDOW.pack(heights, heights[128*SIDE+128])
	var stage_wall := []; var activation := []; var frames := []; var native := []
	var camera := Camera3D.new(); camera.position=Vector3(0,30,45); root.add_child(camera); camera.look_at(Vector3.ZERO)
	var environment := WorldEnvironment.new(); environment.environment=Environment.new(); root.add_child(environment)
	var light := DirectionalLight3D.new(); light.rotation_degrees=Vector3(-45,-30,0); root.add_child(light)
	var surface: Node = SURFACE.new(); root.add_child(surface)
	for index in 9:
		var frame_started := Time.get_ticks_usec()
		var started := Time.get_ticks_usec()
		var staged: bool = surface.call("StagePreparedWindow", heights, images, index*256, 159000, heights[128*SIDE+128], 1, index+1, true)
		var stage_ms := float(Time.get_ticks_usec()-started)/1000.0
		var activate_started := Time.get_ticks_usec()
		var active: bool = staged and surface.call("CanActivateStagedWindow", index*256, 159000, 16.0) and surface.call("ActivateStagedWindow", index+1)
		var activate_ms := float(Time.get_ticks_usec()-activate_started)/1000.0
		if not active: _failures.append("native stage/activate failed anchor=%d" % index)
		surface.call("UpdateCamera", camera.global_position)
		var diagnostics: Dictionary = surface.call("GetPreparedDiagnostics")
		await process_frame
		if index>0: stage_wall.append(stage_ms); activation.append(activate_ms); frames.append(float(Time.get_ticks_usec()-frame_started)/1000.0); native.append(diagnostics)
	surface.queue_free(); camera.queue_free(); environment.queue_free(); light.queue_free()
	return {"warmup_calls":1,"measured_calls":8,"stage_wall_ms":_stats(stage_wall),"activation_ms":_stats(activation),"frame_interval_ms":_stats(frames),"raw_native_diagnostics":native}

func _incremental_phase() -> Array:
	var output: Array = []
	var heights := PackedFloat32Array(); heights.resize(SIDE*SIDE)
	for index in heights.size(): heights[index] = 100.125 + float(index % SIDE)*.03125 + float(index / SIDE)*.015625
	var images: Array[Image] = WINDOW.pack(heights, heights[128*SIDE+128])
	var geometry_script := preload("res://scripts/Terrain/Surface/RingTerrain3DCollisionGeometry.cs")
	var camera := Camera3D.new(); camera.position=Vector3(0,30,45); root.add_child(camera); camera.look_at(Vector3.ZERO)
	var environment := WorldEnvironment.new(); environment.environment=Environment.new(); root.add_child(environment)
	var light := DirectionalLight3D.new(); light.rotation_degrees=Vector3(-45,-30,0); root.add_child(light)
	for tile_cells in [16,32]:
		var surface: Node = SURFACE.new(); root.add_child(surface)
		var raw: Array = []; var begin_times := []; var advance_times := []; var preparation := []; var activation := []; var frames := []; var activation_frames := []; var projection := []
		var resident_high_water := 0
		for index in 10:
			var geometry = geometry_script.new()
			if not geometry.Prepare(heights, index*256, 159000, heights[128*SIDE+128], 1, tile_cells): _failures.append("geometry prepare failed")
			await process_frame # Geometry preparation is deliberately outside publication frame timing.
			var frame_started := Time.get_ticks_usec(); var started := Time.get_ticks_usec()
			var begun: bool = surface.call("BeginBudgetedPreparedWindow", heights, images, geometry, index*256, 159000, heights[128*SIDE+128], 1, index+1)
			var begin_ms := float(Time.get_ticks_usec()-started)/1000.0; var advance_raw := []; var ready := false
			var deadline := Time.get_ticks_msec()+30000
			while begun and not ready and Time.get_ticks_msec()<deadline:
				var advance_started := Time.get_ticks_usec()
				var state: Dictionary = surface.call("AdvanceBudgetedPreparation", 1.0, .75)
				var call_ms := float(Time.get_ticks_usec()-advance_started)/1000.0
				if not bool(state.get("ok",false)): _failures.append(str(state.get("error","incremental preparation failed"))); break
				await process_frame
				var frame_ms := float(Time.get_ticks_usec()-frame_started)/1000.0
				frame_started = Time.get_ticks_usec()
				advance_raw.append({"call_ms":call_ms,"frame_interval_ms":frame_ms,"phase":state.phase})
				if index >= 2:
					frames.append(frame_ms)
					advance_times.append(call_ms)
				ready=bool(state.get("ready",false))
			if not ready: _failures.append("incremental preparation timeout")
			var prep_ms := float(Time.get_ticks_usec()-started)/1000.0
			var activate_started := Time.get_ticks_usec(); var active: bool = ready and surface.call("ActivateStagedWindow",index+1); var activate_ms:=float(Time.get_ticks_usec()-activate_started)/1000.0
			if not active: _failures.append("incremental activate failed")
			surface.call("UpdateCamera", camera.global_position)
			await process_frame
			var activation_frame_ms := float(Time.get_ticks_usec()-frame_started)/1000.0
			var diagnostics: Dictionary=surface.call("GetPreparedDiagnostics")
			resident_high_water = maxi(resident_high_water, int(diagnostics.active_resident_count) + int(diagnostics.staged_resident_count) + int(diagnostics.idle_resident_count))
			var projection_ms := float((geometry.GetDiagnostics() as Dictionary).get("projection_ms",0.0))
			raw.append({"warmup":index < 2,"begin_ms":begin_ms,"advance":advance_raw,"preparation_wall_ms":prep_ms,"activation_ms":activate_ms,"activation_frame_ms":activation_frame_ms,"projection_ms":projection_ms,"native":diagnostics})
			if index>=2:
				begin_times.append(begin_ms); preparation.append(prep_ms); activation.append(activate_ms); frames.append(activation_frame_ms); activation_frames.append(activation_frame_ms); projection.append(projection_ms)
		if resident_high_water > 2: _failures.append("native resident bound exceeded")
		surface.queue_free()
		await process_frame
		output.append({"tile_cells":tile_cells,"upload_budget_ms":1.0,"collision_budget_ms":.75,"warmups":2,"measured":8,"raw":raw,"begin_ms":_stats(begin_times),"advance_ms":_stats(advance_times),"preparation_wall_ms":_stats(preparation),"activation_ms":_stats(activation),"activation_frame_ms":_stats(activation_frames),"frame_interval_ms":_stats(frames),"projection_ms":_stats(projection),"height_upload_bytes":9*128*128*4,"resident_high_water":resident_high_water})
	camera.queue_free(); environment.queue_free(); light.queue_free()
	return output

func _stats(values: Array) -> Dictionary:
	if values.is_empty(): return {}
	var sorted := values.duplicate(); sorted.sort()
	var last := sorted.size() - 1
	return {"count":sorted.size(),"p50":float(sorted[clampi(int(ceil(sorted.size()*.5))-1,0,last)]),"p95":float(sorted[clampi(int(ceil(sorted.size()*.95))-1,0,last)]),"p99":float(sorted[clampi(int(ceil(sorted.size()*.99))-1,0,last)]),"max":sorted.back()}

func _finish() -> void:
	if _done: return
	_done = true
	if not _completed: _failures.append("benchmark did not complete all phases")
	var directory := _report_path.get_base_dir(); DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(directory))
	_report["failures"]=_failures; _report["completed"]=_completed
	var file:=FileAccess.open(_report_path,FileAccess.WRITE)
	if file==null: _failures.append("could not write report")
	else: file.store_string(JSON.stringify(_report,"  "));file.close()
	if _failures.is_empty(): print("TERRAIN_GENERATION_PERFORMANCE=PASS") ; quit(0)
	else: push_error("TERRAIN_GENERATION_PERFORMANCE=FAIL ","; ".join(_failures));quit(1)
