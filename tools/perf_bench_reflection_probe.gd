extends RefCounted
## Scenario kind "reflection_probe" of tools/perf_bench.gd (docs/reflections-plan.md 2.5 / B1): A/B stills of what glossy surfaces fall back to when a
## screen-space reflection ray misses. Per scenario 8 stills <out>/reflection_probe/<name>_<bg|sky>_<ssr|nossr>[_spheres].png: reflected_light_source
## as found or forced to the sky, engine SSR forced on or off (WetWorld.SsrPolicyWeightOverride 1.0 / 0.0), with and without two metallic test spheres.
## Optional scenario key "force_wet": [film, soak, standing, rain] (WetWorld.ForceUniform). Everything is restored at the end, also on an error path.

const COMMON := preload("res://tools/shadow_harness_common.gd")
const TVW := preload("res://tools/tree_visibility_world.gd")

const HALF_SIZE_SETTING := "rendering/environment/screen_space_reflection/half_size"


## Returns the result entry for report["reflection_probes"]. `b` is the perf_bench.gd SceneTree script (its helpers and state are reused).
static func run(b: Object, sc: Dictionary) -> Dictionary:
	var name := String(sc["name"])
	var pose: Dictionary = b.spec["poses"][String(sc["pose"])]
	var result := {"name": name, "kind": "reflection_probe", "pose_name": String(sc["pose"]), "pose": pose, "setup": {}, "config": {}, "stills": []}
	b._log("scenario %s (reflection_probe)" % name)
	b._release_keys()
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	var wet: Node = b.game.get_node_or_null("WetWorld")
	var env: Environment = b._ssr_env()
	var spheres: Array[MeshInstance3D] = []
	var found_source := -1
	var found_ssr := false
	var has_override := wet != null and TVW.has_property(wet, "SsrPolicyWeightOverride")
	if env != null:
		found_source = int(env.reflected_light_source)
		found_ssr = env.ssr_enabled
	await _body(b, sc, result, name, pose, wet, env, has_override, spheres, found_source, found_ssr)
	# Restore, whatever happened above.
	if env != null:
		if found_source >= 0:
			env.reflected_light_source = found_source as Environment.ReflectionSource
		if not has_override:
			env.ssr_enabled = found_ssr  # the fallback path wrote it directly; with the override the WetWorld policy owns ssr_enabled
	if has_override:
		# Pass through weight 1 so the policy restores its exact baseline (enabled, fade in, fade out) before the gate takes over again.
		wet.set("SsrPolicyWeightOverride", 1.0)
		for _i in 3:
			await b.process_frame
		wet.set("SsrPolicyWeightOverride", -1.0)
	for s in spheres:
		if is_instance_valid(s):
			s.queue_free()
	if result["setup"].has("force_wet") and wet != null and wet.has_method("ClearForce"):
		wet.call("ClearForce")
	b._log("reflection_probe %s: %d stills%s" % [name, result["stills"].size(), (", ERROR " + String(result["error"])) if result.has("error") else ""])
	return result


static func _config(env: Environment, wet: Node) -> Dictionary:
	var cfg := {}
	if env != null:
		cfg["background_mode"] = int(env.background_mode)
		cfg["reflected_light_source"] = int(env.reflected_light_source)
		cfg["ambient_light_source"] = int(env.ambient_light_source)
		cfg["ssr_enabled"] = env.ssr_enabled
		cfg["ssr_max_steps"] = env.ssr_max_steps
		cfg["ssr_fade_in"] = env.ssr_fade_in
		cfg["ssr_fade_out"] = env.ssr_fade_out
		cfg["ssr_depth_tolerance"] = env.ssr_depth_tolerance
		cfg["sky"] = env.sky != null
		if env.sky != null:
			cfg["sky_process_mode"] = int(env.sky.process_mode)
			cfg["sky_radiance_size"] = int(env.sky.radiance_size)
	cfg["ssr_half_size_setting"] = ProjectSettings.get_setting(HALF_SIZE_SETTING, null)
	if wet != null:
		cfg["wet_ssr_policy_weight"] = wet.get("SsrPolicyWeight")
		cfg["wet_ssr_half_size_active"] = wet.get("SsrHalfSizeActive")
	return cfg


static func _body(b: Object, sc: Dictionary, result: Dictionary, name: String, pose: Dictionary, wet: Node, env: Environment, has_override: bool,
		spheres: Array[MeshInstance3D], found_source: int, found_ssr: bool) -> void:
	if env == null:
		result["error"] = "no main Environment"
		return
	var began := Time.get_ticks_msec()
	var place_error: String = await b._place(pose, sc, 0.0)
	if place_error != "":
		result["error"] = "placement: %s" % place_error
		return
	await COMMON.settle(b, 0.4)
	var idle: bool = await COMMON.wait_builds_idle(b, b.game, 180.0)
	var time_info: Dictionary = b._apply_time(b._opt(sc, "time"), true)
	if time_info.has("error"):
		result["error"] = "time of day: %s" % String(time_info["error"])
		return
	await COMMON.settle(b, float(b._opt(sc, "settle_s")))
	idle = idle and await COMMON.wait_builds_idle(b, b.game, 90.0)
	if not b.quiet_done:
		b.report["ssr_quiet"] = TVW.quiet_scene(b.game)
		TVW.hide_overlays(b.game)
		b.quiet_done = true
	TVW.freeze_world(b.game)
	b.report["still_noise"] = TVW.freeze_noise(b, b.game, PackedStringArray(["clouds", "taa"]))
	result["setup"] = {"idle": idle, "settle_ms": Time.get_ticks_msec() - began, "reached": COMMON.pose_now(b.game), "time": time_info}
	result["config"] = _config(env, wet)  # before anything below changes
	if sc.has("force_wet"):
		var fw: Array = sc["force_wet"]
		if wet == null or not wet.has_method("ForceUniform") or not wet.has_method("ClearForce") or fw.size() != 4:
			result["error"] = "force_wet: WetWorld node or ForceUniform/ClearForce missing (or force_wet is not 4 numbers)"
			return
		wet.call("ForceUniform", float(fw[0]), float(fw[1]), float(fw[2]), float(fw[3]))
		result["setup"]["force_wet"] = fw
		await COMMON.settle(b, 2.0)
	result["setup"]["ssr_override_property"] = has_override
	# Test spheres, placed from the main camera.
	var cam: Camera3D = b.root.get_viewport().get_camera_3d()
	if cam == null:
		result["error"] = "no main camera"
		return
	var xf: Transform3D = cam.global_transform
	var fwd: Vector3 = -xf.basis.z
	var right: Vector3 = xf.basis.x
	var up: Vector3 = xf.basis.y
	for side in [-1.0, 1.0]:
		var mi := MeshInstance3D.new()
		var mesh := SphereMesh.new()
		mesh.radius = 0.35
		mesh.height = 0.7
		mi.mesh = mesh
		mi.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
		var mat := StandardMaterial3D.new()
		mat.albedo_color = Color.WHITE
		mat.metallic = 1.0
		mat.roughness = 0.0 if side < 0.0 else 0.35
		mi.material_override = mat
		mi.top_level = true
		mi.visible = false
		b.game.add_child(mi)
		mi.global_position = xf.origin + fwd * 2.5 - up * 0.30 + right * 0.45 * side
		spheres.append(mi)
	var dir := String(b.out_dir).path_join("reflection_probe")
	DirAccess.make_dir_recursive_absolute(dir)
	for refl in ["bg", "sky"]:
		env.reflected_light_source = (found_source as Environment.ReflectionSource) if refl == "bg" else Environment.REFLECTION_SOURCE_SKY
		for ssr in ["ssr", "nossr"]:
			if has_override:
				wet.set("SsrPolicyWeightOverride", 1.0 if ssr == "ssr" else 0.0)
			else:
				env.ssr_enabled = ssr == "ssr"
			for with_spheres in [false, true]:
				for s in spheres:
					s.visible = with_spheres
				for _i in 20:
					TVW.hold_wind()
					await b.process_frame
				await RenderingServer.frame_post_draw
				var img: Image = TVW.grab(b)
				var path := dir.path_join("%s_%s_%s%s.png" % [name, refl, ssr, "_spheres" if with_spheres else ""])
				if img.save_png(path) != OK:
					result["error"] = "save_png failed (%s)" % path
				result["stills"].append(path)
