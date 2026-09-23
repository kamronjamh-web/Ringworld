extends SceneTree

## Deterministic production-shader regression for terrain-contact FFT geometry.
## The field is synthetic: R=water depth, G=shore distance, B=validity. PNGs
## are review aids; region assertions check only stated geometry contracts.
const OUTPUT: String = "res://.godot/water_fft_shoreline"
const SIZE: Vector2i = Vector2i(640, 400)
const SHADER_PATH: String = "res://materials/ring_ocean_proxy.gdshader"
const RADIUS: float = 5000000.0
var _shader: Shader
var _silhouette: Shader
var _displacement: Texture2DArray
var _normal_foam: Texture2DArray
var _full: ImageTexture
var _shore: ImageTexture
var _invalid: ImageTexture
var _zero_depth: ImageTexture
var _shallow_depth: ImageTexture
var _mid_depth: ImageTexture

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("Forward+ RenderingDevice required")
		return
	call_deferred("_run")

func _run() -> void:
	if DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT)) != OK:
		_fail("could not create output directory")
		return
	_shader = Shader.new()
	_shader.code = _expand(FileAccess.get_file_as_string(SHADER_PATH)).replace("TIME", "11.5")
	_silhouette = Shader.new()
	_silhouette.code = _silhouette_source(_shader.code).replace("depth_draw_always", "depth_draw_always, unshaded")
	_displacement = _array_texture(false)
	_normal_foam = _array_texture(true)
	_full = _shore_field("full")
	_shore = _shore_field("shore")
	_invalid = _shore_field("invalid")
	_zero_depth = _shore_field("zero_depth")
	_shallow_depth = _shore_field("shallow_depth")
	_mid_depth = _shore_field("mid_depth")
	if _displacement == null or _normal_foam == null or _shore == null:
		_fail("could not create synthetic FFT inputs")
		return
	var base: Dictionary = {"height": 1.0, "chop": 1.0, "mode": 0, "field": _shore}
	var baseline: Image = await _render(base, "baseline_legacy")
	var full: Image = await _render(base.merged({"mode": 2, "field": _full}, true), "full_field")
	var shore: Image = await _render(base.merged({"mode": 2}, true), "shore")
	var shore_height_zero: Image = await _render(base.merged({"mode": 2, "height": 0.0}, true), "shore_height_0")
	var shore_chop_zero: Image = await _render(base.merged({"mode": 2, "chop": 0.0}, true), "shore_chop_0")
	var invalid: Image = await _render(base.merged({"mode": 2, "field": _invalid}, true), "invalid_field_pinned")
	var zero_depth: Image = await _render(base.merged({"mode": 2, "field": _zero_depth}, true), "zero_depth_pinned")
	var no_atlas: Image = await _render(base.merged({"mode": 1, "field": null}, true), "required_no_atlas_pinned")
	var legacy_deep: Image = await _render(base.merged({"mode": 0, "field": _zero_depth}, true), "legacy_bypass_deep")
	# A filled silhouette proves contact/offshore movement but cannot expose an
	# interior horizontal shift. Sample the production factor function directly
	# for the vertical-before-chop ordering contract.
	var factors: Image = await _render_factors(_shore, 2, "shore_factors")
	var invalid_factors: Image = await _render_factors(_invalid, 2, "invalid_factors")
	var shallow_factors: Image = await _render_factors(_shallow_depth, 2, "shallow_depth_factors")
	var mid_depth_factors: Image = await _render_factors(_mid_depth, 2, "mid_depth_factors")
	for image: Image in [baseline, full, shore, shore_height_zero, shore_chop_zero, invalid, zero_depth, no_atlas, legacy_deep]:
		if not _valid(image): _fail("empty or non-finite capture"); return
	var full_delta: float = _occupancy_delta(full, shore_height_zero, 0.55, 1.0)
	var land_anchor: float = _occupancy_delta(shore, shore_height_zero, 0.0, 0.28)
	var offshore_vertical: float = _occupancy_delta(shore, shore_height_zero, 0.62, 1.0)
	var mid_vertical: float = _occupancy_delta(shore, shore_height_zero, 0.38, 0.60)
	var mid_chop: float = _occupancy_delta(shore, shore_chop_zero, 0.38, 0.60)
	var invalid_pin: float = _occupancy_delta(invalid, shore_height_zero)
	var zero_pin: float = _occupancy_delta(zero_depth, shore_height_zero)
	var no_atlas_pin: float = _occupancy_delta(no_atlas, shore_height_zero)
	var legacy_delta: float = _occupancy_delta(baseline, legacy_deep)
	var mid_factor: Color = _factor_at(factors, -10.0)
	var offshore_factor: Color = _factor_at(factors, 8.0)
	var invalid_factor: Color = _factor_at(invalid_factors, 8.0)
	var shallow_depth_factor: Color = _factor_at(shallow_factors, 40.0)
	var midpoint_depth_factor: Color = _factor_at(mid_depth_factors, 40.0)
	print("WATER_FFT_SHORELINE_METRICS full=%.6f land=%.6f offshore_vertical=%.6f silhouette_mid_vertical=%.6f silhouette_mid_chop=%.6f factors_mid=(%.3f,%.3f) factors_offshore=(%.3f,%.3f) invalid_factor=(%.3f,%.3f) shallow_depth=(%.3f,%.3f) midpoint_depth=(%.3f,%.3f) invalid=%.6f zero_depth=%.6f no_atlas=%.6f legacy=%.6f" % [full_delta, land_anchor, offshore_vertical, mid_vertical, mid_chop, mid_factor.r, mid_factor.g, offshore_factor.r, offshore_factor.g, invalid_factor.r, invalid_factor.g, shallow_depth_factor.r, shallow_depth_factor.g, midpoint_depth_factor.r, midpoint_depth_factor.g, invalid_pin, zero_pin, no_atlas_pin, legacy_delta])
	if full_delta < .0002 or offshore_vertical < .0002: _fail("valid deep field did not displace offshore geometry"); return
	if land_anchor > offshore_vertical * .18: _fail("shore contact band was not anchored relative to offshore geometry"); return
	if mid_factor.r < .9 or mid_factor.g < .35 or mid_factor.g > .65 or offshore_factor.r < .9 or offshore_factor.g < .9: _fail("production shoreline factors did not retain vertical displacement before horizontal chop"); return
	if invalid_factor.r > .01 or invalid_factor.g > .01: _fail("invalid shoreline field did not produce pinned factors"); return
	if shallow_depth_factor.r > .02 or shallow_depth_factor.g > .02: _fail("shallow depth did not suppress FFT factors"); return
	if midpoint_depth_factor.r < .45 or midpoint_depth_factor.r > .55 or midpoint_depth_factor.g < .45 or midpoint_depth_factor.g > .55: _fail("mid-depth did not produce approximately half FFT factors"); return
	if invalid_pin > .0001 or zero_pin > .0001 or no_atlas_pin > .0001: _fail("invalid, zero-depth, or required-no-atlas field was not pinned"); return
	if legacy_delta > .0001: _fail("legacy field-bypass deep ocean changed with shoreline input"); return
	print("WATER_FFT_SHORELINE_VISUAL_TEST=PASS output=%s assertions=finite,anchored_contact,offshore_displacement,chop_before_vertical,invalid_pinned,zero_depth_pinned,shallow_depth_suppressed,mid_depth_half,no_atlas_pinned,legacy_bypass human_review=OPEN" % OUTPUT)
	quit(0)

func _render(c: Dictionary, label: String) -> Image:
	var viewport: SubViewport = SubViewport.new(); viewport.size = SIZE; viewport.own_world_3d = true; viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS; root.add_child(viewport)
	var environment: Environment = Environment.new(); environment.background_mode = Environment.BG_COLOR; environment.background_color = Color(0.035, .06, .09); environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR; environment.ambient_light_color = Color.WHITE; environment.ambient_light_energy = .7
	var world: WorldEnvironment = WorldEnvironment.new(); world.environment = environment; viewport.add_child(world)
	# Face the XY water grid directly so X image regions correspond to the
	# synthetic cross-shore field; the terrain bank remains visible at left.
	var camera: Camera3D = Camera3D.new(); camera.position = Vector3(0, 0, 100); camera.fov = 52; camera.near = .1; camera.far = 500; viewport.add_child(camera); camera.look_at(Vector3(0, 0, 0), Vector3.UP)
	var light: DirectionalLight3D = DirectionalLight3D.new(); light.rotation_degrees = Vector3(-35, -20, 0); light.light_energy = 1.1; viewport.add_child(light)
	var bank: MeshInstance3D = MeshInstance3D.new(); var box: BoxMesh = BoxMesh.new(); box.size = Vector3(34, 12, 24); bank.mesh = box; bank.position = Vector3(-48, -6, -4); var bank_material: StandardMaterial3D = StandardMaterial3D.new(); bank_material.albedo_color = Color(.19, .12, .065); bank_material.roughness = 1.0; bank.material_override = bank_material; viewport.add_child(bank)
	var water: MeshInstance3D = MeshInstance3D.new(); water.mesh = _grid(128.0, 2.0); water.material_override = _material(c); viewport.add_child(water)
	for frame: int in range(12): await process_frame
	await RenderingServer.frame_post_draw
	var image: Image = viewport.get_texture().get_image()
	if not image.is_empty(): image.save_png("%s/%s.png" % [OUTPUT, label])
	viewport.queue_free(); await process_frame
	return image

func _material(c: Dictionary) -> ShaderMaterial:
	var material: ShaderMaterial = ShaderMaterial.new(); material.shader = _silhouette
	var pairs: Array = [["camera_world_position", Vector3(0,0,100)], ["ring_center", Vector3(0,0,RADIUS)], ["ring_radius_meters", RADIUS], ["ring_width_meters", 10000.0], ["sun_direction", Vector3(0,0,-1)], ["far_fade_enabled", false], ["geomorph_enabled", false], ["analytic_cloud_shadow_enabled", false], ["ring_atmosphere_enabled", false], ["refraction_strength", 0.0], ["shore_foam_strength", 0.0], ["fft_ready", true], ["fft_displacement", _displacement], ["fft_normal_foam", _normal_foam], ["fft_anchor_world", Vector3.ZERO], ["fft_around_axis", Vector3(1,0,0)], ["fft_across_axis", Vector3(0,1,0)], ["fft_period_0", Vector2(12,12)], ["fft_period_1", Vector2(48,48)], ["fft_phase_0", Vector2.ZERO], ["fft_phase_1", Vector2.ZERO], ["fft_cascade_count", 2], ["fft_resolution", 32.0], ["fft_detail_distance_meters", 180.0], ["fft_geometry_distance_meters", 180.0], ["fft_geometry_filter_meters", 2.0], ["fft_geometry_height_scale", c.height], ["fft_geometry_choppiness", c.chop], ["fft_geometry_spacing_meters", 2.0], ["fft_normal_strength", 0.0], ["fft_foam_strength", 0.0], ["fft_surface_base_offset_meters", 0.0], ["fft_shoreline_field_mode", c.mode], ["fft_shoreline_field", c.field], ["fft_shoreline_field_center_canonical", Vector2.ZERO], ["fft_shoreline_field_cell_meters", 2.0], ["fft_shoreline_fade_distance_meters", 10.0], ["fft_shoreline_chop_fade_distance_meters", 20.0], ["fft_shoreline_shallow_depth_meters", 3.0], ["fft_deep_water_wave_depth_meters", 100.0], ["wave_geometry_strength", 0.0]]
	for pair: Array in pairs: material.set_shader_parameter(pair[0], pair[1])
	return material

func _shore_field(kind: String) -> ImageTexture:
	var image: Image = Image.create(512, 512, false, Image.FORMAT_RGBAF)
	for y: int in 512:
		for x: int in 512:
			var canonical_x: float = (float(x) - 256.0) * 2.0
			var depth: float = 0.0 if kind == "zero_depth" else 3.0 if kind == "shallow_depth" else 51.5 if kind == "mid_depth" else 150.0
			var distance: float = 64.0 if kind == "full" else maxf(0.0, canonical_x + 20.0)
			var valid: float = 0.0 if kind == "invalid" else 1.0
			image.set_pixel(x, y, Color(depth, distance, valid, 1.0))
	return ImageTexture.create_from_image(image)

func _array_texture(normal: bool) -> Texture2DArray:
	var images: Array[Image] = []
	for layer: int in 2:
		var image: Image = Image.create(32, 32, true, Image.FORMAT_RGBAF)
		for y: int in 32:
			for x: int in 32: image.set_pixel(x, y, Color(.38 if not normal else 0.0, .62 if not normal else 0.0, .27 if not normal else 0.0, 0.0))
		image.generate_mipmaps(); images.append(image)
	var texture: Texture2DArray = Texture2DArray.new()
	return texture if texture.create_from_images(images) == OK else null

func _render_factors(field: Texture2D, mode: int, label: String) -> Image:
	var shader: Shader = Shader.new()
	shader.code = """shader_type canvas_item;
uniform float detail_filtering_strength = 1.0;
#include "res://materials/ring_water_fft.gdshaderinc"
void fragment() {
	vec2 canonical = vec2((UV.x - 0.5) * 128.0, 0.0);
	vec2 factors = ring_fft_shoreline_factors(canonical, vec2(0.0));
	COLOR = vec4(factors, 0.0, 1.0);
}
"""
	var viewport: SubViewport = SubViewport.new(); viewport.size = Vector2i(256, 16); viewport.transparent_bg = false; viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS; root.add_child(viewport)
	var rect: ColorRect = ColorRect.new(); rect.size = Vector2(256,16); var material: ShaderMaterial = ShaderMaterial.new(); material.shader = shader; material.set_shader_parameter("fft_shoreline_field_mode", mode); material.set_shader_parameter("fft_shoreline_field", field); material.set_shader_parameter("fft_shoreline_field_center_canonical", Vector2.ZERO); material.set_shader_parameter("fft_shoreline_field_cell_meters", 2.0); material.set_shader_parameter("fft_shoreline_fade_distance_meters", 10.0); material.set_shader_parameter("fft_shoreline_chop_fade_distance_meters", 20.0); material.set_shader_parameter("fft_shoreline_shallow_depth_meters", 3.0); material.set_shader_parameter("fft_deep_water_wave_depth_meters", 100.0); rect.material = material; viewport.add_child(rect)
	for frame: int in range(4): await process_frame
	await RenderingServer.frame_post_draw
	var image: Image = viewport.get_texture().get_image(); if not image.is_empty(): image.save_png("%s/%s.png" % [OUTPUT,label])
	viewport.queue_free(); await process_frame
	return image

func _factor_at(image: Image, canonical_x: float) -> Color:
	var x: int = clampi(roundi((canonical_x / 128.0 + .5) * 255.0), 0, 255)
	return image.get_pixel(x, image.get_height() / 2)

func _grid(side: float, step: float) -> ArrayMesh:
	var tool: SurfaceTool = SurfaceTool.new(); tool.begin(Mesh.PRIMITIVE_TRIANGLES); var half: float = side*.5; var cells: int = int(side/step)
	for y: int in cells:
		for x: int in cells:
			var a: Vector3 = Vector3(-half+x*step, -half+y*step, 0); var b: Vector3 = a+Vector3(step,0,0); var d: Vector3 = a+Vector3(0,step,0); var e: Vector3 = a+Vector3(step,step,0)
			for p: Vector3 in [a,d,b,b,d,e]: tool.set_normal(Vector3(0,0,-1)); tool.set_color(Color(.12,.42,.68,0)); tool.set_uv(Vector2((p.x+half)/side,(p.y+half)/side)); tool.add_vertex(p)
	return tool.commit()

func _expand(source: String) -> String:
	var out: String = ""
	for line: String in source.split("\n"):
		if line.begins_with("#include \""):
			var start: int = line.find("\"")+1; var stop: int = line.find("\"", start)
			if stop > start: out += _expand(FileAccess.get_file_as_string(line.substr(start, stop-start)))+"\n"; continue
		out += line+"\n"
	return out

func _silhouette_source(source: String) -> String:
	var start: int = source.find("void fragment()"); var brace: int = source.find("{", start); var depth: int = 0
	for index: int in range(brace, source.length()):
		var character: String = source.substr(index,1)
		if character == "{": depth += 1
		elif character == "}":
			depth -= 1
			if depth == 0: return source.substr(0,start)+"void fragment() { ALBEDO=vec3(1.0); ROUGHNESS=1.0; ALPHA=1.0; }\n"+source.substr(index+1)
	return source

func _valid(image: Image) -> bool:
	if image.is_empty(): return false
	for y: int in range(0,image.get_height(),12):
		for x: int in range(0,image.get_width(),12):
			var color: Color = image.get_pixel(x,y)
			if is_nan(color.r) or is_inf(color.r): return false
	return true

func _occupancy_delta(a: Image, b: Image, x0 := 0.0, x1 := 1.0) -> float:
	var changed: int = 0; var count: int = 0; var left: int = int(a.get_width()*x0); var right: int = int(a.get_width()*x1)
	for y: int in range(12,a.get_height()-12,2):
		for x: int in range(left,right,2):
			if (a.get_pixel(x,y).get_luminance() > .75) != (b.get_pixel(x,y).get_luminance() > .75): changed += 1
			count += 1
	return float(changed)/maxf(1,count)

func _fail(message: String) -> void:
	push_error("WATER_FFT_SHORELINE_VISUAL_TEST=FAIL "+message); quit(1)
