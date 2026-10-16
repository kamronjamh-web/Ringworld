extends SceneTree
# P0 grass-cards spike harness (docs/grass-cards-redesign.md section 7).
# Windowed 1600x900, vsync off. Usage (user args after --):
#   -- mode=measure bookmarks=0,2 out=<dir>      full matrix + baselines
#   -- mode=shots out=<dir>                       screenshots at 300/400 m medium, all bookmarks
#   -- mode=smoke                                 build + 30 frames, print stats
const SPIKE_SCENE := preload("res://scenes/grass_cards_spike/GrassCardsSpike.tscn")
const WARMUP := 90
const SAMPLES := 400
const REACHES := [200.0, 300.0, 400.0]
const DENS := ["low", "med", "high"]

var spike
var out_dir := ""
var args := {}
var results: Array = []

func _initialize() -> void:
	for a in OS.get_cmdline_user_args():
		var kv := a.split("=", true, 1)
		if kv.size() == 2:
			args[kv[0]] = kv[1]
	out_dir = args.get("out", "user://grass_cards_p0")
	DisplayServer.window_set_mode(DisplayServer.WINDOW_MODE_WINDOWED)
	DisplayServer.window_set_size(Vector2i(1600, 900))
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	Engine.max_fps = 0
	call_deferred("_boot")

func _boot() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("Forward+ RenderingDevice required")
		quit(1)
		return
	spike = SPIKE_SCENE.instantiate()
	spike.auto_setup = false
	root.add_child(spike)
	spike.setup(false)
	RenderingServer.viewport_set_measure_render_time(root.get_viewport_rid(), true)
	await _frames(20)
	print("VIEWPORT ", root.get_visible_rect().size, " adapter=", RenderingServer.get_video_adapter_name(), " method=", RenderingServer.get_current_rendering_method())
	match args.get("mode", "smoke"):
		"measure": await _run_measure()
		"shots": await _run_shots()
		"idle": await _run_idle()
		_: await _run_smoke()
	quit(0)

func _frames(n: int) -> void:
	for i in n:
		await RenderingServer.frame_post_draw

func _smi() -> Dictionary:
	var o := []
	OS.execute("nvidia-smi", ["--query-gpu=pstate,power.draw,clocks.sm,clocks.mem,temperature.gpu", "--format=csv,noheader,nounits"], o)
	var parts: PackedStringArray = (o[0] if o.size() > 0 else "").strip_edges().split(",")
	if parts.size() < 5:
		return {"pstate": "?", "power": 0.0, "sm": 0, "mem": 0, "temp": 0}
	return {"pstate": parts[0].strip_edges(), "power": float(parts[1]), "sm": int(parts[2]), "mem": int(parts[3]), "temp": int(parts[4])}

func _healthy(s: Dictionary) -> bool:
	return s.pstate == "P0" and s.mem > 3000

func _stats(a: Array) -> Dictionary:
	var s := a.duplicate()
	s.sort()
	var sum := 0.0
	for v in s: sum += v
	return {"mean": sum / s.size(), "median": s[s.size() / 2], "p95": s[int(s.size() * 0.95)], "min": s[0], "max": s[s.size() - 1]}

func _window(label: String) -> Dictionary:
	var vp := root.get_viewport_rid()
	for attempt in 3:
		await _frames(WARMUP)
		var before := _smi()
		var gpu: Array = []
		var calls: Array = []
		var prims: Array = []
		var cpu: Array = []
		for i in SAMPLES:
			await RenderingServer.frame_post_draw
			gpu.append(RenderingServer.viewport_get_measured_render_time_gpu(vp))
			cpu.append(RenderingServer.viewport_get_measured_render_time_cpu(vp))
			calls.append(Performance.get_monitor(Performance.RENDER_TOTAL_DRAW_CALLS_IN_FRAME))
			prims.append(Performance.get_monitor(Performance.RENDER_TOTAL_PRIMITIVES_IN_FRAME))
		var after := _smi()
		var ok := _healthy(before) and _healthy(after)
		var r := {"label": label, "gpu": _stats(gpu), "cpu": _stats(cpu), "draw_calls": _stats(calls).median, "primitives": _stats(prims).median,
			"smi_before": before, "smi_after": after, "attempt": attempt, "valid": ok}
		if ok:
			return r
		print("THROTTLED window discarded: ", label, " ", before, " ", after)
		OS.delay_msec(20000)
	return {"label": label, "valid": false}

func _run_smoke() -> void:
	DirAccess.make_dir_recursive_absolute(out_dir)
	spike.assets.SaveAtlasPreview(spike.atlas, out_dir.path_join("atlas_preview.png"))
	spike.assets.SaveAtlasPreview(spike.atlas_full, out_dir.path_join("atlas_full_preview.png"))
	for v in spike.VARIANTS.size():
		spike.variant = v
		spike.rebuild(400.0, "med", true)
		await _frames(10)
		print("SMOKE variant=", spike.VARIANT_NAMES[v], " instances=", spike.total_instances, " build_ms=", spike.build_ms)
	spike.variant = 1
	spike.rebuild(400.0, "med", true)
	await _frames(30)
	print("SMOKE instances=", spike.total_instances, " chunks=", spike.chunks.size(), " build_ms=", spike.build_ms, " visible=", spike.visible_instances())
	print("SMOKE gpu=", RenderingServer.viewport_get_measured_render_time_gpu(root.get_viewport_rid()))
	print("SMOKE smi=", _smi())

func _record(r: Dictionary, bookmark: int, reach: float, dens: String, collapse: bool, vname: String = "-") -> void:
	r["variant"] = vname
	r["bookmark"] = bookmark
	r["reach"] = reach
	r["density"] = dens
	r["collapse"] = collapse
	r["instances_total"] = spike.total_instances
	r["instances_in_visible_chunks"] = spike.visible_instances() if reach > 0 else 0
	results.append(r)
	if r.valid:
		print("ROW v=%s b=%d reach=%d dens=%s collapse=%s inst=%d vis=%d gpu_med=%.3f mean=%.3f p95=%.3f calls=%d prims=%d state=%s/%dMHz/%.0fW/%dC" % [
			vname, bookmark, int(reach), dens, str(collapse), r.instances_total, r.instances_in_visible_chunks, r.gpu.median, r.gpu.mean, r.gpu.p95,
			int(r.draw_calls), int(r.primitives), r.smi_after.pstate, r.smi_after.sm, r.smi_after.power, r.smi_after.temp])
	else:
		print("ROW INVALID b=%d reach=%d dens=%s" % [bookmark, int(reach), dens])
	DirAccess.make_dir_recursive_absolute(out_dir)
	var f := FileAccess.open(out_dir.path_join("measure_results.json"), FileAccess.WRITE)
	f.store_string(JSON.stringify(results, "  "))
	f.close()

func _ints(key: String, def: String) -> Array:
	var a: Array = []
	for b in String(args.get(key, def)).split(","):
		a.append(int(b))
	return a

func _run_measure() -> void:
	var books := _ints("bookmarks", "0")
	var variants := _ints("variants", "0,1,2,3")
	var reaches := _ints("reaches", "300,400")
	var denses: PackedStringArray = String(args.get("dens", "med,high")).split(",")
	for b in books:
		spike.set_bookmark(b)
		spike.variant = 0
		spike.rebuild(400.0, "med", true)
		spike.set_cards_visible(false)
		await _frames(10)
		_record(await _window("baseline_pre"), b, 0.0, "none", false, "-")
		spike.set_cards_visible(true)
		for v in variants:
			for reach in reaches:
				for dens in denses:
					spike.variant = v
					spike.rebuild(float(reach), dens, true)
					await _frames(10)
					_record(await _window("cards"), b, float(reach), dens, true, spike.VARIANT_NAMES[v])
		spike.variant = 0
		spike.rebuild(400.0, "med", true)
		spike.set_cards_visible(false)
		await _frames(10)
		_record(await _window("baseline_post"), b, 0.0, "none", false, "-")
		spike.set_cards_visible(true)

func _save(tag: String) -> void:
	root.get_viewport().get_texture().get_image().save_png(out_dir.path_join(tag + ".png"))

func _run_shots() -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(out_dir) if out_dir.begins_with("user://") else out_dir)
	var variants := _ints("variants", "0,1,2,3")
	var reaches := _ints("reaches", "300")
	var books := _ints("bookmarks", "0,1,4,2,3")
	for reach in reaches:
		for v in variants:
			spike.variant = v
			spike.rebuild(float(reach), "med", true)
			for b in books:
				spike.set_bookmark(b)
				await _frames(30)
				_save("%s_r%d_%s" % [spike.VARIANT_NAMES[v], reach, spike.BOOKMARK_NAMES[b]])
	spike.rebuild(float(reaches[0]), "med", true)
	for b in books:
		spike.set_bookmark(b)
		spike.set_cards_visible(false)
		await _frames(10)
		_save("ground_%s" % spike.BOOKMARK_NAMES[b])
		spike.set_cards_visible(true)
	print("SHOTS done")

# P0c idle / streaming check for the hero ring: stationary = zero builds; moves inside the hysteresis margin = zero;
# a cell crossing builds only the newly covered strip; jittering across a cell border does not flap.
func _run_idle() -> void:
	spike.variant = 4
	spike.rebuild(400.0, "med", true)
	spike.set_bookmark(0)
	await _frames(10)
	var b0: int = spike.hero_cell_builds
	var r0: int = spike.hero_reanchors
	print("IDLE start cells=", spike.hero_cells.size(), " instances=", spike.hero_instances, " builds=", b0)
	await _frames(300)
	print("IDLE stationary 300 frames: builds +", spike.hero_cell_builds - b0, " re-anchors +", spike.hero_reanchors - r0, " (expect 0 / 0)")
	var p: Vector3 = spike.camera.position
	for step in 40:
		spike.camera.position = p + Vector3(sin(step * 0.7) * 0.6, 0, cos(step * 0.5) * 0.6)
		spike.update_hero()
	print("IDLE jitter within hysteresis: builds +", spike.hero_cell_builds - b0, " re-anchors +", spike.hero_reanchors - r0, " (expect 0 / 0)")
	var cross_x: float = (spike.hero_anchor.x + 0.5) * spike.hero_cfg.hero_cell + spike.hero_cfg.hero_cell * 0.5 + spike.hero_cfg.hero_hyst + 0.3
	spike.camera.position = Vector3(cross_x, p.y, p.z)
	spike.update_hero()
	print("IDLE one-cell crossing: builds +", spike.hero_cell_builds - b0, " re-anchors +", spike.hero_reanchors - r0, " build_ms=", spike.hero_build_ms, " (expect 5 cells / 1)")
	var b1: int = spike.hero_cell_builds
	for step in 40:
		spike.camera.position = Vector3(cross_x + (0.2 if step % 2 == 0 else -0.2), p.y, p.z)
		spike.update_hero()
	print("IDLE jitter around the new border: builds +", spike.hero_cell_builds - b1, " (expect 0, no flapping)")
	print("IDLE cells now=", spike.hero_cells.size(), " instances=", spike.hero_instances)
