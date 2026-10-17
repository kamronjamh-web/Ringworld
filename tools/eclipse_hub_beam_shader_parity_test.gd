extends SceneTree
# Windowed GPU parity: the GLSL hub beam state math in materials/ring_eclipse_hub_beam_math.gdshaderinc (tip climb, handoff, cut, soft tip,
# day visibility, level, and the refuel flash) versus EclipseHubBeamMath in C#. Needs a rendering device: do not run headless, and do not run it while another
# Godot process is open. Modelled on tools/emitter_grid_shader_parity_test.gd.
# Usage: godot --path . --script res://tools/eclipse_hub_beam_shader_parity_test.gd
const FIXTURE := preload("res://tools/EclipseHubBeamShaderFixture.cs")

const SAMPLE_COUNT := 2048
const TOLERANCE := 4e-3
const FIELDS := 9
# name, global gain ev_level
const CASES := [
	{"name": "pre_fire", "level": 1.0},
	{"name": "main_climb", "level": 1.0},
	{"name": "ignition", "level": 1.0},
	{"name": "regular_climb", "level": 1.0},
	{"name": "handoff", "level": 1.0},
	{"name": "sustain", "level": 0.7},
	# Phase E4d: Sustain refuel flashes from the grid's normal fronts (ev_flash, ev_refuel, eg_fronts); the edge case is the Instability gate.
	{"name": "sustain_flash", "level": 1.0},
	{"name": "instability_edge", "level": 1.0},
	{"name": "retract", "level": 1.0},
	{"name": "after_cut", "level": 1.0},
]

var failed := false
var fixture


func _init() -> void:
	call_deferred("_run")
	create_timer(300.0).timeout.connect(func() -> void: _fail("timeout"))


func _fail(message: String) -> void:
	failed = true
	push_error("ECLIPSE_HUB_BEAM_SHADER_PARITY=FAIL " + message)
	quit(1)


func _frames(count: int) -> void:
	for i in count:
		await process_frame


func _run() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("requires Vulkan/Forward+ (do not run headless)")
		return
	fixture = FIXTURE.new()
	var total_lit := 0
	for case in CASES:
		var lit: int = await _run_case(case)
		if lit < 0:
			return
		total_lit += lit
	print("ECLIPSE_HUB_BEAM_SHADER_PARITY=PASS lit_total=%d" % total_lit)
	quit(0)


## Returns the number of lit samples (level above 0.01), or -1 on failure.
func _run_case(case: Dictionary) -> int:
	var uniforms: Dictionary = fixture.Setup(case["name"], case["level"])
	var samples: PackedFloat32Array = fixture.Samples(SAMPLE_COUNT, 424242)
	var expected: PackedFloat32Array = fixture.Reference(samples)
	var image := Image.create_from_data(SAMPLE_COUNT, 3, false, Image.FORMAT_RGBAF, _rows(samples))
	var texture := ImageTexture.create_from_image(image)
	var material := ShaderMaterial.new()
	material.shader = load("res://tools/shaders/eclipse_hub_beam_parity.gdshader")
	material.set_shader_parameter("samples", texture)
	for key in uniforms:
		material.set_shader_parameter(key, uniforms[key])
	var vp := SubViewport.new()
	vp.size = Vector2i(SAMPLE_COUNT * FIELDS, 1)
	vp.transparent_bg = true
	vp.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	var rect := ColorRect.new()
	rect.size = Vector2(SAMPLE_COUNT * FIELDS, 1)
	rect.material = material
	vp.add_child(rect)
	root.add_child(vp)
	await _frames(4)
	var out := vp.get_texture().get_image()
	out.convert(Image.FORMAT_RGBA8)
	var bytes := out.get_data()
	var names := ["tip", "handoff", "mask", "level", "flash", "flash_tip", "cloud_mask", "cloud_factor", "core_keep"]
	var worst := [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0]
	var bad := 0
	var lit := 0
	var partial_tip := 0
	var flashing := 0
	for i in SAMPLE_COUNT:
		for field in FIELDS:
			var o := (i * FIELDS + field) * 4
			var gpu := PackedByteArray([bytes[o], bytes[o + 1], bytes[o + 2], bytes[o + 3]]).decode_float(0)
			var want: float = expected[i * FIELDS + field]
			if field == 3 and want > 0.01:
				lit += 1
			if field == 0 and want > 0.0 and want < 1.0:
				partial_tip += 1
			if field == 4 and want > 0.0:
				flashing += 1
			if is_nan(gpu) or is_inf(gpu):
				bad += 1
				continue
			var err := absf(gpu - want)
			worst[field] = maxf(worst[field], err)
			if err > TOLERANCE:
				bad += 1
				if bad < 12:
					print("  mismatch %s %s sample=%d gpu=%.7f cpu=%.7f" % [case["name"], names[field], i, gpu, want])
	print("ECLIPSE_HUB_BEAM_SHADER_PARITY case=%s tau=%.2f lit_samples=%d partial_tips=%d worst_abs_err(tip=%.7f handoff=%.7f mask=%.7f level=%.7f flash=%.7f flash_tip=%.7f cloud_mask=%.7f cloud_factor=%.7f core_keep=%.7f) flashing=%d fronts=%d bad=%d" % [
		case["name"], float(uniforms["ev_time"]), lit, partial_tip, worst[0], worst[1], worst[2], worst[3], worst[4], worst[5], worst[6], worst[7], worst[8], flashing, int(uniforms["eg_front_count"]), bad])
	vp.queue_free()
	if bad > 0:
		_fail("case %s parity" % case["name"])
		return -1
	if case["name"] == "sustain_flash" and flashing < 100:
		_fail("case sustain_flash has too few flashing samples (%d) to be meaningful" % flashing)
		return -1
	return lit


# Three rows of RGBAF texels from the flat 12-floats-per-sample array (fixture.SampleStride()).
func _rows(samples: PackedFloat32Array) -> PackedByteArray:
	var stride: int = fixture.SampleStride()
	var rows := PackedFloat32Array()
	rows.resize(SAMPLE_COUNT * 12)
	for i in SAMPLE_COUNT:
		for row in 3:
			for k in 4:
				rows[SAMPLE_COUNT * 4 * row + i * 4 + k] = samples[i * stride + row * 4 + k]
	return rows.to_byte_array()
