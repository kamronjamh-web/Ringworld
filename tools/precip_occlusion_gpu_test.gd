extends SceneTree
# Precipitation occlusion map GPU test (docs/storm-revamp-s1-design.md sections 1 and 6, WO2b + WO3 probe).
# Run (windowed Vulkan, not --headless; no other Godot process running):
#   Godot --path . --resolution 960x540 --script res://tools/precip_occlusion_gpu_test.gd [-- --out <dir>]
# Fixture: a synthetic flat-up world (no voxel mesher needed) with an analytic terrain heightfield, a roof, a POI-layer tower, a cave-roof slab and an untagged
# control pillar (all occluders), plus a tree, grass patch and player capsule that the layer policy must exclude. The map node runs in manual-frame mode.
# Prints PRECIP_OCCLUSION_GPU=PASS or =FAIL <reason>; per-check lines start with PRECIP_OCC.

const NODE_SCRIPT := preload("res://scripts/Weather/RingPrecipitationOcclusion.cs")
const PLANE_Y := 50.0
const MAP := 512
var out_dir := ""
var failures: Array[String] = []
var fixture: Node3D
var sun: DirectionalLight3D
var occlusion: Node
var main_camera: Camera3D

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		_finish("FAIL requires Vulkan/Forward+ (not headless)"); return
	call_deferred("_run")

func ground(x: float, z: float) -> float:
	return 1.5 * sin(x / 23.0) + 1.0 * cos(z / 17.0) + 0.005 * x

func check(name: String, ok: bool, detail := "") -> void:
	print("PRECIP_OCC %s %s %s" % [name, "PASS" if ok else "FAIL", detail])
	if not ok: failures.append(name + " " + detail)

func _finish(message: String) -> void:
	var verdict := message
	if verdict == "": verdict = "PASS" if failures.is_empty() else "FAIL " + " | ".join(failures)
	if verdict.begins_with("PASS"): print("PRECIP_OCCLUSION_GPU=PASS")
	else: push_error("PRECIP_OCCLUSION_GPU=" + verdict)
	quit(0 if verdict.begins_with("PASS") else 1)

func add_box(parent: Node, center: Vector3, size: Vector3, layers := 1, color := Color(.6, .6, .6)) -> MeshInstance3D:
	var mesh := MeshInstance3D.new(); var box := BoxMesh.new(); box.size = size; mesh.mesh = box
	var material := StandardMaterial3D.new(); material.albedo_color = color; mesh.material_override = material
	mesh.position = center; mesh.layers = layers; parent.add_child(mesh); return mesh

func build_ground(parent: Node) -> void:
	var half := 80; var vertices := PackedVector3Array(); var normals := PackedVector3Array(); var indices := PackedInt32Array()
	var n := half * 2 + 1
	for iz in n:
		for ix in n:
			var x := float(ix - half); var z := float(iz - half)
			vertices.append(Vector3(x, ground(x, z), z))
			var dx := 1.5 / 23.0 * cos(x / 23.0) + 0.005; var dz := -1.0 / 17.0 * sin(z / 17.0)
			normals.append(Vector3(-dx, 1, -dz).normalized())
	for iz in n - 1:
		for ix in n - 1:
			var a := iz * n + ix; var b := a + 1; var c := a + n; var d := c + 1
			indices.append_array([a, b, c, b, d, c])
	var arrays := []; arrays.resize(Mesh.ARRAY_MAX)
	arrays[Mesh.ARRAY_VERTEX] = vertices; arrays[Mesh.ARRAY_NORMAL] = normals; arrays[Mesh.ARRAY_INDEX] = indices
	var mesh := ArrayMesh.new(); mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
	var instance := MeshInstance3D.new(); instance.mesh = mesh; instance.name = "Terrain"
	var material := StandardMaterial3D.new(); material.albedo_color = Color(.35, .5, .25); instance.material_override = material
	parent.add_child(instance)

func add_multimesh(parent: Node, mesh: Mesh, positions: Array, name: String) -> MultiMeshInstance3D:
	var multi := MultiMesh.new(); multi.transform_format = MultiMesh.TRANSFORM_3D; multi.mesh = mesh; multi.instance_count = positions.size()
	for i in positions.size(): multi.set_instance_transform(i, Transform3D(Basis(), positions[i]))
	var instance := MultiMeshInstance3D.new(); instance.multimesh = multi; instance.name = name; parent.add_child(instance)
	return instance

func frames(count: int) -> void:
	for i in count: await process_frame

func read_map() -> PackedFloat32Array:
	var rid: RID = occlusion.get("PublishedTextureRid")
	var holder: Array = []
	var rd := RenderingServer.get_rendering_device()
	RenderingServer.call_on_render_thread(func(): holder.append(rd.texture_get_data(rid, 0)))
	var guard := 0
	while holder.is_empty() and guard < 60:
		await process_frame; guard += 1
	if holder.is_empty(): return PackedFloat32Array()
	return (holder[0] as PackedByteArray).to_float32_array()

func texel_world(tx: int, ty: int) -> Vector2:
	# Map camera: image right = +X, image up = -Z (basis X=(1,0,0), Y=(0,0,-1), Z=(0,1,0)).
	return Vector2(((tx + 0.5) / MAP - 0.5) * 128.0, ((ty + 0.5) / MAP - 0.5) * 128.0)

func map_height(map: PackedFloat32Array, tx: int, ty: int) -> float:
	var d := map[ty * MAP + tx]
	return -1000.0 if d <= 0.0 else PLANE_Y - d

func wait_for_render(previous: int, limit := 120) -> bool:
	for i in limit:
		await process_frame
		if int(occlusion.get("RenderCount")) > previous: return true
	return false

func main_image() -> Image:
	await frames(6)
	return root.get_texture().get_image()

func image_diff(a: Image, b: Image) -> Dictionary:
	var differing := 0; var max_delta := 0.0
	for y in a.get_height():
		for x in a.get_width():
			var ca := a.get_pixel(x, y); var cb := b.get_pixel(x, y)
			var delta := maxf(absf(ca.r - cb.r), maxf(absf(ca.g - cb.g), absf(ca.b - cb.b)))
			if delta > 0.0: differing += 1
			max_delta = maxf(max_delta, delta)
	return {"differing": differing, "max": max_delta}

func _run() -> void:
	create_timer(240.0).timeout.connect(func(): _finish("FAIL watchdog expired"))
	var args := OS.get_cmdline_user_args()
	out_dir = ProjectSettings.globalize_path("res://.godot/storm_s1/gpu_test")
	for i in args.size():
		if args[i] == "--out" and i + 1 < args.size(): out_dir = args[i + 1]
	DirAccess.make_dir_recursive_absolute(out_dir)
	var device := RenderingServer.get_video_adapter_name()
	print("PRECIP_OCC device=", device)
	# ---- fixture
	var environment := Environment.new(); environment.background_mode = Environment.BG_COLOR; environment.background_color = Color(.55, .65, .8)
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR; environment.ambient_light_color = Color(.4, .42, .5); environment.ambient_light_energy = .6
	var world_environment := WorldEnvironment.new(); world_environment.environment = environment; root.add_child(world_environment)
	fixture = Node3D.new(); fixture.name = "Fixture"; root.add_child(fixture)
	sun = DirectionalLight3D.new(); sun.shadow_enabled = true; sun.light_energy = 1.4; sun.rotation_degrees = Vector3(-55, 35, 0); sun.directional_shadow_max_distance = 200; fixture.add_child(sun)
	var omni := OmniLight3D.new(); omni.shadow_enabled = true; omni.position = Vector3(10, 12, 30); omni.omni_range = 40; omni.light_energy = 2.0; fixture.add_child(omni)
	build_ground(fixture)
	var roof := add_box(fixture, Vector3(20, 6.0, -10), Vector3(10, 0.3, 10))
	var poi := add_box(fixture, Vector3(-25, 7.5, 15), Vector3(12, 15, 12), 2, Color(.5, .55, .7))
	var slab := add_box(fixture, Vector3(-20, 11.0, -30), Vector3(20, 2, 20), 1, Color(.45, .4, .35))
	var pillar := add_box(fixture, Vector3(-50, 5.0, 50), Vector3(3, 10, 3), 1, Color(.8, .3, .3))
	# Excluded-by-policy objects: trees added BEFORE the producer (initial tree walk), grass and player AFTER (NodeAdded path).
	var tree_owner := Node3D.new(); tree_owner.name = "Trees"; tree_owner.add_to_group("no_precipitation_occlusion"); fixture.add_child(tree_owner)
	var trunk := CylinderMesh.new(); trunk.height = 20.0; trunk.top_radius = 1.5; trunk.bottom_radius = 1.5
	add_multimesh(tree_owner, trunk, [Vector3(35, 10, 30), Vector3(40, 10, 34), Vector3(36, 10, 38)], "TreeTrunks")
	var grass_owner := Node3D.new(); grass_owner.name = "GrassPatch"; grass_owner.add_to_group("no_precipitation_occlusion"); fixture.add_child(grass_owner)
	var blade := BoxMesh.new(); blade.size = Vector3(0.3, 0.6, 0.3)
	var blades := []
	for ix in 20:
		for iz in 20: blades.append(Vector3(-60 + ix, ground(-60 + ix, -60 + iz) + 0.3, -60 + iz))
	add_multimesh(grass_owner, blade, blades, "GrassBlades")
	var player := Node3D.new(); player.name = "Player"; player.add_to_group("no_precipitation_occlusion"); fixture.add_child(player)
	var capsule := MeshInstance3D.new(); var capsule_mesh := CapsuleMesh.new(); capsule_mesh.height = 1.8; capsule_mesh.radius = 0.4; capsule.mesh = capsule_mesh; capsule.position = Vector3(0, 1.0, 40); player.add_child(capsule)
	main_camera = Camera3D.new(); main_camera.position = Vector3(70, 40, 80); fixture.add_child(main_camera); main_camera.look_at(Vector3(0, 2, 0)); main_camera.current = true
	await frames(8)
	var image_before := await main_image()
	image_before.save_png(out_dir + "/main_before.png")
	# ---- producer in manual frame mode
	occlusion = NODE_SCRIPT.new(); occlusion.name = "PrecipitationOcclusion"; root.add_child(occlusion)
	var late := MeshInstance3D.new(); late.mesh = BoxMesh.new(); late.position = Vector3(5000, 0, 5000); late.add_to_group("no_precipitation_occlusion"); fixture.add_child(late)
	await frames(2)
	check("layer_policy_lights", sun.layers == (1 << 19) and omni.layers == (1 << 19), "sun=%d omni=%d" % [sun.layers, omni.layers])
	check("layer_policy_excluded", (grass_owner.get_child(0) as MultiMeshInstance3D).layers == (1 << 18) and (tree_owner.get_child(0) as MultiMeshInstance3D).layers == (1 << 18) and capsule.layers == (1 << 18),
		"tree=%d grass=%d player=%d" % [(tree_owner.get_child(0) as MultiMeshInstance3D).layers, (grass_owner.get_child(0) as MultiMeshInstance3D).layers, capsule.layers])
	check("layer_policy_late_node", late.layers == (1 << 18), "late=%d" % late.layers)
	check("layer_policy_untouched", roof.layers == 1 and poi.layers == 2 and slab.layers == 1 and pillar.layers == 1, "roof=%d poi=%d" % [roof.layers, poi.layers])
	var map_basis := Basis(Vector3(1, 0, 0), Vector3(0, 0, -1), Vector3(0, 1, 0))
	var map_transform := Transform3D(map_basis, Vector3(0, PLANE_Y, 0))
	occlusion.call("SubmitManualFrame", map_transform, PLANE_Y - 1.7)
	if not await wait_for_render(0):
		_finish("FAIL map never rendered"); return
	await frames(6)
	var map := await read_map()
	check("map_readback", map.size() == MAP * MAP, "size=%d" % map.size())
	if map.size() != MAP * MAP: _finish(""); return
	# ---- heights
	var open_count := 0; var open_bad := 0; var open_max := 0.0
	var boxes := [Rect2(15, -15, 10, 10), Rect2(-31, 9, 12, 12), Rect2(-30, -40, 20, 20), Rect2(-51.5, 48.5, 3, 3)]
	for ty in range(8, MAP - 8, 3):
		for tx in range(8, MAP - 8, 3):
			var w := texel_world(tx, ty)
			var near_box := false
			for r: Rect2 in boxes:
				if r.grow(1.5).has_point(w): near_box = true
			if near_box or absf(w.x) > 78.0 or absf(w.y) > 78.0: continue
			open_count += 1
			var error := absf(map_height(map, tx, ty) - ground(w.x, w.y))
			open_max = maxf(open_max, error)
			if error > 0.3: open_bad += 1
	check("open_matches_terrain", open_count > 4000 and open_bad == 0, "texels=%d bad=%d max_err=%.3f m" % [open_count, open_bad, open_max])
	var expected := {"roof": [Vector2(20, -10), 6.15], "poi": [Vector2(-25, 15), 15.0], "cave_roof": [Vector2(-20, -30), 12.0], "control_pillar": [Vector2(-50, 50), 10.0]}
	for key in expected:
		var centre: Vector2 = expected[key][0]; var want: float = expected[key][1]
		var tx := int((centre.x / 128.0 + 0.5) * MAP); var ty := int((centre.y / 128.0 + 0.5) * MAP)
		var got := map_height(map, tx, ty)
		check("height_" + key, absf(got - want) < 0.1, "got %.3f want %.3f" % [got, want])
	for key in ["tree", "grass", "player"]:
		var centre := Vector2(35, 30) if key == "tree" else (Vector2(-50, -50) if key == "grass" else Vector2(0, 40))
		var tx := int((centre.x / 128.0 + 0.5) * MAP); var ty := int((centre.y / 128.0 + 0.5) * MAP)
		var got := map_height(map, tx, ty); var ground_h := ground(centre.x, centre.y)
		check("absent_" + key, absf(got - ground_h) < 0.3, "map %.3f ground %.3f" % [got, ground_h])
	# ---- shadow draws
	var info: Dictionary = occlusion.call("GetRenderInfoDictionary")
	var main_shadow := RenderingServer.viewport_get_render_info(root.get_viewport_rid(), RenderingServer.VIEWPORT_RENDER_INFO_TYPE_SHADOW, RenderingServer.VIEWPORT_RENDER_INFO_DRAW_CALLS_IN_FRAME)
	check("map_zero_shadow_draws", int(info.shadow_draw_calls) == 0 and int(info.shadow_objects) == 0, "map shadow draws=%d objects=%d (main viewport shadow draws=%d)" % [int(info.shadow_draw_calls), int(info.shadow_objects), main_shadow])
	check("main_viewport_shadow_control", main_shadow > 0, "main shadow draws=%d" % main_shadow)
	print("PRECIP_OCC map_render_info objects=%d primitives=%d draw_calls=%d" % [int(info.objects), int(info.primitives), int(info.draw_calls)])
	# Negative control: put the sun back on layer 1 and the shadow map must render for the map viewport.
	sun.layers = 1; omni.layers = 1
	var before_control := int(occlusion.get("RenderCount")); occlusion.call("RequestRender")
	await wait_for_render(before_control); await frames(3)
	var control: Dictionary = occlusion.call("GetRenderInfoDictionary")
	check("shadow_control_detects_leak", int(control.shadow_draw_calls) > 0, "shadow draws with lights on layer 1 = %d" % int(control.shadow_draw_calls))
	sun.layers = 1 << 19; omni.layers = 1 << 19
	before_control = int(occlusion.get("RenderCount")); occlusion.call("RequestRender")
	await wait_for_render(before_control); await frames(3)
	var restored: Dictionary = occlusion.call("GetRenderInfoDictionary")
	check("shadow_draws_zero_again", int(restored.shadow_draw_calls) == 0, "draws=%d" % int(restored.shadow_draw_calls))
	# ---- main view unchanged by the producer (lights moved to their own layer)
	var image_after := await main_image()
	image_after.save_png(out_dir + "/main_after.png")
	var diff := image_diff(image_before, image_after)
	check("main_view_unchanged", int(diff.differing) == 0, "differing pixels=%d max channel delta=%.5f" % [int(diff.differing), float(diff.max)])
	# ---- shader include: top-down exposure view at y = 5 (under the roof, POI and cave roof; above the ground)
	await exposure_check()
	# ---- probe
	await probe_check()
	# ---- rebase: transform republish only, no re-render
	await rebase_check()
	# ---- per-update cost
	await cost_check()
	# ---- debug overlay screenshot
	occlusion.call("SetDebugOverlay", true)
	occlusion.call("SetProbeListener", Vector3(20, ground(20, -10) + 1.7, -10) + shifted_by)
	await frames(40)
	var shot := root.get_texture().get_image(); shot.save_png(out_dir + "/overlay.png")
	print("PRECIP_OCC overlay_screenshot=", out_dir + "/overlay.png")
	occlusion.call("SetDebugOverlay", false)
	_finish("")

func exposure_check() -> void:
	var diag := SubViewport.new(); diag.size = Vector2i(MAP, MAP); diag.render_target_update_mode = SubViewport.UPDATE_ALWAYS; diag.msaa_3d = Viewport.MSAA_DISABLED
	var camera := Camera3D.new(); camera.projection = Camera3D.PROJECTION_ORTHOGONAL; camera.size = 128.0; camera.near = 0.05; camera.far = 200.0
	diag.add_child(camera); root.add_child(diag)
	camera.global_transform = Transform3D(Basis(Vector3(1, 0, 0), Vector3(0, 0, -1), Vector3(0, 1, 0)), Vector3(0, 100, 0))
	var shader := Shader.new()
	shader.code = "shader_type spatial;\nrender_mode unshaded, depth_test_disabled, cull_disabled;\n#include \"res://materials/ring_precip_occlusion.gdshaderinc\"\nvoid fragment(){ vec3 world=(INV_VIEW_MATRIX*vec4(VERTEX,1.0)).xyz; ALBEDO=vec3(ring_precip_exposure(world)); }"
	var plane := MeshInstance3D.new(); var quad := QuadMesh.new(); quad.size = Vector2(128, 128); quad.orientation = PlaneMesh.FACE_Y; plane.mesh = quad
	var material := ShaderMaterial.new(); material.shader = shader; material.render_priority = 127; plane.material_override = material
	plane.layers = 1 << 18; plane.position = Vector3(0, 5.0, 0); fixture.add_child(plane)
	await frames(8)
	var image := diag.get_texture().get_image(); image.save_png(out_dir + "/exposure_topdown.png")
	var result := _score_exposure(image)
	check("shader_exposure_matches", float(result.ratio) > 0.995, "agree=%.4f of %d texels (sheltered expected %d, got %d)" % [float(result.ratio), int(result.total), int(result.expected_dark), int(result.got_dark)])
	_exposure_image = image; _exposure_diag = diag; _exposure_camera = camera; _exposure_plane = plane

var shifted_by := Vector3.ZERO
var _exposure_image: Image
var _exposure_diag: SubViewport
var _exposure_camera: Camera3D
var _exposure_plane: MeshInstance3D

func _score_exposure(image: Image) -> Dictionary:
	var sheltered := [Rect2(15, -15, 10, 10), Rect2(-31, 9, 12, 12), Rect2(-30, -40, 20, 20), Rect2(-51.5, 48.5, 3, 3)]
	var agree := 0; var total := 0; var expected_dark := 0; var got_dark := 0
	for ty in range(0, MAP, 2):
		for tx in range(0, MAP, 2):
			var w := texel_world(tx, ty)
			var near_edge := false; var inside := false
			for r: Rect2 in sheltered:
				if r.has_point(w): inside = true
				elif r.grow(1.0).has_point(w): near_edge = true
				if r.grow(-1.0).has_point(w): inside = true
			if near_edge: continue
			var dark := image.get_pixel(tx, ty).r < 0.5
			total += 1
			var want_dark := false
			for r: Rect2 in sheltered:
				if r.has_point(w): want_dark = true
			if want_dark: expected_dark += 1
			if dark: got_dark += 1
			if dark == want_dark: agree += 1
	return {"ratio": float(agree) / maxf(1.0, float(total)), "total": total, "expected_dark": expected_dark, "got_dark": got_dark}

func probe_check() -> void:
	var cases := [
		{"name": "under_roof", "at": Vector2(20, -10), "cover": 6.15, "open_max": 0.05},
		{"name": "under_cave_roof", "at": Vector2(-20, -30), "cover": 12.0, "open_max": 0.05},
		{"name": "in_open", "at": Vector2(55, -40), "cover": 0.0, "open_min": 0.97},
		{"name": "roof_edge", "at": Vector2(15.0, -10), "open_min": 0.25, "open_max": 0.75},
	]
	for c in cases:
		var at: Vector2 = c.at; var eye := ground(at.x, at.y) + 1.7
		occlusion.call("SetProbeListener", Vector3(at.x, eye, at.y))
		var fresh := false
		for i in 90:
			await process_frame
			if bool(occlusion.get("IsFresh")) and i > 8: fresh = true; break
		var height: float = occlusion.get("ListenerCoverHeightMeters"); var open: float = occlusion.get("ListenerOpenFraction"); var depth: float = occlusion.get("ListenerCoverDepthMeters")
		var ok := fresh
		if c.has("cover"): ok = ok and absf(height - (maxf(0.0, float(c.cover) - eye) if float(c.cover) > 0.0 else 0.0)) < 0.3
		if c.has("open_max"): ok = ok and open <= float(c.open_max)
		if c.has("open_min"): ok = ok and open >= float(c.open_min)
		check("probe_" + String(c.name), ok, "fresh=%s coverHeight=%.2f open=%.2f coverDepth=%.2f (eye %.2f)" % [fresh, height, open, depth, eye])
	# Outside the map the probe must not claim freshness.
	occlusion.call("SetProbeListener", Vector3(300, 3, 0))
	await frames(25)
	check("probe_outside_map_not_fresh", not bool(occlusion.get("IsFresh")), "")
	occlusion.call("ClearProbeListener")

func rebase_check() -> void:
	var requests := int(occlusion.get("RenderRequests")); var renders := int(occlusion.get("RenderCount"))
	var origin_before: Vector3 = (occlusion.get("PublishedTransform") as Transform3D).origin
	var delta := Vector3(1000.0, -40.0, 520.0)
	shifted_by = delta
	fixture.position += delta
	_exposure_camera.global_position += delta
	occlusion.call("ShiftManualFrame", delta)
	await frames(45)
	var origin_after: Vector3 = (occlusion.get("PublishedTransform") as Transform3D).origin
	check("rebase_no_rerender", int(occlusion.get("RenderRequests")) == requests and int(occlusion.get("RenderCount")) == renders, "requests %d->%d renders %d->%d" % [requests, int(occlusion.get("RenderRequests")), renders, int(occlusion.get("RenderCount"))])
	check("rebase_globals_follow", absf((origin_after.x - origin_before.x) - delta.x) < 0.01 and absf((origin_after.z - origin_before.z) - delta.z) < 0.01 and absf(origin_after.y - origin_before.y - delta.y) < 0.01, "origin %s -> %s" % [origin_before, origin_after])
	var image := _exposure_diag.get_texture().get_image(); image.save_png(out_dir + "/exposure_after_rebase.png")
	var result := _score_exposure(image)
	check("rebase_exposure_still_matches", float(result.ratio) > 0.995, "agree=%.4f" % float(result.ratio))

var _ts_scan: Array = []

func scan_timestamps(map_id: int) -> void:
	var rd := RenderingServer.get_rendering_device()
	RenderingServer.call_on_render_thread(func():
		var begin := -1.0; var end := -1.0; var names: Array = []
		for i in rd.get_captured_timestamps_count():
			var stamp_name := rd.get_captured_timestamp_name(i)
			if stamp_name.find(str(map_id)) < 0: continue
			names.append(stamp_name)
			var t := float(rd.get_captured_timestamp_gpu_time(i)) / 1000000.0
			if stamp_name.begins_with("vp_begin"): begin = t
			elif stamp_name.begins_with("vp_end"): end = t
		_ts_scan.append({"begin": begin, "end": end, "names": names}))

func cost_check() -> void:
	_exposure_diag.render_target_update_mode = SubViewport.UPDATE_DISABLED
	_exposure_plane.visible = false
	var map_rid: RID = (occlusion.get("MapViewport") as SubViewport).get_viewport_rid()
	RenderingServer.viewport_set_measure_render_time(map_rid, true)
	await frames(4)
	var samples: Array[float] = []; var cpu: Array[float] = []; var logged := false
	for i in 24:
		var before := int(occlusion.get("RenderCount")); occlusion.call("RequestRender")
		if not await wait_for_render(before): break
		var best_gpu := 0.0; var best_cpu := 0.0
		for f in 8:
			_ts_scan.clear(); scan_timestamps(map_rid.get_id())
			await process_frame
			for entry in _ts_scan:
				if not logged and not (entry.names as Array).is_empty(): print("PRECIP_OCC timestamp_names ", entry.names); logged = true
				if float(entry.begin) >= 0.0 and float(entry.end) > float(entry.begin): best_gpu = maxf(best_gpu, float(entry.end) - float(entry.begin))
			best_gpu = maxf(best_gpu, RenderingServer.viewport_get_measured_render_time_gpu(map_rid))
			best_cpu = maxf(best_cpu, RenderingServer.viewport_get_measured_render_time_cpu(map_rid))
		if best_gpu > 0.0: samples.append(best_gpu)
		if best_cpu > 0.0: cpu.append(best_cpu)
	samples.sort(); cpu.sort()
	var median := samples[samples.size() / 2] if samples.size() > 0 else -1.0
	var p95 := samples[mini(samples.size() - 1, int(samples.size() * 0.95))] if samples.size() > 0 else -1.0
	var info: Dictionary = occlusion.call("GetRenderInfoDictionary")
	print("PRECIP_OCC cost_per_update synthetic_fixture gpu_ms median=%.3f p95=%.3f samples=%d cpu_ms median=%.3f primitives=%d draw_calls=%d" % [median, p95, samples.size(), cpu[cpu.size() / 2] if cpu.size() > 0 else -1.0, int(info.primitives), int(info.draw_calls)])
	check("cost_measured", samples.size() >= 8 and median >= 0.0 and median < 0.5, "median %.3f ms (budget 0.5 ms on the 4070; synthetic fixture, real-world cost reported separately)" % median)
