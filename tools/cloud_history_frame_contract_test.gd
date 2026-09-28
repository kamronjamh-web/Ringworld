extends SceneTree

# CPU-only contract coverage for the gameplay callback-paired cloud frame.
const RADIUS := 5_000_000.0
const EPS := .02
const CLOUDS := preload("res://addons/SunshineClouds2/SunshineClouds.gd")
var failed := false

func _initialize() -> void:
	call_deferred("_run")

func _run() -> void:
	_test_stable_curved_motion_is_identity()
	_test_sector_rebase_maps_physical_128m()
	_test_curved_orientation_preserves_real_point()
	_test_circumference_seam()
	_test_rigid_presentation_change()
	_test_large_canonical_small_motion()
	_test_invalidations()
	if failed: quit(1)
	print("CLOUD_HISTORY_FRAME_CONTRACT_TEST=PASS")
	quit(0)

func _frame(around: float, across: float = 0.0) -> Transform3D:
	var angle := around / RADIUS
	# This is an independent curved ring embedding used only for physical-point
	# expectations; it is not copied from the production remap implementation.
	var origin := Vector3(RADIUS * sin(angle), 2.0 * RADIUS * pow(sin(angle * .5), 2.0), across)
	return Transform3D(Basis(Vector3(cos(angle), sin(angle), 0.0), Vector3(-sin(angle), cos(angle), 0.0), Vector3(0,0,1)), origin)

func _physical(frame: Transform3D, local: Vector3) -> Vector3:
	return frame * local

func _remap(previous: Transform3D, current: Transform3D, previous_around: float, current_around: float, previous_across: float, current_across: float) -> Transform3D:
	var result: Dictionary = CLOUDS.gameplay_frame_remap({"frame":current,"around":current_around,"across":current_across,"radius":RADIUS},{"frame":previous,"around":previous_around,"across":previous_across,"radius":RADIUS},Transform3D.IDENTITY,999999.0)
	_require(bool(result.valid), "production remap rejected valid frame pair")
	return (result.mapped_camera as Transform3D).affine_inverse()

func _require(condition: bool, reason: String) -> void:
	if not condition:
		failed = true
		push_error("CLOUD_HISTORY_FRAME_CONTRACT_TEST FAILED: " + reason)

func _near(a: Vector3, b: Vector3, label: String) -> void:
	_require(a.distance_to(b) <= EPS, label + " distance=" + str(a.distance_to(b)))

func _test_stable_curved_motion_is_identity() -> void:
	var previous := _frame(1_200_000.0, 50.0)
	var current := _frame(1_200_006.0, 50.0)
	var map := _remap(previous, current, 1_200_000.0, 1_200_006.0, 50.0, 50.0)
	var point := _physical(current, Vector3(37.0, 900.0, -14.0))
	_near(map * point, point, "stable curved frame map must be identity")

func _test_sector_rebase_maps_physical_128m() -> void:
	# A world-origin rebase is represented by two local frames for the same
	# physical canonical point, separated by 128 m in their frame origin.
	var previous := Transform3D.IDENTITY
	var current := Transform3D(Basis.IDENTITY, Vector3(128.0, 0.0, 0.0))
	var map := _remap(previous, current, 0.0, 0.0, 0.0, 0.0)
	var prior_camera := Transform3D(Basis.IDENTITY, Vector3(10.0, 20.0, 30.0))
	var mapped := map.affine_inverse() * prior_camera
	_require(is_equal_approx(mapped.origin.x, 138.0), "128m sector rebase was not mapped into current world")

func _test_curved_orientation_preserves_real_point() -> void:
	var previous_around := 900_000.0
	var current_around := previous_around + 80.0
	var previous := _frame(previous_around, 12.0)
	var current := _frame(current_around, 12.0)
	var map := _remap(previous, current, previous_around, current_around, 12.0, 12.0)
	var local := Vector3(-19.0, 1400.0, 31.0)
	# The expected physical point comes directly from the current curved frame.
	var expected := _physical(current, local)
	_near(map * expected, expected, "curved basis rotation changed physical point")

func _test_circumference_seam() -> void:
	var circumference := TAU * RADIUS
	var previous_around := circumference - 2.0
	var current_around := 3.0
	var map := _remap(_frame(previous_around), _frame(current_around), previous_around, current_around, 0.0, 0.0)
	var point := _physical(_frame(current_around), Vector3(5, 600, 0))
	_near(map * point, point, "seam remap changed physical point")

func _test_invalidations() -> void:
	var limit := maxf(4000.0, 2.0 * 1600.0)
	var frame := _frame(0.0)
	_require(not bool(CLOUDS.gameplay_frame_remap({"frame":frame,"around":0.0,"across":0.0,"radius":NAN},{"frame":frame,"around":0.0,"across":0.0,"radius":RADIUS},Transform3D.IDENTITY,limit).valid), "invalid radius accepted")
	_require(not bool(CLOUDS.gameplay_frame_remap({"frame":frame,"around":0.0,"across":0.0,"radius":RADIUS},{"frame":frame,"around":0.0,"across":0.0,"radius":RADIUS+1.0},Transform3D.IDENTITY,limit).valid), "radius change accepted")
	_require(not bool(CLOUDS.gameplay_frame_remap({"frame":Transform3D(Basis(Vector3.ZERO,Vector3.UP,Vector3.FORWARD),Vector3.ZERO),"around":0.0,"across":0.0,"radius":RADIUS},{"frame":frame,"around":0.0,"across":0.0,"radius":RADIUS},Transform3D.IDENTITY,limit).valid), "singular basis accepted")
	_require(bool(CLOUDS.gameplay_frame_remap({}, {}, Transform3D.IDENTITY, limit).has_contract) == false, "no-contract incorrectly supplied")
	var previous := {"frame": frame, "around": 0.0, "across": 0.0, "radius": RADIUS}
	for key in ["around", "across"]:
		var jumped := previous.duplicate()
		jumped[key] = limit + 1.0
		_require(not bool(CLOUDS.gameplay_frame_remap(jumped, previous, Transform3D.IDENTITY, limit).valid), "large " + key + " jump accepted")
	var invalid := previous.duplicate()
	invalid.frame = Transform3D(Basis.IDENTITY, Vector3(NAN, 0, 0))
	var invalid_result: Dictionary = CLOUDS.gameplay_frame_remap(invalid, previous, Transform3D.IDENTITY, limit)
	_require(not bool(invalid_result.valid) and invalid_result.mapped_camera == Transform3D.IDENTITY, "invalid origin entered camera matrix")
	_require(not bool(CLOUDS.gameplay_frame_remap(previous, {}, Transform3D.IDENTITY, limit).valid), "missing previous contract accepted")

func _test_rigid_presentation_change() -> void:
	var presentation := Transform3D(Basis(Vector3.UP, deg_to_rad(47.0)), Vector3(128, -30, 50))
	var point_before := Vector3(8, 500, 12)
	var point_after := presentation * point_before
	var map := _remap(Transform3D.IDENTITY, presentation, 0, 0, 0, 0)
	_near(map * point_after, point_before, "rigid presentation rotation/translation changed correspondence")

func _test_large_canonical_small_motion() -> void:
	var previous := {"frame": Transform3D.IDENTITY, "around": 220_000_000_000.0, "across": 0.0, "radius": 100_000_000_000.0}
	var current := previous.duplicate()
	current.around = float(previous.around) + .125
	current.frame = Transform3D(Basis.IDENTITY, Vector3(.125, 0, 0))
	var result: Dictionary = CLOUDS.gameplay_frame_remap(current, previous, Transform3D.IDENTITY, 4000.0)
	_require(bool(result.valid), "large canonical coordinate rejected a small motion")
	_near((result.mapped_camera as Transform3D).origin, Vector3.ZERO, "small motion lost precision at large canonical position")
