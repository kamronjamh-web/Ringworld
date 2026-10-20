extends SceneTree

## Shadow revamp S6 - screen-space contact shadow GPU test (docs/shadow-revamp-plan.md S6).
## Synthetic scene (SubViewport with its own world): ground plane + thin slab, a receiver material that outputs ring_contact_visibility() as the
## light() diffuse (white albedo, no ambient, so the rendered pixel IS the contact visibility), the real RingContactShadows node + compositor effect.
## Gates (all printed as CONTACT_SHADOW lines, final CONTACT_SHADOW=PASS/FAIL):
##   * slab at sun elevation 30 / 60 degrees, tall (length limited) and short (height limited): measured shadow extent on the ground (image and texture readback)
##     within 10% (or one march step) of min(L cos e, h / tan e); everything else lit (acne check); sky pixels and a sky-only pose give an all-1 texture
##   * native, FSR2 0.67, bilinear 0.77, MSAA 4x: texture size == internal size (floor(size * scale)) and the same length gate
##   * grass probes: alpha-scissor card and depth_draw_opaque + discard card write prepass depth (shadow behind the opaque half only)
##   * isolation: a second viewport sharing the main world (minimap) is skipped by the effect and renders fully lit; a far-style viewport (own world, same size,
##     camera far 100000) renders fully lit; toggling the feature off restores the unshadowed image
##   * static FSR2 camera: temporal luminance std of the shadow region with contact on <= off + 0.004
## Run (windowed Vulkan, not --headless; no other Godot process):  godot --path . --script res://tools/contact_shadow_gpu_test.gd
## Options: -- --only=<phase,...> (lengths,modes,grass,isolation,flicker,sky) --out=<dir> --keep-going

const RES := Vector2i(1280, 720)
const SETTLE_FRAMES := 14
const CAMERA_POS := Vector3(0, 10, 18)
const CAMERA_TARGET := Vector3(0, 0.3, 0)
const LENGTH := 1.5
const THICKNESS := 0.25
const STEPS := 16
const RECEIVER_SHADER := """
shader_type spatial;
render_mode cull_disabled;
#include "res://materials/ring_shadow_compose.gdshaderinc"
void fragment() {
	ALBEDO = vec3(1.0);
	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
}
void light() {
	DIFFUSE_LIGHT = vec3(ring_contact_visibility(FRAGCOORD.xy, VIEWPORT_SIZE, INV_PROJECTION_MATRIX, 1.0, RING_SHADOW_DEFAULT));
}
"""
const SCISSOR_SHADER := """
shader_type spatial;
render_mode cull_disabled, unshaded;
void fragment() {
	ALBEDO = vec3(1.0, 0.0, 0.0);
	ALPHA = UV.x < 0.5 ? 1.0 : 0.0;
	ALPHA_SCISSOR_THRESHOLD = 0.5;
}
"""
const DISCARD_SHADER := """
shader_type spatial;
render_mode cull_disabled, depth_draw_opaque, unshaded;
void fragment() {
	if (UV.x >= 0.5) discard;
	ALBEDO = vec3(1.0, 0.0, 0.0);
}
"""

var failed := false
var checks := 0
var failures: PackedStringArray = []
var only: PackedStringArray = []
var out_dir := "res://.godot/contact_shadow_test"
var keep_going := true
var start_off := false
var vp: SubViewport
var cam: Camera3D
var sun: DirectionalLight3D
var contact: Node
var ground: MeshInstance3D
var slab: MeshInstance3D
var receiver: ShaderMaterial
var world_env: WorldEnvironment
var rd: RenderingDevice
var extra_viewports: Array[SubViewport] = []


func _init() -> void:
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--only="):
			only = argument.substr(7).split(",")
		elif argument.begins_with("--out="):
			out_dir = argument.substr(6)
		elif argument == "--off":
			start_off = true
	call_deferred("_run")
	create_timer(900.0).timeout.connect(func() -> void: _fatal("timeout"))


func _wants(phase: String) -> bool:
	return only.is_empty() or phase in only


func _check(name: String, ok: bool, detail: String = "") -> void:
	checks += 1
	print("CONTACT_SHADOW %s %s %s" % ["ok  " if ok else "FAIL", name, detail])
	if not ok:
		failures.append(name)


func _fatal(message: String) -> void:
	if failed:
		return
	failed = true
	push_error("CONTACT_SHADOW=FAIL " + message)
	call_deferred("quit", 1)


func _frames(count: int) -> void:
	for i in count:
		await RenderingServer.frame_post_draw


func _run() -> void:
	rd = RenderingServer.get_rendering_device()
	if rd == null:
		_fatal("requires Vulkan/Forward+ (do not run headless)")
		return
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(out_dir))
	if not _build_scene():
		return
	await _frames(SETTLE_FRAMES)
	if _wants("lengths"):
		await _phase_lengths()
	if _wants("sky"):
		await _phase_sky()
	if _wants("modes"):
		await _phase_modes()
	if _wants("grass"):
		await _phase_grass()
	if _wants("isolation"):
		await _phase_isolation()
	if _wants("flicker"):
		await _phase_flicker()
	print("CONTACT_SHADOW checks=%d failures=%d" % [checks, failures.size()])
	if failures.is_empty() and not failed:
		print("CONTACT_SHADOW=PASS")
		_cleanup_and_quit(0)
	else:
		push_error("CONTACT_SHADOW=FAIL " + ", ".join(failures))
		_cleanup_and_quit(1)


func _cleanup_and_quit(code: int) -> void:
	if is_instance_valid(vp):
		vp.queue_free()
	for extra in extra_viewports:
		if is_instance_valid(extra):
			extra.queue_free()
	await _frames(4)
	quit(code)


# ---------------------------------------------------------------- scene

func _environment() -> Environment:
	var environment := Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color.BLACK
	environment.tonemap_mode = Environment.TONE_MAPPER_LINEAR
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_DISABLED
	environment.reflected_light_source = Environment.REFLECTION_SOURCE_DISABLED
	environment.ssao_enabled = false
	environment.glow_enabled = false
	return environment


func _orient(light: DirectionalLight3D, to_light: Vector3) -> void:
	var direction := to_light.normalized()
	var up_hint := Vector3.RIGHT if absf(direction.dot(Vector3.UP)) > 0.95 else Vector3.UP
	light.transform = Transform3D(Basis.looking_at(-direction, up_hint), Vector3.ZERO)


func _to_sun(elevation_degrees: float, azimuth_degrees: float = 0.0) -> Vector3:
	var e := deg_to_rad(elevation_degrees)
	var a := deg_to_rad(azimuth_degrees)
	return Vector3(-cos(e) * cos(a), sin(e), -cos(e) * sin(a))


func _make_world_content(parent: Node, with_contact_node: bool) -> Dictionary:
	var content := {}
	var environment_node := WorldEnvironment.new()
	environment_node.environment = _environment()
	parent.add_child(environment_node)
	var camera := Camera3D.new()
	camera.position = CAMERA_POS
	camera.fov = 50.0
	camera.near = 0.05
	camera.far = 400.0
	camera.current = true
	parent.add_child(camera)
	camera.look_at(CAMERA_TARGET, Vector3.UP)
	var light := DirectionalLight3D.new()
	light.name = "Sun"
	light.light_energy = 1.0
	light.shadow_enabled = true
	light.directional_shadow_max_distance = 60.0
	parent.add_child(light)
	_orient(light, _to_sun(30.0))
	var ground_mesh := MeshInstance3D.new()
	var plane := PlaneMesh.new()
	plane.size = Vector2(200, 200)
	ground_mesh.mesh = plane
	ground_mesh.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	parent.add_child(ground_mesh)
	var slab_mesh := MeshInstance3D.new()
	var box := BoxMesh.new()
	box.size = Vector3(1.0, 2.0, 0.2)
	slab_mesh.mesh = box
	slab_mesh.position = Vector3(0, 1.0, 0)
	slab_mesh.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	parent.add_child(slab_mesh)
	content["env"] = environment_node
	content["camera"] = camera
	content["sun"] = light
	content["ground"] = ground_mesh
	content["slab"] = slab_mesh
	if with_contact_node:
		var script: Script = load("res://scripts/Shadows/RingContactShadows.cs")
		var node: Node = script.new() if script != null else null
		if node == null:
			_fatal("RingContactShadows.cs not loadable (build the C# project first)")
			return content
		node.set("SunLightPath", NodePath("../Sun"))
		node.set("GraphicsPath", NodePath("../NoGraphics"))
		parent.add_child(node)
		content["contact"] = node
	return content


func _build_scene() -> bool:
	var shader := Shader.new()
	shader.code = RECEIVER_SHADER
	receiver = ShaderMaterial.new()
	receiver.shader = shader
	vp = SubViewport.new()
	vp.own_world_3d = true
	vp.size = RES
	vp.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(vp)
	var content := _make_world_content(vp, true)
	if failed:
		return false
	cam = content["camera"]
	sun = content["sun"]
	ground = content["ground"]
	slab = content["slab"]
	contact = content["contact"]
	world_env = content["env"]
	ground.material_override = receiver
	slab.material_override = receiver
	_configure(not start_off)
	return true


func _configure(enabled: bool, length: float = LENGTH, thickness: float = THICKNESS, strength: float = 1.0, fade: float = 150.0, steps: int = STEPS) -> void:
	contact.call("ConfigureForTest", enabled, length, thickness, strength, fade, 0.35, steps)


func _set_slab(height: float) -> void:
	(slab.mesh as BoxMesh).size = Vector3(1.0, height, 0.2)
	slab.position = Vector3(0, height * 0.5, 0)


# ---------------------------------------------------------------- measurement

func _decode(byte_value: int) -> float:
	var v := float(byte_value) / 255.0
	return v / 12.92 if v <= 0.04045 else pow((v + 0.055) / 1.055, 2.4)


func _ground_rect(camera: Camera3D, view_size: Vector2i) -> Rect2i:
	var low := Vector2(1e9, 1e9)
	var high := Vector2(-1e9, -1e9)
	for corner in [Vector3(-5, 0, -1), Vector3(7, 0, -1), Vector3(-5, 0, 7), Vector3(7, 0, 7)]:
		var p := camera.unproject_position(corner)
		low = low.min(p)
		high = high.max(p)
	var x0 := clampi(int(low.x) - 2, 0, view_size.x - 1)
	var y0 := clampi(int(low.y) - 2, 0, view_size.y - 1)
	var x1 := clampi(int(high.x) + 2, 0, view_size.x - 1)
	var y1 := clampi(int(high.y) + 2, 0, view_size.y - 1)
	return Rect2i(x0, y0, x1 - x0 + 1, y1 - y0 + 1)


## visibility_of(px, py) -> float for output pixels; returns {extent, lit_fraction, lit_min, shadow_min, sun_side_min, samples}.
func _measure(camera: Camera3D, view_size: Vector2i, sample: Callable) -> Dictionary:
	var rect := _ground_rect(camera, view_size)
	var extent := 0.0
	var core_extent := 0.0
	var rest_min := 1.0
	var rest_count := 0
	var rest_dark := 0
	var sun_side_min := 1.0
	var band_samples := 0
	for py in range(rect.position.y, rect.end.y):
		for px in range(rect.position.x, rect.end.x):
			var screen := Vector2(px + 0.5, py + 0.5)
			var origin := camera.project_ray_origin(screen)
			var direction := camera.project_ray_normal(screen)
			if direction.y >= -0.0001:
				continue
			var t := -origin.y / direction.y
			var point := origin + direction * t
			var v: float = sample.call(px, py)
			if absf(point.z) < 0.07 and point.x > 0.55 and point.x < 4.0:
				band_samples += 1
				if v < 0.99:
					extent = maxf(extent, point.x - 0.5)
				if v < 0.5:
					core_extent = maxf(core_extent, point.x - 0.5)
			elif absf(point.z) < 0.07 and point.x < -0.6 and point.x > -4.0:
				sun_side_min = minf(sun_side_min, v)
			elif point.z > 0.3 and point.z < 6.0 and absf(point.x) < 6.0:
				rest_count += 1
				rest_min = minf(rest_min, v)
				if v < 0.99:
					rest_dark += 1
	return {"extent": extent, "core_extent": core_extent, "rest_min": rest_min, "rest_dark": rest_dark, "rest_count": rest_count, "sun_side_min": sun_side_min, "band_samples": band_samples}


func _image_sampler(image: Image) -> Callable:
	return func(px: int, py: int) -> float: return _decode(image.get_pixel(px, py).r8)


func _read_contact() -> Dictionary:
	var effect: Object = contact.get("Effect")
	var out := {"ok": false}
	if effect == null:
		return out
	var rid: RID = effect.get("image")
	var size: Vector2i = effect.call("get_reported_size")
	if not rid.is_valid() or size.x <= 0:
		return out
	var bytes := rd.texture_get_data(rid, 0)
	var format: int = effect.call("get_texture_format")
	out = {"ok": true, "size": size, "bytes": bytes, "format": format}
	return out


func _texture_sampler(texture: Dictionary, view_size: Vector2i) -> Callable:
	var size: Vector2i = texture["size"]
	var bytes: PackedByteArray = texture["bytes"]
	var wide: bool = texture["format"] != RenderingDevice.DATA_FORMAT_R8_UNORM
	return func(px: int, py: int) -> float:
		var ix := clampi(int((px + 0.5) / view_size.x * size.x), 0, size.x - 1)
		var iy := clampi(int((py + 0.5) / view_size.y * size.y), 0, size.y - 1)
		if wide:
			return bytes.decode_half((iy * size.x + ix) * 2)
		return float(bytes[iy * size.x + ix]) / 255.0


func _expected_extent(elevation: float, height: float) -> float:
	var e := deg_to_rad(elevation)
	return minf(LENGTH * cos(e), height / tan(e))


func _grab() -> Image:
	# FSR2 accumulates history over many frames: after a sun / slab change let it converge before reading the image.
	await _frames(70 if vp.scaling_3d_mode == Viewport.SCALING_3D_MODE_FSR2 else SETTLE_FRAMES)
	var image := vp.get_texture().get_image()
	image.convert(Image.FORMAT_RGBA8)
	return image


func _length_case(label: String, elevation: float, height: float, save: bool = true) -> Dictionary:
	_set_slab(height)
	_orient(sun, _to_sun(elevation))
	var image := await _grab()
	var texture := _read_contact()
	var expected := _expected_extent(elevation, height)
	var step_h := LENGTH * cos(deg_to_rad(elevation)) / STEPS
	var tolerance := maxf(0.10 * expected, step_h * 1.5)
	# Upscaled output blurs the edge by about two internal pixels (one internal pixel is roughly 0.026 m on the ground at this camera).
	if vp.scaling_3d_mode != Viewport.SCALING_3D_MODE_BILINEAR or vp.scaling_3d_scale < 0.999:
		tolerance += 2.0 * 0.026 / vp.scaling_3d_scale
	var from_image := _measure(cam, RES, _image_sampler(image))
	var detail := "expected=%.3f image_extent=%.3f (core %.3f) tol=%.3f rest_min=%.3f rest_dark=%d/%d sun_side_min=%.3f" % [
		expected, from_image.extent, from_image.core_extent, tolerance, from_image.rest_min, from_image.rest_dark, from_image.rest_count, from_image.sun_side_min]
	if texture["ok"]:
		var from_texture := _measure(cam, RES, _texture_sampler(texture, RES))
		detail += " texture_extent=%.3f tex_rest_dark=%d" % [from_texture.extent, from_texture.rest_dark]
		_check(label + " texture length", absf(from_texture.extent - expected) <= tolerance, detail)
		_check(label + " texture rest lit", from_texture.rest_dark == 0 and from_texture.sun_side_min >= 0.99, "")
	else:
		_check(label + " texture readable", false, "no contact texture")
	_check(label + " image length", absf(from_image.extent - expected) <= tolerance, detail)
	_check(label + " image rest lit", from_image.rest_dark == 0 and from_image.sun_side_min >= 0.99, "")
	if save:
		image.save_png(ProjectSettings.globalize_path(out_dir.path_join(label.replace(" ", "_") + ".png")))
	return from_image


# ---------------------------------------------------------------- phases

func _phase_lengths() -> void:
	print("CONTACT_SHADOW -- phase lengths (native)")
	_configure(true)
	await _length_case("native e30 tall", 30.0, 2.0)
	await _length_case("native e30 short", 30.0, 0.3)
	await _length_case("native e60 tall", 60.0, 2.0)
	await _length_case("native e60 short", 60.0, 0.4)
	var size: Vector2i = contact.get("ReportedSize")
	_check("native texture size", size == RES, "reported=%s expected=%s" % [size, RES])
	var params: Vector4 = contact.get("PublishedParams")
	var key: Vector4 = contact.get("PublishedKey")
	_check("globals published", params.x == 1.0 and Vector2i(int(key.x), int(key.y)) == RES, "params=%s key=%s" % [params, key])


func _phase_sky() -> void:
	print("CONTACT_SHADOW -- phase sky")
	_configure(true)
	cam.look_at_from_position(CAMERA_POS, CAMERA_POS + Vector3(0, 1, -0.2), Vector3.UP)
	await _frames(SETTLE_FRAMES)
	var texture := _read_contact()
	var low := 255
	if texture["ok"]:
		var bytes: PackedByteArray = texture["bytes"]
		for b in bytes:
			low = mini(low, b)
	_check("sky-only texture all 1", texture["ok"] and low == 255, "min byte=%d" % low)
	cam.position = CAMERA_POS
	cam.look_at(CAMERA_TARGET, Vector3.UP)
	await _frames(SETTLE_FRAMES)


func _set_mode(mode: int, scale: float, msaa: int) -> void:
	vp.scaling_3d_mode = mode
	vp.scaling_3d_scale = scale
	vp.msaa_3d = msaa
	await _frames(SETTLE_FRAMES)


func _phase_modes() -> void:
	var modes := [
		{"name": "fsr2_067", "mode": Viewport.SCALING_3D_MODE_FSR2, "scale": 0.67, "msaa": Viewport.MSAA_DISABLED},
		{"name": "bilinear_077", "mode": Viewport.SCALING_3D_MODE_BILINEAR, "scale": 0.77, "msaa": Viewport.MSAA_DISABLED},
		{"name": "msaa4x", "mode": Viewport.SCALING_3D_MODE_BILINEAR, "scale": 1.0, "msaa": Viewport.MSAA_4X},
	]
	for entry in modes:
		print("CONTACT_SHADOW -- phase modes: " + entry.name)
		_configure(true)
		await _set_mode(entry.mode, entry.scale, entry.msaa)
		var expected_size := Vector2i(int(RES.x * entry.scale), int(RES.y * entry.scale))
		var size: Vector2i = contact.get("ReportedSize")
		_check(entry.name + " texture size == internal size", size == expected_size, "reported=%s expected=%s" % [size, expected_size])
		var key: Vector4 = contact.get("PublishedKey")
		_check(entry.name + " key == texture size", Vector2i(int(key.x), int(key.y)) == size, "key=%s" % key)
		await _length_case(entry.name + " e30 tall", 30.0, 2.0)
		await _length_case(entry.name + " e30 short", 30.0, 0.3)
		await _length_case(entry.name + " e60 tall", 60.0, 2.0)
		await _length_case(entry.name + " e60 short", 60.0, 0.4)
	await _set_mode(Viewport.SCALING_3D_MODE_BILINEAR, 1.0, Viewport.MSAA_DISABLED)


func _card_material(code: String) -> ShaderMaterial:
	var shader := Shader.new()
	shader.code = code
	var material := ShaderMaterial.new()
	material.shader = shader
	return material


func _phase_grass() -> void:
	print("CONTACT_SHADOW -- phase grass (prepass depth of scissor / discard cards)")
	_configure(true)
	var previous := slab.visible
	slab.visible = false
	var card := MeshInstance3D.new()
	var quad := QuadMesh.new()
	quad.size = Vector2(4.0, 1.4)
	card.mesh = quad
	card.position = Vector3(0, 0.7, 0)
	card.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	vp.add_child(card)
	_orient(sun, _to_sun(45.0, 40.0))
	for entry in [{"name": "alpha_scissor", "code": SCISSOR_SHADER}, {"name": "depth_draw_opaque_discard", "code": DISCARD_SHADER}]:
		card.material_override = _card_material(entry.code)
		var image := await _grab()
		var texture := _read_contact()
		var samplers := {"image": _image_sampler(image)}
		if texture["ok"]:
			samplers["texture"] = _texture_sampler(texture, RES)
		for source in samplers:
			var sampler: Callable = samplers[source]
			var shadow_sum := 0.0
			var shadow_count := 0
			var lit_sum := 0.0
			var lit_count := 0
			var rect := _ground_rect(cam, RES)
			for py in range(rect.position.y, rect.end.y):
				for px in range(rect.position.x, rect.end.x):
					var screen := Vector2(px + 0.5, py + 0.5)
					var origin := cam.project_ray_origin(screen)
					var direction := cam.project_ray_normal(screen)
					if direction.y >= -0.0001:
						continue
					var point := origin + direction * (-origin.y / direction.y)
					if point.z < 0.05 or point.z > 0.30:
						continue
					var v: float = sampler.call(px, py)
					if point.x > -1.5 and point.x < -0.7:
						shadow_sum += v
						shadow_count += 1
					elif point.x > 1.0 and point.x < 2.0:
						lit_sum += v
						lit_count += 1
			var shadow_mean := shadow_sum / maxf(shadow_count, 1.0)
			var lit_mean := lit_sum / maxf(lit_count, 1.0)
			_check("grass %s %s: opaque half shadows, discarded half lit" % [entry.name, source], shadow_count > 15 and lit_count > 15 and shadow_mean < 0.3 and lit_mean > 0.95,
				"shadow_mean=%.3f (n=%d) lit_mean=%.3f (n=%d)" % [shadow_mean, shadow_count, lit_mean, lit_count])
		image.save_png(ProjectSettings.globalize_path(out_dir.path_join("grass_%s.png" % entry.name)))
	card.queue_free()
	slab.visible = previous
	_orient(sun, _to_sun(30.0))
	await _frames(SETTLE_FRAMES)


func _phase_isolation() -> void:
	print("CONTACT_SHADOW -- phase isolation")
	_configure(not start_off)
	_set_slab(2.0)
	_orient(sun, _to_sun(30.0))
	# Minimap-like viewport sharing the main world: different size, own camera looking down at the slab.
	var minimap := SubViewport.new()
	minimap.world_3d = vp.find_world_3d()
	minimap.size = Vector2i(320, 320)
	minimap.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	var minimap_camera := Camera3D.new()
	minimap_camera.position = Vector3(0, 12, 6)
	minimap_camera.fov = 40.0
	minimap_camera.far = 400.0
	minimap_camera.current = true
	minimap.add_child(minimap_camera)
	root.add_child(minimap)
	extra_viewports.append(minimap)
	minimap_camera.look_at(Vector3(1.0, 0, 0), Vector3.UP)
	print("CONTACT_SHADOW minimap camera current=%s transform=%s" % [minimap_camera.is_current(), minimap_camera.global_transform])
	# Far-style viewport: own world, same size as main, camera far 100000, same scene content and receiver material.
	var far_viewport := SubViewport.new()
	far_viewport.own_world_3d = true
	far_viewport.size = RES
	far_viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(far_viewport)
	extra_viewports.append(far_viewport)
	var far_content := _make_world_content(far_viewport, false)
	var far_camera: Camera3D = far_content["camera"]
	far_camera.far = 100000.0
	far_content["ground"].material_override = receiver
	far_content["slab"].material_override = receiver
	_orient(far_content["sun"], _to_sun(30.0))
	(far_content["slab"].mesh as BoxMesh).size = Vector3(1.0, 2.0, 0.2)
	await _frames(SETTLE_FRAMES + 4)
	var main_image := await _grab()
	var main_measure := _measure(cam, RES, _image_sampler(main_image))
	var expected := _expected_extent(30.0, 2.0)
	_check("isolation: main still shadowed with minimap + far viewports active", absf(main_measure.extent - expected) <= 0.15 * expected, "extent=%.3f expected=%.3f" % [main_measure.extent, expected])
	var size: Vector2i = contact.get("ReportedSize")
	_check("isolation: texture size unchanged (minimap skipped)", size == RES, "reported=%s" % size)
	var effect: Object = contact.get("Effect")
	var skipped: int = effect.get("skipped_views") if effect != null else 0
	_check("isolation: effect skipped other-view callbacks", skipped > 0, "skipped_views=%d" % skipped)
	var minimap_image := minimap.get_texture().get_image()
	minimap_image.convert(Image.FORMAT_RGBA8)
	var minimap_low := 255
	var minimap_bright := 0
	var minimap_high := 0
	for py in range(0, minimap_image.get_height()):
		for px in range(0, minimap_image.get_width()):
			var r := minimap_image.get_pixel(px, py).r8
			minimap_high = maxi(minimap_high, r)
			if r > 0:
				minimap_bright += 1
				minimap_low = mini(minimap_low, r)
	print("CONTACT_SHADOW minimap max byte=%d" % minimap_high)
	_check("isolation: minimap fully lit", minimap_bright > 1000 and minimap_low >= 253, "lit pixels=%d min byte=%d" % [minimap_bright, minimap_low])
	minimap_image.save_png(ProjectSettings.globalize_path(out_dir.path_join("isolation_minimap.png")))
	var far_image := far_viewport.get_texture().get_image()
	far_image.convert(Image.FORMAT_RGBA8)
	var far_measure := _measure(far_camera, RES, _image_sampler(far_image))
	_check("isolation: far-style viewport (far 100000) has no contact darkening", far_measure.extent < 0.05 and far_measure.rest_dark == 0 and far_measure.sun_side_min >= 0.99,
		"extent=%.3f rest_dark=%d" % [far_measure.extent, far_measure.rest_dark])
	far_image.save_png(ProjectSettings.globalize_path(out_dir.path_join("isolation_far.png")))
	# Toggle off restores the unshadowed image.
	_configure(false)
	var off_image := await _grab()
	var off_measure := _measure(cam, RES, _image_sampler(off_image))
	_check("isolation: toggle off -> no darkening", off_measure.extent < 0.05 and off_measure.rest_dark == 0, "extent=%.3f" % off_measure.extent)
	var off_params: Vector4 = contact.get("PublishedParams")
	_check("isolation: toggle off -> params zero", off_params == Vector4(0, 0, 0, 0), "params=%s" % off_params)
	_configure(true)
	await _frames(SETTLE_FRAMES)
	var back_measure := _measure(cam, RES, _image_sampler(await _grab()))
	_check("isolation: toggle back on", absf(back_measure.extent - expected) <= 0.15 * expected, "extent=%.3f" % back_measure.extent)
	for extra in extra_viewports:
		extra.queue_free()
	extra_viewports.clear()
	await _frames(4)


func _luma_region(image: Image, rect: Rect2i) -> PackedFloat32Array:
	var values := PackedFloat32Array()
	values.resize(rect.size.x * rect.size.y)
	var index := 0
	for py in range(rect.position.y, rect.end.y):
		for px in range(rect.position.x, rect.end.x):
			values[index] = _decode(image.get_pixel(px, py).r8)
			index += 1
	return values


func _temporal_std(contact_enabled: bool) -> float:
	_configure(contact_enabled)
	_set_slab(2.0)
	_orient(sun, _to_sun(30.0))
	await _frames(40)
	var rect := Rect2i(Vector2i(cam.unproject_position(Vector3(0.4, 0, 0.1))) - Vector2i(10, 60), Vector2i(150, 90))
	rect = rect.intersection(Rect2i(Vector2i.ZERO, RES))
	var frames: Array[PackedFloat32Array] = []
	for i in 24:
		await RenderingServer.frame_post_draw
		var image := vp.get_texture().get_image()
		image.convert(Image.FORMAT_RGBA8)
		frames.append(_luma_region(image, rect))
	var count := frames[0].size()
	var total := 0.0
	for index in count:
		var mean := 0.0
		for frame in frames:
			mean += frame[index]
		mean /= frames.size()
		var variance := 0.0
		for frame in frames:
			variance += (frame[index] - mean) * (frame[index] - mean)
		total += sqrt(variance / frames.size())
	return total / count


func _phase_flicker() -> void:
	print("CONTACT_SHADOW -- phase flicker (static FSR2 camera)")
	await _set_mode(Viewport.SCALING_3D_MODE_FSR2, 0.67, Viewport.MSAA_DISABLED)
	var off_std: float = await _temporal_std(false)
	var on_std: float = await _temporal_std(true)
	_check("flicker: static FSR2 temporal std (on <= off + 0.004)", on_std <= off_std + 0.004, "off=%.5f on=%.5f" % [off_std, on_std])
	await _set_mode(Viewport.SCALING_3D_MODE_BILINEAR, 1.0, Viewport.MSAA_DISABLED)
