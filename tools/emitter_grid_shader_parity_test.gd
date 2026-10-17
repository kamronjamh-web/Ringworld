extends SceneTree
# Windowed GPU parity: the GLSL pulse/visibility math in materials/ring_emitter_grid_pulse.gdshaderinc (used by the beam and head
# shaders) versus EmitterGridPulseClock / EmitterGridIntensity. Needs a rendering device: do not run headless, and do not run it
# while another Godot process is open.
# Usage: godot --path . --script res://tools/emitter_grid_shader_parity_test.gd
const FIXTURE := preload("res://tools/EmitterGridShaderFixture.cs")

const SAMPLE_COUNT := 2048
const TOLERANCE := 4e-3
const FIELDS := 5
# name, retrograde, manual speed, clock time (seconds on the weather clock)
const CASES := [
	{"name": "prograde_auto", "retro": false, "manual": false, "t": 12345.6},
	{"name": "retrograde_auto", "retro": true, "manual": false, "t": 12345.6},
	# Eclipse hub sun (spec C.2): ringworld_hub_day lifts the per-point day to max(day, hub_day).
	{"name": "prograde_hub_day", "retro": false, "manual": false, "t": 12345.6, "hub": 0.55},
	{"name": "prograde_manual_slow", "retro": false, "manual": true, "t": 777.25},
	{"name": "relaunch_flare", "retro": false, "manual": false, "t": -1.0},
	# Eclipse ignition fronts (spec E.3, phase E4d): z/w lanes, bidirectional with plateau, absorbed plateau spans, forced night.
	{"name": "ignition_bidirectional", "retro": false, "manual": false, "t": 12345.6, "events": "bidirectional"},
	{"name": "ignition_bidirectional_retro", "retro": true, "manual": false, "t": 12345.6, "events": "bidirectional"},
	{"name": "ignition_plateau_absorbed", "retro": false, "manual": false, "t": 12345.6, "events": "plateau"},
	{"name": "ignition_plateau_absorbed_retro", "retro": true, "manual": false, "t": 12345.6, "events": "plateau"},
]

var failed := false
var fixture


func _init() -> void:
	call_deferred("_run")
	create_timer(300.0).timeout.connect(func() -> void: _fail("timeout"))


func _fail(message: String) -> void:
	failed = true
	push_error("EMITTER_GRID_SHADER_PARITY=FAIL " + message)
	quit(1)


func _frames(count: int) -> void:
	for i in count:
		await process_frame


func _run() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("requires Vulkan/Forward+ (do not run headless)")
		return
	fixture = FIXTURE.new()
	for case in CASES:
		var t: float = case["t"]
		if t < 0.0:
			# Just after a launch so the knot links are flare-lit: launch time of the clock is seed dependent, so scan for it.
			t = _flare_time(case)
		if not await _run_case(case, t):
			return
	print("EMITTER_GRID_SHADER_PARITY=PASS")
	quit(0)


func _flare_time(case: Dictionary) -> float:
	fixture.Setup(case["retro"], case["manual"], 0.0)
	return fixture.TimeAfterLaunch()


func _run_case(case: Dictionary, t: float) -> bool:
	var uniforms: Dictionary = fixture.SetupEvents(case["retro"], case["manual"], t, case.get("events", ""))
	var samples: PackedFloat32Array = fixture.Samples(SAMPLE_COUNT, 424242)
	var solar_visibility := 0.8
	var hub_day: float = case.get("hub", 0.0)
	var expected: PackedFloat32Array = fixture.Reference(samples, solar_visibility, hub_day)
	var image := Image.create_from_data(SAMPLE_COUNT, 3, false, Image.FORMAT_RGBAF, _rows(samples))
	var texture := ImageTexture.create_from_image(image)
	var material := ShaderMaterial.new()
	material.shader = load("res://tools/shaders/emitter_grid_intensity_parity.gdshader")
	material.set_shader_parameter("samples", texture)
	material.set_shader_parameter("solar_visibility", solar_visibility)
	material.set_shader_parameter("hub_day", hub_day)
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
	var names := ["pulse", "intensity", "background", "day", "lit"]
	var worst := [0.0, 0.0, 0.0, 0.0, 0.0]
	var bad := 0
	var lit := 0
	for i in SAMPLE_COUNT:
		for field in FIELDS:
			var o := (i * FIELDS + field) * 4
			var gpu := PackedByteArray([bytes[o], bytes[o + 1], bytes[o + 2], bytes[o + 3]]).decode_float(0)
			var want: float = expected[i * FIELDS + field]
			if field == 0 and want > 0.01:
				lit += 1
			if is_nan(gpu) or is_inf(gpu):
				bad += 1
				continue
			var err := absf(gpu - want)
			worst[field] = maxf(worst[field], err)
			if err > TOLERANCE:
				bad += 1
				if bad < 12:
					print("  mismatch %s %s sample=%d gpu=%.7f cpu=%.7f" % [case["name"], names[field], i, gpu, want])
	print("EMITTER_GRID_SHADER_PARITY case=%s t=%.2f fronts=%d lit_samples=%d worst_abs_err(pulse=%.7f intensity=%.7f background=%.7f day=%.7f lit_rule=%.1f) events=%d bad=%d" % [
		case["name"], t, int(uniforms["eg_front_count"]), lit, worst[0], worst[1], worst[2], worst[3], worst[4], fixture.EventFrontCount(), bad])
	vp.queue_free()
	if lit < 50:
		_fail("case %s has too few lit samples to be meaningful" % case["name"])
		return false
	if bad > 0:
		_fail("case %s parity" % case["name"])
		return false
	return true


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
