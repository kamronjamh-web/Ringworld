extends SceneTree

## TR-7b: the shared tree card (crossed cards at 325 m, camera-facing quad at 900 m) must look like the authored LOD2 mesh it replaces. The card atlas is TR-7a's bake of that
## LOD2 (albedo and shading normal); the shaders and materials are used as shipped (simplified tree shading), haze off, at the pixel scale of a 1080p, 70 degree frame
## (fov 8.3 degrees over 128 px). Ambient and sun are gated separately, because a test that mixes them into one luminance cannot be trusted at another ambient level
## (the game has sun energy 4 and ambient 0.35).
##
## Two models are drawn. The "shipped model" is the LOD2 as the game draws it at 325 m (128 px). The "reference model" is the same meshes with each material's shader swapped for a
## test-local copy that reads the albedo texture at mip 0, drawn in a 512 px viewport (512 and 1024 agree within 0.2 %): the model as it looks close up, seen from 325 m. It is not the
## shipped material because the model's own leaf-texture mips change its look with distance and resolution (a whole leaf cluster's alpha becomes one average tested against the cutoff, and
## its colour includes the texels under the cut-out), so "the model at 325 m at 1080p" is one point on a curve; a colour gate against it could not hold at 4K or at another hand-off
## distance. The cards and the quad carry the close-up look outward. Covered area, which is the hand-off silhouette, is gated against the shipped model. TR-7c (user D7 and D8: yes) gives the model distance-stable leaf mips (the loader swaps the leaf atlases and the shader reads the cutout alpha isotropically), so the shipped model now has
## gates of its own against the reference model, species means: covered area within 10 % at 128 px, its 64 and 46 px area within 15 and 20 % of the 128 px value, ambient luminance within 5 %
## and channels within 8 % of the luminance, sun only within 10 / 10 / 15 %; the reference shader copy replaces the isotropic alpha read as well. No multiplier or enlargement is used here or allowed.
##
## Gates (card = the crossed cards at 325 m; quad = the far quad at 900 m with the lens narrowed so a tree keeps its 325 m pixel size):
##   (TR-7c-2b: the cards' and quad's gates against the reference model carry the measured floor of cards thinned by an alpha threshold: cards species mean 12 / 10 / 15 %, card variants 25 / 25 / 25 %,
##   ambient channels 10 % for cards and quad; the quad's species sun gates stay 10 / 10 / 15 %. See CARD_SUN_TOLERANCE.)
##   cards: covered area against the shipped model within 8 % per variant (ambient case, azimuth mean);
##   quad: covered area against the cards' covered area at 46 px (HANDOFF_SIZE, the hand-off pixel size; mean of 12 azimuths) within 8 % per variant; the shipped model at 46 px keeps too little
##         area to be a target, so the quad is calibrated down to the cards (the quad against the shipped model at 128 px is reported only, quad_over_shipped_model_128);
##   both: ambient against the reference model: luminance within 5 % and each channel's difference within 10 % of the reference luminance (per variant);
##   both: sun only against the reference model over 12 azimuths (15 + 30 k degrees) at noon, 30 and 8 degrees: the species mean of the variant ratios within 12 / 10 / 15 % (cards) or 10 / 10 / 15 % (quad);
##         each variant within 25 % (cards) or 40 % (quad: one picture per tree);
##   cards: scale stability: covered fraction in a 64 px viewport within 15 % and in a 46 px viewport within 20 % of the 128 px value (species mean, same lens);
##   quad: the camera-side check for seed 0 and for a mirrored seed (fract(seed * 7919) > 0.5); the mirror identity (seed 0 with the sun 90 degrees to the right of the camera
##         equals the mirrored seed with the sun 90 degrees to the left, within 3 %); scale: covered fraction at 46 px within 20 % of its 128 px value, and against the cards at 46 px within 10 %;
##   hand-off step: cards against the shipped model, sun only, species mean within 10 / 10 / 15 % (report json handoff_step; the step D7 was about; TR-7c tightened it from 25 %);
##   shipped model (TR-7c): against the reference model, the species-mean gates above (report json model, model_scale); the quad / shipped model area at 128 px stays report only;
##   report only (TR-6b): camera 60 degrees above the horizon, noon and sun 30, reference model, cards and quad (report json top_view);
##   control: with the bake swapped for the pack's pictures the card gates (ambient, noon) must fail for at least half of the variants (the test must be able to see the bake missing);
##   control (TR-7c): with the importer's mips back on the model (UseLeafMips(false)) the model gates (coverage, ambient, noon) must fail for maple and pine (the test must be able to see the leaf mips missing).
## Modes (environment TREE_BILLBOARD_MATCH_ARGS, since run_tests.ps1 passes no arguments):
##   (none)        verify: the bake and the checked-in calibration (resources/trees/prepared/lolipop_impostor_calibration.res, version 3) must load, then the gates and the control.
##   --calibrate   per scan cutoff, apply it to both tiers and measure the cards (325 m) and the quad (900 m); each tier's cutoff is the crossing with the shipped model's covered area for the cards and
##                 with the cards' covered area at 46 px for the quad (cards first, then the quad); write the calibration (the catalog rebuilds each tier's alpha mips for its cutoff), then verify. Run on the 4070 and check in.
##   --sweep       development aid: mean card/reference ratios over all variants per lighting case; no gate.
##   --dump        also save, for v3, v13 and v19 at 325 m in ambient and at noon, the shipped model, the reference model and the cards as one labelled contact sheet,
##                 .godot/tree_billboard_match/d7_contact_sheet_<mode>.png (3 columns: shipped, reference, cards; 6 rows: per variant ambient then noon; 256 px tiles on grey).
## Run (windowed Vulkan, not --headless; no other Godot process running):
##   tools/run_tests.ps1 -Tests tree_billboard_match_gpu_test -Label tr7b
## Prints TREE_BILLBOARD_MATCH_GPU_TEST=PASS|FAIL. Report: .godot/tree_billboard_match/report_<mode>.json and table_<mode>.csv (mode = verify or calibrate; a run never overwrites the other's evidence).
const SIZE := 128
const REFERENCE_SIZE := 512
const VARIANTS := 30
const FOV_DEGREES := 8.3
const CARD_DISTANCE := 325.0
const QUAD_DISTANCE := 900.0
const ELEVATIONS := [90.0, 30.0, 8.0] # sun elevation above the horizon, degrees
const AMBIENT := -1 # lighting case: white ambient only, the sun off (the sun direction stays at noon so the day factor is 1)
const AMBIENT_ENERGY := 1.0
const AZIMUTHS := [30.0, 120.0, 210.0, 300.0] # camera azimuth around the tree relative to the sun azimuth, degrees (ambient case and coverage)
const SPECIES := ["maple", "fir", "pine"]
const SUN_TOLERANCE := [0.10, 0.10, 0.15] # species mean of the variant sun ratios, per sun elevation
# The cards' bounds are the measured floor of cards thinned by an alpha threshold (docs/work-orders/terrain-vegetation/TR.md, "TR-7c-2 lead decision"): they go back to 10 %, 20 / 20 / 25 % and 8 % if TR-7d is built.
const CARD_SUN_TOLERANCE := [0.12, 0.10, 0.15] # cards species mean of the variant sun ratios, per sun elevation
const CARD_VARIANT_SUN_TOLERANCE := [0.25, 0.25, 0.25]
const CARD_CHANNEL_TOLERANCE := 0.10 # of the reference luminance, cards and quad
const QUAD_VARIANT_SUN_TOLERANCE := [0.40, 0.40, 0.40]
const LUMINANCE_TOLERANCE := 0.05
const CHANNEL_TOLERANCE := 0.08 # of the reference luminance
const COVERAGE_TOLERANCE := 0.08
const MODEL_COVERAGE_TOLERANCE := 0.10 # shipped model against the reference model, species mean covered area at 128 px
const MODEL_SCALE_64_TOLERANCE := 0.15
const MODEL_SCALE_46_TOLERANCE := 0.20
const MIRROR_TOLERANCE := 0.03
const SCALE_CARD_64_TOLERANCE := 0.15
const SCALE_CARD_46_TOLERANCE := 0.20
const SCALE_QUAD_TOLERANCE := 0.20
const QUAD_AGAINST_CARDS_TOLERANCE := 0.10
const HANDOFF_SIZE := 46 # viewport edge at which a 325 m tree has its 900 m pixel size: where the cards hand over to the quad
const MIRRORED_SEED := 0.75 / 7919.0 # fract(seed * 7919) = 0.75 > 0.5: the shader draws the picture mirrored
const TOP_VIEW_ELEVATION := 60.0
const CUTOFF_SCAN := [0.05, 0.08, 0.16, 0.24, 0.32, 0.40, 0.48, 0.56, 0.64, 0.72, 0.80, 0.88]
const DUMP_VARIANTS := [3, 13, 19]
const DUMP_BACKGROUND := Color(0.62, 0.66, 0.72, 1.0) # sky-ish grey in place of the measuring magenta
const DUMP_KIND_NAMES := {"ship": "shipped model", "ref": "reference model (mip 0)", "card": "cards"}
const BACKGROUND := Color(1.0, 0.0, 1.0, 1.0)
const OUT := "res://.godot/tree_billboard_match"
const UP := Vector3(0, 0, 1) # the tree's surface-up in the test world (ring_center lies far along +Z, so the shaders' surface_up is +Z)
const MIP_ZERO_FROM := "texture(albedo_texture, UV)"
const MIP_ZERO_TO := "textureLod(albedo_texture, UV, 0.0)"
const ALPHA_MIP_ZERO_FROM := "textureLod(leaf_alpha_texture, UV, leaf_alpha_lod)" # TR-7c: the isotropic cutout alpha read of the shipped shader (the reference model still reads mip 0)
const ALPHA_MIP_ZERO_TO := "textureLod(leaf_alpha_texture, UV, 0.0)"

var failed := false
var done := false
var calibrate := false
var dump := false
var mode_name := "verify"
var viewport: SubViewport
var environment: Environment
var camera: Camera3D
var sun: DirectionalLight3D
var driver: Node
var mesh_instance: MeshInstance3D
var card_instance: MultiMeshInstance3D
var quad_instance: MultiMeshInstance3D
var card_multi: MultiMesh
var quad_multi: MultiMesh
var lod2_meshes: Array[Mesh] = []
var heights: Array[float] = []
var materials: Array[ShaderMaterial] = []
var impostor_materials: Array[ShaderMaterial] = []
var quad_seed := 0.0 # instance custom data z of the quad (the mirroring seed)
var capture_dump := false
var dump_images := {}
var shader_originals := {}

func _initialize() -> void:
	var arguments := OS.get_cmdline_user_args()
	var env_arguments := OS.get_environment("TREE_BILLBOARD_MATCH_ARGS")
	calibrate = "--calibrate" in arguments or env_arguments.contains("--calibrate")
	dump = "--dump" in arguments or env_arguments.contains("--dump")
	mode_name = "calibrate" if calibrate else "verify"
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice required; Dummy renderer is invalid")
		return
	call_deferred("_run")

func _run() -> void:
	create_timer(3400.0).timeout.connect(_on_watchdog)
	_make_scene()
	if failed: return
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUT))
	var env_arguments := OS.get_environment("TREE_BILLBOARD_MATCH_ARGS")
	if not driver.call("ImpostorBakeLoaded"):
		if env_arguments.contains("--sweep"): print("TREE_BILLBOARD_MATCH sweep without the bake (pack pictures): %s" % driver.call("GetImpostorBakeFallbackReason"))
		else:
			_fail("the impostor atlas bake is missing or stale (%s); run tools/prepare_tree_impostor_atlas.gd first" % driver.call("GetImpostorBakeFallbackReason"))
			return
	if env_arguments.contains("--sweep"):
		await _run_sweep()
		return
	if not driver.call("LeafMipsLoaded"):
		_fail("the distance-stable leaf mips are not on the model (resource missing or stale); run tools/run_tests.ps1 -Tests prepare_tree_leaf_mips first")
		return
	var report := {"mode": mode_name, "fingerprint": driver.call("GetImpostorFingerprint")}
	if calibrate:
		await _run_calibration(report)
		if failed: return
	elif not driver.call("ImpostorCalibrationLoaded"):
		_fail("no valid calibration: %s is missing or stale for fingerprint %s (run with --calibrate)" % ["resources/trees/prepared/lolipop_impostor_calibration.res", driver.call("GetImpostorFingerprint")])
		return
	report["cutoffs"] = {"cards": Array(driver.call("GetImpostorCalibrationCutoffs")), "quad": Array(driver.call("GetImpostorCalibrationQuadCutoffs"))}
	var tables := {}
	# Shipped model, reference model, cards (325 m) and quad (900 m): every lighting case. Sun cases use 12 azimuths, ambient 4.
	capture_dump = dump
	tables["ship"] = await _measure_kind("ship", CARD_DISTANCE)
	tables["ref"] = await _measure_kind("ref", CARD_DISTANCE, REFERENCE_SIZE)
	if failed: return
	tables["card"] = await _measure_kind("card", CARD_DISTANCE)
	capture_dump = false
	tables["quad"] = await _measure_kind("quad", QUAD_DISTANCE)
	var ship_targets := _ship_coverage(tables["ship"])
	var quad_targets := await _cards_at_handoff()
	if failed: return
	var card_result := _evaluate(tables["ship"], tables["ref"], tables["card"], CARD_VARIANT_SUN_TOLERANCE, ship_targets, "the shipped model", CARD_SUN_TOLERANCE, CARD_CHANNEL_TOLERANCE)
	var quad_result := _evaluate(tables["ship"], tables["ref"], tables["quad"], QUAD_VARIANT_SUN_TOLERANCE, quad_targets, "the cards at %d px" % HANDOFF_SIZE, SUN_TOLERANCE, CARD_CHANNEL_TOLERANCE)
	report["card"] = card_result
	report["quad"] = quad_result
	report["quad_coverage_target_cards_at_46"] = quad_targets
	report["quad_over_shipped_model_128"] = _quad_over_shipped(tables["ship"], tables["quad"])
	var failures: Array[String] = []
	for entry: Array in [["card", card_result], ["quad", quad_result]]:
		for message: String in entry[1]["failures"]: failures.append("%s: %s" % [entry[0], message])
	# Hand-off step: cards against the shipped model, sun only (the species mean within the sun tolerances, TR-7c).
	var handoff := _handoff_step(tables["ship"], tables["card"])
	report["handoff_step"] = handoff
	for species in 3:
		for elevation in 3:
			var step: float = handoff["species_mean"][species][elevation]
			if absf(step - 1.0) > float(SUN_TOLERANCE[elevation]):
				failures.append("handoff_step: %s sun%d cards/shipped model %.3f outside %.0f %%" % [SPECIES[species], int(ELEVATIONS[elevation]), step, float(SUN_TOLERANCE[elevation]) * 100.0])
	# The shipped model against the reference model (TR-7c): covered area, ambient, sun, and its own scale stability.
	var model_result := _evaluate_model(tables["ref"], tables["ship"])
	report["model"] = model_result
	for message: String in model_result["failures"]: failures.append("model: " + message)
	var model_scale := await _model_scale(tables["ship"])
	report["model_scale"] = model_scale
	for message: String in model_scale["failures"]: failures.append(message)
	# Scale stability: the covered fraction against the 128 px value, in smaller viewports with the same lens.
	var scale := await _scale_stability(tables["card"], tables["quad"], quad_targets)
	report["scale"] = scale
	for message: String in scale["failures"]: failures.append(message)
	# The quad is lit from the camera side (seed 0 and the mirrored seed), and the mirror identity.
	var camera_side := await _camera_side_check()
	report["camera_side"] = camera_side
	for message: String in camera_side["failures"]: failures.append(message)
	var mirror := await _mirror_identity()
	report["mirror_identity"] = mirror
	for message: String in mirror["failures"]: failures.append(message)
	# Report only (TR-6b): the top view.
	var top_view := await _top_view()
	report["top_view"] = top_view
	# Control: the pack's own pictures in place of the bake. It has to miss the gates, or the test could not see a missing bake.
	if driver.call("UseImpostorBake", false):
		_fail("the control could not switch the bake off")
		return
	tables["control"] = await _measure_kind("card", CARD_DISTANCE, SIZE, [AMBIENT, 0])
	var control_result := _evaluate(tables["ship"], tables["ref"], tables["control"], SUN_TOLERANCE, ship_targets, "the shipped model", SUN_TOLERANCE, CHANNEL_TOLERANCE)
	report["control_pack_pictures"] = control_result
	driver.call("UseImpostorBake", true)
	# Control (TR-7c): the importer's mips and the anisotropic alpha read back on the model; the model gates must fail for maple and pine.
	if driver.call("UseLeafMips", false):
		_fail("the control could not switch the leaf mips off")
		return
	tables["control_model"] = await _measure_kind("ship", CARD_DISTANCE, SIZE, [AMBIENT, 0])
	var control_model := _evaluate_model(tables["ref"], tables["control_model"])
	driver.call("UseLeafMips", true)
	report["control_importer_mips"] = control_model
	_write_report(report, tables)
	if dump: await _write_dump()
	for entry: Array in [["card", card_result], ["quad", quad_result]]:
		var r: Dictionary = entry[1]
		print("TREE_BILLBOARD_MATCH %s: failing variants %d/%d; species means (maple/fir/pine): coverage vs target %s, ambient luminance vs reference %s, sun noon %s, sun30 %s, sun8 %s; worst variant sun deviation %s" % [entry[0], r["failing_variants"], VARIANTS, _fmt(r["species_coverage"]), _fmt(r["species_ambient_luminance"]), _fmt(r["species_sun"][0]), _fmt(r["species_sun"][1]), _fmt(r["species_sun"][2]), _fmt(r["worst_variant_sun"])])
	var over_shipped: Dictionary = report["quad_over_shipped_model_128"]
	print("TREE_BILLBOARD_MATCH quad / shipped model covered area at 128 px (report only), species mean maple/fir/pine: %s" % _fmt(over_shipped["species_mean"]))
	var step_means: Array = handoff["species_mean"]
	print("TREE_BILLBOARD_MATCH handoff_step (cards / shipped model, sun only, species mean maple fir pine): noon %s, sun30 %s, sun8 %s" % [_fmt([step_means[0][0], step_means[1][0], step_means[2][0]]), _fmt([step_means[0][1], step_means[1][1], step_means[2][1]]), _fmt([step_means[0][2], step_means[1][2], step_means[2][2]])])
	print("TREE_BILLBOARD_MATCH scale (species mean covered fraction against 128 px): cards 64 px %s, cards 46 px %s; quad 46 px %s; quad/cards at 46 px %s" % [_fmt(scale["cards_64"]), _fmt(scale["cards_46"]), _fmt(scale["quad_46"]), _fmt(scale["quad_over_cards_46"])])
	print("TREE_BILLBOARD_MATCH camera side: quad lit from the camera in %d / %d of %d variants (seed 0 / mirrored; shipped model %d); mirror identity worst %.3f" % [camera_side["quad_ok"], camera_side["quad_ok_mirrored"], VARIANTS, camera_side["mesh_ok"], mirror["worst"]])
	var model_means: Dictionary = model_result
	print("TREE_BILLBOARD_MATCH model (shipped model against the reference model, species mean maple/fir/pine): coverage %s, ambient luminance %s, sun noon %s, sun30 %s, sun8 %s; scale 64 px %s, 46 px %s" % [_fmt(model_means["species_coverage"]), _fmt(model_means["species_ambient_luminance"]), _fmt(model_means["species_sun"][0]), _fmt(model_means["species_sun"][1]), _fmt(model_means["species_sun"][2]), _fmt(model_scale["model_64"]), _fmt(model_scale["model_46"])])
	var control_species_failed: Array = control_model["species_failed"]
	print("TREE_BILLBOARD_MATCH control (importer mips on the model, coverage/ambient/noon): species failing maple/fir/pine %s; coverage %s, ambient luminance %s, noon %s" % [control_species_failed, _fmt(control_model["species_coverage"]), _fmt(control_model["species_ambient_luminance"]), _fmt(control_model["species_sun"][0])])
	if not (control_species_failed[0] and control_species_failed[2]):
		_fail("the control with the importer mips on the model does not fail the model gates for both maple and pine (%s); the test cannot tell missing leaf mips" % [control_species_failed])
		return
	var control_fail_variants: int = control_result["failing_variants"]
	print("TREE_BILLBOARD_MATCH control (pack pictures instead of the bake): failing variants %d/%d" % [control_fail_variants, VARIANTS])
	if control_fail_variants < VARIANTS / 2:
		_fail("the control with the pack's pictures fails only %d of %d variants; the test cannot tell a missing bake" % [control_fail_variants, VARIANTS])
		return
	if failures.size() != 0:
		for message in failures.slice(0, 40): print("TREE_BILLBOARD_MATCH GATE %s" % message)
		_fail("%d gate failures (see .godot/tree_billboard_match/report_%s.json; first: %s)" % [failures.size(), mode_name, failures[0]])
		return
	print("TREE_BILLBOARD_MATCH_GPU_TEST=PASS cards and quad inside tolerance for %d variants; quad lit from the camera side; control fails %d" % [VARIANTS, control_fail_variants])
	done = true
	quit(0)

func _fmt(values: Array) -> String:
	var parts := []
	for value: float in values: parts.append("%.3f" % value)
	return "/".join(parts)

## Development aid: mean card/reference ratios over all variants per lighting case, with the current cutoffs.
func _run_sweep() -> void:
	var ref_table := await _measure_kind("ref", CARD_DISTANCE, REFERENCE_SIZE)
	if failed: return
	var ship_table := await _measure_kind("ship", CARD_DISTANCE)
	var card_table := await _measure_kind("card", CARD_DISTANCE)
	for lighting: int in _lighting_cases():
		var lum := 0.0; var rr := 0.0; var gg := 0.0; var bb := 0.0; var cov := 0.0
		for variant in VARIANTS:
			var a: Array = _mean_cases(ref_table[variant][lighting])
			var b: Array = _mean_cases(card_table[variant][lighting])
			var c: Array = _mean_cases(ship_table[variant][lighting])
			lum += b[4] / maxf(a[4], 1e-6); rr += b[1] / maxf(a[1], 1e-6); gg += b[2] / maxf(a[2], 1e-6); bb += b[3] / maxf(a[3], 1e-6); cov += b[0] / maxf(c[0], 1e-6)
		print("SWEEP %s | lum %.2f rgb %.2f/%.2f/%.2f cov %.2f" % [_lighting_name(lighting), lum / VARIANTS, rr / VARIANTS, gg / VARIANTS, bb / VARIANTS, cov / VARIANTS])
	done = true
	quit(0)

# --- calibration -----------------------------------------------------------------------------------------------------------------------------------------------

## Per variant and tier, the alpha cutoff at which the covered area (mean over the azimuths, ambient case) meets its target: the cards at 325 m against the shipped model; the quad at 900 m (same
## pixel size) against the cards' covered area at the hand-off size (_cards_at_handoff, with the final card cutoffs). Both tiers are scanned together (the same cutoff on both), then the cards are
## refined, then the quad; the catalog builds each tier's alpha mips for the cutoff in use, so what is measured here is what ships.
func _run_calibration(report: Dictionary) -> void:
	var neutral := PackedFloat32Array()
	driver.call("ApplyImpostorCalibration", neutral, neutral)
	var defaults: PackedFloat32Array = driver.call("GetImpostorDefaultCutoffs")
	var ship_cover: Array[float] = []
	for variant in VARIANTS:
		var total := 0.0
		for azimuth: float in AZIMUTHS:
			total += (await _measure("ship", variant, CARD_DISTANCE, AMBIENT, azimuth))[0]
		ship_cover.append(total / AZIMUTHS.size())
	var card_cover: Array = []
	var quad_cover: Array = []
	for variant in VARIANTS:
		card_cover.append([])
		quad_cover.append([])
	for cutoff: float in CUTOFF_SCAN:
		var scan_cutoffs := PackedFloat32Array(); scan_cutoffs.resize(VARIANTS); scan_cutoffs.fill(cutoff)
		driver.call("ApplyImpostorCalibration", scan_cutoffs, scan_cutoffs)
		for variant in VARIANTS:
			var card_total := 0.0
			var quad_total := 0.0
			for azimuth: float in AZIMUTHS:
				card_total += (await _measure("card", variant, CARD_DISTANCE, AMBIENT, azimuth))[0]
				quad_total += (await _measure("quad", variant, QUAD_DISTANCE, AMBIENT, azimuth))[0]
			card_cover[variant].append(card_total / AZIMUTHS.size())
			quad_cover[variant].append(quad_total / AZIMUTHS.size())
	var cutoffs := PackedFloat32Array(); cutoffs.resize(VARIANTS)
	var quad_cutoffs := PackedFloat32Array(); quad_cutoffs.resize(VARIANTS)
	var cutoff_note := []
	var unreachable := []
	var card_state := []
	var quad_state := []
	for variant in VARIANTS:
		card_state.append(_bracket(CUTOFF_SCAN, card_cover[variant], ship_cover[variant], defaults[variant]))
	# Refinement: the coverage curve is not linear between scan cutoffs (the alpha mips are rebuilt for every cutoff), so bisect inside each bracket, per variant, keeping the closest measured cutoff.
	# The cards first, against the shipped model (the quad takes the same cutoffs in these calls; it is not measured here).
	for iteration in 7:
		for variant in VARIANTS: cutoffs[variant] = (card_state[variant]["lo"] + card_state[variant]["hi"]) * 0.5
		driver.call("ApplyImpostorCalibration", cutoffs, cutoffs)
		for variant in VARIANTS:
			var card_total := 0.0
			for azimuth: float in AZIMUTHS:
				card_total += (await _measure("card", variant, CARD_DISTANCE, AMBIENT, azimuth))[0]
			_refine(card_state[variant], cutoffs[variant], card_total / AZIMUTHS.size(), ship_cover[variant])
	for variant in VARIANTS: cutoffs[variant] = card_state[variant]["best"]
	driver.call("ApplyImpostorCalibration", cutoffs, cutoffs)
	# The quad's target: the cards' covered area at the hand-off pixel size, with the final card cutoffs. The quad is calibrated at 128 px (its 46 px image is too few pixels to bisect on and it holds its area).
	var handoff := await _cards_at_handoff()
	if failed: return
	for variant in VARIANTS: quad_state.append(_bracket(CUTOFF_SCAN, quad_cover[variant], handoff[variant], defaults[variant]))
	for iteration in 7:
		for variant in VARIANTS: quad_cutoffs[variant] = (quad_state[variant]["lo"] + quad_state[variant]["hi"]) * 0.5
		driver.call("ApplyImpostorCalibration", cutoffs, quad_cutoffs)
		for variant in VARIANTS:
			var quad_total := 0.0
			for azimuth: float in AZIMUTHS:
				quad_total += (await _measure("quad", variant, QUAD_DISTANCE, AMBIENT, azimuth))[0]
			_refine(quad_state[variant], quad_cutoffs[variant], quad_total / AZIMUTHS.size(), handoff[variant])
	for variant in VARIANTS:
		quad_cutoffs[variant] = quad_state[variant]["best"]
		if not card_state[variant]["found"]: unreachable.append("card v%d" % variant)
		if not quad_state[variant]["found"]: unreachable.append("quad v%d" % variant)
		cutoff_note.append({"variant": variant, "authored": defaults[variant], "card": cutoffs[variant], "quad": quad_cutoffs[variant], "shipped_model_coverage": ship_cover[variant], "quad_target_cards_at_46": handoff[variant], "card_coverage_per_scan_cutoff": card_cover[variant], "quad_coverage_per_scan_cutoff": quad_cover[variant], "card_best_error": card_state[variant]["best_error"], "quad_best_error": quad_state[variant]["best_error"]})
	driver.call("ApplyImpostorCalibration", cutoffs, quad_cutoffs)
	var save_error: int = driver.call("SaveImpostorCalibration", cutoffs, quad_cutoffs)
	if save_error != OK:
		_fail("could not write the calibration resource (error %d)" % save_error)
		return
	report["calibration"] = {"cutoffs": cutoff_note, "card_cutoff_values": Array(cutoffs), "quad_cutoff_values": Array(quad_cutoffs), "no_crossing": unreachable}
	print("TREE_BILLBOARD_MATCH calibration (version 3: cards and quad) written; no crossing between the scan values for %s" % [unreachable])

## Per variant, the cards' covered fraction (ambient) in a HANDOFF_SIZE viewport at 325 m, mean over 12 azimuths (15 + 30 k): the target of the quad's covered area.
func _cards_at_handoff() -> Array[float]:
	var result: Array[float] = []
	for variant in VARIANTS:
		var total := 0.0
		for k in 12: total += (await _measure("card", variant, CARD_DISTANCE, AMBIENT, 15.0 + 30.0 * k, HANDOFF_SIZE))[0]
		result.append(total / 12.0)
	return result

## Per variant, the shipped model's ambient covered fraction (azimuth mean): the cards' target.
func _ship_coverage(ship: Array) -> Array[float]:
	var result: Array[float] = []
	for variant in ship.size(): result.append(_mean_cases(ship[variant][AMBIENT])[0])
	return result

## Report only: the quad's covered area at 128 px against the shipped model's, species means and per variant.
func _quad_over_shipped(ship: Array, quad: Array) -> Dictionary:
	var per_variant := []
	var per_species := [[], [], []]
	for variant in VARIANTS:
		var ratio: float = _mean_cases(quad[variant][AMBIENT])[0] / maxf(_mean_cases(ship[variant][AMBIENT])[0], 1e-6)
		per_variant.append(ratio)
		per_species[_species(variant)].append(ratio)
	return {"species_mean": [_mean_of(per_species[0]), _mean_of(per_species[1]), _mean_of(per_species[2])], "per_variant": per_variant}

## The bracket of scan cutoffs around the crossing of the (decreasing) coverage curve with the target: {lo, hi, found, best, best_error}. Without a crossing (found false) the bracket is
## the lowest or highest scan cutoff alone.
func _bracket(scan: Array, coverage: Array, target: float, fallback: float) -> Dictionary:
	if target <= 0.0: return {"lo": fallback, "hi": fallback, "found": false, "best": fallback, "best_error": 1.0}
	var best := 0
	for i in range(scan.size()):
		if absf(coverage[i] / target - 1.0) < absf(coverage[best] / target - 1.0): best = i
	for i in range(scan.size() - 1):
		var a: float = coverage[i]
		var b: float = coverage[i + 1]
		if (a >= target and b <= target) or (a <= target and b >= target):
			return {"lo": scan[i], "hi": scan[i + 1], "found": true, "best": scan[best], "best_error": absf(coverage[best] / target - 1.0)}
	var end: float = scan[0] if coverage[0] < target else scan[scan.size() - 1]
	return {"lo": end, "hi": end, "found": false, "best": end, "best_error": absf(coverage[best] / target - 1.0)}

## One bisection step: `cutoff` (the bracket's middle) was measured to cover `coverage`; coverage falls as the cutoff rises.
func _refine(state: Dictionary, cutoff: float, coverage: float, target: float) -> void:
	if not state["found"] or target <= 0.0: return
	var error := absf(coverage / target - 1.0)
	if error < state["best_error"]:
		state["best_error"] = error
		state["best"] = cutoff
	if coverage > target: state["lo"] = cutoff
	else: state["hi"] = cutoff

# --- measurement -----------------------------------------------------------------------------------------------------------------------------------------------

func _lighting_cases() -> Array:
	return [AMBIENT, 0, 1, 2]

func _lighting_name(lighting: int) -> String:
	return "ambient" if lighting == AMBIENT else "sun%d" % int(ELEVATIONS[lighting])

## Camera azimuths of a lighting case relative to the sun: 4 for ambient (coverage and colour), 12 (15 + 30 k) for the sun cases.
func _azimuths(lighting: int) -> Array:
	if lighting == AMBIENT: return AZIMUTHS
	var result := []
	for k in 12: result.append(15.0 + 30.0 * k)
	return result

func _species(variant: int) -> int:
	return 0 if variant < 12 else (1 if variant < 15 else 2)

## table[variant][lighting] = the azimuths' [covered fraction of the viewport, mean linear r, g, b, luminance] for one kind ("ship", "ref", "card" or "quad"). The reference model is
## measured with the albedo read at mip 0 (the shader swap is made and undone here).
func _measure_kind(kind: String, distance: float, size: int = SIZE, lightings: Array = [AMBIENT, 0, 1, 2]) -> Array:
	if kind == "ref" and not _swap_reference(true): return []
	var table := []
	for variant in VARIANTS:
		var per_lighting := {}
		for lighting: int in lightings:
			var cases := []
			for azimuth: float in _azimuths(lighting):
				cases.append(await _measure(kind, variant, distance, lighting, azimuth, size))
			per_lighting[lighting] = cases
		table.append(per_lighting)
	if kind == "ref": _swap_reference(false)
	return table

func _mean_cases(cases: Array) -> Array:
	var total := [0.0, 0.0, 0.0, 0.0, 0.0]
	for value: Array in cases:
		for i in 5: total[i] += value[i]
	for i in 5: total[i] /= cases.size()
	return total

func _mean_of(values: Array) -> float:
	var total := 0.0
	for value: float in values: total += value
	return total / maxf(values.size(), 1)

## Swap each LOD2 material's shader for a copy that reads the albedo at mip 0 (true) or put the shipped shaders back (false). Fails the test when the text to replace is not found.
func _swap_reference(enable: bool) -> bool:
	if not enable:
		for material: ShaderMaterial in shader_originals: material.shader = shader_originals[material]
		shader_originals.clear()
		return true
	var made := {}
	for material: ShaderMaterial in materials:
		var shader: Shader = material.shader
		shader_originals[material] = shader
		if not made.has(shader):
			var code: String = shader.code
			if not code.contains(MIP_ZERO_FROM):
				_swap_reference(false)
				_fail("the reference model's shader swap did not find '%s' in %s" % [MIP_ZERO_FROM, shader.resource_path])
				return false
			if not code.contains(ALPHA_MIP_ZERO_FROM):
				_swap_reference(false)
				_fail("the reference model's shader swap did not find '%s' in %s" % [ALPHA_MIP_ZERO_FROM, shader.resource_path])
				return false
			var modified := Shader.new()
			modified.code = code.replace(MIP_ZERO_FROM, MIP_ZERO_TO).replace(ALPHA_MIP_ZERO_FROM, ALPHA_MIP_ZERO_TO)
			made[shader] = modified
		material.shader = made[shader]
	return true

## Gates for one tier against the reference model (colour, sun) and a per-variant covered-area target (`cover_target`, named `target_name` in messages: the shipped model for the cards and the control,
## the cards at the hand-off size for the quad). The control reuses it with the species tolerance per variant (its table has no sun 8 case).
func _evaluate(_ship: Array, ref: Array, table: Array, variant_sun_tolerance: Array, cover_target: Array, target_name: String, species_sun_tolerance: Array, channel_tolerance: float) -> Dictionary:
	var failing := {}
	var failures: Array[String] = []
	var rows := []
	var cover_ratio := [[], [], []]
	var ambient_luminance := [[], [], []]
	var ambient_rgb := [[], [], []]
	var sun_ratio := [[[], [], []], [[], [], []], [[], [], []]] # [species][elevation] -> variant ratios
	var worst_variant_sun := [0.0, 0.0, 0.0]
	var worst_coverage := 0.0
	var worst_ambient_luminance := 0.0
	var worst_channel := 0.0
	for variant in table.size():
		var species := _species(variant)
		var row := {"variant": variant}
		var target_cover: float = cover_target[variant]
		var ref_ambient := _mean_cases(ref[variant][AMBIENT])
		var ambient := _mean_cases(table[variant][AMBIENT])
		var coverage_error: float = ambient[0] / maxf(target_cover, 1e-6) - 1.0
		var luminance_error: float = ambient[4] / maxf(ref_ambient[4], 1e-6) - 1.0
		var channel_error := 0.0
		for channel in [1, 2, 3]: channel_error = maxf(channel_error, absf(ambient[channel] - ref_ambient[channel]) / maxf(ref_ambient[4], 1e-6))
		cover_ratio[species].append(ambient[0] / maxf(target_cover, 1e-6))
		ambient_luminance[species].append(ambient[4] / maxf(ref_ambient[4], 1e-6))
		ambient_rgb[species].append([ambient[1] / maxf(ref_ambient[1], 1e-6), ambient[2] / maxf(ref_ambient[2], 1e-6), ambient[3] / maxf(ref_ambient[3], 1e-6)])
		worst_coverage = maxf(worst_coverage, absf(coverage_error))
		worst_ambient_luminance = maxf(worst_ambient_luminance, absf(luminance_error))
		worst_channel = maxf(worst_channel, channel_error)
		if absf(coverage_error) > COVERAGE_TOLERANCE:
			failing[variant] = true
			failures.append("v%d covered area %.3f of %s (limit %.2f)" % [variant, 1.0 + coverage_error, target_name, COVERAGE_TOLERANCE])
		if absf(luminance_error) > LUMINANCE_TOLERANCE:
			failing[variant] = true
			failures.append("v%d ambient luminance %.3f of the reference model (limit %.2f)" % [variant, 1.0 + luminance_error, LUMINANCE_TOLERANCE])
		if channel_error > channel_tolerance:
			failing[variant] = true
			failures.append("v%d ambient channel difference %.3f of the reference luminance (limit %.2f)" % [variant, channel_error, channel_tolerance])
		row["ambient"] = {"coverage_ratio": 1.0 + coverage_error, "luminance_ratio": 1.0 + luminance_error, "channel_error": channel_error, "r": ambient[1] / maxf(ref_ambient[1], 1e-6), "g": ambient[2] / maxf(ref_ambient[2], 1e-6), "b": ambient[3] / maxf(ref_ambient[3], 1e-6)}
		for elevation in ELEVATIONS.size():
			if not table[variant].has(elevation): continue
			var ratio: float = _mean_cases(table[variant][elevation])[4] / maxf(_mean_cases(ref[variant][elevation])[4], 1e-6)
			sun_ratio[species][elevation].append(ratio)
			worst_variant_sun[elevation] = maxf(worst_variant_sun[elevation], absf(ratio - 1.0))
			if absf(ratio - 1.0) > float(variant_sun_tolerance[elevation]):
				failing[variant] = true
				failures.append("v%d sun%d luminance %.3f of the reference model (variant limit %.2f)" % [variant, int(ELEVATIONS[elevation]), ratio, variant_sun_tolerance[elevation]])
			row["sun%d" % int(ELEVATIONS[elevation])] = {"ratio": ratio}
		rows.append(row)
	var species_sun := [[], [], []] # [elevation] -> species means
	var species_coverage := []
	var species_ambient_luminance := []
	var species_ambient_rgb := []
	for species in 3:
		species_coverage.append(_mean_of(cover_ratio[species]))
		species_ambient_luminance.append(_mean_of(ambient_luminance[species]))
		var rgb := [0.0, 0.0, 0.0]
		for value: Array in ambient_rgb[species]:
			for c in 3: rgb[c] += value[c] / ambient_rgb[species].size()
		species_ambient_rgb.append(rgb)
		for elevation in ELEVATIONS.size():
			if sun_ratio[species][elevation].is_empty():
				species_sun[elevation].append(1.0)
				continue
			var mean := _mean_of(sun_ratio[species][elevation])
			species_sun[elevation].append(mean)
			if absf(mean - 1.0) > float(species_sun_tolerance[elevation]):
				failures.append("%s sun%d species mean luminance %.3f of the reference model (limit %.2f)" % [SPECIES[species], int(ELEVATIONS[elevation]), mean, species_sun_tolerance[elevation]])
	return {"failing_variants": failing.size(), "failing_variant_list": failing.keys(), "failures": failures, "species_coverage": species_coverage, "species_ambient_luminance": species_ambient_luminance, "species_ambient_rgb": species_ambient_rgb, "species_sun": species_sun, "worst_variant_sun": worst_variant_sun, "worst_coverage": worst_coverage, "worst_ambient_luminance": worst_ambient_luminance, "worst_channel": worst_channel, "rows": rows}

## Gates of the shipped model (the LOD2 with the distance-stable leaf mips) against the reference model (the same meshes read at mip 0), species means of the variant values (TR-7c): covered area
## (ambient, 128 px), ambient luminance and channels, sun only per elevation present in the table. `species_failed` marks the species that miss any gate (the control reads it).
func _evaluate_model(ref: Array, ship: Array) -> Dictionary:
	var cover := [[], [], []]
	var luminance := [[], [], []]
	var channel := [[], [], []]
	var sun_ratio := [[[], [], []], [[], [], []], [[], [], []]]
	var rows := []
	for variant in VARIANTS:
		var species := _species(variant)
		var r := _mean_cases(ref[variant][AMBIENT])
		var s := _mean_cases(ship[variant][AMBIENT])
		var channel_error := 0.0
		for c in [1, 2, 3]: channel_error = maxf(channel_error, absf(s[c] - r[c]) / maxf(r[4], 1e-6))
		cover[species].append(s[0] / maxf(r[0], 1e-6))
		luminance[species].append(s[4] / maxf(r[4], 1e-6))
		channel[species].append(channel_error)
		var row := {"variant": variant, "coverage_ratio": s[0] / maxf(r[0], 1e-6), "ambient_luminance_ratio": s[4] / maxf(r[4], 1e-6), "channel_error": channel_error}
		for elevation in ELEVATIONS.size():
			if not ship[variant].has(elevation): continue
			var ratio: float = _mean_cases(ship[variant][elevation])[4] / maxf(_mean_cases(ref[variant][elevation])[4], 1e-6)
			sun_ratio[species][elevation].append(ratio)
			row["sun%d" % int(ELEVATIONS[elevation])] = ratio
		rows.append(row)
	var failures: Array[String] = []
	var species_failed := [false, false, false]
	var species_coverage := []
	var species_luminance := []
	var species_channel := []
	var species_sun := [[], [], []]
	for species in 3:
		var coverage_mean := _mean_of(cover[species])
		var luminance_mean := _mean_of(luminance[species])
		var channel_mean := _mean_of(channel[species])
		species_coverage.append(coverage_mean)
		species_luminance.append(luminance_mean)
		species_channel.append(channel_mean)
		if absf(coverage_mean - 1.0) > MODEL_COVERAGE_TOLERANCE:
			species_failed[species] = true
			failures.append("%s covered area %.3f of the reference model (limit %.2f)" % [SPECIES[species], coverage_mean, MODEL_COVERAGE_TOLERANCE])
		if absf(luminance_mean - 1.0) > LUMINANCE_TOLERANCE:
			species_failed[species] = true
			failures.append("%s ambient luminance %.3f of the reference model (limit %.2f)" % [SPECIES[species], luminance_mean, LUMINANCE_TOLERANCE])
		if channel_mean > CHANNEL_TOLERANCE:
			species_failed[species] = true
			failures.append("%s ambient channel difference %.3f of the reference luminance (limit %.2f)" % [SPECIES[species], channel_mean, CHANNEL_TOLERANCE])
		for elevation in ELEVATIONS.size():
			if sun_ratio[species][elevation].is_empty():
				species_sun[elevation].append(1.0)
				continue
			var mean := _mean_of(sun_ratio[species][elevation])
			species_sun[elevation].append(mean)
			if absf(mean - 1.0) > float(SUN_TOLERANCE[elevation]):
				species_failed[species] = true
				failures.append("%s sun%d luminance %.3f of the reference model (limit %.2f)" % [SPECIES[species], int(ELEVATIONS[elevation]), mean, SUN_TOLERANCE[elevation]])
	return {"failures": failures, "species_failed": species_failed, "species_coverage": species_coverage, "species_ambient_luminance": species_luminance, "species_channel_error": species_channel, "species_sun": species_sun, "rows": rows}

## The shipped model's covered fraction (ambient, mean over the 4 azimuths) in 64 and 46 px viewports against its 128 px value, species means, same lens (TR-7c: 15 % and 20 %).
func _model_scale(ship: Array) -> Dictionary:
	var failures: Array[String] = []
	var model_64 := await _measure_kind("ship", CARD_DISTANCE, 64, [AMBIENT])
	var model_46 := await _measure_kind("ship", CARD_DISTANCE, 46, [AMBIENT])
	var ratios := {"model_64": [[], [], []], "model_46": [[], [], []]}
	for variant in VARIANTS:
		var base: float = _mean_cases(ship[variant][AMBIENT])[0]
		ratios["model_64"][_species(variant)].append(_mean_cases(model_64[variant][AMBIENT])[0] / maxf(base, 1e-6))
		ratios["model_46"][_species(variant)].append(_mean_cases(model_46[variant][AMBIENT])[0] / maxf(base, 1e-6))
	var result := {}
	var limits := {"model_64": MODEL_SCALE_64_TOLERANCE, "model_46": MODEL_SCALE_46_TOLERANCE}
	for key: String in ratios:
		var means := []
		for species in 3:
			var mean := _mean_of(ratios[key][species])
			means.append(mean)
			if absf(mean - 1.0) > float(limits[key]): failures.append("model scale %s: %s species mean %.3f (limit %.2f)" % [key, SPECIES[species], mean, limits[key]])
		result[key] = means
		result[key + "_per_variant"] = ratios[key]
	result["failures"] = failures
	return result

## Cards against the shipped model, sun only (the step at the hand-off from the model to the cards): species mean of the variant ratios per sun elevation, and every variant.
func _handoff_step(ship: Array, card: Array) -> Dictionary:
	var per_species := [[[], [], []], [[], [], []], [[], [], []]]
	var rows := []
	for variant in VARIANTS:
		var row := []
		for elevation in 3:
			var ratio: float = _mean_cases(card[variant][elevation])[4] / maxf(_mean_cases(ship[variant][elevation])[4], 1e-6)
			per_species[_species(variant)][elevation].append(ratio)
			row.append(ratio)
		rows.append(row)
	var means := []
	for species in 3: means.append([_mean_of(per_species[species][0]), _mean_of(per_species[species][1]), _mean_of(per_species[species][2])])
	return {"species_mean": means, "per_variant_noon_30_8": rows, "bound": SUN_TOLERANCE}

## Covered fraction (ambient, mean over the 4 azimuths) in 64 and 46 px viewports against the 128 px value (species mean of the variant ratios), with the same lens. The quad at 46 px is also
## judged against the cards at 46 px over 12 azimuths (`quad_over_cards_target`, _cards_at_handoff).
func _scale_stability(card_table: Array, quad_table: Array, quad_over_cards_target: Array) -> Dictionary:
	var failures: Array[String] = []
	var cards_64 := await _measure_kind("card", CARD_DISTANCE, 64, [AMBIENT])
	var cards_46 := await _measure_kind("card", CARD_DISTANCE, 46, [AMBIENT])
	var quad_46 := await _measure_kind("quad", QUAD_DISTANCE, 46, [AMBIENT])
	var ratios := {"cards_64": [[], [], []], "cards_46": [[], [], []], "quad_46": [[], [], []], "quad_over_cards_46": [[], [], []]}
	for variant in VARIANTS:
		var species := _species(variant)
		var card_128: float = _mean_cases(card_table[variant][AMBIENT])[0]
		var quad_128: float = _mean_cases(quad_table[variant][AMBIENT])[0]
		var card_64: float = _mean_cases(cards_64[variant][AMBIENT])[0]
		var card_46: float = _mean_cases(cards_46[variant][AMBIENT])[0]
		var quad_at_46: float = _mean_cases(quad_46[variant][AMBIENT])[0]
		ratios["cards_64"][species].append(card_64 / maxf(card_128, 1e-6))
		ratios["cards_46"][species].append(card_46 / maxf(card_128, 1e-6))
		ratios["quad_46"][species].append(quad_at_46 / maxf(quad_128, 1e-6))
		ratios["quad_over_cards_46"][species].append(quad_at_46 / maxf(float(quad_over_cards_target[variant]), 1e-6))
	var result := {}
	var limits := {"cards_64": SCALE_CARD_64_TOLERANCE, "cards_46": SCALE_CARD_46_TOLERANCE, "quad_46": SCALE_QUAD_TOLERANCE, "quad_over_cards_46": QUAD_AGAINST_CARDS_TOLERANCE}
	for key: String in ratios:
		var means := []
		for species in 3:
			var mean := _mean_of(ratios[key][species])
			means.append(mean)
			if absf(mean - 1.0) > float(limits[key]): failures.append("scale %s: %s species mean %.3f (limit %.2f)" % [key, SPECIES[species], mean, limits[key]])
		result[key] = means
		result[key + "_per_variant"] = ratios[key]
	result["failures"] = failures
	return result

## Sun 30 degrees, no ambient, camera at azimuth 0 (sun behind the camera) and 180 (sun behind the tree): the lit-from-the-camera-side quad is brighter in the first. Seed 0 and the mirrored seed.
func _camera_side_check() -> Dictionary:
	var failures: Array[String] = []
	var quad_ok := 0
	var quad_ok_mirrored := 0
	var mesh_ok := 0
	var rows := []
	var failing_mirrored := []
	var failing := []
	for variant in VARIANTS:
		var mesh_front: Array = await _measure("ship", variant, QUAD_DISTANCE, 1, 0.0)
		var mesh_back: Array = await _measure("ship", variant, QUAD_DISTANCE, 1, 180.0)
		quad_seed = 0.0
		var quad_front: Array = await _measure("quad", variant, QUAD_DISTANCE, 1, 0.0)
		var quad_back: Array = await _measure("quad", variant, QUAD_DISTANCE, 1, 180.0)
		quad_seed = MIRRORED_SEED
		var mirror_front: Array = await _measure("quad", variant, QUAD_DISTANCE, 1, 0.0)
		var mirror_back: Array = await _measure("quad", variant, QUAD_DISTANCE, 1, 180.0)
		quad_seed = 0.0
		var mesh_is: bool = mesh_front[4] > mesh_back[4]
		var quad_is: bool = quad_front[4] > quad_back[4]
		var mirror_is: bool = mirror_front[4] > mirror_back[4]
		if mesh_is: mesh_ok += 1
		if quad_is: quad_ok += 1
		if mirror_is: quad_ok_mirrored += 1
		if mesh_is and not quad_is: failing.append(variant)
		if mesh_is and not mirror_is: failing_mirrored.append(variant)
		rows.append({"variant": variant, "mesh_behind_camera": mesh_front[4], "mesh_behind_tree": mesh_back[4], "quad_behind_camera": quad_front[4], "quad_behind_tree": quad_back[4], "mirrored_behind_camera": mirror_front[4], "mirrored_behind_tree": mirror_back[4]})
	if failing.size() != 0: failures.append("camera side: the quad (seed 0) is not lit from the camera side for variants %s" % [failing])
	if failing_mirrored.size() != 0: failures.append("camera side: the quad (mirrored seed) is not lit from the camera side for variants %s" % [failing_mirrored])
	return {"failing": failing, "failing_mirrored": failing_mirrored, "quad_ok": quad_ok, "quad_ok_mirrored": quad_ok_mirrored, "mesh_ok": mesh_ok, "rows": rows, "failures": failures}

## Seed 0 with the sun 90 degrees to the right of the camera equals the mirrored seed with the sun 90 degrees to the left (sun 30, no ambient): luminance within 3 %.
## The camera at azimuth a looks along -(cos a, sin a); its right is the direction at a + 90, so the sun (azimuth 0) is on its right at a = 270 and on its left at a = 90.
func _mirror_identity() -> Dictionary:
	var failures: Array[String] = []
	var rows := []
	var worst := 0.0
	for variant in VARIANTS:
		quad_seed = 0.0
		var plain: Array = await _measure("quad", variant, QUAD_DISTANCE, 1, 270.0)
		quad_seed = MIRRORED_SEED
		var mirrored: Array = await _measure("quad", variant, QUAD_DISTANCE, 1, 90.0)
		quad_seed = 0.0
		var error: float = mirrored[4] / maxf(plain[4], 1e-6) - 1.0
		worst = maxf(worst, absf(error))
		if absf(error) > MIRROR_TOLERANCE: failures.append("mirror identity v%d: luminance differs by %.3f (limit %.2f)" % [variant, error, MIRROR_TOLERANCE])
		rows.append({"variant": variant, "plain_sun_right": plain[4], "mirrored_sun_left": mirrored[4], "error": error, "coverage_ratio": mirrored[0] / maxf(plain[0], 1e-6)})
	return {"worst": worst, "rows": rows, "failures": failures}

## Report only, for TR-6b: camera 60 degrees above the horizon, noon and sun 30 (4 azimuths), cards and quad against the reference model.
func _top_view() -> Dictionary:
	var per_kind := {}
	for kind: String in ["ref", "card", "quad"]:
		if kind == "ref" and not _swap_reference(true): return {}
		var table := []
		for variant in VARIANTS:
			var cases := {}
			for lighting: int in [0, 1]:
				var list := []
				for azimuth: float in AZIMUTHS:
					list.append(await _measure(kind, variant, QUAD_DISTANCE if kind == "quad" else CARD_DISTANCE, lighting, azimuth, REFERENCE_SIZE if kind == "ref" else SIZE, TOP_VIEW_ELEVATION))
				cases[lighting] = list
			table.append(cases)
		if kind == "ref": _swap_reference(false)
		per_kind[kind] = table
	var species_ratio := {}
	var rows := []
	for kind: String in ["card", "quad"]:
		var means := []
		for species in 3:
			var values := [[], []]
			for variant in VARIANTS:
				if _species(variant) != species: continue
				for lighting in 2:
					values[lighting].append(_mean_cases(per_kind[kind][variant][lighting])[4] / maxf(_mean_cases(per_kind["ref"][variant][lighting])[4], 1e-6))
			means.append([_mean_of(values[0]), _mean_of(values[1])])
		species_ratio[kind] = means
	for variant in VARIANTS:
		rows.append({"variant": variant, "ref_noon": _mean_cases(per_kind["ref"][variant][0])[4], "card_noon": _mean_cases(per_kind["card"][variant][0])[4], "quad_noon": _mean_cases(per_kind["quad"][variant][0])[4], "ref_sun30": _mean_cases(per_kind["ref"][variant][1])[4], "card_sun30": _mean_cases(per_kind["card"][variant][1])[4], "quad_sun30": _mean_cases(per_kind["quad"][variant][1])[4]})
	var card_means: Array = species_ratio["card"]
	var quad_means: Array = species_ratio["quad"]
	print("TREE_BILLBOARD_MATCH top view (camera 60 deg above the horizon, report only), species mean luminance against the reference model, maple/fir/pine, noon | sun30: cards %s | %s, quad %s | %s" % [_fmt([card_means[0][0], card_means[1][0], card_means[2][0]]), _fmt([card_means[0][1], card_means[1][1], card_means[2][1]]), _fmt([quad_means[0][0], quad_means[1][0], quad_means[2][0]]), _fmt([quad_means[0][1], quad_means[1][1], quad_means[2][1]])])
	return {"elevation_degrees": TOP_VIEW_ELEVATION, "species_mean_luminance_ratio_noon_sun30": species_ratio, "rows": rows}

## Render one case and return [covered fraction of the viewport, mean linear r, g, b, luminance]. `lighting` is AMBIENT (white ambient, no sun) or a sun elevation index (sun only). Kinds: "ship"
## and "ref" (the LOD2 model; "ref" only inside _measure_kind's shader swap), "card" (the crossed cards) and "quad" (the far quad). `size` is the viewport edge in pixels (the lens is unchanged).
func _measure(kind: String, variant: int, distance: float, lighting: int, azimuth_degrees: float, size: int = SIZE, camera_elevation_degrees: float = 0.0) -> Array:
	if viewport.size != Vector2i(size, size):
		viewport.size = Vector2i(size, size)
		for i in 6: await RenderingServer.frame_post_draw
	var elevation := deg_to_rad(ELEVATIONS[0 if lighting == AMBIENT else lighting])
	var sun_direction := Vector3(cos(elevation), 0.0, sin(elevation))
	sun.transform = Transform3D(Basis.looking_at(-sun_direction, Vector3.UP), Vector3.ZERO)
	sun.visible = lighting != AMBIENT
	environment.ambient_light_energy = AMBIENT_ENERGY if lighting == AMBIENT else 0.0
	for material in materials: material.set_shader_parameter("sun_direction", sun_direction)
	for material in impostor_materials: material.set_shader_parameter("sun_direction", sun_direction)
	# At 900 m the lens narrows so the tree keeps its 325 m pixel size (a 900 m tree is too few pixels to measure); the card is then judged at a finer mip than it is shown at.
	camera.fov = rad_to_deg(2.0 * atan(tan(deg_to_rad(FOV_DEGREES * 0.5)) * CARD_DISTANCE / distance))
	var azimuth := deg_to_rad(azimuth_degrees)
	var tilt := deg_to_rad(camera_elevation_degrees)
	var middle := UP * heights[variant] * 0.5
	var position := middle + Vector3(cos(azimuth) * cos(tilt), sin(azimuth) * cos(tilt), sin(tilt)) * distance
	camera.transform = Transform3D(Basis.looking_at(middle - position, UP), position)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_camera_world_position", position)
	var model := kind == "ship" or kind == "ref"
	mesh_instance.visible = model
	card_instance.visible = kind == "card"
	quad_instance.visible = kind == "quad"
	# The catalog meshes are normalised to unit height; the instance scale is the tree's height.
	var placement := Transform3D(Basis(Vector3.RIGHT, PI * 0.5).scaled(Vector3.ONE * heights[variant]), Vector3.ZERO)
	if model:
		mesh_instance.mesh = lod2_meshes[variant]
		mesh_instance.transform = placement
	else:
		var multi: MultiMesh = card_multi if kind == "card" else quad_multi
		multi.set_instance_transform(0, placement)
		multi.set_instance_custom_data(0, Color(float(variant), 1.0, quad_seed if kind == "quad" else 0.0, 0.0))
	for i in 3: await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	if image == null or image.is_empty():
		_fail("empty capture %s variant %d" % [kind, variant])
		return [0.0, 0.0, 0.0, 0.0, 0.0]
	if capture_dump and variant in DUMP_VARIANTS and azimuth_degrees == (AZIMUTHS[0] if lighting == AMBIENT else 45.0) and lighting in [AMBIENT, 0] and kind in ["ship", "ref", "card"]:
		var tile := image.duplicate() as Image
		for y in tile.get_height():
			for x in tile.get_width():
				if tile.get_pixel(x, y).is_equal_approx(BACKGROUND): tile.set_pixel(x, y, DUMP_BACKGROUND)
		tile.resize(256, 256, Image.INTERPOLATE_NEAREST if size < 256 else Image.INTERPOLATE_LANCZOS)
		dump_images["%d_%d_%s" % [variant, 0 if lighting == AMBIENT else 1, kind]] = tile
	var stats: PackedFloat32Array = driver.call("MeasureForeground", image, BACKGROUND)
	return [stats[0] / float(size * size), stats[1], stats[2], stats[3], stats[4]]

# --- scene -----------------------------------------------------------------------------------------------------------------------------------------------------

func _make_scene() -> void:
	viewport = SubViewport.new()
	viewport.size = Vector2i(SIZE, SIZE)
	viewport.own_world_3d = true
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)
	environment = Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = BACKGROUND
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	environment.ambient_light_color = Color(1.0, 1.0, 1.0) # white, so the ambient-only gate compares albedo
	environment.ambient_light_energy = 0.0
	environment.reflected_light_source = Environment.REFLECTION_SOURCE_DISABLED
	environment.tonemap_mode = Environment.TONE_MAPPER_LINEAR
	var world := WorldEnvironment.new()
	world.environment = environment
	viewport.add_child(world)
	camera = Camera3D.new()
	camera.fov = FOV_DEGREES
	camera.near = 1.0
	camera.far = 5000.0
	camera.current = true
	viewport.add_child(camera)
	sun = DirectionalLight3D.new()
	sun.light_energy = 1.0 # the sun-only gate: ambient 0
	sun.shadow_enabled = false
	viewport.add_child(sun)
	driver = load("res://scripts/Vegetation/ImportedTreeCatalogDriver.cs").new()
	root.add_child(driver)
	var tree_basis := Basis(Vector3.RIGHT, PI * 0.5) # the model's +Y becomes the test world's surface-up (+Z)
	mesh_instance = MeshInstance3D.new()
	mesh_instance.transform = Transform3D(tree_basis, Vector3.ZERO)
	mesh_instance.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	viewport.add_child(mesh_instance)
	var card_mesh := driver.call("LoadSharedImpostorMesh") as Mesh
	var quad_mesh := driver.call("LoadFarQuadMesh") as Mesh
	if card_mesh == null or quad_mesh == null:
		_fail("shared impostor assets unavailable")
		return
	card_multi = _multi(card_mesh)
	quad_multi = _multi(quad_mesh)
	card_instance = _multi_instance(card_multi, tree_basis)
	quad_instance = _multi_instance(quad_multi, tree_basis)
	for variant in VARIANTS:
		var mesh := driver.call("LoadVariantMesh", variant, 2) as Mesh
		if mesh == null:
			_fail("LOD2 mesh missing for variant %d" % variant)
			return
		lod2_meshes.append(mesh)
		# The authored heights are not the placed ones (the placer scales by tree.Height); use the strip's typical heights per species: maple 0-11, fir 12-14, pine 15-29.
		heights.append(18.0 if variant < 12 else (11.0 if variant < 15 else 20.0))
		for surface in mesh.get_surface_count():
			var material := mesh.surface_get_material(surface) as ShaderMaterial
			if material != null and not materials.has(material): materials.append(material)
	impostor_materials = [driver.call("LoadSharedImpostorMaterial"), driver.call("LoadFarQuadMaterial")]
	_publish_frame_globals()
	for material in materials + impostor_materials: _configure(material)

func _multi(mesh: Mesh) -> MultiMesh:
	var multi := MultiMesh.new()
	multi.transform_format = MultiMesh.TRANSFORM_3D
	multi.use_custom_data = true
	multi.mesh = mesh
	multi.instance_count = 1
	multi.set_instance_transform(0, Transform3D.IDENTITY)
	multi.set_instance_custom_data(0, Color(0, 1, 0, 0))
	return multi

func _multi_instance(multi: MultiMesh, tree_basis: Basis) -> MultiMeshInstance3D:
	var instance := MultiMeshInstance3D.new()
	instance.multimesh = multi
	instance.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	instance.custom_aabb = AABB(Vector3(-2, -1, -2), Vector3(4, 4, 4))
	viewport.add_child(instance)
	return instance

# The ray frame is a set of global shader parameters for the main-viewport tree shaders (RING_ATMOSPHERE_FRAME_GLOBAL); haze is off so only lighting differs.
func _publish_frame_globals() -> void:
	RenderingServer.global_shader_parameter_set("ringworld_solar_visibility", 1.0)
	RenderingServer.global_shader_parameter_set("ring_local_wind_enabled", 0.0)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_ray_frame_valid", true)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_camera_world_position", Vector3.ZERO)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_camera_height_meters", 0.0)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_outward_world", Vector3.RIGHT)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_axis_world", Vector3.UP)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_sidewall_negative_meters", 1000000.0)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_sidewall_positive_meters", 1000000.0)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_coordinate_scale", 1.0)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_to_sun", Vector3.LEFT)

func _configure(material: ShaderMaterial) -> void:
	material.set_shader_parameter("ring_center", Vector3(0.0, 0.0, 1000000.0)) # surface_up = +Z at the tree
	material.set_shader_parameter("sun_direction", Vector3(1, 0, 0))
	material.set_shader_parameter("vegetation_wind_enabled", false)
	material.set_shader_parameter("simplified_tree_shading", true) # the shipped default (SimplifyDistantTreeMaterials)
	material.set_shader_parameter("ring_atmosphere_enabled", false)
	material.set_shader_parameter("ring_radius_meters", 5000000.0)
	material.set_shader_parameter("atmosphere_haze_model", 1)
	material.set_shader_parameter("ring_haze_sigma_R", Vector3.ZERO)
	material.set_shader_parameter("ring_haze_sigma_A", 0.0)
	material.set_shader_parameter("ring_haze_sigma_M0", 0.0)
	material.set_shader_parameter("atmosphere_boundary_max_opacity", 0.0)
	material.set_shader_parameter("atmosphere_upper_max_opacity", 0.0)
	material.set_shader_parameter("atmosphere_distance_haze_max_opacity", 0.0)

# --- report ----------------------------------------------------------------------------------------------------------------------------------------------------

func _write_report(report: Dictionary, tables: Dictionary) -> void:
	var file := FileAccess.open(OUT.path_join("report_%s.json" % mode_name), FileAccess.WRITE)
	if file != null: file.store_string(JSON.stringify(report, "\t"))
	var csv := FileAccess.open(OUT.path_join("table_%s.csv" % mode_name), FileAccess.WRITE)
	if csv == null: return
	csv.store_line("set,variant,lighting,azimuth_deg,covered_fraction,r,g,b,luminance")
	for set_name: String in tables:
		var table: Array = tables[set_name]
		for variant in table.size():
			for lighting: int in table[variant]:
				var azimuths := _azimuths(lighting)
				for i in azimuths.size():
					var c: Array = table[variant][lighting][i]
					csv.store_line("%s,%d,%s,%d,%.6f,%.5f,%.5f,%.5f,%.5f" % [set_name, variant, _lighting_name(lighting), int(azimuths[i]), c[0], c[1], c[2], c[3], c[4]])

## The D7 contact sheet: 3 columns (shipped model, reference model, cards) by 6 rows (per variant, ambient then noon), 256 px tiles on a grey background, each labelled. Drawn through a SubViewport
## so the labels use the engine's font; saved to .godot/tree_billboard_match/d7_contact_sheet_<mode>.png.
func _write_dump() -> void:
	const TILE := 256
	const LABEL := 22
	var rows := DUMP_VARIANTS.size() * 2
	var sheet := SubViewport.new()
	sheet.size = Vector2i(TILE * 3, (TILE + LABEL) * rows)
	sheet.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	sheet.transparent_bg = false
	root.add_child(sheet)
	var background := ColorRect.new()
	background.color = DUMP_BACKGROUND.darkened(0.35)
	background.size = Vector2(sheet.size)
	sheet.add_child(background)
	var row := 0
	for variant: int in DUMP_VARIANTS:
		for lighting in 2:
			var column := 0
			for kind: String in ["ship", "ref", "card"]:
				var key := "%d_%d_%s" % [variant, lighting, kind]
				var origin := Vector2(column * TILE, row * (TILE + LABEL))
				var label := Label.new()
				label.text = "v%d  %s  %s" % [variant, "ambient" if lighting == 0 else "noon sun", DUMP_KIND_NAMES[kind]]
				label.position = origin
				label.add_theme_font_size_override("font_size", 14)
				sheet.add_child(label)
				if dump_images.has(key):
					var picture := TextureRect.new()
					picture.texture = ImageTexture.create_from_image(dump_images[key])
					picture.position = origin + Vector2(0, LABEL)
					picture.size = Vector2(TILE, TILE)
					sheet.add_child(picture)
				column += 1
			row += 1
	for i in 4: await RenderingServer.frame_post_draw
	var path := ProjectSettings.globalize_path(OUT.path_join("d7_contact_sheet_%s.png" % mode_name))
	sheet.get_texture().get_image().save_png(path)
	sheet.queue_free()
	print("TREE_BILLBOARD_MATCH D7 contact sheet: %s (%d of %d tiles)" % [path, dump_images.size(), rows * 3])

func _fail(message: String) -> void:
	if done: return
	failed = true
	done = true
	push_error("TREE_BILLBOARD_MATCH_GPU_TEST=FAIL " + message)
	quit(1)

func _on_watchdog() -> void:
	if not done: _fail("watchdog timeout")
