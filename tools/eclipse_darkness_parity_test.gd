extends SceneTree

## Phase E2 parity gate: outside an eclipse the pitch-black darkness changes nothing. Runs the integration scene at fixed non-eclipse clock
## times (noon, dusk, midnight) once with the current day_side_atmosphere.gd and once with the pre-E2 copy (BASELINE_REVISION) swapped in, and compares the
## published lighting state (environment ambient/fog, sky palette, star visibility, sun and planet light) plus the neutral darkness outputs.
## Headless-safe (the dummy renderer stores no global shader parameters, so only the script-published values are compared).

const WORLD_SCENE: PackedScene = preload("res://scenes/VoxelRingIntegrationTest.tscn")
const BASELINE_PATH := "res://.godot/eclipse_parity/baseline_day_side_atmosphere.gd"
## The last commit before E2 (ce2b360 added the darkness). HEAD stopped being a valid baseline once E2 was committed.
const BASELINE_REVISION := "ce2b360^"
const TOLERANCE := 2.0e-5

var _world: Node = null
var _failures: PackedStringArray = []


func _initialize() -> void:
	_world = WORLD_SCENE.instantiate()
	root.add_child(_world)
	call_deferred("_run")


func _run() -> void:
	for i in 4:
		await process_frame
	var clock := _world.get_node_or_null("RingDayNightCycle")
	var atmosphere := _world.get_node_or_null("DaySideAtmosphere")
	if clock == null or atmosphere == null:
		_finish("scene nodes missing")
		return
	var baseline := _load_baseline()
	if baseline == null:
		_finish("could not read the pre-E2 day_side_atmosphere.gd (git show " + BASELINE_REVISION + ")")
		return
	# Pick three non-eclipse times: keep the day phase of the authored clock, away from any eclipse window.
	var times := [1000.0, 1000.0 + 1125.0, 1000.0 + 2250.0, 1000.0 + 3375.0]
	clock.set("Paused", true)
	var current: Array = []
	for t in times:
		current.append(await _sample(clock, atmosphere, t))
	var saved := _snapshot(atmosphere)
	atmosphere.set_script(baseline)
	_restore(atmosphere, saved)
	if "eclipse_darkness_weight" in atmosphere:
		_finish("baseline script swap did not take effect")
		return
	var old: Array = []
	for t in times:
		old.append(await _sample(clock, atmosphere, t))
	var max_diff := 0.0
	for i in times.size():
		var a: Dictionary = current[i]
		var b: Dictionary = old[i]
		for key in a.keys():
			if not b.has(key):
				continue
			var d := _diff(a[key], b[key])
			max_diff = maxf(max_diff, d)
			if d > TOLERANCE:
				_failures.append("t=%.0f %s differs: new=%s old=%s" % [times[i], key, str(a[key]), str(b[key])])
	if float(current[0].get("eclipse_darkness_weight", 0.0)) != 0.0:
		_failures.append("test times are inside an eclipse (darkness weight > 0); pick other times")
	print("ECLIPSE_DARKNESS_PARITY max_abs_diff=%s poses=%d" % [max_diff, times.size()])
	_finish("" if _failures.is_empty() else "; ".join(_failures))


func _sample(clock: Node, atmosphere: Node, t: float) -> Dictionary:
	clock.call("set_simulation_seconds", t)
	# Blend factors converge exponentially (blend_speed 2.5/s); wait long enough for both runs to be at rest.
	await create_timer(7.0).timeout
	var env: Environment = (_world.get_node("WorldEnvironment") as WorldEnvironment).environment
	var sun := _world.get_node("StarLight") as DirectionalLight3D
	var planet := _world.get_node_or_null("PlanetLight") as DirectionalLight3D
	var sky := atmosphere.get("ring_sky_material") as ShaderMaterial
	var out := {
		"ambient_color": env.ambient_light_color,
		"ambient_energy": env.ambient_light_energy,
		"ambient_sky_contribution": env.ambient_light_sky_contribution,
		"fog_light_color": env.fog_light_color,
		"fog_light_energy": env.fog_light_energy,
		"fog_density": env.fog_density,
		"fog_sky_affect": env.fog_sky_affect,
		"sun_energy": sun.light_energy,
		"sun_color": sun.light_color,
		"day_factor": float(atmosphere.get("current_day_factor")),
		"eclipse_darkness_weight": float(atmosphere.get("eclipse_darkness_weight")) if "eclipse_darkness_weight" in atmosphere else 0.0,
	}
	if planet != null:
		out["planet_energy"] = planet.light_energy
	if sky != null:
		for p in ["sky_top_color", "sky_horizon_color", "star_visibility", "atmosphere_factor"]:
			out["sky_" + p] = sky.get_shader_parameter(p)
	return out


func _diff(a: Variant, b: Variant) -> float:
	if a is Color:
		var ca := a as Color
		var cb := b as Color
		return maxf(maxf(absf(ca.r - cb.r), absf(ca.g - cb.g)), maxf(absf(ca.b - cb.b), absf(ca.a - cb.a)))
	if a is Vector3:
		return (a as Vector3).distance_to(b as Vector3)
	if a is float or a is int:
		return absf(float(a) - float(b))
	return 0.0 if a == b else 1.0


func _snapshot(node: Node) -> Dictionary:
	var values := {}
	for info: Dictionary in node.get_property_list():
		if int(info.get("usage", 0)) & PROPERTY_USAGE_SCRIPT_VARIABLE:
			values[info["name"]] = node.get(info["name"])
	return values


func _restore(node: Node, values: Dictionary) -> void:
	var names := {}
	for info: Dictionary in node.get_property_list():
		if int(info.get("usage", 0)) & PROPERTY_USAGE_SCRIPT_VARIABLE:
			names[info["name"]] = true
	for key in values.keys():
		if names.has(key):
			node.set(key, values[key])


func _load_baseline() -> Script:
	var output: Array = []
	var code := OS.execute("git", ["-C", ProjectSettings.globalize_path("res://"), "show", BASELINE_REVISION + ":scripts/day_side_atmosphere.gd"], output, true)
	if code != 0 or output.is_empty() or String(output[0]).is_empty():
		return null
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path("res://.godot/eclipse_parity"))
	var file := FileAccess.open(BASELINE_PATH, FileAccess.WRITE)
	if file == null:
		return null
	file.store_string(String(output[0]))
	file.close()
	var script := GDScript.new()
	script.source_code = String(output[0])
	if script.reload() != OK:
		return null
	return script


func _finish(message: String) -> void:
	if message.is_empty():
		print("ECLIPSE_DARKNESS_PARITY=PASS")
		quit(0)
	else:
		push_error("ECLIPSE_DARKNESS_PARITY=FAIL " + message)
		quit(1)
