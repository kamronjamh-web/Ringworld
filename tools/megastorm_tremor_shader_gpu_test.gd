extends SceneTree
# Megastorm ground tremor shader identity (WO6c). Run (windowed Vulkan, not --headless; no other Godot process running):
#   Godot --path . --script res://tools/megastorm_tremor_shader_gpu_test.gd [-- --out <dir>]. Prints MEGASTORM_TREMOR_SHADER=PASS.
# Renders the real include functions (ring_rain_ripple_gradient from ring_rain_ripples.gdshaderinc and ring_wind_gust_at_world from
# ring_wind_field.gdshaderinc, the readers of the puddle shiver and the vegetation sway) to a float viewport twice: from the live includes and from a
# reference made at run time by stripping the tremor lines out of them (the code as it was before WO6c). With ring_ground_tremor.x = 0 (any phase or
# frequency) the two must be byte-identical (the untriggered identity); at 0.6 they must differ by a visible but bounded shiver. A sheet of the
# shiver at 8 phases is written next to the output.
const LIVE_RIPPLES := "res://materials/ring_rain_ripples.gdshaderinc"
const LIVE_WIND := "res://materials/ring_wind_field.gdshaderinc"
const REF_DIR := "res://.godot/megastorm_tremor/"
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
	push_error("MEGASTORM_TREMOR_SHADER=FAIL " + message)
	quit(1)

func _strip(text: String, needles: Array) -> String:
	var kept: PackedStringArray = []
	var removed := 0
	for line in text.split("\n"):
		var drop := false
		for n in needles:
			if line.contains(n): drop = true
		if drop: removed += 1
		else: kept.append(line)
	if removed != needles.size():
		_fail("expected to strip %d lines, stripped %d" % [needles.size(), removed])
	return "\n".join(kept)

func _write(path: String, text: String) -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(REF_DIR))
	var f := FileAccess.open(path, FileAccess.WRITE)
	f.store_string(text)
	f.close()

func _shader(ripples: String, wind: String) -> Shader:
	var s := Shader.new()
	s.code = """shader_type canvas_item;
#include "%s"
#include "%s"
uniform float rain = 0.8;
uniform float fat = 0.3;
uniform float hail = 0.2;
void fragment() {
	vec2 p = UV * 6.0 + vec2(3.7, 1.9);
	vec2 g = ring_rain_ripple_gradient(p, rain, fat, hail, 5.0);
	float w = ring_wind_gust_at_world(vec3(UV * 50.0, 3.0), 0.0);
	COLOR = vec4(g, w, 1.0);
}
""" % [ripples, wind]
	return s

func _render(shader: Shader, rain := 0.8, fat := 0.3, hail := 0.2) -> Image:
	var m := ShaderMaterial.new()
	m.shader = shader
	m.set_shader_parameter("rain", rain)
	m.set_shader_parameter("fat", fat)
	m.set_shader_parameter("hail", hail)
	rect.material = m
	await process_frame
	await process_frame
	await process_frame
	return vp.get_texture().get_image()

func _max_diff(a: Image, b: Image) -> Vector3:
	var worst := Vector3.ZERO
	for y in range(0, SIZE):
		for x in range(0, SIZE):
			var ca := a.get_pixel(x, y)
			var cb := b.get_pixel(x, y)
			worst.x = maxf(worst.x, absf(ca.r - cb.r))
			worst.y = maxf(worst.y, absf(ca.g - cb.g))
			worst.z = maxf(worst.z, absf(ca.b - cb.b))
	return worst

func _run() -> void:
	if DisplayServer.get_name().to_lower().contains("headless") or RenderingServer.get_rendering_device() == null:
		_fail("needs a display and RenderingDevice")
		return
	var out := ProjectSettings.globalize_path("res://.godot/captures/megastorm/wo6c")
	var args := OS.get_cmdline_user_args()
	var oi := args.find("--out")
	if oi >= 0 and oi + 1 < args.size(): out = args[oi + 1]
	DirAccess.make_dir_recursive_absolute(out)
	vp = SubViewport.new()
	vp.size = Vector2i(SIZE, SIZE)
	vp.use_hdr_2d = true
	vp.transparent_bg = false
	vp.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(vp)
	rect = ColorRect.new()
	rect.size = Vector2(SIZE, SIZE)
	vp.add_child(rect)
	# Inputs the includes read: a random ripple atlas, the wet clock, a wind field with a real gust base and strength.
	var img := Image.create(64, 64, false, Image.FORMAT_RGBA8)
	var rng := RandomNumberGenerator.new()
	rng.seed = 7
	for y in 64:
		for x in 64:
			img.set_pixel(x, y, Color(rng.randf(), rng.randf(), rng.randf(), rng.randf()))
	RenderingServer.global_shader_parameter_set("ring_wet_ripple_tex", ImageTexture.create_from_image(img))
	RenderingServer.global_shader_parameter_set("ring_wet_clock", Vector4(3.7, 4.0, 8192.0, 1000.0))
	RenderingServer.global_shader_parameter_set("ring_wind_field_enabled", 1.0)
	RenderingServer.global_shader_parameter_set("ring_wind_gust_base", 0.8)
	RenderingServer.global_shader_parameter_set("ring_wind_gust_strength", 0.5)
	var live_ripples := FileAccess.get_file_as_string(LIVE_RIPPLES)
	var live_wind := FileAccess.get_file_as_string(LIVE_WIND)
	_write(REF_DIR + "ref_ripples.gdshaderinc", _strip(live_ripples, ["ring_ground_tremor.gdshaderinc", "ring_tremor_gradient(plane_m)"]))
	_write(REF_DIR + "ref_wind.gdshaderinc", _strip(live_wind, ["ring_ground_tremor.gdshaderinc", "ring_tremor_sway(position)"]))
	if failed: return
	var live := _shader(LIVE_RIPPLES, LIVE_WIND)
	var ref := _shader(REF_DIR + "ref_ripples.gdshaderinc", REF_DIR + "ref_wind.gdshaderinc")
	var ok := true
	var report := []
	# Untriggered identity: any phase or frequency at amplitude 0 is byte-identical to the pre-tremor code.
	for state in [Vector4(0, 9, 0, 0), Vector4(0, 9, 2.5, 0), Vector4(0, 31, 5.9, 1)]:
		RenderingServer.global_shader_parameter_set("ring_ground_tremor", state)
		var a: Image = await _render(live)
		var b: Image = await _render(ref)
		var same := a.get_data() == b.get_data()
		var dd := _max_diff(a, b)
		report.append("identity %s: %s (format %d, max |diff| %s, sample %s)" % [state, same, a.get_format(), dd, a.get_pixel(10, 10)])
		ok = ok and same
	# Triggered: the shiver shows, bounded.
	RenderingServer.global_shader_parameter_set("ring_ground_tremor", Vector4(0.6, 9, 1.2, 0))
	var on: Image = await _render(live)
	var off: Image = await _render(ref)
	var d := _max_diff(on, off)
	report.append("tremor 0.6 max |diff| ripple %.3f %.3f gust %.3f" % [d.x, d.y, d.z])
	ok = ok and d.x > 0.05 and d.y > 0.05 and d.x < 1.2 and d.y < 1.2 and d.z > 0.02 and d.z < 0.2
	# The puddle shiver alone (no rain) at 8 phases over one cycle, as a sheet: amplitude 0.6 (full hum).
	var sheet := Image.create(SIZE * 8, SIZE, false, Image.FORMAT_RGB8)
	var distinct := {}
	for i in 8:
		RenderingServer.global_shader_parameter_set("ring_ground_tremor", Vector4(0.6, 9, TAU * i / 8.0, 0))
		var frame: Image = await _render(live, 0.0, 0.0, 0.0)
		for y in range(0, SIZE):
			for x in range(0, SIZE):
				var c := frame.get_pixel(x, y)
				sheet.set_pixel(i * SIZE + x, y, Color(clampf(0.5 + c.r * 0.8, 0, 1), clampf(0.5 + c.g * 0.8, 0, 1), 0.5))
		distinct[str(frame.get_pixel(40, 40)) + str(frame.get_pixel(90, 70))] = true
	sheet.save_png(out.path_join("tremor_ripple_sheet.png"))
	report.append("shiver phases distinct: %d of 8" % distinct.size())
	ok = ok and distinct.size() >= 7
	# With no rain and no tremor the ripple gradient really is flat (no shiver without the global).
	RenderingServer.global_shader_parameter_set("ring_ground_tremor", Vector4(0, 9, 1.0, 0))
	var flat: Image = await _render(live, 0.0, 0.0, 0.0)
	var flat_ok := true
	for y in range(0, SIZE, 7):
		for x in range(0, SIZE, 7):
			var c := flat.get_pixel(x, y)
			flat_ok = flat_ok and absf(c.r) < 1e-6 and absf(c.g) < 1e-6
	report.append("no rain, no tremor flat: %s" % flat_ok)
	ok = ok and flat_ok
	RenderingServer.global_shader_parameter_set("ring_ground_tremor", Vector4(0, 9, 0, 0))
	for line in report: print("MEGASTORM_TREMOR_SHADER ", line)
	if ok:
		print("MEGASTORM_TREMOR_SHADER=PASS")
		print("MEGASTORM_TREMOR_SHADER_WRAPPER=PASS")
		quit(0)
	else:
		_fail("see report above")
