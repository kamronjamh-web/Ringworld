extends RefCounted
## Scenario kind "ab_matrix" of tools/perf_bench.gd (docs/reflections-plan.md section 5, B3-B5): at one pose, a list of named variants measured against a baseline
## in an interleaved A-X-A pattern inside one game boot (A0 X1 A1 X2 A2 ... Xn An per round), plus optionally one quiet still per variant.
## Scenario keys: pose, window_s, rounds, hold_clocks, variant_settle_s (1.0), stills, force_wet [film, soak, standing, rain] (WetWorld.ForceUniform),
## baseline [ops] (applied for the whole scenario), variants [{name, ops}]. An op is one of
##   {"env": {prop: value}}                          properties of the main Environment
##   {"sky": {prop: value}}                          properties of the main Environment's sky resource
##   {"node": "Path", "set": {prop: value}}          properties of a node below the game root
##   {"node": "Path", "call": "Method", "args": []}  a method call (re-run after the restore so an "apply" method pushes the old values again)
##   {"ssr_half_size": bool}                         RenderingServer.environment_set_ssr_half_size (restored to the effective state before the op: true when WetWorld.SsrHalfSizeActive, else the project setting)
## A missing node, property or method is an error for that variant only. Results go to report["ab_matrices"]: per metric the baseline windows, the variant
## windows and the per-round delta X - (A before + A after) / 2.

const COMMON := preload("res://tools/shadow_harness_common.gd")
const TVW := preload("res://tools/tree_visibility_world.gd")

const HALF_SIZE_SETTING := "rendering/environment/screen_space_reflection/half_size"
const BASE_METRICS := ["gpu_total_p50", "wall_p50", "wall_p95", "render_cpu_p50", "process_p50", "setup_p50", "draws_p50"]


# ------------------------------------------------------------------ ops

static func _coerce(old: Variant, value: Variant) -> Variant:
	match typeof(old):
		TYPE_INT:
			return int(value)
		TYPE_FLOAT:
			return float(value)
		TYPE_BOOL:
			return bool(value)
	return value


static func _set_props(target: Object, props: Dictionary, st: Dictionary, what: String) -> String:
	for key in props:
		var k := String(key)
		if not TVW.has_property(target, k):
			return "%s has no property %s" % [what, k]
		var old: Variant = target.get(k)
		st["undo"].append([target, k, old])
		target.set(k, _coerce(old, props[key]))
	return ""


## Applies ops in order; fills st {"undo": [[obj, prop, old]], "calls": [[node, method, args]], "half": bool}. Returns "" or an error (st still holds what was applied).
static func _apply(b: Object, ops: Array, st: Dictionary) -> String:
	for op in ops:
		if op.has("env") or op.has("sky"):
			var env: Environment = b._ssr_env()
			if env == null:
				return "no main Environment"
			if op.has("env"):
				var e := _set_props(env, op["env"], st, "Environment")
				if e != "":
					return e
			if op.has("sky"):
				if env.sky == null:
					return "Environment has no sky"
				var e2 := _set_props(env.sky, op["sky"], st, "Sky")
				if e2 != "":
					return e2
		if op.has("node"):
			var path := String(op["node"])
			var node: Node = b.game.get_node_or_null(path)
			if node == null:
				return "node %s not found" % path
			if op.has("set"):
				var e3 := _set_props(node, op["set"], st, path)
				if e3 != "":
					return e3
			if op.has("call"):
				var method := String(op["call"])
				if not node.has_method(method):
					return "%s has no method %s" % [path, method]
				var call_args: Array = op.get("args", [])
				st["calls"].append([node, method, call_args])
				node.callv(method, call_args)
		if op.has("ssr_half_size"):
			if not bool(st["half"]):
				st["half_prev"] = _effective_half(b)
			st["half"] = true
			RenderingServer.environment_set_ssr_half_size(bool(op["ssr_half_size"]))
	return ""


## The half-size state in effect now: WetWorld holds it at half size while wet on presets that ask for it (SsrHalfSizeActive), otherwise the project setting.
static func _effective_half(b: Object) -> bool:
	var wet: Node = b.game.get_node_or_null("WetWorld")
	if wet != null and bool(wet.get("SsrHalfSizeActive")):
		return true
	return bool(ProjectSettings.get_setting(HALF_SIZE_SETTING, false))


static func _undo(st: Dictionary) -> void:
	var undo: Array = st["undo"]
	for i in range(undo.size() - 1, -1, -1):
		var u: Array = undo[i]
		if is_instance_valid(u[0]):
			u[0].set(u[1], u[2])
	if bool(st.get("half", false)):
		RenderingServer.environment_set_ssr_half_size(bool(st.get("half_prev", false)))
	for c in st["calls"]:
		if is_instance_valid(c[0]):
			c[0].callv(c[1], c[2])
	st["undo"] = []
	st["calls"] = []
	st["half"] = false


static func _new_state() -> Dictionary:
	return {"undo": [], "calls": [], "half": false}


# ------------------------------------------------------------------ stats

static func _median(values: Array) -> float:
	if values.is_empty():
		return 0.0
	var s := values.duplicate()
	s.sort()
	return float(s[s.size() / 2])


static func _spread(values: Array) -> float:
	if values.is_empty():
		return 0.0
	return float(values.max()) - float(values.min())


static func _wet_state(b: Object) -> Dictionary:
	var env: Environment = b._ssr_env()
	var w: Node = b.game.get_node_or_null("WetWorld")
	return {"wet_valid": w.get("Valid") if w != null else null, "ssr_enabled": env.ssr_enabled if env != null else null,
		"ssr_policy_weight": w.get("SsrPolicyWeight") if w != null else null, "ssr_gate_wet_signal": w.get("SsrGateWetSignal") if w != null else null,
		"ssr_gate_wet": w.get("SsrGateWet") if w != null else null, "ssr_gate_poi_near": w.get("SsrGatePoiNear") if w != null else null}


static func _force_wet(b: Object, sc: Dictionary, result: Dictionary) -> String:
	var wet: Node = b.game.get_node_or_null("WetWorld")
	var fw: Array = sc["force_wet"]
	if wet == null or not wet.has_method("ForceUniform") or not wet.has_method("ClearForce") or fw.size() != 4:
		return "force_wet: WetWorld node or ForceUniform/ClearForce missing (or force_wet is not 4 numbers)"
	wet.call("ForceUniform", float(fw[0]), float(fw[1]), float(fw[2]), float(fw[3]))
	if result.has("setup"):
		result["setup"]["force_wet"] = fw
	return ""


# ------------------------------------------------------------------ measure

## Returns the result entry for report["ab_matrices"]. `b` is the perf_bench.gd SceneTree script (its helpers and state are reused).
static func measure(b: Object, sc: Dictionary) -> Dictionary:
	var name := String(sc["name"])
	var pose: Dictionary = b.spec["poses"][String(sc["pose"])]
	var variants_in: Array = sc.get("variants", [])
	var rounds := int(b.args.get("rounds", b._opt(sc, "rounds")))
	var seconds := float(b._opt(sc, "window_s")) * float(b.args.get("window-scale", "1"))
	var result := {"name": name, "kind": "ab_matrix", "pose_name": String(sc["pose"]), "pose": pose, "window_s": seconds, "rounds": rounds, "setup": {},
		"metrics_list": [], "baseline": {}, "variants": [], "windows": []}
	b._log("scenario %s (ab_matrix, %d variants, %d x %.0f s)" % [name, variants_in.size(), rounds, seconds])
	var base_state := _new_state()
	await _measure_body(b, sc, result, pose, variants_in, rounds, seconds, base_state)
	# Restore, whatever happened above.
	_undo(base_state)
	var wet: Node = b.game.get_node_or_null("WetWorld")
	if sc.has("force_wet") and wet != null and wet.has_method("ClearForce"):
		wet.call("ClearForce")
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	if result.has("error"):
		b._log("scenario %s FAILED: %s" % [name, result["error"]])
	b._write()
	return result


static func _measure_body(b: Object, sc: Dictionary, result: Dictionary, pose: Dictionary, variants_in: Array, rounds: int, seconds: float, base_state: Dictionary) -> void:
	var name := String(result["name"])
	b._release_keys()
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	var began := Time.get_ticks_msec()
	var place_error: String = await b._place(pose, sc, 0.0)
	if place_error != "":
		result["error"] = "placement: %s" % place_error
		return
	await COMMON.settle(b, 0.4)
	var idle: bool = await COMMON.wait_builds_idle(b, b.game, 180.0)
	var time_info: Dictionary = b._apply_time(b._opt(sc, "time"), bool(b._opt(sc, "hold_clocks")))
	if time_info.has("error"):
		result["error"] = "time of day: %s" % String(time_info["error"])
		return
	await COMMON.settle(b, float(b._opt(sc, "settle_s")))
	idle = idle and await COMMON.wait_builds_idle(b, b.game, 90.0)
	result["setup"] = {"idle": idle, "settle_ms": Time.get_ticks_msec() - began, "reached": COMMON.pose_now(b.game), "time": time_info}
	if sc.has("force_wet"):
		var fe := _force_wet(b, sc, result)
		if fe != "":
			result["error"] = fe
			return
		await COMMON.settle(b, 2.0)
	var base_err := _apply(b, sc.get("baseline", []), base_state)
	if base_err != "":
		result["error"] = "baseline: %s" % base_err
		return
	await COMMON.settle(b, 1.0)
	var setup_extra: Dictionary = _wet_state(b)
	for k in setup_extra:
		result["setup"][k] = setup_extra[k]
	result["setup"]["ssr_half_size_effective"] = _effective_half(b)
	var settle_s := float(sc.get("variant_settle_s", 1.0))
	# Per variant records.
	var vrecs: Array = []
	for v in variants_in:
		vrecs.append({"name": String(v["name"]), "ops": v.get("ops", []), "abs_w": [], "delta_w": []})  # abs_w / delta_w: per round the X window and X - mean(A)
	var a_windows: Array = []
	var windows: Array = result["windows"]
	var all_w: Array = []  # the window dictionaries, for the metric list
	for r in rounds:
		var prev: Dictionary = await b._window("%s_r%d_A0" % [name, r], seconds, {})
		a_windows.append(prev)
		all_w.append(prev)
		windows.append({"tag": prev["tag"], "m": prev["m"]})
		for j in variants_in.size():
			var rec: Dictionary = vrecs[j]
			if rec.has("error"):
				continue
			var st := _new_state()
			var ap_err := _apply(b, variants_in[j].get("ops", []), st)
			if ap_err != "":
				rec["error"] = ap_err
				_undo(st)
				continue
			await COMMON.settle(b, settle_s)
			var x: Dictionary = await b._window("%s_r%d_%s" % [name, r, rec["name"]], seconds, {})
			_undo(st)
			await COMMON.settle(b, settle_s)
			var nxt: Dictionary = await b._window("%s_r%d_A%d" % [name, r, j + 1], seconds, {})
			a_windows.append(nxt)
			all_w.append(x)
			all_w.append(nxt)
			windows.append({"tag": x["tag"], "m": x["m"]})
			windows.append({"tag": nxt["tag"], "m": nxt["m"]})
			rec["abs_w"].append(x)
			rec["delta_w"].append([prev, x, nxt])
			prev = nxt
		b._write()
	# Metrics list.
	var metrics: Array = BASE_METRICS.duplicate()
	if not all_w.is_empty():
		for key in all_w[0]["m"]:
			var ks := String(key)
			if ks.begins_with("vp.") and ks.ends_with(".gpu_p50"):
				metrics.append(ks)
	result["metrics_list"] = metrics
	for mname in metrics:
		var vals: Array = []
		for w in a_windows:
			vals.append(float(w["m"].get(mname, 0.0)))
		result["baseline"][mname] = {"values": vals, "median": _median(vals), "spread": _spread(vals)}
	var failed_names: Array = []
	for rec in vrecs:
		var entry := {"name": rec["name"], "ops": rec["ops"], "abs": {}, "delta": {}}
		if rec.has("error"):
			entry["error"] = rec["error"]
			failed_names.append(String(rec["name"]))
		for mname in metrics:
			var xs: Array = []
			var ds: Array = []
			for w in rec["abs_w"]:
				xs.append(float(w["m"].get(mname, 0.0)))
			for trio in rec["delta_w"]:
				ds.append(float(trio[1]["m"].get(mname, 0.0)) - (float(trio[0]["m"].get(mname, 0.0)) + float(trio[2]["m"].get(mname, 0.0))) * 0.5)
			entry["abs"][mname] = xs
			entry["delta"][mname] = {"values": ds, "median": _median(ds), "min": float(ds.min()) if not ds.is_empty() else 0.0, "max": float(ds.max()) if not ds.is_empty() else 0.0}
		result["variants"].append(entry)
		if not rec.has("error"):
			b._log("  ab %-18s dGPU %+6.2f ms  dwall %+6.2f ms  (baseline spread gpu %.2f wall %.2f)" % [rec["name"], float(entry["delta"]["gpu_total_p50"]["median"]),
				float(entry["delta"]["wall_p50"]["median"]), float(result["baseline"]["gpu_total_p50"]["spread"]), float(result["baseline"]["wall_p50"]["spread"])])
	if not failed_names.is_empty():
		b._log("  ab %s: failed variants: %s" % [name, ", ".join(failed_names)])


# ------------------------------------------------------------------ stills

## Quiet stills of the baseline and every variant (<out>/ab_matrix/<scenario>__<variant>.png). Adds {"stills": {name: path}} to the scenario's entry in
## b.report["ab_matrices"]. Returns "" or an error text.
static func stills(b: Object, sc: Dictionary) -> String:
	var name := String(sc["name"])
	var entry: Dictionary = {}
	for e in b.report["ab_matrices"]:
		if String(e["name"]) == name:
			entry = e
	if entry.is_empty() or entry.has("error"):
		return ""  # nothing measured, nothing to grab
	var paths := {}
	entry["stills"] = paths
	var base_state := _new_state()
	var err: String = await _stills_body(b, sc, entry, paths, base_state)
	_undo(base_state)
	var wet: Node = b.game.get_node_or_null("WetWorld")
	if sc.has("force_wet") and wet != null and wet.has_method("ClearForce"):
		wet.call("ClearForce")
	if err != "":
		entry["stills_error"] = err
		b._log("ab_matrix stills %s FAILED: %s" % [name, err])
	else:
		b._log("ab_matrix stills %s: %d" % [name, paths.size()])
	b._write()
	return err


static func _grab(b: Object, path: String) -> String:
	for _i in 20:
		TVW.hold_wind()
		await b.process_frame
	await RenderingServer.frame_post_draw
	var img: Image = TVW.grab(b)
	if img.save_png(path) != OK:
		return "save_png failed (%s)" % path
	return ""


static func _stills_body(b: Object, sc: Dictionary, entry: Dictionary, paths: Dictionary, base_state: Dictionary) -> String:
	var name := String(sc["name"])
	var pose: Dictionary = b.spec["poses"][String(sc["pose"])]
	b._release_keys()
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	var place_error: String = await b._place(pose, sc, 0.0)
	if place_error != "":
		return "placement: %s" % place_error
	await COMMON.settle(b, 0.4)
	await COMMON.wait_builds_idle(b, b.game, 180.0)
	var time_info: Dictionary = b._apply_time(b._opt(sc, "time"), true)
	if time_info.has("error"):
		return "time of day: %s" % String(time_info["error"])
	await COMMON.settle(b, float(b._opt(sc, "settle_s")))
	await COMMON.wait_builds_idle(b, b.game, 90.0)
	if not b.quiet_done:
		b.report["ssr_quiet"] = TVW.quiet_scene(b.game)
		TVW.hide_overlays(b.game)
		b.quiet_done = true
	TVW.freeze_world(b.game)
	b.report["still_noise"] = TVW.freeze_noise(b, b.game, PackedStringArray(["clouds", "taa"]))
	if sc.has("force_wet"):
		var fe := _force_wet(b, sc, {})
		if fe != "":
			return fe
		await COMMON.settle(b, 2.0)
	var base_err := _apply(b, sc.get("baseline", []), base_state)
	if base_err != "":
		return "baseline: %s" % base_err
	await COMMON.settle(b, 1.0)
	var dir := String(b.out_dir).path_join("ab_matrix")
	DirAccess.make_dir_recursive_absolute(dir)
	var settle_s := float(sc.get("variant_settle_s", 1.0))
	var path := dir.path_join("%s__baseline.png" % name)
	var grab_err: String = await _grab(b, path)
	if grab_err != "":
		return grab_err
	paths["baseline"] = path
	var failed: Array = []
	for v in sc.get("variants", []):
		var vname := String(v["name"])
		var st := _new_state()
		var ap_err := _apply(b, v.get("ops", []), st)
		if ap_err != "":
			_undo(st)
			failed.append("%s (%s)" % [vname, ap_err])
			continue
		await COMMON.settle(b, settle_s)
		path = dir.path_join("%s__%s.png" % [name, vname])
		grab_err = await _grab(b, path)
		_undo(st)
		if grab_err != "":
			return grab_err
		paths[vname] = path
	if not failed.is_empty():
		entry["stills_failed_variants"] = failed
	return ""
