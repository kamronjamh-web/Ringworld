extends SceneTree
const OUTPUT := "res://.godot/terrain-generation-tests/native-adapter-report.json"
const SURFACE := preload("res://scripts/Terrain/Surface/RingTerrain3DSurface.cs")
const SIDE := 384
const CIRCUMFERENCE := 31415936
const MINIMUM_CHECKS := 70
var checks := 0
var failures := PackedStringArray()
var completed := false
var finished := false
var timings: Dictionary = {}
func _init() -> void:
	call_deferred("_run")
	create_timer(60.0).timeout.connect(_watchdog)
func _run() -> void:
	var direct = SURFACE.new(); var imported = SURFACE.new(); root.add_child(direct); root.add_child(imported)
	var f := _fixture(100.25)
	_expect(direct.CommitPreparedWindow(f.h, f.i, 2048, 1000, f.frame, 1, 1, true), "direct initial")
	_expect(imported.CommitPreparedWindow(f.h, f.i, 2048, 1000, f.frame, 1, 1, false), "import initial")
	timings.direct_initial = direct.GetPreparedDiagnostics()
	timings.import_initial = imported.GetPreparedDiagnostics()
	await process_frame; await physics_frame
	if not _compare(direct, imported, f): return
	if not _paint(direct, true): return
	if not _paint(imported, false): return
	if not _ranges(direct, true): return
	if not _ranges(imported, false): return
	if not _invalid_ownership(direct, f): return
	imported.free()
	await process_frame
	if not await _orientation_collision(direct): return
	completed = true
	direct.free()
	_finish()
func _fixture(bias: float) -> Dictionary:
	var h := PackedFloat32Array(); h.resize(SIDE * SIDE)
	for z in SIDE:
		for x in SIDE: h[z * SIDE + x] = clampf(-7999.75 + float((x * 37 + z * 71) % 16000) + bias, -7999.875, 7999.875)
	h[0] = -7999.875
	h[h.size() - 1] = 7999.875
	var frame: float = h[128 * SIDE + 128]
	return {"h":h,"frame":frame,"i":_pack(h,frame)}
func _pack(h: PackedFloat32Array, frame: float) -> Array[Image]:
	var result: Array[Image] = []
	for rz in 3:
		for rx in 3:
			var p := PackedFloat32Array(); p.resize(128*128)
			for y in 128:
				for x in 128: p[y*128+x]=h[(rz*128+y)*SIDE+rx*128+x]-frame
			result.append(Image.create_from_data(128,128,false,Image.FORMAT_RF,p.to_byte_array()))
	return result
func _compare(a,b,f:Dictionary)->bool:
	for p in [Vector2i(-256,-256),Vector2i(-2,-2),Vector2i(0,0),Vector2i(254,254),Vector2i(256,256),Vector2i(510,510)]:
		var expected:float=f.h[(p.y/2+128)*SIDE+p.x/2+128]-f.frame
		_expect(_same(a.GetNativeHeight(Vector3(p.x,0,p.y)),expected),"direct native "+str(p))
		_expect(_same(b.GetNativeHeight(Vector3(p.x,0,p.y)),expected),"import native "+str(p))
		_expect(_same(a.GetNativeHeight(Vector3(p.x,0,p.y)),b.GetNativeHeight(Vector3(p.x,0,p.y))),"native parity "+str(p))
	return true
func _paint(s,direct:bool)->bool:
	var data=s.TerrainNativeData; var r=data.call("get_region",Vector2i(0,0)); var control:Image=r.call("get_control_map"); var color:Image=r.call("get_color_map")
	control.set_pixel(17,23,Color(0.1234567,0,0,1)); color.set_pixel(17,23,Color8(17,129,251,73))
	color.generate_mipmaps()
	var cb=control.get_data(); var colb=color.get_data(); var rid=r.get_instance_id(); var hid=(r.call("get_height_map") as Image).get_instance_id()
	var n:=_fixture(200.5); _expect(s.CommitPreparedWindow(n.h,n.i,2048,1000,n.frame,1,2,direct),"paint update")
	timings["direct_update" if direct else "import_update"] = s.GetPreparedDiagnostics()
	var after=data.call("get_region",Vector2i(0,0)); _expect((after.call("get_control_map") as Image).get_data()==cb and (after.call("get_color_map") as Image).get_data()==colb,"paint bytes survive")
	if direct:
		_expect(after.get_instance_id()==rid,"direct region identity")
		_expect((after.call("get_height_map") as Image).get_instance_id()==hid,"direct image identity")
		_expect(not after.call("is_edited") and after.call("is_modified"),"direct flags")
	return true
func _ranges(s,direct:bool)->bool:
	var wide:=_fixture(350.0); _expect(s.CommitPreparedWindow(wide.h,wide.i,2048,1000,wide.frame,1,3,direct),"wide range")
	var data=s.TerrainNativeData; var old:Vector2=data.call("get_height_range"); var rr:Vector2=data.call("get_region",Vector2i(0,0)).call("get_height_range")
	_expect(rr.x<=rr.y and old.x<=0 and old.y>=0,"native ranges")
	var aggregate := Vector2.ZERO
	for z in 3:
		for x in 3:
			var expected := Vector2(INF, -INF)
			var pixels: PackedFloat32Array = (wide.i[z * 3 + x] as Image).get_data().to_float32_array()
			for height in pixels:
				expected.x = minf(expected.x, height)
				expected.y = maxf(expected.y, height)
			var actual: Vector2 = data.call("get_region", Vector2i(x - 1, z - 1)).call("get_height_range")
			_expect(actual.is_equal_approx(expected), "region bounds match RF samples")
			aggregate.x = minf(aggregate.x, expected.x)
			aggregate.y = maxf(aggregate.y, expected.y)
	_expect(old.is_equal_approx(aggregate), "aggregate matches region bounds including zero")
	var flat:=PackedFloat32Array();flat.resize(SIDE*SIDE);flat.fill(123.375);_expect(s.CommitPreparedWindow(flat,_pack(flat,123.375),4096,1000,123.375,1,4,direct),"small range")
	var shrink:Vector2=data.call("get_height_range");_expect(shrink.x<=0 and shrink.y>=0 and shrink.y-shrink.x<old.y-old.x,"bounds shrink")
	_expect(shrink == Vector2.ZERO, "flat region shrinks aggregate exactly to zero")
	if DisplayServer.get_name() != "headless":
		_expect(_same(s.TerrainNode.call("get_material").call("get_shader_param","ring_anchor_height"),123.375),"material frame update")
	return true
func _invalid_ownership(s,f:Dictionary)->bool:
	var before:Dictionary=s.GetPreparedDiagnostics().duplicate();var old=s.SampleCommittedHeight(4096,1000);var n:=_fixture(400.0)
	_expect(not s.CommitPreparedWindow(n.h,n.i,4096,1000,n.frame,1,4,true),"stale rejected")
	var nan: PackedFloat32Array = f.h.duplicate(); nan[0] = NAN; _expect(not s.CommitPreparedWindow(nan,f.i,4096,1000,f.frame,1,5,true),"NaN rejected")
	var short:Array[Image]=[];short.append(f.i[0]);_expect(not s.CommitPreparedWindow(f.h,short,4096,1000,f.frame,1,5,true),"count rejected")
	var swap:Array[Image]=f.i.duplicate();var t=swap[0];swap[0]=swap[1];swap[1]=t;_expect(not s.CommitPreparedWindow(f.h,swap,4096,1000,f.frame,1,5,true),"ordering rejected")
	_expect(s.GetPreparedDiagnostics().revision==before.revision and _same(s.SampleCommittedHeight(4096,1000),old),"rejection retains ready")
	var own:=_fixture(500.0);_expect(s.CommitPreparedWindow(own.h,own.i,4096,1000,own.frame,1,5,true),"ownership commit");var value=s.SampleCommittedHeight(4096,1000);own.h[128*SIDE+128]=-7000;own.i[4].set_pixel(0,0,Color(7000,0,0,1));_expect(_same(s.SampleCommittedHeight(4096,1000),value),"input mutation isolated")
	_expect(_same(s.GetNativeHeight(Vector3.ZERO), 0.0), "input image mutation cannot change native height")
	return true
func _orientation_collision(s)->bool:
	var heights := PackedFloat32Array()
	heights.resize(SIDE * SIDE)
	for z in SIDE:
		for x in SIDE:
			heights[z * SIDE + x] = 5500.25 + (x - 128) * 0.03125 + (z - 128) * 0.015625
	var around := float(CIRCUMFERENCE - 128)
	_expect(s.CommitPreparedWindow(heights, _pack(heights, 5500.25), int(around), 317000, 5500.25, -1, 6, true), "reversed frame crossing periodic seam")
	_expect(_same(s.SampleCommittedHeight(around - CIRCUMFERENCE, 317000), 5500.25), "wrapped query")
	var local: Vector3 = s.MapCanonicalPosition(around + 1.25, 316998.75, 5500.75)
	var canonical: PackedFloat64Array = s.GetCanonicalPosition(local)
	_expect(absf(canonical[0] - (around + 1.25)) < 0.001 and absf(canonical[1] - 316998.75) < 0.001, "fractional mapping")
	_expect(_same(s.SampleCommittedHeight(around + .75, 316999.25), 5500.25 + .375 * .03125 + .375 * .015625), "triangle interpolation")
	await physics_frame
	await process_frame
	for offset in [Vector2(0, 0), Vector2(1.25, -1.25), Vector2(160, 48), Vector2(-160, -48)]:
		var a: float = around + offset.x
		var z: float = 317000 + offset.y
		var height: float = s.SampleCommittedHeight(a, z)
		var origin: Vector3 = s.MapCanonicalPosition(a, z, height)
		var hit: Dictionary = s.get_world_3d().direct_space_state.intersect_ray(PhysicsRayQueryParameters3D.create(origin + Vector3.UP * 20, origin - Vector3.UP * 20, 1))
		_expect(not hit.is_empty(), "curved collider at " + str(offset))
		if not hit.is_empty():
			var hc: PackedFloat64Array = s.GetCanonicalPosition(hit.position)
			_expect(absf(hc[2] - s.SampleCommittedHeight(hc[0], hc[1])) < .003, "curved ray/query parity")
	var state: Dictionary = s.GetPreparedDiagnostics()
	_expect(state.ready and state.revision == 6 and state.render_revision == 6 and state.query_revision == 6 and state.collision_revision == 6, "all published revisions agree")
	return true
func _same(a: float, b: float) -> bool:
	return is_equal_approx(a, b) and absf(a - b) < .001

func _expect(ok: bool, label: String) -> void:
	checks += 1
	if not ok:
		failures.append(label)

func _watchdog() -> void:
	if not finished:
		failures.append("watchdog")
		_finish()
func _finish() -> void:
	if finished:
		return
	finished = true
	if not completed:
		failures.append("test stages did not complete")
	if checks < MINIMUM_CHECKS:
		failures.append("too few checks %d" % checks)
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path("res://.godot/terrain-generation-tests"))
	var file: FileAccess = FileAccess.open(OUTPUT, FileAccess.WRITE)
	if file != null:
		file.store_string(JSON.stringify({"checks": checks, "completed": completed, "failures": failures, "timings": timings}, "  "))
		file.close()
	else:
		failures.append("could not write test report")
	if completed and failures.is_empty():
		print("TERRAIN_GENERATION_NATIVE_ADAPTER=PASS ", JSON.stringify({"checks": checks}))
		quit(0)
		return
	push_error("TERRAIN_GENERATION_NATIVE_ADAPTER=FAIL " + "; ".join(failures))
	quit(1)
