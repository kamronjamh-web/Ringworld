extends SceneTree

# Render production shaders, not a replacement test shader. No gameplay scene or saved preferences.
const PROBE := preload("res://tools/TerrainCloudReceiverGpuTest.cs")
const TIERS := ["ring_cloud_layer", "ring_far_cloud_clipmap", "ring_cloud_transmittance_mask", "ring_mesh_cloud", "ring_local_volumetric_cloud", "ring_cloud_canopy", "ring_volumetric_cloud"]
var _probe: RefCounted
var _viewport: SubViewport
var _material: ShaderMaterial
var _light: DirectionalLight3D
var _geometry_root: Node3D
var _camera: Camera3D
var _fog_backdrop: StandardMaterial3D
var _fog_volume: FogVolume
var _tier := "startup"
var _phase := "startup"
var _started := Time.get_ticks_msec()
var _failed := false
var _output := "res://.godot/terrain-cloud-receivers-%s" % Time.get_ticks_usec()
var _results: Array[Dictionary] = []

func _initialize() -> void:
	call_deferred("_run")

func _process(_delta: float) -> bool:
	if not _failed and Time.get_ticks_msec() - _started > 180000:
		_fail("watchdog: %s/%s" % [_tier, _phase])
	return _failed

func _run() -> void:
	if RenderingServer.get_rendering_device() == null:
		return _fail("requires RenderingDevice GPU session")
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(_output))
	RenderingServer.global_shader_parameter_set("ringworld_solar_visibility", 1.0)
	Engine.time_scale = 0.0
	_probe = PROBE.new()
	for tier in TIERS:
		_tier = tier
		_build(tier)
		if _fog_volume != null:
			print("fog production uniforms: ", _material.shader.get_shader_uniform_list())
			print("fog configured density=", _material.get_shader_parameter("density_scale"), " coverage=", _material.get_shader_parameter("coverage"), " origin=", _material.get_shader_parameter("canonical_origin_km"))
			var production: Material = _fog_volume.material
			var control := FogMaterial.new()
			control.density = 0.02
			control.albedo = Color.WHITE
			control.emission = Color(0.5, 0.5, 0.5)
			_fog_volume.material = control
			var native_control := await _capture("clear", "native_control")
			_fog_volume.visible = false
			var native_empty := await _capture("clear", "native_empty")
			if native_control == null or native_empty == null: return
			var native: Dictionary = _probe.Compare(native_control, native_empty)
			if float(native.mean) < 0.005:
				return _fail("native minimal fog pipeline control invisible: %s" % native)
			_fog_volume.visible = true
			_fog_volume.material = production
		var clear := await _capture("clear")
		if clear == null: return
		for state in ["disabled", "unknown", "stale", "out_band"]:
			var inactive := await _capture(state)
			if inactive == null: return
			var stats: Dictionary = _probe.Compare(clear, inactive)
			if float(stats.mean) > 0.0015 or float(stats.alpha_max) > 0.004:
				return _fail("%s parity: %s" % [state, stats])
		var blocked := await _capture("blocked")
		if blocked == null: return
		var direct: Dictionary = _probe.Compare(clear, blocked)
		if float(direct.mean) < 0.001 or float(direct.before) <= float(direct.after) + 0.0005:
			return _fail("blocked field did not reduce actual solar pixels: %s" % direct)
		if float(direct.alpha_max) > 0.004:
			return _fail("terrain shadow changed alpha: %s" % direct)
		if tier in ["ring_local_volumetric_cloud", "ring_volumetric_cloud", "ring_cloud_transmittance_mask"]:
			# An eye-height change must retain shadow behavior at the new physical sample.
			if _camera != null: _camera.position.y += 1.7
			if tier == "ring_cloud_transmittance_mask":
				_material.set_shader_parameter("camera_canonical_km", Vector3(0, 4.0017, 171))
			var eye_clear := await _capture("clear", "eye_clear")
			var eye_blocked := await _capture("blocked", "eye_blocked")
			if eye_clear == null or eye_blocked == null: return
			var eye: Dictionary = _probe.Compare(eye_clear, eye_blocked)
			if float(eye.mean) < 0.001 or float(eye.alpha_max) > 0.004:
				return _fail("eye-height solar/alpha contract: %s" % eye)
		if tier == "ring_mesh_cloud":
			# Rotate and rebase the world while retaining the same canonical cloud.
			# Global clear/regional blocked forces the actual inverse world frame to
			# hit the 32km regional square; a missing inverse samples global clear.
			var basis := Basis(Vector3.UP, 0.7)
			var origin := Vector3(250000, 100, 500000)
			_geometry_root.transform = Transform3D(basis, origin)
			_material.set_shader_parameter("terrain_cloud_world_to_ring", basis.inverse())
			_material.set_shader_parameter("ring_center", basis * Vector3(-4997800, 0, 0) + origin)
			_material.set_shader_parameter("camera_world_position", basis * Vector3(0, 0, 12000) + origin)
			_material.set_shader_parameter("surface_up", basis * Vector3.UP)
			_material.set_shader_parameter("sun_direction", basis * Vector3.UP)
			var rotated_blocked := await _capture("blocked", "rotated_rebased_blocked")
			var regional_blocked := await _capture("regional_blocked", "rotated_rebased_regional")
			if rotated_blocked == null or regional_blocked == null: return
			var frame: Dictionary = _probe.Compare(rotated_blocked, regional_blocked)
			if float(frame.mean) > 0.0015:
				return _fail("rotated/rebased canonical frame missed regional shadow: %s" % frame)
		# Actual shader terminator: solar tint vanishes, while ambient/night remains.
		_set_day(false)
		var night_clear := await _capture("clear", "night_clear")
		var night_blocked := await _capture("blocked", "night_blocked")
		if night_clear == null or night_blocked == null: return
		var ambient: Dictionary = _probe.Compare(night_clear, night_blocked)
		if float(ambient.mean) > 0.0015:
			return _fail("ambient/night changed: %s" % ambient)
		_set_day(true)
		if tier in ["ring_local_volumetric_cloud", "ring_volumetric_cloud"]:
			# Native fog illumination is outside the field contract. With no native
			# illumination only the original emission remains and must match exactly.
			_light.light_energy = 0.0
			# Raise density within the authored range so native packed emission is measurable.
			_material.set_shader_parameter("density_scale", 0.12)
			var emission_clear := await _capture("clear", "emission_clear")
			var emission_blocked := await _capture("blocked", "emission_blocked")
			if emission_clear == null or emission_blocked == null: return
			var emission: Dictionary = _probe.Compare(emission_clear, emission_blocked)
			if float(emission.mean) > 0.0015 or float(emission.before) < 0.0001:
				return _fail("fog emission changed: %s" % emission)
			# Fog captures are opaque, so RGBA alpha alone is not an optical-depth
			# assertion. A second backdrop tests actual transmission independently
			# of the preserved native emission and solar tint.
			_material.set_shader_parameter("density_scale", 0.02)
			var transmission_black := await _capture("clear", "transmission_black")
			if transmission_black == null: return
			_fog_backdrop.albedo_color = Color(0.5, 0.5, 0.5)
			var transmission_clear := await _capture("clear", "transmission_clear")
			var transmission_blocked := await _capture("blocked", "transmission_blocked")
			if transmission_clear == null or transmission_blocked == null: return
			var transmission: Dictionary = _probe.Compare(transmission_clear, transmission_blocked)
			var background_response: Dictionary = _probe.Compare(transmission_black, transmission_clear)
			if float(transmission.mean) > 0.0015 or float(background_response.mean) < 0.001:
				return _fail("native fog transmission changed or backdrop invisible: shadow=%s response=%s" % [transmission, background_response])
		if tier in ["ring_cloud_layer", "ring_far_cloud_clipmap"]:
			# Compare the additive flash increment in both solar states. The cloud
			# body may be darker, but the lightning carrier must retain its radiance.
			var flash_base_clear := await _capture("clear", "flash_base_clear")
			var flash_base_blocked := await _capture("blocked", "flash_base_blocked")
			_material.set_shader_parameter("far_lightning_event_a", Vector4(0.5 if tier == "ring_cloud_layer" else 0.0, 0.5, 0.2, 0.03))
			_material.set_shader_parameter("far_lightning_event_weights", Vector2.ONE)
			var flash_clear := await _capture("clear", "flash_clear")
			var flash_blocked := await _capture("blocked", "flash_blocked")
			if flash_base_clear == null or flash_base_blocked == null or flash_clear == null or flash_blocked == null: return
			var fc: Dictionary = _probe.Compare(flash_base_clear, flash_clear)
			var fb: Dictionary = _probe.Compare(flash_base_blocked, flash_blocked)
			if float(fc.mean) < 0.0001 or abs(float(fc.mean) - float(fb.mean)) > 0.003:
				return _fail("flash increment missing or shadowed: clear=%s blocked=%s" % [fc, fb])
		_results.append({"tier": tier, "solar": direct, "ambient": ambient, "inactive_states": 4})
		_viewport.free()
		await process_frame
	var file := FileAccess.open(_output + "/results.json", FileAccess.WRITE)
	if file == null: return _fail("cannot write results")
	file.store_string(JSON.stringify(_results, "\t"))
	file.close()
	print("PASS terrain cloud actual receiver tiers=7 output=%s native fog emission preserved; native scattering remains outside mask contract" % _output)
	quit(0)

func _build(tier: String) -> void:
	_material = _probe.CreateMaterial(tier)
	_viewport = SubViewport.new()
	_viewport.size = Vector2i(128, 128)
	_viewport.transparent_bg = true
	_viewport.own_world_3d = true
	_viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	_viewport.msaa_3d = Viewport.MSAA_DISABLED
	root.add_child(_viewport)
	_light = null
	_camera = null
	_fog_backdrop = null
	_fog_volume = null
	_geometry_root = null
	if tier == "ring_cloud_transmittance_mask":
		_material.set_shader_parameter("density_scale", 0.048)
		var rect := ColorRect.new()
		rect.size = Vector2(128, 128)
		rect.material = _material
		_viewport.add_child(rect)
		return
	var world := WorldEnvironment.new()
	var environment := Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color.BLACK
	environment.tonemap_mode = Environment.TONE_MAPPER_LINEAR
	environment.tonemap_exposure = 0.25
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	environment.ambient_light_color = Color.BLACK
	environment.ambient_light_energy = 0.0
	world.environment = environment
	_viewport.add_child(world)
	_geometry_root = Node3D.new()
	_viewport.add_child(_geometry_root)
	var camera := Camera3D.new()
	_camera = camera
	camera.position = Vector3(0, 0, 12000)
	camera.near = 10.0
	camera.far = 30000.0
	camera.fov = 45.0
	camera.current = true
	_geometry_root.add_child(camera)
	if tier in ["ring_local_volumetric_cloud", "ring_volumetric_cloud"]:
		# Forward+ skips the BG_COLOR fog-only pass when transparent_bg is true.
		# Native fog needs a scene color/depth target. Place the camera inside
		# the physical cloud and the backdrop 100m away for measurable transmission.
		_viewport.transparent_bg = false
		environment.volumetric_fog_enabled = true
		environment.volumetric_fog_density = 0.0
		camera.position.z = 0.0
		environment.volumetric_fog_length = 512.0
		environment.volumetric_fog_ambient_inject = 0.0
		environment.volumetric_fog_temporal_reprojection_enabled = false
		var backdrop := MeshInstance3D.new()
		var backdrop_quad := QuadMesh.new()
		backdrop_quad.size = Vector2(30000, 30000)
		backdrop.mesh = backdrop_quad
		backdrop.position.z = -100.0
		_fog_backdrop = StandardMaterial3D.new()
		_fog_backdrop.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
		_fog_backdrop.albedo_color = Color.BLACK
		backdrop.material_override = _fog_backdrop
		_viewport.add_child(backdrop)
		var fog := FogVolume.new()
		_fog_volume = fog
		fog.shape = RenderingServer.FOG_VOLUME_SHAPE_BOX
		fog.size = Vector3(8000, 8000, 8000)
		fog.material = _material
		_viewport.add_child(fog)
		_light = DirectionalLight3D.new()
		_light.light_energy = 2.0
		_light.light_volumetric_fog_energy = 1.0
		_light.rotation_degrees = Vector3(-90, 0, 0)
		_viewport.add_child(_light)
	else:
		var mesh := MeshInstance3D.new()
		var quad := QuadMesh.new()
		quad.size = Vector2(8000, 8000)
		mesh.mesh = quad
		mesh.material_override = _material
		_geometry_root.add_child(mesh)

func _set_day(day: bool) -> void:
	RenderingServer.global_shader_parameter_set("ringworld_solar_visibility", 1.0 if day else 0.0)
	# Eclipse switches effective day to zero for every shader, independent of its
	# radial or tangent surface normal. This avoids substituting a test lighting formula.

func _capture(state: String, label := "") -> Image:
	_phase = label if not label.is_empty() else state
	_probe.SetState(_material, state)
	for _i in 4: await process_frame
	await RenderingServer.frame_post_draw
	var image := _viewport.get_texture().get_image()
	if image == null or image.is_empty():
		_fail("empty GPU capture")
		return null
	var filename := _output + "/%s_%s.png" % [_tier, _phase]
	if image.save_png(filename) != OK:
		_fail("cannot write " + filename)
		return null
	return image

func _fail(detail: String) -> void:
	if _failed: return
	_failed = true
	push_error("FAIL terrain cloud receiver %s/%s: %s" % [_tier, _phase, detail])
	quit(1)
