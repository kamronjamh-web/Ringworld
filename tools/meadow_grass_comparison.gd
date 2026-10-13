extends SceneTree

# Rendered fixture: run without --headless, with the .NET Godot executable.
# godot --path . --resolution 1280x720 --script tools/meadow_grass_comparison.gd
const OUT := "res://artifacts/meadow-grass"
const WARMUP := 40
const SAMPLES := 40
var fixture
var camera: Camera3D
var report := {"fixture":"bounded production grass layer", "density":24, "reduced_near_geometry":true, "near_triangles_per_source":16, "resolution":"1280x720", "warmup_frames":WARMUP, "sample_frames":SAMPLES, "cases":[], "limits":["GPU timing is whole viewport, not isolated grass pass", "Ground uses production biome texture averages and coverage include; no full terrain renderer", "legacy_current uses configured .42-.92m range, with macro variation clamped by current runtime", "Camera positions refresh presentation only; streaming roots stay fixed", "GPU timing requires a rendered window and RenderingDevice"]}

func _initialize() -> void:
	call_deferred("run")

func run() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("MEADOW_COMPARISON requires rendered RenderingDevice")
		quit(1)
		return
	root.size = Vector2i(1280,720)
	DisplayServer.window_set_size(Vector2i(1280,720))
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	Engine.max_fps = 0
	root.msaa_3d = Viewport.MSAA_2X
	var stage := Node3D.new()
	root.add_child(stage)
	camera = Camera3D.new()
	camera.current = true
	camera.far = 180
	stage.add_child(camera)
	var sun := DirectionalLight3D.new()
	sun.rotation_degrees = Vector3(-42,-35,0)
	sun.light_color = Color(1,.86,.65)
	sun.light_energy = 1.6
	sun.shadow_enabled = true
	sun.directional_shadow_max_distance = 90
	stage.add_child(sun)
	var world := WorldEnvironment.new()
	var env := Environment.new()
	env.background_mode = Environment.BG_COLOR
	env.background_color = Color(.67,.77,.86)
	env.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	env.ambient_light_color = Color(.68,.76,.9)
	env.ambient_light_energy = .5
	env.tonemap_mode = Environment.TONE_MAPPER_FILMIC
	env.tonemap_exposure = 1.15
	world.environment = env
	stage.add_child(world)
	fixture = load("res://tools/MeadowGrassComparison.cs").new()
	stage.add_child(fixture)
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUT))
	RenderingServer.viewport_set_measure_render_time(root.get_viewport_rid(),true)
	for lod in [false,true]:
		var anchors := ""
		for label in ["legacy_current","legacy_short","meadow_short"]:
			set_pose(Vector3(-4,1.6,6),Vector3(0,.1,0))
			fixture.BuildCase(label,lod)
			var deadline := Time.get_ticks_msec()+25000
			while not fixture.ReadyForCapture and fixture.Failure.is_empty() and Time.get_ticks_msec()<deadline:
				await process_frame
			if not fixture.ReadyForCapture:
				push_error("MEADOW_COMPARISON build failed "+str(fixture.Failure))
				quit(1)
				return
			var snapshot: Dictionary = fixture.Snapshot()
			var signature: String = snapshot.anchor_signature
			if anchors.is_empty(): anchors=signature
			elif anchors != signature:
				push_error("MEADOW_COMPARISON root anchors differ: "+label+" "+signature+" vs "+anchors)
				quit(1)
				return
			var mode := "lod" if lod else "near"
			var item := {"label":label, "mode":mode, "snapshot":snapshot, "captures":[]}
			if not lod:
				item.captures.append(await capture(label+"_near_full",Vector3(-4,1.6,6),Vector3(0,.1,0)))
				item.captures.append(await capture(label+"_near_overview",Vector3(0,14,16),Vector3.ZERO))
			else:
				for i in range(5):
					item.captures.append(await capture(label+"_lod_grazing_%d"%i,Vector3(0,.55,8-i*4),Vector3(0,.08,-35)))
			report.cases.append(item)
			print("MEADOW_COMPARISON case=",label," mode=",mode," roots=",snapshot.get("resident_roots",0)," anchors=",signature)
	var file := FileAccess.open(OUT.path_join("comparison.json"),FileAccess.WRITE)
	if file == null:
		push_error("MEADOW_COMPARISON cannot write report")
		quit(1)
		return
	file.store_string(JSON.stringify(report,"\t"))
	print("MEADOW_COMPARISON=PASS artifacts/meadow-grass/comparison.json")
	quit(0)

func set_pose(position: Vector3,target: Vector3) -> void:
	camera.position=position
	camera.look_at(target,Vector3.UP)
	if fixture != null: fixture.RefreshPresentation()

func capture(label: String,position: Vector3,target: Vector3) -> Dictionary:
	set_pose(position,target)
	for i in range(WARMUP):
		fixture.RefreshPresentation()
		await RenderingServer.frame_post_draw
	var gpu := []
	var cpu := []
	var wall := []
	var primitives := []
	var draws := []
	for i in range(SAMPLES):
		var started := Time.get_ticks_usec()
		fixture.RefreshPresentation()
		await RenderingServer.frame_post_draw
		wall.append((Time.get_ticks_usec()-started)*.001)
		gpu.append(RenderingServer.viewport_get_measured_render_time_gpu(root.get_viewport_rid()))
		cpu.append(RenderingServer.viewport_get_measured_render_time_cpu(root.get_viewport_rid()))
		primitives.append(Performance.get_monitor(Performance.RENDER_TOTAL_PRIMITIVES_IN_FRAME))
		draws.append(Performance.get_monitor(Performance.RENDER_TOTAL_DRAW_CALLS_IN_FRAME))
	var path := OUT.path_join(label+".png")
	var image := root.get_texture().get_image()
	if image == null or image.is_empty() or image.save_png(path) != OK:
		push_error("MEADOW_COMPARISON capture failed "+label)
		quit(1)
	return {"label":label,"path":path,"position":str(position),"target":str(target),"viewport_gpu_ms":stats(gpu,true),"viewport_cpu_ms":stats(cpu,true),"wall_ms":stats(wall),"draw_calls":stats(draws),"triangles_reported":stats(primitives),"sources":fixture.Snapshot()}

func stats(values: Array,positive_only := false) -> Dictionary:
	var sorted := []
	for value in values:
		if is_finite(float(value)) and (not positive_only or float(value)>0): sorted.append(float(value))
	sorted.sort()
	if sorted.is_empty(): return {"available":false,"samples":0}
	var total := 0.0
	for value in sorted: total+=value
	return {"available":true,"samples":sorted.size(),"median":sorted[int(sorted.size()/2)],"p95":sorted[mini(sorted.size()-1,int(sorted.size()*.95))],"mean":total/sorted.size()}
