extends SceneTree

# Production publisher and material wiring; never save the disposable world.
const WORLD := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const DRIVER := preload("res://tools/RainPrecipitationTestDriver.cs")
const EFFECTS := preload("res://scripts/RingLocalWeatherEffects.cs")
const ISOLATION := preload("res://scripts/Tools/MinimapWindContractTest.cs")
const WIND_DRIVER := preload("res://tools/VegetationWindTestDriver.cs")
var world: Node
var local: Node
var weather: Node
var grid: Node3D
var driver: RefCounted
var failed := false
var probe: SubViewport
var probe_material: ShaderMaterial
var wind_driver: RefCounted

# GPU-side comparison avoids the editor-only GlobalShaderParameterGet API.
const PAYLOAD_PROBE := """
shader_type canvas_item;
global uniform float ring_local_wind_enabled;
global uniform vec2 ring_local_wind_vector;
global uniform vec4 ring_local_wind_wave;
global uniform vec3 ring_local_wind_center;
global uniform vec3 ring_local_wind_axis;
global uniform float ring_wind_field_enabled;
global uniform vec2 ring_wind_mean_vector;
global uniform vec2 ring_tree_wind_parts;
uniform float expected_enabled;
uniform vec2 expected_wind;
uniform vec4 expected_wave;
uniform vec3 expected_center;
uniform vec3 expected_axis;
uniform float expected_shared_enabled;
uniform vec2 expected_mean;
uniform vec2 expected_parts;
void fragment() {
	bool matches = abs(ring_local_wind_enabled - expected_enabled) < 0.00001
		&& length(ring_local_wind_vector - expected_wind) < 0.00001
		&& length(ring_local_wind_wave - expected_wave) < 0.00001
		&& length(ring_local_wind_center - expected_center) == 0.0
		&& length(ring_local_wind_axis - expected_axis) < 0.00001
		&& abs(ring_wind_field_enabled - expected_shared_enabled) < 0.00001
		&& length(ring_wind_mean_vector - expected_mean) < 0.00001
		&& length(ring_tree_wind_parts - expected_parts) < 0.00001;
	COLOR = matches ? vec4(0.0, 1.0, 0.0, 1.0) : vec4(1.0, 0.0, 0.0, 1.0);
}
"""

func _initialize() -> void:
	call_deferred("run")

func run() -> void:
	world = WORLD.instantiate()
	var preferences := "res://.godot/vegetation-wind-state/%d" % Time.get_ticks_msec()
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(preferences))
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", "")
	var isolated: Dictionary = ISOLATION.new().ExecuteFixture("isolation", world, preferences)
	if not check(bool(isolated.get("pass", false)), "preference isolation failed: " + str(isolated.get("detail", ""))): return
	if "--vegetation-wind-certified" in OS.get_cmdline_user_args():
		var launcher := preload("res://scenes/WorldSizeLauncher.tscn").instantiate()
		root.add_child(launcher)
		if not launcher.is_node_ready(): await launcher.ready
		launcher.call("_on_certified_preset_pressed")
		launcher.call("_configure_game", world, 5000000.0)
		launcher.free()
	root.add_child(world)
	for i in range(8): await process_frame
	local = world.get_node("LocalWeatherEffects")
	weather = world.get_node("RingVolumetricClouds")
	grid = world.get_node("ChunkGridManager")
	world.get_node("Explorer").set_physics_process(false)
	world.get_node("SunshineCloudBridge").set_process(false)
	weather.set_process(false)
	local.set_process(false)
	local.set("EnableLightning", false)
	driver = DRIVER.new()
	wind_driver = WIND_DRIVER.new()
	set_wind(Vector2(12, -5), 120.0)
	if not check_payload(Vector2(12, -5), true): return
	if not await check_gpu_payload(diag(), "initial publish"): return
	if not check(Vector2(local.get("ResolvedWindMetersPerSecond")) == Vector2(12, -5), "public wind changed"): return
	var particles := local.get_node("CameraLocalPrecipitation") as GPUParticles3D
	var particle_id := particles.get_instance_id()
	var restarts := int(local.get("ParticleRestartCount"))
	var count := int(diag().push_count)
	for i in range(200): local.call("_Process", .016)
	if not check(int(diag().push_count) == count, "stationary frozen field keeps sending shader globals"): return
	# These use the actual live Resource and production publisher, with the
	# clock frozen. No fixture manually assigns the shader's parts global.
	for strengths in [Vector2(0, 1), Vector2(.15, 0), Vector2.ZERO,
		Vector2(.42, 2.2), Vector2(2, 9), Vector2(NAN, INF), Vector2(.37, 1.8)]:
		var sanitized: Vector2 = wind_driver.call("SetTreeWindParts", local, strengths)
		local.call("_Process", .016)
		var live_parts := diag()
		if not check(Vector2(live_parts.parts).is_equal_approx(sanitized), "live tree parts expectation differs from sanitized settings"): return
		if not await check_gpu_payload(live_parts, "live tree strengths " + str(strengths)): return
	var frozen_parts := diag().duplicate()
	for i in range(32): local.call("_Process", .016)
	if not check(Vector2(diag().parts) == Vector2(frozen_parts.parts), "frozen live strengths changed"): return
	if not await check_gpu_payload(frozen_parts, "frozen unchanged tree parts"): return
	set_wind(Vector2(12, -5), 120.05)
	if not check(int(diag().push_count) - count == 1, "time-only update should change exactly the wave payload"): return
	# Large weather times must retain small-frame animation; no float phase cast.
	set_wind(Vector2(12, -5), 1.0e9)
	var old_wave := Vector4(diag().wave)
	set_wind(Vector2(12, -5), 1.0e9 + .05)
	if not check(Vector4(diag().wave).distance_to(old_wave) > .0001, "long-running wind lost time precision"): return
	set_wind(Vector2(12, -5), 120.0)
	var repeat := diag()
	set_wind(Vector2(12, -5), 55.0)
	set_wind(Vector2(12, -5), 120.0)
	if not check(Vector4(diag().wave) == Vector4(repeat.wave), "clock restore is not deterministic"): return
	# Center uses exact comparisons, retaining a 1m rebase at large coordinates.
	grid.position += Vector3(4000000, -7000, 2500)
	local.call("_Process", .016)
	var before_center := Vector3(diag().center)
	grid.position += Vector3.RIGHT
	local.call("_Process", .016)
	if not check(Vector3(diag().center) - before_center == Vector3.RIGHT, "small far-origin shift was lost"): return
	grid.basis = Basis(Vector3.RIGHT, .47) * grid.basis
	local.call("_Process", .016)
	if not check(Vector3(diag().axis).is_equal_approx(grid.global_basis.y.normalized()), "wind ignores rotated ring axis"): return
	if not check_payload(Vector2(12, -5), true): return
	if not await check_gpu_payload(diag(), "rotated/rebased ring"): return
	# Vegetation toggle does not disable rain/audio or reallocate their resources.
	local.set("EnableVegetationWind", false)
	local.call("_Process", .016)
	if not check_payload(Vector2.ZERO, false): return
	if not await check_gpu_payload(diag(), "vegetation disabled"): return
	if not check(Vector2(local.get("ResolvedWindMetersPerSecond")) == Vector2(12, -5) and particles.get_instance_id() == particle_id and particles.amount == 4096 and int(local.get("ParticleRestartCount")) == restarts, "vegetation toggle changed rain allocation/state"): return
	local.set("EnableVegetationWind", true)
	local.call("_Process", .016)
	if not check_payload(Vector2(12, -5), true): return
	for enabled_property in ["Enabled", "source"]:
		var node := weather if enabled_property == "source" else local
		node.set("Enabled", false)
		local.call("_Process", .016)
		if not check_payload(Vector2.ZERO, false): return
		node.set("Enabled", true)
		local.call("_Process", .016)
		if not check_payload(Vector2(12, -5), true): return
	set_wind(Vector2.ZERO, 300.0)
	if not check_payload(Vector2.ZERO, false): return
	set_wind(Vector2(NAN, INF), 300.0)
	if not check_payload(Vector2.ZERO, false): return
	set_wind(Vector2(1.0e30, -1.0e30), 300.0)
	if not check(Vector2(diag().wind).is_finite() and Vector2(diag().wind).length() <= 80.01, "extreme wind escaped the cap"): return
	set_wind(Vector2(12, -5), NAN)
	if not check(Vector4(diag().wave).is_finite() and is_finite(float(diag().source_time)), "invalid clock reached shader"): return
	set_wind(Vector2(12, -5), 120.0)
	# Freeze normal process-tree execution, not a synthetic TIME uniform.
	local.set_process(true)
	paused = true
	count = int(diag().push_count)
	var frozen := Vector4(diag().wave)
	for i in range(6): await process_frame
	if not check(int(diag().push_count) == count and Vector4(diag().wave) == frozen, "scene pause kept publishing wind"): return
	paused = false
	local.set_process(false)
	# Inspect the actual material templates, including both centered flat LODs.
	var flat_count := 0
	var three_d_count := 0
	var imported_count := 0
	for node in world.find_children("*", "MultiMeshInstance3D", true, false):
		var is_legacy_template := node.name.begins_with("BiomeTrees")
		var is_imported_template := node.name.begins_with("ImportedForestLod")
		if not is_legacy_template and not is_imported_template: continue
		if is_imported_template:
			imported_count += 1
			var imported_mesh: Mesh = node.multimesh.mesh
			if not check(imported_mesh != null and not imported_mesh is QuadMesh, "imported tree template is unexpectedly flat: " + node.name): return
			var imported_material_count := 0
			for surface in range(imported_mesh.get_surface_count()):
				var surface_material := imported_mesh.surface_get_material(surface) as ShaderMaterial
				if not surface_material: continue
				imported_material_count += 1
				if not check(surface_material.shader != null and surface_material.shader.resource_path == "res://materials/imported_tree.gdshader", "imported tree surface does not use imported wind shader: " + node.name): return
				var imported_wind = surface_material.get_shader_parameter("vegetation_wind_enabled")
				# An unset parameter uses this shader's declared true default.
				if not check(imported_wind == null or bool(imported_wind), "imported 3D tree wind disabled: " + node.name): return
			if not check(imported_material_count > 0, "imported tree template has no shader surface material: " + node.name): return
			continue
		var mat := node.material_override as ShaderMaterial
		if not mat: continue
		var value = mat.get_shader_parameter("vegetation_wind_enabled")
		if node.multimesh.mesh is QuadMesh:
			flat_count += 1
			if not check(value != null and not bool(value), "flat tree quad receives root-zero bend: " + node.name): return
		else:
			three_d_count += 1
			if not check(value == null or bool(value), "3D tree wind disabled: " + node.name): return
	# Legacy templates retain their own flat/3D assertions whenever a scene
	# installs them. The current production scene installs imported LOD templates.
	if not check(flat_count >= 2 and (three_d_count > 0 or imported_count > 0), "production tree material templates not found"): return
	# New ready owner takes precedence; an old still-processing/closing scene
	# must neither replace its vector nor clear its globals during cleanup.
	# Other production consumers retain references until their scene exits;
	# suspend them for this fixture-only removal of their required publisher.
	for node in world.find_children("*", "Node", true, false): node.set_process(false)
	var old := local
	var next := EFFECTS.new()
	next.name = "WindOwnerReplacement"
	next.set("Enabled", true)
	next.set("EnableLightning", false)
	world.add_child(next)
	next.set_process(false)
	local = next
	set_wind(Vector2(-9, 4), 200.0)
	var next_owner := int(diag().owner)
	if not await check_gpu_payload(diag(), "replacement owner tree parts"): return
	old.call("_Process", .016)
	if not check(int(diag().active_owner) == next_owner, "old owner stole the active field"): return
	if not check_payload(Vector2(-9, 4), true): return
	if not await check_gpu_payload(diag(), "old owner cannot replace tree parts"): return
	old.queue_free()
	for i in range(3): await process_frame
	if not check_payload(Vector2(-9, 4), true): return
	if not await check_gpu_payload(diag(), "old owner exit"): return
	var final_diagnostics := diag()
	local.queue_free()
	for i in range(3): await process_frame
	var cleared_payload := final_diagnostics.duplicate()
	cleared_payload.enabled = 0.0
	cleared_payload.shared_enabled = 0.0
	cleared_payload.mean = Vector2.ZERO
	cleared_payload.parts = Vector2(.15, 1)
	if not await check_gpu_payload(cleared_payload, "active owner exit"): return
	# A fresh local owner reuses the same authoritative weather/field object.
	# Equal payload values must still restore globals after owner cleanup.
	var restored := EFFECTS.new()
	var unclaimed_diagnostics: Dictionary = restored.call("GetVegetationWindDiagnostics")
	if not check(int(unclaimed_diagnostics.active_owner) == 0, "active owner exit retained ownership"):
		restored.free()
		return
	restored.name = "WindOwnerReactivated"
	restored.set("Enabled", true)
	restored.set("EnableLightning", false)
	world.add_child(restored)
	restored.set_process(false)
	local = restored
	set_wind(Vector2(-9, 4), 200.0)
	if not check(int(diag().active_owner) == int(diag().owner) and int(diag().owner) != next_owner, "fresh publisher did not claim owner"): return
	if not check_payload(Vector2(-9, 4), true): return
	if not await check_gpu_payload(diag(), "same source fresh owner reactivation"): return
	local.queue_free()
	for i in range(3): await process_frame
	if not await check_gpu_payload(cleared_payload, "reactivated owner exit"): return
	if probe: probe.queue_free()
	world.queue_free()
	for i in range(3): await process_frame
	print("VEGETATION_WIND_STATE=PASS 3d_templates=", three_d_count, " flat_templates=", flat_count, " imported_templates=", imported_count, " diagnostics=", final_diagnostics)
	quit(0)

func set_wind(wind: Vector2, time: float) -> void:
	driver.call("SetIntensity", local, .5, false, wind)
	weather.set("CloudSimulationTimeSeconds", time)
	local.call("_Process", .016)
func diag() -> Dictionary:
	var result: Dictionary = local.call("GetVegetationWindDiagnostics")
	result.merge(wind_driver.call("GetSharedWindPayload", local))
	return result
func check_payload(wind: Vector2, enabled: bool) -> bool:
	var d := diag()
	if not check(Vector2(d.wind).is_equal_approx(wind) and (float(d.enabled) > .5) == enabled and Vector3(d.center).is_finite() and Vector3(d.axis).is_finite() and Vector4(d.wave).is_finite(), "invalid publisher payload"): return false
	return true

func check_gpu_payload(expected: Dictionary, label: String) -> bool:
	if RenderingServer.get_rendering_device() == null: return true
	if not probe:
		probe = SubViewport.new()
		probe.size = Vector2i(128, 128)
		probe.own_world_3d = true
		probe.disable_3d = true
		probe.render_target_update_mode = SubViewport.UPDATE_ALWAYS
		root.add_child(probe)
		var shader := Shader.new()
		shader.code = PAYLOAD_PROBE
		probe_material = ShaderMaterial.new()
		probe_material.shader = shader
		var quad := ColorRect.new()
		quad.size = Vector2(128, 128)
		quad.material = probe_material
		probe.add_child(quad)
	for key in ["enabled", "wind", "wave", "center", "axis", "shared_enabled", "mean", "parts"]:
		probe_material.set_shader_parameter("expected_" + key, expected[key])
	for i in range(4): await RenderingServer.frame_post_draw
	var image := probe.get_texture().get_image()
	if not check(image != null and not image.is_empty(), "empty GPU payload probe: " + label): return false
	var pixel := image.get_pixel(4, 4)
	if not check(pixel.g > .99 and pixel.r < .01, "GPU payload mismatch: %s pixel=%s expected=%s" % [label, pixel, expected]): return false
	print("VEGETATION_WIND_GPU_BINDING=PASS ", label)
	return true

func check(ok: bool, message: String) -> bool:
	if not ok and not failed:
		failed = true
		push_error("VEGETATION_WIND_STATE=FAIL " + message)
		quit(1)
	return ok
