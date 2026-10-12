extends "res://tools/far_cloud_detail_gpu_test.gd"

# Run the existing production weather/detail/handoff scenarios with both
# geometry presentations. The source scene, shaders and weather field are shared.
var patch_checks: Array = []
var pose_checks_done := false

func fixture_radius() -> float:
	return 80000.0 if OS.get_cmdline_user_args().has("--far-cloud-small-ring") else 5000000.0

func fixture_timeout_seconds() -> float:
	return 180.0

func configure_fixture_renderer() -> void:
	# Gameplay intentionally disables this renderer for small worlds. Exercise
	# its production geometry/camera explicitly without changing that policy.
	if fixture_radius() == 80000.0: _far.set("Enabled", true)

func _capture(label: String, hide_clouds := false, sync_far := true, hide_shell := false) -> Image:
	if _failed: return Image.new()
	# Stop streaming installs, weather/global publication and camera updates.
	# The far node alone may sync explicitly for controlled pose captures.
	_world.process_mode = Node.PROCESS_MODE_DISABLED
	_far.process_mode = Node.PROCESS_MODE_ALWAYS
	hide_animated_water()
	_far.call("ConfigureFarCloudPatchCulling", false)
	var baseline := await super._capture(label + "_unculled", hide_clouds, sync_far, hide_shell)
	if _failed: return Image.new()
	_far.call("ConfigureFarCloudPatchCulling", true)
	var culled := await super._capture(label + "_patches", hide_clouds, false, hide_shell)
	if _failed: return Image.new()
	var delta := _mean_absolute_delta(baseline, culled)
	var pixel_stats := _pixel_change_stats(baseline, culled, .01)
	var changed_fraction := pixel_stats.x / float(baseline.get_width() * baseline.get_height())
	var diagnostic: Dictionary = _far.call("GetFarCloudPatchDiagnostics")
	patch_checks.append({"label":label,"mean_rgb_delta":delta,"maximum_pixel_rgb_delta":pixel_stats.y,"changed_pixel_fraction_over_01":pixel_stats.x / float(baseline.get_width() * baseline.get_height()),"patches":diagnostic})
	write_patch_report()
	if delta > .001 or changed_fraction > .01 or not _all_finite(culled):
		_fail("patch parity failed " + label + ": " + str(delta))
		return Image.new()
	if int(diagnostic.patches) != 20:
		_fail("unexpected cloud patch count: " + str(diagnostic))
		return Image.new()
	if not pose_checks_done and not hide_clouds:
		pose_checks_done = true
		await additional_poses()
	if _failed: return Image.new()
	write_patch_report()
	return culled

func write_patch_report() -> void:
	FileAccess.open("res://.godot/far_cloud_detail/patch_report.json", FileAccess.WRITE).store_string(JSON.stringify({"failed":_failed,"checks":patch_checks}, "\t"))

func _fail(message: String) -> void:
	if _failed: return
	super._fail(message)
	write_patch_report()

func hide_animated_water() -> void:
	var stack: Array = [_far.get_node("FarRingViewport")]
	while not stack.is_empty():
		var node: Node = stack.pop_back()
		if node is GeometryInstance3D and ("Water" in str(node.name) or "Ocean" in str(node.name)):
			node.visible = false
		stack.append_array(node.get_children())

func additional_poses() -> void:
	var camera := _world.get_node("Explorer/Head/Camera3D") as Camera3D
	var grid := _world.get_node("ChunkGridManager")
	var saved := camera.global_transform
	for height in [3.0, 20700.0, minf(200000.0, fixture_radius() * .35)]:
		if _failed: return
		var p: Vector3 = grid.call("CanonicalRingPositionToWorld", Vector3(0, height, 159000))
		var radial_probe: Vector3 = grid.call("CanonicalRingPositionToWorld", Vector3(0, height + 10, 159000))
		var up: Vector3 = (radial_probe - p).normalized()
		camera.global_position = p
		camera.look_at(grid.call("CanonicalRingPositionToWorld", Vector3(60000, 3000, 159000)), up)
		_far.call("ConfigureFarCloudPatchCulling", false)
		var baseline := await super._capture("pose_%s_baseline" % height, false, true)
		_far.call("ConfigureFarCloudPatchCulling", true)
		var culled := await super._capture("pose_%s_patches" % height, false, false)
		if _failed: return
		var delta := _mean_absolute_delta(baseline, culled)
		var pixel_stats := _pixel_change_stats(baseline, culled, .01)
		var changed_fraction := pixel_stats.x / float(baseline.get_width() * baseline.get_height())
		patch_checks.append({"height":height,"mean_rgb_delta":delta,"maximum_pixel_rgb_delta":pixel_stats.y,"changed_pixel_fraction_over_01":pixel_stats.x / float(baseline.get_width() * baseline.get_height()),"patches":_far.call("GetFarCloudPatchDiagnostics")})
		write_patch_report()
		if delta > .001 or changed_fraction > .01:
			_fail("pose patch parity failed: " + str(height))
			return
	# Point the actual source camera out of the ring, then inspect conservative
	# patch bounds against the actual far camera's planes (no timing benchmark).
	camera.look_at(camera.global_position + camera.global_basis.y * 100, camera.global_basis.x)
	var hidden: Array = []
	var stack: Array = [_far.get_node("FarRingViewport")]
	while not stack.is_empty():
		var node: Node = stack.pop_back()
		if node in _cloud_nodes: continue
		if node is GeometryInstance3D:
			hidden.append([node, node.visible])
			node.visible = false
		stack.append_array(node.get_children())
	await super._capture("look_away_patches", false, true)
	if _failed: return
	var away: Dictionary = _far.call("GetFarCloudPatchDiagnostics")
	patch_checks.append({"look_away":away})
	if int(away.frustum_intersecting_patches) >= int(away.patches): _fail("look-away frustum rejected no cloud patches")
	if int(away.viewport_visible_objects) >= 20: _fail("isolated look-away renderer rejected no cloud patch objects")
	if _failed: return
	for item in hidden: item[0].visible = item[1]
	camera.global_transform = saved
	_far.set_process(true)
	await process_frame
	await process_frame
	_far.set_process(false)
