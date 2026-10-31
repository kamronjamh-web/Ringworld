extends SceneTree
# Megastorm blast shader identity and shape (WO11). Run (windowed Vulkan, not --headless; no other Godot process running):
#   Godot --path . --script res://tools/megastorm_blast_shader_gpu_test.gd. Prints MEGASTORM_BLAST_SHADER=PASS.
# Renders the real tree include function (ring_tree_wind_geometry from ring_local_wind.gdshaderinc, the reader of the trees and impostors) and the water slam function
# (ring_megastorm_blast_water_normal) to a float viewport twice: from the live includes and from a reference made at run time by cutting the blast block out of
# ring_local_wind.gdshaderinc (the code as it was before WO11). With ring_megastorm_blast.x = 0 (any other value of the global) the two must be byte-identical (the
# untriggered identity; the grass lines are the same guarded call, checked by the source-level guard test below). At strength 1 a point behind the front is thrown over
# along the direction of travel by a bounded amount, a point ahead of it is untouched, and the water normal tilts inside the packet and is untouched outside.
const LIVE_WIND := "res://materials/ring_local_wind.gdshaderinc"
const REF_DIR := "res://.godot/megastorm_blast/"
const SIZE := 128

var failed := false
var vp: SubViewport
var rect: ColorRect

func _init() -> void:
	create_timer(120.0).timeout.connect(func(): _fail("timeout"))
	call_deferred("_run")

func _fail(message: String) -> void:
	if failed: return
	failed = true
	push_error("MEGASTORM_BLAST_SHADER=FAIL " + message)
	quit(1)

func _write(path: String, text: String) -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(REF_DIR))
	var f := FileAccess.open(path, FileAccess.WRITE)
	f.store_string(text)
	f.close()

func _cut_block(text: String) -> String:
	var start := text.find("\tif (ring_megastorm_blast.x > 0.0) {")
	var marker := "structure += blast_offset; derivative += blast_derivative;\n\t}\n"
	var norm := text.replace("\r\n", "\n")
	start = norm.find("\tif (ring_megastorm_blast.x > 0.0) {")
	var end := norm.find(marker)
	if start < 0 or end < 0:
		_fail("blast block not found in the live include")
		return norm
	return norm.substr(0, start) + norm.substr(end + marker.length())

func _shader(wind: String) -> Shader:
	var s := Shader.new()
	s.code = """shader_type canvas_item;
#include "%s"
#include "res://materials/ring_megastorm_blast.gdshaderinc"
void fragment() {
	vec3 root = vec3(UV.x * 400.0 - 200.0, 0.0, UV.y * 20.0);
	vec3 off; mat3 jac;
	ring_tree_wind_geometry(root, vec3(0.0, 0.8, 0.0), mat3(vec3(1.0, 0.0, 0.0), vec3(0.0, 12.0, 0.0), vec3(0.0, 0.0, 1.0)), vec4(0.0, 0.5, 0.0, 0.1), 1, true, off, jac);
	vec3 n = ring_megastorm_blast_water_normal(vec3(0.0, 1.0, 0.0), root);
	COLOR = vec4(off.x + n.x * 0.001, off.y + n.y * 0.001 + jac[1].y * 0.0001, off.z + n.z * 0.001, 1.0);
}
""" % wind
	return s

func _render(shader: Shader) -> Image:
	var m := ShaderMaterial.new()
	m.shader = shader
	rect.material = m
	await process_frame
	await process_frame
	await process_frame
	return vp.get_texture().get_image()

func _run() -> void:
	if DisplayServer.get_name().to_lower().contains("headless") or RenderingServer.get_rendering_device() == null:
		_fail("needs a display and RenderingDevice")
		return
	vp = SubViewport.new()
	vp.size = Vector2i(SIZE, SIZE)
	vp.use_hdr_2d = true
	vp.transparent_bg = false
	vp.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(vp)
	rect = ColorRect.new()
	rect.size = Vector2(SIZE, SIZE)
	vp.add_child(rect)
	# A live shared wind field with a real mean wind, so the tree structure path is on.
	var g := RenderingServer
	g.global_shader_parameter_set("ring_wind_field_enabled", 1.0)
	g.global_shader_parameter_set("ring_wind_gust_base", 0.8)
	g.global_shader_parameter_set("ring_wind_gust_strength", 0.5)
	g.global_shader_parameter_set("ring_local_wind_enabled", 1.0)
	g.global_shader_parameter_set("ring_local_wind_axis", Vector3(0, 0, 1))
	g.global_shader_parameter_set("ring_local_wind_center", Vector3(0, -1000.0, 0))
	g.global_shader_parameter_set("ring_wind_mean_vector", Vector2(20, 0))
	g.global_shader_parameter_set("ring_tree_wind_response", Vector4(1.0, 0.4, 1.0, 1.0))
	g.global_shader_parameter_set("ring_tree_wind_trunk", Vector2(15, 0))
	g.global_shader_parameter_set("ring_tree_wind_canopy", Vector2(20, 0))
	g.global_shader_parameter_set("ring_tree_wind_parts", Vector2(1.0, 1.0))
	var img := Image.create(16, 16, false, Image.FORMAT_R8)
	img.fill(Color(0.6, 0, 0))
	g.global_shader_parameter_set("ring_wind_gust_texture", ImageTexture.create_from_image(img))
	var live_text := FileAccess.get_file_as_string(LIVE_WIND)
	_write(REF_DIR + "ref_wind.gdshaderinc", _cut_block(live_text))
	if failed: return
	var live := _shader(LIVE_WIND)
	var ref := _shader(REF_DIR + "ref_wind.gdshaderinc")
	var ok := true
	var report := []
	# Source-level guards: every blast reader is behind the strength check, so a world without a megastorm reaches none of it.
	for pair in [["res://materials/ring_standing_grass.gdshader", "ring_megastorm_blast_bend(root)"], ["res://materials/ring_grass_card_common.gdshaderinc", "ring_megastorm_blast_bend(root)"],
			["res://materials/ring_ocean_proxy.gdshader", "ring_megastorm_blast_water_normal("], [LIVE_WIND, "ring_megastorm_blast_tree("]]:
		var text := FileAccess.get_file_as_string(pair[0]).replace("\r\n", "\n")
		var at := text.find(pair[1])
		var guarded := at > 0 and text.rfind("ring_megastorm_blast.x > 0.0", at) >= 0 and at - text.rfind("ring_megastorm_blast.x > 0.0", at) < 400
		report.append("guard %s: %s" % [pair[0].get_file(), guarded])
		ok = ok and guarded
	# Untriggered identity: strength 0 with any other values is byte-identical to the pre-blast code.
	for state in [Vector4(0, 0, 1.309, 0), Vector4(0, 123.0, 0.7, 1.0), Vector4(0, -40.0, 1.309, 0.5)]:
		g.global_shader_parameter_set("ring_megastorm_blast", state)
		var a: Image = await _render(live)
		var b: Image = await _render(ref)
		var same := a.get_data() == b.get_data()
		report.append("identity %s: %s (sample %s)" % [state, same, a.get_pixel(10, 10)])
		ok = ok and same
	# Triggered: the front at x = 100 travelling +x. Points behind it (x < 100) are thrown over along +x; a point ahead is untouched.
	g.global_shader_parameter_set("ring_megastorm_blast_n", Vector3(1, 0, 0))
	g.global_shader_parameter_set("ring_megastorm_blast_up", Vector3(0, 1, 0))
	g.global_shader_parameter_set("ring_megastorm_blast_origin", Vector3(0, 0, 0))
	g.global_shader_parameter_set("ring_megastorm_blast", Vector4(1.0, 100.0, 1.309, 1.0))
	var on: Image = await _render(live)
	var off: Image = await _render(ref)
	var px := func(img: Image, x_world: float) -> Color: return img.get_pixel(int((x_world + 200.0) / 400.0 * SIZE), 4)
	var behind: Color = px.call(on, 0.0)
	var behind_ref: Color = px.call(off, 0.0)
	var ahead: Color = px.call(on, 150.0)
	var ahead_ref: Color = px.call(off, 150.0)
	var thrown := behind.r - behind_ref.r
	report.append("behind throw dx %.2f m (tree 12 m, weight 0.8), dy %.2f; ahead dx %.4f" % [thrown, behind.g - behind_ref.g, ahead.r - ahead_ref.r])
	ok = ok and thrown > 3.0 and thrown < 12.0 and behind.g - behind_ref.g < -0.1 and absf(ahead.r - ahead_ref.r) < 1e-3 and absf(ahead.g - ahead_ref.g) < 1e-3
	# Strength scales the bend and the held lean is lower than the throw (x = -2800, s = 2900 m).
	var held: Color = px.call(on, -200.0)
	report.append("held lean at s=300 m: dx %.2f" % (held.r - px.call(off, -200.0).r))
	g.global_shader_parameter_set("ring_megastorm_blast", Vector4(0.0, 100.0, 1.309, 1.0))
	RenderingServer.global_shader_parameter_set("ring_megastorm_blast", Vector4(0, 0, 1.309, 0))
	for line in report: print("MEGASTORM_BLAST_SHADER ", line)
	if ok:
		print("MEGASTORM_BLAST_SHADER=PASS")
		print("MEGASTORM_BLAST_SHADER_WRAPPER=PASS")
		quit(0)
	else:
		_fail("see report above")
