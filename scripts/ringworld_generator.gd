extends Node3D

@export_group("Ring Dimensions")
# GTA-map-fit width preset: 20 km width, radius = 4x width (80 km).
@export var ring_radius: float = 80000.0
@export var ring_width: float = 20000.0
@export var segment_count: int = 252
@export var segment_thickness: float = 36.0
@export var auto_segment_count_from_arc_length: bool = true
@export var target_segment_arc_length: float = 2000.0

@export_group("Traversal Timing")
@export var traversal_time_seconds: float = 300.0
@export var auto_calculate_player_speed: bool = false
@export var player_speed: float = 8.0
@export var gravity_strength: float = 42.0

@export_group("High LOD Streaming")
@export var build_high_lod_segments: bool = true
@export var enable_streaming: bool = true
@export var high_lod_segment_range: int = 32
@export var streaming_updates_per_second: float = 8.0

@export_group("Far Ring LOD")
@export var build_far_ring_lod: bool = true
@export var far_ring_segment_count: int = 1024
@export var far_ring_surface_offset: float = 256.0
@export var far_ring_material: Material = preload("res://materials/ring_surface_far.tres")
@export var terrain_biome_catalog: Resource = preload("res://resources/voxel/terrain_biome_catalog_default.tres")
@export_range(0.0, 1.0, 0.01) var far_ring_biome_color_strength: float = 0.92

@export_group("Far Ring Atmosphere Tint")
@export var far_ring_sun_light_path: NodePath = NodePath("../StarLight")
@export var far_day_atmosphere_color: Color = Color(0.35, 0.52, 0.74, 1.0)
@export var far_night_atmosphere_color: Color = Color(0.06, 0.1, 0.16, 1.0)
@export var far_tint_strength: float = 0.0
@export var far_tint_min_distance: float = 20000.0
@export var far_tint_max_distance: float = 260000.0
@export var far_day_start_dot: float = -0.2
@export var far_day_full_dot: float = 0.35

@export_group("Section Day/Night Shading")
@export var section_day_start_dot: float = -0.2
@export var section_day_full_dot: float = 0.35
@export var section_night_brightness: float = 0.45
@export var section_night_direct_light_factor: float = 0.0

@export_group("Landmarks")
@export var rib_interval: int = 12
@export var rib_height: float = 150.0
@export var tower_interval: int = 36
@export var tower_height: float = 280.0

@export_group("References")
@export var player_path: NodePath = NodePath("../Player")
@export var gravity_controller_path: NodePath = NodePath("GravityController")
@export var ring_segment_scene: PackedScene = preload("res://scenes/RingSegment.tscn")
@export var surface_material_a: Material = preload("res://materials/ring_surface_a.tres")
@export var surface_material_b: Material = preload("res://materials/ring_surface_b.tres")
@export var landmark_material: Material = preload("res://materials/landmark_glow.tres")

@onready var segments_root: Node3D = $Segments
@onready var far_ring_root: Node3D = $FarRing

var loaded_high_lod_segments: Dictionary = {}
var streaming_timer: float = 0.0
var last_stream_center_index: int = -1
var cached_player: Node3D = null
var cached_sun_light: DirectionalLight3D = null
# The playable boxes share these two terrain-only copies.  The exported source
# materials remain assigned to ribs and other structural geometry at strength 0.
var terrain_surface_material_a: ShaderMaterial = null
var terrain_surface_material_b: ShaderMaterial = null
var terrain_surface_source_a: Material = null
var terrain_surface_source_b: Material = null


func _ready() -> void:
	regenerate_ring()
	set_physics_process(true)


func _physics_process(delta: float) -> void:
	_update_dynamic_material_uniforms()

	if not build_high_lod_segments or not enable_streaming:
		return

	streaming_timer += delta
	var update_interval: float = 1.0 / maxf(streaming_updates_per_second, 1.0)
	if streaming_timer < update_interval:
		return

	streaming_timer = 0.0
	_update_streaming_window(false)


func get_circumference() -> float:
	return TAU * ring_radius


func regenerate_ring() -> void:
	ring_radius = max(ring_radius, 10.0)
	ring_width = max(ring_width, 10.0)
	if auto_segment_count_from_arc_length:
		segment_count = int(ceil(get_circumference() / maxf(target_segment_arc_length, 25.0)))

	segment_count = max(segment_count, 32)
	segment_thickness = max(segment_thickness, 1.0)
	traversal_time_seconds = max(traversal_time_seconds, 1.0)
	high_lod_segment_range = max(high_lod_segment_range, 4)
	far_ring_segment_count = max(far_ring_segment_count, 24)
	far_ring_surface_offset = max(far_ring_surface_offset, 0.0)

	if auto_calculate_player_speed:
		# circumference = 2 * PI * ring_radius
		# player_speed = circumference / traversal_time_seconds
		player_speed = get_circumference() / traversal_time_seconds

	_clear_children(segments_root)
	if far_ring_root != null:
		_clear_children(far_ring_root)

	_prepare_terrain_surface_materials()

	loaded_high_lod_segments.clear()
	streaming_timer = 0.0
	last_stream_center_index = -1
	cached_player = _get_player()

	var gravity_controller := get_node_or_null(gravity_controller_path)
	if gravity_controller != null:
		gravity_controller.ring_radius = ring_radius
		gravity_controller.ring_width = ring_width
		gravity_controller.gravity_strength = gravity_strength

	if ring_segment_scene == null:
		push_error("Ring segment scene is missing.")
		return

	if build_far_ring_lod:
		_build_far_ring_lod()

	if not build_high_lod_segments:
		pass
	elif enable_streaming:
		_update_streaming_window(true)
	else:
		for i in range(segment_count):
			var segment: Node3D = _create_high_lod_segment(i)
			segments_root.add_child(segment)

	set_physics_process(true)
	_update_dynamic_material_uniforms()
	call_deferred("_apply_to_player")


func _build_far_ring_lod() -> void:
	if far_ring_root == null:
		return

	var segment_angle: float = TAU / float(far_ring_segment_count)
	var segment_length: float = ring_radius * segment_angle
	var far_thickness: float = maxf(2.0, segment_thickness * 0.35)

	for i in range(far_ring_segment_count):
		var angle: float = float(i) * segment_angle
		var outward := Vector3(cos(angle), 0.0, sin(angle))
		var inward := -outward
		var tangent := Vector3(-sin(angle), 0.0, cos(angle))
		var width_axis := Vector3.UP

		var segment_basis := Basis(width_axis, inward, tangent).orthonormalized()
		var segment_center := outward * (ring_radius + far_ring_surface_offset + far_thickness * 0.5)

		var far_segment := MeshInstance3D.new()
		far_segment.name = "FarRingSegment_%03d" % i
		far_segment.transform = Transform3D(segment_basis, segment_center)

		var far_mesh := BoxMesh.new()
		far_mesh.size = Vector3(ring_width, far_thickness, segment_length + 1.0)
		far_segment.mesh = far_mesh
		far_segment.material_override = far_ring_material
		far_segment.cast_shadow = 0
		far_segment.ignore_occlusion_culling = true
		far_ring_root.add_child(far_segment)


func _update_streaming_window(force_rebuild: bool) -> void:
	var player := _get_player()
	if player == null:
		return

	var center_index: int = _get_segment_index_from_position(player.global_position)
	if not force_rebuild and center_index == last_stream_center_index:
		return

	last_stream_center_index = center_index

	var desired_indices: Dictionary = {}
	for offset in range(-high_lod_segment_range, high_lod_segment_range + 1):
		var segment_index: int = _wrap_segment_index(center_index + offset)
		desired_indices[segment_index] = true

		if not loaded_high_lod_segments.has(segment_index):
			var segment: Node3D = _create_high_lod_segment(segment_index)
			loaded_high_lod_segments[segment_index] = segment
			segments_root.add_child(segment)

	var unload_indices: Array[int] = []
	for key in loaded_high_lod_segments.keys():
		var loaded_index: int = int(key)
		if not desired_indices.has(loaded_index):
			unload_indices.append(loaded_index)

	for unload_index in unload_indices:
		var segment_node := loaded_high_lod_segments[unload_index] as Node3D
		loaded_high_lod_segments.erase(unload_index)
		if segment_node != null:
			segment_node.queue_free()


func _create_high_lod_segment(index: int) -> Node3D:
	var segment_angle: float = TAU / float(segment_count)
	var segment_length: float = ring_radius * segment_angle
	var angle: float = float(index) * segment_angle

	var outward := Vector3(cos(angle), 0.0, sin(angle))
	var inward := -outward
	var tangent := Vector3(-sin(angle), 0.0, cos(angle))
	var width_axis := Vector3.UP

	var segment_basis := Basis(width_axis, inward, tangent).orthonormalized()
	var segment_center := outward * (ring_radius + segment_thickness * 0.5)

	var segment := ring_segment_scene.instantiate() as Node3D
	segment.transform = Transform3D(segment_basis, segment_center)
	segment.name = "RingSegment_%04d" % index

	_configure_segment(segment, segment_length, index)
	_spawn_landmarks(index, segment, segment_length)
	return segment


func _get_segment_index_from_position(world_position: Vector3) -> int:
	var radial := world_position - global_position
	var angle: float = atan2(radial.z, radial.x)
	var normalized_angle: float = fposmod(angle, TAU)
	var fractional_index: float = normalized_angle / TAU * float(segment_count)
	return clampi(int(floor(fractional_index)), 0, segment_count - 1)


func _wrap_segment_index(index: int) -> int:
	var wrapped: int = index % segment_count
	if wrapped < 0:
		wrapped += segment_count
	return wrapped


func _get_player() -> Node3D:
	if cached_player == null or not is_instance_valid(cached_player):
		cached_player = get_node_or_null(player_path) as Node3D

	return cached_player


func _get_sun_light() -> DirectionalLight3D:
	if cached_sun_light == null or not is_instance_valid(cached_sun_light):
		cached_sun_light = get_node_or_null(far_ring_sun_light_path) as DirectionalLight3D

	return cached_sun_light


func _update_dynamic_material_uniforms() -> void:
	var to_sun: Vector3 = _get_to_sun_direction()
	_update_far_ring_material_uniforms(to_sun)
	_update_section_material_uniforms(to_sun)


func _get_to_sun_direction() -> Vector3:
	var sun_light := _get_sun_light()
	if sun_light == null:
		return Vector3.UP

	var light_ray_direction: Vector3 = -sun_light.global_transform.basis.z.normalized()
	return -light_ray_direction


func _update_far_ring_material_uniforms(to_sun: Vector3) -> void:
	var shader_material := far_ring_material as ShaderMaterial
	if shader_material == null:
		return

	shader_material.set_shader_parameter("ring_center", global_position)
	shader_material.set_shader_parameter("sun_direction", to_sun)
	shader_material.set_shader_parameter("day_atmosphere_color", far_day_atmosphere_color)
	shader_material.set_shader_parameter("night_atmosphere_color", far_night_atmosphere_color)
	shader_material.set_shader_parameter("tint_strength", far_tint_strength)
	shader_material.set_shader_parameter("min_tint_distance", far_tint_min_distance)
	shader_material.set_shader_parameter("max_tint_distance", maxf(far_tint_max_distance, far_tint_min_distance + 1.0))
	shader_material.set_shader_parameter("day_start_dot", far_day_start_dot)
	shader_material.set_shader_parameter("day_full_dot", far_day_full_dot)
	shader_material.set_shader_parameter("ring_radius_meters", ring_radius)
	shader_material.set_shader_parameter("ring_width_meters", ring_width)
	shader_material.set_shader_parameter("biome_color_strength", far_ring_biome_color_strength)
	_configure_far_ring_biome_palette(shader_material)


func _configure_far_ring_biome_palette(shader_material: ShaderMaterial) -> void:
	# The legacy whole-ring renderer has no terrain mesh samples to upload. It
	# still reads the actual biome catalog rather than carrying a second palette,
	# so its analytic distant surface stays visually aligned with voxel terrain
	# when a project changes the shared biome resource.
	var palette := {
		5: Color(0.22, 0.34, 0.14, 1.0), # Grassland
		3: Color(0.60, 0.64, 0.57, 1.0), # Tundra
		4: Color(0.51, 0.42, 0.22, 1.0), # Desert
		6: Color(0.10, 0.30, 0.12, 1.0), # Forest
		7: Color(0.38, 0.39, 0.40, 1.0), # Alpine
	}
	if terrain_biome_catalog != null:
		var biomes: Variant = terrain_biome_catalog.get("Biomes")
		if biomes == null:
			biomes = terrain_biome_catalog.get("biomes")
		if biomes != null:
			for biome in biomes:
				if biome == null:
					continue
				var biome_id: Variant = biome.get("Id")
				if biome_id == null:
					biome_id = biome.get("id")
				var surface_color: Variant = biome.get("SurfaceColor")
				if surface_color == null:
					surface_color = biome.get("surface_color")
				if biome_id != null and surface_color is Color:
					palette[int(biome_id)] = surface_color

	shader_material.set_shader_parameter("biome_grassland_color", palette[5])
	shader_material.set_shader_parameter("biome_tundra_color", palette[3])
	shader_material.set_shader_parameter("biome_desert_color", palette[4])
	shader_material.set_shader_parameter("biome_forest_color", palette[6])
	shader_material.set_shader_parameter("biome_alpine_color", palette[7])


func _update_section_material_uniforms(to_sun: Vector3) -> void:
	# Keep the original resources neutral for structural ribs, then update
	# the two shared terrain variants used by every playable box.
	_update_section_material_uniform(surface_material_a, to_sun, 0.0)
	_update_section_material_uniform(surface_material_b, to_sun, 0.0)
	_update_section_material_uniform(terrain_surface_material_a, to_sun, far_ring_biome_color_strength)
	_update_section_material_uniform(terrain_surface_material_b, to_sun, far_ring_biome_color_strength)


func _update_section_material_uniform(material: Material, to_sun: Vector3, biome_color_strength: float) -> void:
	var shader_material := material as ShaderMaterial
	if shader_material == null:
		return

	shader_material.set_shader_parameter("ring_center", global_position)
	shader_material.set_shader_parameter("sun_direction", to_sun)
	shader_material.set_shader_parameter("day_start_dot", section_day_start_dot)
	shader_material.set_shader_parameter("day_full_dot", section_day_full_dot)
	shader_material.set_shader_parameter("night_brightness", section_night_brightness)
	shader_material.set_shader_parameter("direct_light_night_factor", section_night_direct_light_factor)
	shader_material.set_shader_parameter("ring_radius_meters", ring_radius)
	shader_material.set_shader_parameter("ring_width_meters", ring_width)
	shader_material.set_shader_parameter("biome_color_strength", biome_color_strength)
	_configure_far_ring_biome_palette(shader_material)


func _prepare_terrain_surface_materials() -> void:
	terrain_surface_material_a = _get_terrain_surface_material(surface_material_a, terrain_surface_source_a, terrain_surface_material_a)
	terrain_surface_source_a = surface_material_a
	terrain_surface_material_b = _get_terrain_surface_material(surface_material_b, terrain_surface_source_b, terrain_surface_material_b)
	terrain_surface_source_b = surface_material_b


func _get_terrain_surface_material(source: Material, cached_source: Material, cached_variant: ShaderMaterial) -> ShaderMaterial:
	if source == null:
		return null
	if source == cached_source and cached_variant != null:
		return cached_variant

	var source_shader := source as ShaderMaterial
	if source_shader == null:
		push_warning("Ring terrain material must be a ShaderMaterial to inherit biome colors.")
		return null

	return source_shader.duplicate() as ShaderMaterial


func _configure_segment(segment: Node3D, segment_length: float, index: int) -> void:
	var visual := segment.get_node_or_null("Visual") as MeshInstance3D
	var collider := segment.get_node_or_null("Collider") as CollisionShape3D
	var guide_line := segment.get_node_or_null("GuideLine") as MeshInstance3D

	var box_mesh := BoxMesh.new()
	box_mesh.size = Vector3(ring_width, segment_thickness, segment_length + 0.5)

	if visual != null:
		visual.mesh = box_mesh
		visual.material_override = terrain_surface_material_a if index % 2 == 0 else terrain_surface_material_b

	var box_shape := BoxShape3D.new()
	box_shape.size = box_mesh.size
	if collider != null:
		collider.shape = box_shape

	if guide_line != null:
		var guide_mesh := BoxMesh.new()
		guide_mesh.size = Vector3(max(2.0, ring_width * 0.03), 0.35, segment_length * 0.9)
		guide_line.mesh = guide_mesh
		guide_line.position = Vector3(0.0, segment_thickness * 0.5 + 0.2, 0.0)
		guide_line.material_override = landmark_material


func _spawn_landmarks(index: int, segment_parent: Node3D, segment_length: float) -> void:
	if rib_interval > 0 and index % rib_interval == 0:
		var rib := MeshInstance3D.new()
		var rib_mesh := BoxMesh.new()
		rib_mesh.size = Vector3(ring_width * 1.08, rib_height, max(6.0, segment_length * 0.18))
		rib.mesh = rib_mesh
		rib.material_override = surface_material_b
		rib.position = Vector3(0.0, segment_thickness * 0.5 + rib_height * 0.5, 0.0)
		segment_parent.add_child(rib)

	if tower_interval > 0 and index % tower_interval == 0:
		var tower := MeshInstance3D.new()
		var tower_mesh := CylinderMesh.new()
		tower_mesh.top_radius = 10.0
		tower_mesh.bottom_radius = 14.0
		tower_mesh.height = tower_height
		tower.mesh = tower_mesh
		tower.material_override = landmark_material
		tower.position = Vector3(ring_width * 0.35, segment_thickness * 0.5 + tower_height * 0.5, 0.0)
		segment_parent.add_child(tower)

		var mirror_tower := MeshInstance3D.new()
		mirror_tower.mesh = tower_mesh
		mirror_tower.material_override = landmark_material
		mirror_tower.position = Vector3(-ring_width * 0.35, segment_thickness * 0.5 + tower_height * 0.5, 0.0)
		segment_parent.add_child(mirror_tower)


func _apply_to_player() -> void:
	var player := get_node_or_null(player_path)
	if player != null and player.has_method("apply_ring_settings"):
		player.apply_ring_settings(ring_radius, traversal_time_seconds, player_speed, gravity_strength)


func _clear_children(root: Node3D) -> void:
	for child in root.get_children():
		root.remove_child(child)
		child.queue_free()
