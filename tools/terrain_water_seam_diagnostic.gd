extends SceneTree

## Diagnostic-only reproduction for two observed seam candidates.  It uses the
## production shader code/functions but synthetic geometry and FFT data; its
## numbers are therefore evidence of the code paths, not measurements of a
## particular saved ringworld or of real ocean amplitudes.
const OUTPUT: String = "res://.godot/terrain_water_seam_diagnostic"
const TERRAIN_SHADER: String = "res://materials/ring_terrain_proxy.gdshader"
const FFT_INCLUDE: String = "res://materials/ring_water_fft.gdshaderinc"
const SIZE: Vector2i = Vector2i(192, 128)
# CertifiedRingScale.EffectiveCircumferenceMeters / Math.Tau, rounded only for
# this fixture's float uniform. Do not substitute the requested 5,000,000 m.
const CERTIFIED_RING_RADIUS_METERS: float = 31415936.0 / TAU
const FFT_SIZE: int = 128
const FFT_PERIOD_METERS: float = 79.0
const FFT_ENCODE_METERS: float = 8.0

var _coverage: ImageTexture
var _fft: Texture2DArray

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("Forward+ RenderingDevice required")
		return
	call_deferred("_run")

func _run() -> void:
	if DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT)) != OK:
		_fail("could not create output directory")
		return
	_coverage = _solid_texture(Color.WHITE)
	_fft = _synthetic_fft()
	if _fft == null:
		_fail("could not create mipmapped synthetic FFT texture")
		return
	var terrain: Image = await _render_terrain()
	var spacing_2_a: Image = await _render_fft(2.0, "fft_spacing_2_a")
	var spacing_2_b: Image = await _render_fft(2.0, "fft_spacing_2_b")
	var spacing_4: Image = await _render_fft(4.0, "fft_spacing_4")
	var spacing_8: Image = await _render_fft(8.0, "fft_spacing_8")
	if not _finite(terrain) or not _finite(spacing_2_a) or not _finite(spacing_2_b) or not _finite(spacing_4) or not _finite(spacing_8):
		_fail("empty or non-finite GPU capture")
		return
	var split_x: int = terrain.get_width() >> 1
	var terrain_valid: int = _red_count(terrain, 0, split_x)
	var terrain_skirt: int = _red_count(terrain, split_x, terrain.get_width())
	var same: Dictionary = _difference_meters(spacing_2_a, spacing_2_b)
	var two_four: Dictionary = _difference_meters(spacing_2_a, spacing_4)
	var four_eight: Dictionary = _difference_meters(spacing_4, spacing_8)
	var reproduced: bool = terrain_valid == 0 and terrain_skirt > 100 and float(two_four["max"]) > 0.0001 and float(four_eight["max"]) > 0.0001 and float(same["max"]) < 0.00001
	var report: Dictionary = {
		"result": "REPRODUCED" if reproduced else "NOT_REPRODUCED",
		"limitations": "Synthetic FFT texture and isolated quads exercise production shader paths only; metrics are not world amplitudes or scene seam widths.",
		"certified_ring_radius_meters": CERTIFIED_RING_RADIUS_METERS,
		"terrain": {"valid_uv_red_pixels": terrain_valid, "skirt_uv_minus_one_red_pixels": terrain_skirt},
		"water_meters": {"metric": "vector displacement and separately vertical/G displacement", "spacing_2_to_2": same, "spacing_2_to_4": two_four, "spacing_4_to_8": four_eight},
		"readback": {"format": spacing_2_a.get_format(), "encoded_baseline_rgb": [spacing_2_a.get_pixel(0, 0).r, spacing_2_a.get_pixel(0, 0).g, spacing_2_a.get_pixel(0, 0).b]}
	}
	var file: FileAccess = FileAccess.open("%s/report.json" % OUTPUT, FileAccess.WRITE)
	if file == null:
		_fail("could not write JSON report")
		return
	file.store_string(JSON.stringify(report, "\t") + "\n")
	file.close()
	print("TERRAIN_WATER_SEAM_DIAGNOSTIC result=%s terrain_valid=%d terrain_skirt=%d same_vector_max=%.8f two_four_vector_max=%.8f two_four_vector_rms=%.8f two_four_vertical_max=%.8f four_eight_vector_max=%.8f four_eight_vector_rms=%.8f four_eight_vertical_max=%.8f output=%s" % [report["result"], terrain_valid, terrain_skirt, same["max"], two_four["max"], two_four["rms"], two_four["vertical_max"], four_eight["max"], four_eight["rms"], four_eight["vertical_max"], OUTPUT])
	if not reproduced:
		_fail("known terrain-mask or spacing-dependent FFT defect was not reproduced")
		return
	print("TERRAIN_WATER_SEAM_DIAGNOSTIC=PASS result=REPRODUCED")
	quit(0)

func _render_terrain() -> Image:
	var shader: Shader = Shader.new()
	shader.code = _terrain_diagnostic_source(_expand(FileAccess.get_file_as_string(TERRAIN_SHADER)))
	var viewport: SubViewport = SubViewport.new()
	viewport.size = SIZE; viewport.own_world_3d = true; viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)
	var environment: Environment = Environment.new(); environment.background_mode = Environment.BG_COLOR; environment.background_color = Color.BLACK
	var world: WorldEnvironment = WorldEnvironment.new(); world.environment = environment; viewport.add_child(world)
	var camera: Camera3D = Camera3D.new(); camera.projection = Camera3D.PROJECTION_ORTHOGONAL; camera.size = 12.0; camera.position = Vector3(0, 0, 20); camera.near = 0.1; camera.far = 100.0; viewport.add_child(camera); camera.look_at(Vector3.ZERO, Vector3.UP)
	# Left is a normal terrain surface UV; right models generated skirt vertices,
	# whose UV is (-1,-1) while UV2 is zero in RingTerrainProxySector.
	for skirt: bool in [false, true]:
		var node: MeshInstance3D = MeshInstance3D.new(); node.mesh = _quad(-3.5 if not skirt else 0.5, -3.0, 3.0, Vector2(-1, -1) if skirt else Vector2(0.5, 0.5)); node.material_override = _terrain_material(shader, camera.position); node.set_instance_shader_parameter("coverage_atlas_rect", Vector4(0, 0, 1, 1)); viewport.add_child(node)
	for frame: int in range(6): await process_frame
	await RenderingServer.frame_post_draw
	var image: Image = viewport.get_texture().get_image(); image.save_png("%s/terrain_coverage_skirt.png" % OUTPUT)
	viewport.queue_free(); await process_frame
	return image

func _terrain_material(shader: Shader, camera_position: Vector3) -> ShaderMaterial:
	var material: ShaderMaterial = ShaderMaterial.new(); material.shader = shader
	material.set_shader_parameter("coverage_atlas", _coverage); material.set_shader_parameter("camera_world_position", camera_position)
	material.set_shader_parameter("ring_center", Vector3(0, 0, CERTIFIED_RING_RADIUS_METERS)); material.set_shader_parameter("geomorph_enabled", false); material.set_shader_parameter("near_fade_enabled", false); material.set_shader_parameter("far_fade_enabled", false)
	return material

func _render_fft(spacing: float, label: String) -> Image:
	var shader: Shader = Shader.new()
	shader.code = """shader_type canvas_item;
uniform float detail_filtering_strength = 1.0;
#include \"res://materials/ring_water_fft.gdshaderinc\"
void fragment() {
	vec2 canonical = vec2(UV.x * 79.0, UV.y * 79.0);
	vec3 d = ring_fft_geometry_displacement(canonical);
	COLOR = vec4(d / 8.0 + vec3(0.5), 1.0);
}
"""
	var viewport: SubViewport = SubViewport.new(); viewport.size = Vector2i(FFT_SIZE, FFT_SIZE); viewport.use_hdr_2d = true; viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS; root.add_child(viewport)
	var rect: ColorRect = ColorRect.new(); rect.size = Vector2(FFT_SIZE, FFT_SIZE); var material: ShaderMaterial = ShaderMaterial.new(); material.shader = shader; material.set_shader_parameter("fft_displacement", _fft); material.set_shader_parameter("fft_ready", true); material.set_shader_parameter("fft_cascade_count", 2); material.set_shader_parameter("fft_period_1", Vector2(FFT_PERIOD_METERS, FFT_PERIOD_METERS)); material.set_shader_parameter("fft_resolution", float(FFT_SIZE)); material.set_shader_parameter("fft_geometry_filter_meters", 2.0); material.set_shader_parameter("fft_geometry_spacing_meters", spacing); material.set_shader_parameter("fft_geometry_height_scale", 1.0); material.set_shader_parameter("fft_geometry_choppiness", 1.0); rect.material = material; viewport.add_child(rect)
	for frame: int in range(4): await process_frame
	await RenderingServer.frame_post_draw
	var image: Image = viewport.get_texture().get_image(); image.save_png("%s/%s.png" % [OUTPUT, label])
	viewport.queue_free(); await process_frame
	return image

func _synthetic_fft() -> Texture2DArray:
	var images: Array[Image] = []
	for layer: int in 2:
		var image: Image = Image.create(FFT_SIZE, FFT_SIZE, true, Image.FORMAT_RGBAF)
		for y: int in FFT_SIZE:
			for x: int in FFT_SIZE:
				var u: float = TAU * float(x) / float(FFT_SIZE); var v: float = TAU * float(y) / float(FFT_SIZE)
				# Frequencies survive differently in true generated mip levels.
				var height: float = 1.6 * sin(u * 7.0 + v * 3.0) + 0.9 * cos(u * 17.0 - v * 11.0) + 0.4 * sin(u * 29.0 + v * 23.0)
				image.set_pixel(x, y, Color(0.35 * sin(u * 5.0), height, 0.35 * cos(v * 9.0), 1.0))
		image.generate_mipmaps(); images.append(image)
	var texture: Texture2DArray = Texture2DArray.new()
	return texture if texture.create_from_images(images) == OK else null

func _quad(x: float, y: float, side: float, uv: Vector2) -> ArrayMesh:
	var tool: SurfaceTool = SurfaceTool.new(); tool.begin(Mesh.PRIMITIVE_TRIANGLES)
	for point: Vector3 in [Vector3(x, y, 0), Vector3(x, y + side, 0), Vector3(x + side, y, 0), Vector3(x + side, y + side, 0)]: tool.set_uv(uv); tool.set_uv2(Vector2.ZERO); tool.set_normal(Vector3(0, 0, 1)); tool.add_vertex(point)
	# Godot considers clockwise faces front-facing; preserve production cull_back.
	tool.add_index(0); tool.add_index(1); tool.add_index(2); tool.add_index(2); tool.add_index(1); tool.add_index(3)
	return tool.commit()

func _terrain_diagnostic_source(source: String) -> String:
	var marker: int = source.find("void fragment()")
	var shading: int = source.find("vec3 terrain_color", marker)
	if marker < 0 or shading < marker: return ""
	var prefix: String = source.substr(0, shading).replace("render_mode cull_back, depth_draw_opaque;", "render_mode unshaded, cull_back, depth_draw_opaque;")
	return prefix + "ALBEDO = vec3(1.0, 0.0, 0.0); ROUGHNESS = 1.0; }\n"

func _expand(source: String) -> String:
	var result: String = ""
	for line: String in source.split("\n"):
		if line.begins_with("#include \""):
			var first: int = line.find("\"") + 1; var last: int = line.find("\"", first)
			if last > first: result += _expand(FileAccess.get_file_as_string(line.substr(first, last - first))) + "\n"; continue
		result += line + "\n"
	return result

func _difference_meters(a: Image, b: Image) -> Dictionary:
	var maximum: float = 0.0; var squared: float = 0.0; var vertical_maximum: float = 0.0; var vertical_squared: float = 0.0; var samples: int = 0
	for y: int in a.get_height():
		for x: int in a.get_width():
			var da: Vector3 = Vector3(a.get_pixel(x, y).r, a.get_pixel(x, y).g, a.get_pixel(x, y).b) * FFT_ENCODE_METERS - Vector3.ONE * (FFT_ENCODE_METERS * 0.5)
			var db: Vector3 = Vector3(b.get_pixel(x, y).r, b.get_pixel(x, y).g, b.get_pixel(x, y).b) * FFT_ENCODE_METERS - Vector3.ONE * (FFT_ENCODE_METERS * 0.5)
			var delta: float = da.distance_to(db); var vertical_delta: float = absf(da.y - db.y); maximum = maxf(maximum, delta); squared += delta * delta; vertical_maximum = maxf(vertical_maximum, vertical_delta); vertical_squared += vertical_delta * vertical_delta; samples += 1
	return {"max": maximum, "rms": sqrt(squared / maxf(1.0, float(samples))), "vertical_max": vertical_maximum, "vertical_rms": sqrt(vertical_squared / maxf(1.0, float(samples)))}

func _red_count(image: Image, x0: int, x1: int) -> int:
	var total: int = 0
	for y: int in image.get_height():
		for x: int in range(x0, x1):
			if image.get_pixel(x, y).r > 0.7: total += 1
	return total

func _solid_texture(color: Color) -> ImageTexture:
	var image: Image = Image.create(1, 1, false, Image.FORMAT_RGBA8); image.set_pixel(0, 0, color); return ImageTexture.create_from_image(image)

func _finite(image: Image) -> bool:
	if image == null or image.is_empty(): return false
	for y: int in range(0, image.get_height(), 8):
		for x: int in range(0, image.get_width(), 8):
			var pixel: Color = image.get_pixel(x, y)
			if is_nan(pixel.r) or is_inf(pixel.r) or is_nan(pixel.g) or is_inf(pixel.g): return false
	return true

func _fail(message: String) -> void:
	push_error("TERRAIN_WATER_SEAM_DIAGNOSTIC=FAIL " + message); quit(1)
