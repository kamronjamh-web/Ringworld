extends SceneTree
# Windowed GPU parity: the far-megastorm GLSL (across noise, strip column terms, flash glow in materials/ring_megastorm_profile.gdshaderinc and
# materials/ring_far_storm_lighting.gdshaderinc) versus MegastormProfile.AcrossTerms / Column and MegastormLightning.GlowAt in C#.
# Needs a rendering device: do not run headless, and do not run it while another Godot process is open. Modelled on tools/eclipse_hub_beam_shader_parity_test.gd.
# Usage: godot --path . --script res://tools/megastorm_far_parity_test.gd
const FIXTURE := preload("res://tools/MegastormFarParityFixture.cs")

const FIELDS := 14
const ROWS := 6
const NAMES := ["tower_noise", "mammatus_noise", "shaft_noise", "body_base", "body_top", "canopy_under", "canopy_top",
	"canopy_cov", "mammatus", "tower_cov", "body_cov", "glow", "glow_reduced"]
const HEIGHT_TOL := 2.0
const VALUE_TOL := 2e-3
const GLOW_REL_TOL := 5e-3
const MIN_GLOW_CASES := 20

var failed := false


func _init() -> void:
	call_deferred("_run")
	create_timer(300.0).timeout.connect(func() -> void: _fail("timeout"))


func _fail(message: String) -> void:
	failed = true
	push_error("MEGASTORM_FAR_PARITY=FAIL " + message)
	quit(1)


func _frames(count: int) -> void:
	for i in count:
		await process_frame


func _run() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("requires Vulkan/Forward+ (do not run headless)")
		return
	var fixture = FIXTURE.new()
	var count: int = fixture.Build()
	var inputs: PackedFloat32Array = fixture.Inputs()
	var expected: PackedFloat32Array = fixture.Expected()
	var image := Image.create_from_data(count, ROWS, false, Image.FORMAT_RGBAF, inputs.to_byte_array())
	var texture := ImageTexture.create_from_image(image)
	var material := ShaderMaterial.new()
	material.shader = load("res://tools/shaders/megastorm_far_parity.gdshader")
	material.set_shader_parameter("cases", texture)
	var vp := SubViewport.new()
	vp.size = Vector2i(count * FIELDS, 1)
	vp.transparent_bg = true
	vp.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	var rect := ColorRect.new()
	rect.size = Vector2(count * FIELDS, 1)
	rect.material = material
	vp.add_child(rect)
	root.add_child(vp)
	await _frames(4)
	var out := vp.get_texture().get_image()
	out.convert(Image.FORMAT_RGBA8)
	var bytes := out.get_data()
	var worst := []
	worst.resize(NAMES.size())
	worst.fill(0.0)
	var worst_rel := 0.0
	var bad := 0
	var glow_cases := 0
	var glow_reduced_cases := 0
	var max_glow := 0.0
	for i in count:
		for field in NAMES.size():
			var o := (i * FIELDS + field) * 4
			var gpu := PackedByteArray([bytes[o], bytes[o + 1], bytes[o + 2], bytes[o + 3]]).decode_float(0)
			var want: float = expected[i * FIELDS + field]
			if field == 11 and want > 0.05:
				glow_cases += 1
			if field == 12 and want > 0.05:
				glow_reduced_cases += 1
			if field >= 11:
				max_glow = maxf(max_glow, want)
			if is_nan(gpu) or is_inf(gpu):
				bad += 1
				print("  non-finite %s case=%d" % [NAMES[field], i])
				continue
			var err := absf(gpu - want)
			worst[field] = maxf(worst[field], err)
			var ok: bool
			if field >= 11:
				var rel := err / maxf(absf(want), 1e-9)
				if want > 0.05:
					worst_rel = maxf(worst_rel, rel)
				ok = err <= VALUE_TOL or rel <= GLOW_REL_TOL
			elif field >= 3 and field <= 6:
				ok = err <= HEIGHT_TOL
			else:
				ok = err <= VALUE_TOL
			if not ok:
				bad += 1
				if bad < 20:
					print("  mismatch %s case=%d t=%.3f gpu=%.7f cpu=%.7f" % [NAMES[field], i, expected[i * FIELDS + 13], gpu, want])
	var summary := "MEGASTORM_FAR_PARITY cases=%d glow_cases_above_0.05=%d reduced=%d max_glow=%.3f glow_max_rel=%.5f" % [count, glow_cases, glow_reduced_cases, max_glow, worst_rel]
	for field in NAMES.size():
		summary += " %s=%.7f" % [NAMES[field], worst[field]]
	print(summary + " bad=%d" % bad)
	vp.queue_free()
	if bad > 0:
		_fail("parity mismatches: %d" % bad)
		return
	if glow_cases < MIN_GLOW_CASES or glow_reduced_cases < MIN_GLOW_CASES:
		_fail("too few non-zero glow cases (%d normal, %d reduced) to be meaningful" % [glow_cases, glow_reduced_cases])
		return
	print("MEGASTORM_FAR_PARITY=PASS cases=%d" % count)
	quit(0)
