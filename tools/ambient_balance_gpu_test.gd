extends SceneTree

## Ambient balance (ambient lead F1, 2026-11-19): what an ambient-lit surface receives in the booted game, measured with flat cards whose
## shading normal is forced (up, vertical away from the sun, down) 9 m in front of the camera 40 m above hills_eye, read back in linear
## (tonemapper linear, exposure bracket, SSAO / SSR / glow off) and compared with the CPU sky model (RingHazeSkyIrradiance.EvalSky integrated
## over the card normal) and the published ground bounce (DaySideAtmosphere.published_ground_bounce, ring_local_sky.gdshader). Contracts:
##   noon and sun_8_set: clear-weather sky contribution 1; down card within 25 % of the bounce; the Environment ambient energy does not scale the sky part
##   (energy 1 vs the shipped value within 3 %, which tools and the near-fog A_up rely on); with ground_bounce_enabled off the down card loses
##   at least 60 % (noon only: the bounce dominates there).
##   noon only: up card within 30 % of the sky model, vertical card within 30 % of the sky model's side plus half the bounce.
##   sun_8_set: vertical card at least 75 % of half the bounce.
##   night_planet: the published bounce is 0 and the down card is below 15 % of the noon down card.
## Relative contracts only (no goldens), so any machine. GPU, windowed Vulkan; boots the landscape harness world (tools/landscape_baseline_common.gd).
## RUN: tools/run_tests.ps1 -Label <task> -Tests ambient_balance_gpu_test   (prints AMBIENT_BALANCE_GPU=PASS)

const BASE := preload("res://tools/landscape_baseline_common.gd")
const H := preload("res://tools/landscape_harness_common.gd")
const COMMON := H.COMMON
const MARKER := "AMBIENT_BALANCE_GPU"
const POSE := "hills_eye"
const LIFT_M := 40.0
const EXPOSURES := [0.5, 2.5, 10.0, 40.0]
const CARD_SHADER := """
shader_type spatial;
render_mode cull_disabled, depth_draw_opaque;
uniform vec3 n_world = vec3(0.0, 1.0, 0.0);
void fragment() {
	NORMAL = normalize((VIEW_MATRIX * vec4(n_world, 0.0)).xyz);
	ALBEDO = vec3(1.0);
	ROUGHNESS = 1.0;
	METALLIC = 0.0;
	SPECULAR = 0.0;
}
void light() {
}
"""

var ctx := {}
var finished := false
var das: Node
var env: Environment
var failures: Array[String] = []
var noon_down := -1.0


func _init() -> void:
	call_deferred("_run")


func _run() -> void:
	var watchdog := create_timer(900.0)
	watchdog.timeout.connect(func() -> void: _finish("watchdog timeout (900 s)"))
	ctx = await BASE.setup(self, MARKER)
	if ctx.is_empty() or not ctx.has("ok"):
		_finish("harness setup failed")
		return
	das = (ctx["game"] as Node).get_node("DaySideAtmosphere")
	env = (ctx["world_environment"] as WorldEnvironment).environment
	var pose: Dictionary = {}
	for candidate in H.report_poses("t0"):
		if str(candidate["name"]) == POSE:
			pose = (candidate as Dictionary).duplicate()
	if pose.is_empty():
		_finish("pose %s not in the t0 fixture" % POSE)
		return
	pose["lift_m"] = float(pose["lift_m"]) + LIFT_M
	pose["pitch_deg"] = 0.0
	var first := true
	for time_name in ["noon", "sun_8_set", "night_planet"]:
		var info: Dictionary = await H.go_pose(self, ctx["game"], pose, time_name, ctx["ground_model"])
		if info.has("error"):
			_finish("%s: %s" % [time_name, str(info["error"])])
			return
		if first:
			await BASE._disable_auto_exposure(ctx)
			first = false
		await _measure(time_name)
	_finish("")


func _measure(time_name: String) -> void:
	var camera := root.get_viewport().get_camera_3d()
	var sun := ctx["sun"] as DirectionalLight3D
	var up: Vector3 = das.call("_get_surface_up")
	var to_sun := sun.global_basis.z.normalized()
	var horizontal := to_sun - up * to_sun.dot(up)
	if horizontal.length() < 0.05:
		horizontal = camera.global_basis.x - up * camera.global_basis.x.dot(up)
	horizontal = horizontal.normalized()
	var normals := {"up": up, "away": -horizontal, "down": -up}
	var shader := Shader.new()
	shader.code = CARD_SHADER
	var holder := Node3D.new()
	(ctx["game"] as Node).add_child(holder)
	var cards := {}
	var basis := camera.global_basis
	var column := 0
	for n_name in normals:
		var mesh := MeshInstance3D.new()
		var quad := QuadMesh.new()
		quad.size = Vector2(0.6, 0.6)
		mesh.mesh = quad
		mesh.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
		var material := ShaderMaterial.new()
		material.shader = shader
		material.set_shader_parameter("n_world", normals[n_name])
		mesh.material_override = material
		holder.add_child(mesh)
		mesh.global_transform = Transform3D(basis, camera.global_position - basis.z * 9.0 + basis.x * ((column - 1) * 1.2) + basis.y * 0.8)
		cards[n_name] = mesh
		column += 1
	var planet := (ctx["game"] as Node).get_node_or_null("PlanetLight") as Node3D
	var saved := {"ssao": env.ssao_enabled, "ssr": env.ssr_enabled, "glow": env.glow_enabled, "agx": das.get("use_agx_tonemapping"),
		"exposure": das.get("tonemap_exposure"), "planet": planet.visible if planet != null else true}
	env.ssao_enabled = false
	env.ssr_enabled = false
	env.glow_enabled = false
	das.set("use_agx_tonemapping", false)
	if planet != null:
		planet.visible = false
	var shipped := await _read(cards, camera)
	var contribution := env.ambient_light_sky_contribution
	var bounce: Vector3 = das.get("published_ground_bounce")
	var model := _sky_model(up, to_sun, horizontal)
	var energy_saved: float = das.get("day_ambient_energy")
	das.set("day_ambient_energy", 1.0)
	var energy_one := await _read(cards, camera)
	das.set("day_ambient_energy", energy_saved)
	var bounce_off := {}
	if time_name == "noon":
		das.set("ground_bounce_enabled", false)
		bounce_off = await _read(cards, camera)
		das.set("ground_bounce_enabled", true)
	env.ssao_enabled = saved["ssao"]
	env.ssr_enabled = saved["ssr"]
	env.glow_enabled = saved["glow"]
	das.set("use_agx_tonemapping", saved["agx"])
	das.set("tonemap_exposure", saved["exposure"])
	if planet != null:
		planet.visible = saved["planet"]
	holder.queue_free()
	await COMMON.settle(self, 0.5)
	var up_l := _lum(shipped["up"])
	var away_l := _lum(shipped["away"])
	var down_l := _lum(shipped["down"])
	var bounce_l := _lum(bounce)
	print("%s %s sun energy=%.3f colour=%s sin_elevation=%.3f" % [MARKER, time_name, sun.light_energy, str(sun.light_color), up.dot(to_sun)])
	print("%s %s contribution=%.3f bounce=%s cards up=%s away=%s down=%s model up=%.4f side=%.4f energy1 up=%s" % [MARKER, time_name, contribution, str(bounce),
		str(shipped["up"]), str(shipped["away"]), str(shipped["down"]), model["up"], model["away"], str(energy_one["up"])])
	if time_name == "night_planet":
		if bounce_l > 0.0001:
			failures.append("night: published bounce %s is not 0" % str(bounce))
		if noon_down > 0.0 and down_l > noon_down * 0.15:
			failures.append("night: down card %.4f is above 15 %% of noon %.4f" % [down_l, noon_down])
		return
	if time_name == "noon":
		noon_down = down_l
	if not is_equal_approx(contribution, 1.0):
		failures.append("%s: clear-weather sky contribution %.3f, expected 1" % [time_name, contribution])
	_within(down_l, bounce_l, 0.25, "%s down card vs published bounce" % time_name)
	if time_name == "noon":
		# Sky-model parity only at a high sun: the engine's roughest radiance mip is narrower than a cosine lobe (at sun_8_set the up card reads
		# about the zenith radiance, a third of the cosine integral) and the sky shader's giant is not in the CPU model (it lights the anti-sun
		# side at sun_8_set).
		_within(up_l, float(model["up"]), 0.30, "%s up card vs sky model" % time_name)
		_within(away_l, float(model["away"]) + 0.5 * bounce_l, 0.30, "%s vertical card vs sky side + half bounce" % time_name)
	elif away_l < 0.5 * bounce_l * 0.75:
		failures.append("%s: vertical card %.4f is below 75 %% of half the bounce %.4f" % [time_name, away_l, 0.5 * bounce_l])
	_within(_lum(energy_one["up"]), up_l, 0.03, "%s ambient energy must not scale the sky part" % time_name)
	if not bounce_off.is_empty() and _lum(bounce_off["down"]) > down_l * 0.4:
		failures.append("noon: switching the bounce off only took the down card from %.4f to %.4f" % [down_l, _lum(bounce_off["down"])])


# Linear radiance per card from an exposure bracket (RGBA8 sRGB output with the linear tonemapper).
func _read(cards: Dictionary, camera: Camera3D) -> Dictionary:
	var per_exposure := []
	for exposure in EXPOSURES:
		das.set("tonemap_exposure", exposure)
		await COMMON.settle(self, 0.7)
		await RenderingServer.frame_post_draw
		var image := root.get_texture().get_image()
		image.convert(Image.FORMAT_RGBA8)
		var values := {}
		for n_name in cards:
			values[n_name] = _patch(image, Vector2i(camera.unproject_position((cards[n_name] as Node3D).global_position)), 10)
		per_exposure.append(values)
	var result := {}
	for n_name in cards:
		var best := Vector3.ZERO
		for i in EXPOSURES.size():
			var v: Vector3 = per_exposure[i][n_name]
			if maxf(v.x, maxf(v.y, v.z)) < 0.85:
				best = v / float(EXPOSURES[i])
		result[n_name] = best
	return result


# The sky model's cosine-weighted radiance over the upper hemisphere for the up and the away normal (what a white card would receive from the sky).
func _sky_model(up_surface: Vector3, to_sun: Vector3, horizontal: Vector3) -> Dictionary:
	var helper: RefCounted = load("res://scripts/Atmosphere/RingHazeSkyIrradiance.cs").new()
	var frame: Dictionary = das.call("_viewer_atmosphere_frame", up_surface)
	var radius := float(das.call("_get_ring_radius_meters"))
	var half_width := float(das.call("_get_ring_width_meters")) * 0.5
	var across := float(frame["across"])
	var outward: Vector3 = frame["outward"]
	var axis: Vector3 = frame["axis"]
	var height := float(frame["height"])
	var e: Vector3 = (das.call("_haze_sun_irradiance") as Vector3) * (helper.call("GetSunTint") as Vector3)
	var ambient: Vector3 = das.get("_haze_ambient_day_raw")
	var up := -outward.normalized()
	var ax := (axis - up * axis.dot(up)).normalized()
	var tang := up.cross(ax)
	var sums := {"up": 0.0, "away": 0.0}
	var elevations := 12
	var azimuths := 24
	var d_omega := (1.0 / elevations) * (TAU / azimuths)
	for i in elevations:
		var sin_e := (i + 0.5) / elevations
		var cos_e := sqrt(1.0 - sin_e * sin_e)
		for k in azimuths:
			var az := TAU * (k + 0.5) / azimuths
			var dir := up * sin_e + (ax * cos(az) + tang * sin(az)) * cos_e
			var r: Array = helper.call("EvalSky", dir, outward, axis, height, half_width + across, half_width - across, radius, to_sun, e, ambient, 0)
			var radiance := _lum((r[1] as Vector3) + (r[2] as Vector3))
			sums["up"] += radiance * maxf(up.dot(dir), 0.0) * d_omega / PI
			sums["away"] += radiance * maxf((-horizontal).dot(dir), 0.0) * d_omega / PI
	return sums


func _within(value: float, expected: float, tolerance: float, label: String) -> void:
	if expected <= 0.0 or absf(value - expected) > tolerance * expected:
		failures.append("%s: %.4f vs %.4f (tolerance %d %%)" % [label, value, expected, int(tolerance * 100.0)])


func _lum(v: Vector3) -> float:
	return 0.2126 * v.x + 0.7152 * v.y + 0.0722 * v.z


func _srgb_to_linear(c: float) -> float:
	return c / 12.92 if c <= 0.04045 else pow((c + 0.055) / 1.055, 2.4)


func _patch(image: Image, center: Vector2i, half: int) -> Vector3:
	var sum := Vector3.ZERO
	var n := 0
	for y in range(center.y - half, center.y + half):
		for x in range(center.x - half, center.x + half):
			if x < 0 or y < 0 or x >= image.get_width() or y >= image.get_height():
				continue
			var c := image.get_pixel(x, y)
			sum += Vector3(_srgb_to_linear(c.r), _srgb_to_linear(c.g), _srgb_to_linear(c.b))
			n += 1
	return sum / maxf(1.0, float(n))


func _finish(error: String) -> void:
	if finished:
		return
	finished = true
	if error != "":
		failures.append(error)
	if failures.is_empty():
		print("%s=PASS" % MARKER)
	else:
		for failure in failures:
			push_error("%s=FAIL %s" % [MARKER, failure])
	var code := 0 if failures.is_empty() else 1
	quit(code) # quit first: it only takes effect at the end of the frame, so a failing cleanup cannot leave the run hanging
	if not ctx.is_empty():
		BASE.cleanup(ctx)
