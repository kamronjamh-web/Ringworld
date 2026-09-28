extends SceneTree

const SURFACE := preload("res://scripts/Terrain/Surface/RingTerrain3DSurface.cs")
const GEOMETRY := preload("res://scripts/Terrain/Surface/RingTerrain3DCollisionGeometry.cs")
const SIDE := 384
const TILE := 128

var checks := 0
var failures: PackedStringArray = []

func _init() -> void:
	_run.call_deferred()
	create_timer(30.0).timeout.connect(func(): push_error("native gameplay test timed out"); quit(1))

func _run() -> void:
	var surface: Node3D = SURFACE.new()
	root.add_child(surface)
	var world_a := Transform3D(Basis(Vector3.UP, .1), Vector3(20, 30, 40))
	var world_b := Transform3D(Basis(Vector3.UP, -.15), Vector3(276, 50, 40))
	var first := _fixture(-8100.0, Color(.1, .2, .3, 1.0))
	surface.call("SetSurfaceAppearance", .81, .29, .63)
	surface.call("SetSurfaceWetness", .42)
	_expect(_begin(surface, first, 0, 192, -8100.0, 1, -8192.0, 8000.0), "begin min-bound gameplay window")
	surface.call("SetGameplayWorldFrames", Transform3D.IDENTITY, world_a)
	await _prepare(surface)
	await physics_frame
	_expect(surface.call("ActivateStagedWindow", 1), "activate first prepared window")
	_expect(_appearance_or_renderer_skip(surface, .42, .81, .29, .63, false), "first active owner inherits wetness appearance")
	surface.call("SetGameplayWorldFrames", world_a, world_a)
	await physics_frame
	_expect(_ray_matches(world_a), "first transformed collider supports its anchor")
	_expect(absf(float(surface.call("SampleCommittedHeight", 0, 192)) + 8100.0) < .001, "legacy -8100 interior height retained")
	var native: Object = surface.get("TerrainNativeData")
	_expect(native != null and native.call("get_regions_active").size() == 9, "native has nine regions")
	var second := _fixture(-8099.0, Color(.8, .1, .4, 1.0))
	surface.call("SetSurfaceAppearance", .77, .25, .71)
	surface.call("SetSurfaceWetness", .58)
	_expect(_begin(surface, second, 256, 192, -8099.0, 2, -8192.0, 8000.0), "stage translated second window")
	_expect(_appearance_or_renderer_skip(surface, .58, .77, .25, .71, false), "active owner retains updated wetness appearance")
	surface.call("SetGameplayWorldFrames", world_a, world_b)
	_expect(_ray_matches(world_a), "old collider remains supported while replacement begins")
	_expect(absf(float(surface.call("SampleCommittedHeight", 0, 192)) + 8100.0) < .001, "active world remains first while second prepares")
	await _prepare(surface)
	await physics_frame
	_expect(_ray_matches(world_a), "old collider stays at its own transform until activation")
	_expect(surface.call("CanActivateStagedWindow", 256, 192, 0.0), "second staged coverage ready")
	_expect(_appearance_or_renderer_skip(surface, .58, .77, .25, .71, true), "new staged owner inherits wetness appearance")
	await physics_frame
	_expect(surface.call("ActivateStagedWindow", 2), "activate second only after preparation")
	surface.call("SetGameplayWorldFrames", world_b, world_b)
	await physics_frame
	_expect(_ray_matches(world_b), "second collider uses independently staged transform")
	_expect(not _ray_matches(world_a), "retired collider is disabled")
	_expect(absf(float(surface.call("SampleCommittedHeight", 256, 192)) + 8099.0) < .001, "second revision height active")
	var third := _fixture(-8098.5, Color(.2, .4, .1, .37))
	surface.call("SetSurfaceAppearance", .74, .22, .54)
	surface.call("SetSurfaceWetness", .31)
	_expect(_begin(surface, third, 512, 317806, -8098.5, 3, -8192.0, 8000.0), "stage near-width boundary window")
	surface.call("SetGameplayWorldFrames", world_b, world_a)
	await _prepare(surface)
	_expect(surface.call("CanActivateStagedWindow", 512, 317806, 0.0), "near-width staged coverage ready")
	await physics_frame
	_expect(surface.call("ActivateStagedWindow", 3), "activate reused owner with new color")
	_expect(_appearance_or_renderer_skip(surface, .31, .74, .22, .54, false), "reused owner retains wetness appearance")
	surface.call("SetGameplayWorldFrames", world_a, world_a)
	await physics_frame
	_expect(_ray_matches(world_a), "reused collider supports its transformed anchor")
	native = surface.get("TerrainNativeData")
	var region: Object = native.call("get_region", Vector2i.ZERO)
	var color_map: Image = region.call("get_color_map")
	_expect(color_map.get_pixel(0, 0).is_equal_approx(third.biome_images[4].get_pixel(0, 0)), "reused native color image receives new biome bytes")
	_expect(is_equal_approx(color_map.get_pixel(0, 0).a, third.biome_images[4].get_pixel(0, 0).a), "biome alpha remains alpine weight rather than wetness")
	var diag: Dictionary = surface.call("GetPreparedDiagnostics")
	_expect(int(diag.get("active_resident_count", 0)) + int(diag.get("idle_resident_count", 0)) <= 2, "resident native owners bounded")
	surface.queue_free()
	await process_frame
	print("TERRAIN3D_GAMEPLAY_NATIVE=%s checks=%d" % ["PASS" if failures.is_empty() else "FAIL", checks])
	quit(0 if failures.is_empty() else 1)

func _fixture(height: float, color: Color) -> Dictionary:
	var heights := PackedFloat32Array(); heights.resize(SIDE * SIDE); heights.fill(height)
	var height_images: Array[Image] = []; var biome_images: Array[Image] = []
	for ignored in 9:
		var h := PackedByteArray(); h.resize(TILE * TILE * 4)
		# The common frame anchor equals this flat surface height.
		for i in TILE * TILE: h.encode_float(i * 4, 0.0)
		height_images.append(Image.create_from_data(TILE, TILE, false, Image.FORMAT_RF, h))
		var b := Image.create_empty(TILE, TILE, false, Image.FORMAT_RGBA8); b.fill(color); biome_images.append(b)
	return {"heights": heights, "height_images": height_images, "biome_images": biome_images}

func _begin(surface: Node, data: Dictionary, around: int, across: int, height: float, revision: int, minimum: float, maximum: float) -> bool:
	var geometry := GEOMETRY.new()
	if not geometry.call("PrepareGameplay", data.heights, around, across, height, -1, minimum, maximum, 32): geometry.free(); return false
	return surface.call("BeginGameplayPreparedWindow", data.heights, data.height_images, data.biome_images, geometry, around, across, height, -1, revision, minimum, maximum)

func _prepare(surface: Node) -> void:
	for ignored in 240:
		var state: Dictionary = surface.call("AdvanceBudgetedPreparation", .5, .5)
		if not bool(state.get("ok", false)): _expect(false, str(state)); return
		if bool(state.get("ready", false)): return
		await process_frame
	_expect(false, "native gameplay preparation did not become ready")

func _ray_matches(world: Transform3D) -> bool:
	var up := world.basis.y.normalized()
	var query := PhysicsRayQueryParameters3D.create(world.origin + up * 3, world.origin - up * 6, 1)
	var hit := root.get_world_3d().direct_space_state.intersect_ray(query)
	return not hit.is_empty() and (hit.position as Vector3).distance_to(world.origin) < .03

func _appearance_matches(surface: Node, wetness: float, dry: float, wet: float, strength: float) -> bool:
	var terrain: Object = surface.get("TerrainNode")
	return _material_appearance_matches(terrain.call("get_material") if terrain != null else null, wetness, dry, wet, strength, "active")

func _appearance_or_renderer_skip(surface: Node, wetness: float, dry: float, wet: float, strength: float, staged: bool) -> bool:
	if DisplayServer.get_name() == "headless":
		print("SKIP renderer-only Terrain3D wetness uniform assertion (headless): ", "staged" if staged else "active")
		return true
	return _staged_appearance_matches(surface, wetness, dry, wet, strength) if staged else _appearance_matches(surface, wetness, dry, wet, strength)

func _staged_appearance_matches(surface: Node, wetness: float, dry: float, wet: float, strength: float) -> bool:
	return _material_appearance_matches(surface.call("GetStagedTerrainMaterialForDiagnostics"), wetness, dry, wet, strength, "staged")

func _material_appearance_matches(material: Variant, wetness: float, dry: float, wet: float, strength: float, owner: String) -> bool:
	if material == null:
		push_error("TERRAIN3D_GAMEPLAY_NATIVE: %s material unavailable" % owner)
		return false
	var values := []
	for parameter in ["surface_wetness", "surface_dry_roughness", "surface_wet_roughness", "surface_wetness_strength"]:
		var value: Variant = material.call("get_shader_param", parameter)
		if typeof(value) != TYPE_FLOAT:
			push_error("TERRAIN3D_GAMEPLAY_NATIVE: %s %s unavailable (type %s)" % [owner, parameter, typeof(value)])
			return false
		values.append(value)
	return is_equal_approx(values[0], wetness) and is_equal_approx(values[1], dry) and is_equal_approx(values[2], wet) and is_equal_approx(values[3], strength)

func _expect(value: bool, label: String) -> void:
	checks += 1
	if not value: failures.append(label); push_error("TERRAIN3D_GAMEPLAY_NATIVE: " + label)
