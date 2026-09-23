extends SceneTree

## Production-shader fixture for the FFT displacement contract.  PNGs are a
## human silhouette check; assertions catch regressions in the geometry gates.
const OUTPUT := "res://.godot/water_fft_geometry"
const SIZE := Vector2i(640, 400)
const SHADER_PATH := "res://materials/ring_ocean_proxy.gdshader"
const RADIUS := 5000000.0
var frozen: Shader
var silhouette: Shader
var displacement: Texture2DArray
var normal_foam: Texture2DArray
var coverage: ImageTexture

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("Forward+ RenderingDevice required")
		return
	call_deferred("_run")

func _run() -> void:
	if DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT)) != OK:
		_fail("could not create output directory")
		return
	frozen = Shader.new()
	frozen.code = _expand(FileAccess.get_file_as_string(SHADER_PATH)).replace("TIME", "7.25")
	silhouette = Shader.new()
	silhouette.code = _silhouette_source(frozen.code).replace("depth_draw_always", "depth_draw_always, unshaded")
	displacement = _array_texture(false)
	normal_foam = _array_texture(true)
	var coverage_image := Image.create(1, 1, false, Image.FORMAT_RGBA8)
	coverage_image.set_pixel(0, 0, Color.WHITE)
	coverage = ImageTexture.create_from_image(coverage_image)
	if displacement == null or normal_foam == null:
		_fail("could not create deterministic Texture2DArray inputs")
		return
	var base := {"height": 1.0, "chop": 0.75, "spacing": 2.0, "ready": true, "analytic": 0.0, "distance": 70.0, "normal": 0.25, "foam": 0.35}
	var height_zero: Image = await _render(base.merged({"height": 0.0}, true), "height_0")
	var height_one: Image = await _render(base, "height_1_chop_075")
	var chop_zero: Image = await _render(base.merged({"chop": 0.0}, true), "chop_0")
	var translated: Image = await _render(base.merged({"translation": Vector3(17231.0, -911.0, 4021.0)}, true), "rebase_equivalent")
	var analytic_a: Image = await _render(base.merged({"analytic": 0.0}, true), "ready_analytic_0")
	var analytic_b: Image = await _render(base.merged({"analytic": 1.8}, true), "ready_analytic_18")
	var beyond_a: Image = await _render(base.merged({"distance": 230.0, "analytic": 0.0}, true), "beyond_geometry_analytic_0")
	var beyond_b: Image = await _render(base.merged({"distance": 230.0, "analytic": 1.8}, true), "beyond_geometry_analytic_18")
	var isolated: Image = await _render(base.merged({"normal": 0.0, "foam": 0.0}, true), "normal_foam_0")
	var coarse: Image = await _render(base.merged({"spacing": 0.0}, true), "coarse_spacing_0")
	var fallback_a: Image = await _render(base.merged({"ready": false, "analytic": 0.0}, true), "fallback_analytic_0")
	var fallback_b: Image = await _render(base.merged({"ready": false, "analytic": 1.8}, true), "fallback_analytic_18")
	# These captures use the unmodified production vertex function with only its
	# fragment replaced by opaque white. Occupancy therefore measures geometry.
	var shape_height_zero: Image = await _render(base.merged({"height": 0.0}, true), "shape_height_0", true)
	var shape_height_one: Image = await _render(base, "shape_height_1_chop_075", true)
	var shape_chop_zero: Image = await _render(base.merged({"chop": 0.0}, true), "shape_chop_0", true)
	var shape_coarse: Image = await _render(base.merged({"spacing": 0.0}, true), "shape_coarse_spacing_0", true)
	var covered: Image = await _render(base.merged({"spacing": 0.0, "coverage_enabled": true}, true), "coarse_valid_coverage")
	var uncovered: Image = await _render(base.merged({"spacing": 0.0, "coverage_enabled": false}, true), "coarse_coverage_disabled")
	for image: Image in [height_zero, height_one, chop_zero, translated, analytic_a, analytic_b, beyond_a, beyond_b, isolated, coarse, fallback_a, fallback_b, shape_height_zero, shape_height_one, shape_chop_zero, shape_coarse, covered, uncovered]:
		if not _valid(image): _fail("empty or non-finite capture"); return
	var height_delta := _delta(height_zero, height_one)
	var chop_delta := _delta(height_one, chop_zero)
	var height_silhouette := _occupancy_delta(shape_height_zero, shape_height_one)
	var chop_silhouette := _occupancy_delta(shape_height_one, shape_chop_zero)
	var rebase_delta := _delta(height_one, translated)
	var analytic_delta := _delta(analytic_a, analytic_b)
	var beyond_delta := _delta(beyond_a, beyond_b)
	var coarse_silhouette := _occupancy_delta(shape_height_zero, shape_coarse)
	var fallback_delta := _delta(fallback_a, fallback_b)
	var coverage_delta := _delta(covered, uncovered)
	print("WATER_FFT_GEOMETRY_METRICS height=%.6f chop=%.6f height_silhouette=%.6f chop_silhouette=%.6f rebase=%.6f ready_analytic=%.6f beyond_analytic=%.6f coarse_silhouette=%.6f fallback=%.6f coverage=%.6f" % [height_delta, chop_delta, height_silhouette, chop_silhouette, rebase_delta, analytic_delta, beyond_delta, coarse_silhouette, fallback_delta, coverage_delta])
	if height_delta < 0.002 or chop_delta < 0.001 or height_silhouette < .0002 or chop_silhouette < .0001: _fail("FFT height/chop did not move the rendered surface silhouette"); return
	if rebase_delta > 0.006: _fail("equivalent translated origin changed FFT output"); return
	if analytic_delta > 0.003 or beyond_delta > 0.003: _fail("ready FFT ocean still responds to analytic wave settings"); return
	if coarse_silhouette > .0001: _fail("coarse spacing zero moved vertices"); return
	if fallback_delta < 0.002: _fail("analytic fallback no longer moves when FFT is unavailable"); return
	if coverage_delta < .01: _fail("coarse ocean coverage atlas was ignored or disabling coverage still discarded"); return
	print("WATER_FFT_GEOMETRY_VISUAL_TEST=PASS output=%s assertions=finite,height,chop,rebase,analytic_isolation,coarse,fallback human_review=OPEN" % OUTPUT)
	quit(0)

func _render(c: Dictionary, label: String, silhouette_only := false) -> Image:
	var vp := SubViewport.new(); vp.size = SIZE; vp.own_world_3d = true; vp.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(vp)
	var env := Environment.new(); env.background_mode = Environment.BG_COLOR; env.background_color = Color(0.04, 0.07, 0.11); env.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR; env.ambient_light_color = Color.WHITE; env.ambient_light_energy = 0.7; env.tonemap_mode = Environment.TONE_MAPPER_LINEAR
	var world := WorldEnvironment.new(); world.environment = env; vp.add_child(world)
	var translation: Vector3 = c.get("translation", Vector3.ZERO)
	var camera := Camera3D.new(); camera.position = translation + Vector3(0, 34, 0); camera.fov = 58; camera.near = .1; camera.far = 2000; vp.add_child(camera)
	camera.look_at(translation + Vector3(0, 0, -float(c.distance)), Vector3.UP)
	var light := DirectionalLight3D.new(); light.rotation_degrees = Vector3(-35, 25, 0); light.light_energy = 1.2; vp.add_child(light)
	var water := MeshInstance3D.new(); water.mesh = _grid(120.0, 2.0); water.position = translation + Vector3(0, 0, -float(c.distance)); water.material_override = _material(c, translation, silhouette_only); vp.add_child(water)
	# Coverage is one bounded shared texture, independent of coarse mesh UVs.
	var bed := MeshInstance3D.new(); bed.mesh = _grid(130.0, 2.0); bed.position = water.position + Vector3(0, 0, -12); var bm := StandardMaterial3D.new(); bm.albedo_color = Color(.06, .035, .02); bm.roughness = 1; bed.material_override = bm; vp.add_child(bed)
	for frame: int in range(12): await process_frame
	await RenderingServer.frame_post_draw
	var image := vp.get_texture().get_image(); if not image.is_empty(): image.save_png("%s/%s.png" % [OUTPUT, label])
	vp.queue_free(); await process_frame
	return image

func _material(c: Dictionary, translation: Vector3, silhouette_only := false) -> ShaderMaterial:
	var m := ShaderMaterial.new(); m.shader = silhouette if silhouette_only else frozen
	var distance: float = c.distance; var center := translation + Vector3(0, 0, RADIUS - distance)
	for pair: Array in [["camera_world_position", translation], ["ring_center", center], ["ring_radius_meters", RADIUS], ["ring_width_meters", 20000.0], ["sun_direction", Vector3(0,0,-1)], ["far_fade_enabled", false], ["geomorph_enabled", false], ["analytic_cloud_shadow_enabled", false], ["ring_atmosphere_enabled", false], ["refraction_strength", 0.0], ["shore_foam_strength", 0.0], ["fft_ready", c.ready], ["fft_displacement", displacement], ["fft_normal_foam", normal_foam], ["fft_ocean_coverage_atlas", coverage], ["fft_ocean_coverage_enabled", c.get("coverage_enabled", false)], ["fft_ocean_coverage_distance_meters", 1000.0], ["fft_anchor_world", translation], ["fft_around_axis", Vector3(1,0,0)], ["fft_across_axis", Vector3(0,1,0)], ["fft_period_0", Vector2(12,12)], ["fft_period_1", Vector2(48,48)], ["fft_phase_0", Vector2.ZERO], ["fft_phase_1", Vector2.ZERO], ["fft_cascade_count", 2], ["fft_resolution", 32.0], ["fft_detail_distance_meters", 180.0], ["fft_geometry_distance_meters", 120.0], ["fft_geometry_filter_meters", 2.0], ["fft_geometry_height_scale", c.height], ["fft_geometry_choppiness", c.chop], ["fft_geometry_spacing_meters", c.spacing], ["fft_normal_strength", c.normal], ["fft_foam_strength", c.foam], ["fft_crest_lighting_strength", .35], ["wave_geometry_strength", 1.0], ["wave_height_meters", c.analytic], ["wave_length_meters", 8.0], ["wave_detail_strength", 1.0], ["wave_normal_strength", 1.0], ["wave_normal_fade_start_meters", 0.0], ["wave_normal_fade_end_meters", 1000.0]]: m.set_shader_parameter(pair[0], pair[1])
	return m

func _array_texture(is_normal: bool) -> Texture2DArray:
	var images: Array[Image] = []
	for layer: int in 2:
		var image := Image.create(32, 32, true, Image.FORMAT_RGBAF)
		for y: int in 32:
			for x: int in 32:
				var u := float(x) / 32.0 * TAU; var v := float(y) / 32.0 * TAU
				if is_normal: image.set_pixel(x, y, Color(.16*cos(u), .12*sin(v), .05, .35))
				else: image.set_pixel(x, y, Color(.55*sin(u) + .18*cos(v), .72*sin(2.0*u + v), .42*cos(v) + .12*sin(3.0*u), 0))
		image.generate_mipmaps(); images.append(image)
	var texture := Texture2DArray.new()
	return texture if texture.create_from_images(images) == OK else null

func _grid(side: float, step: float) -> ArrayMesh:
	var st := SurfaceTool.new(); st.begin(Mesh.PRIMITIVE_TRIANGLES)
	var half := side * .5; var cells := int(side / step)
	for y: int in cells:
		for x: int in cells:
			var a := Vector3(-half + x*step, -half + y*step, 0); var b := a + Vector3(step,0,0); var d := a + Vector3(0,step,0); var e := a + Vector3(step,step,0)
			for p: Vector3 in [a,d,b,b,d,e]: st.set_normal(Vector3(0,0,-1)); st.set_color(Color(.12,.42,.68,0)); st.set_uv(Vector2((p.x+half)/side,(p.y+half)/side)); st.add_vertex(p)
	return st.commit()

func _expand(source: String) -> String:
	var out := ""
	for line: String in source.split("\n"):
		if line.begins_with("#include \""):
			var start := line.find("\"") + 1; var stop := line.find("\"", start)
			if stop > start: out += _expand(FileAccess.get_file_as_string(line.substr(start, stop-start))) + "\n"; continue
		out += line + "\n"
	return out

func _silhouette_source(source: String) -> String:
	var start := source.find("void fragment()")
	var brace := source.find("{", start)
	if start < 0 or brace < 0: return source
	var depth := 0
	for index: int in range(brace, source.length()):
		var character := source.substr(index, 1)
		if character == "{": depth += 1
		elif character == "}":
			depth -= 1
			if depth == 0:
				return source.substr(0, start) + "void fragment() { ALBEDO = vec3(1.0); ROUGHNESS = 1.0; ALPHA = 1.0; }\n" + source.substr(index + 1)
	return source

func _valid(image: Image) -> bool:
	if image.is_empty(): return false
	var lit := 0
	for y: int in range(0,image.get_height(),8):
		for x: int in range(0,image.get_width(),8):
			var c := image.get_pixel(x,y)
			if is_nan(c.r) or is_nan(c.g) or is_nan(c.b) or is_inf(c.r) or is_inf(c.g) or is_inf(c.b): return false
			if c.get_luminance() > .001: lit += 1
	return lit > 20

func _delta(a: Image, b: Image) -> float:
	var total := 0.0; var n := 0
	for y: int in range(24,a.get_height()-24,4):
		for x: int in range(24,a.get_width()-24,4): total += absf(a.get_pixel(x,y).get_luminance()-b.get_pixel(x,y).get_luminance()); n += 1
	return total / maxf(1,n)

func _occupancy_delta(a: Image, b: Image) -> float:
	# Opaque white fragment variant: a changed threshold is only a changed vertex
	# silhouette, not a lighting or normal threshold crossing.
	var changed := 0; var n := 0
	for y: int in range(12,a.get_height()-12,2):
		for x: int in range(12,a.get_width()-12,2):
			var aa := a.get_pixel(x,y).get_luminance() > .75
			var bb := b.get_pixel(x,y).get_luminance() > .75
			if aa != bb: changed += 1
			n += 1
	return float(changed) / maxf(1,n)

func _fail(message: String) -> void:
	push_error("WATER_FFT_GEOMETRY_VISUAL_TEST=FAIL " + message); quit(1)
