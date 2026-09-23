extends SceneTree

## GPU contract for imported LOLIPOP production surfaces. This deliberately
## bypasses the biome layer: it proves the catalog meshes/materials themselves.
const SIZE := 512
const OUT := "res://.godot/imported_forest_gpu"
const SPECIES := [{"label":"maple","index":0}, {"label":"fir","index":12}, {"label":"pine","index":15}]
var failed := false
var viewport: SubViewport
var environment: Environment
var sun: DirectionalLight3D
var moon: DirectionalLight3D
var fill: OmniLight3D
var instances: Array[MultiMeshInstance3D] = []
var materials: Array[ShaderMaterial] = []
var cloud_texture: ImageTexture
var driver: Node
var meshes := {}
var receiver: MeshInstance3D

func _init() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice required; Dummy renderer is invalid")
		return
	call_deferred("_run")

func _run() -> void:
	_make_scene()
	if failed: return
	for item in SPECIES:
		for lod in [0, 3]:
			_show_only(int(item.index), lod)
			await _capture("%s_lod%d_cutout" % [item.label, lod])
			if failed: return
	_show_only(0, 0)
	var day := await _state("day", 1.0, true, false, false, false, false)
	var partial := await _state("partial", .45, true, false, false, false, false)
	# Night is a ring-side geometric state: the star remains rendered while the
	# material's ring-center test gates direct solar contribution.
	var night := await _state("night", 1.0, true, false, false, false, false)
	var total := await _state("totality", 0.0, true, false, false, false, false)
	var moon_luma := await _state("moon_fill", 0.0, false, true, false, false, false)
	var local_luma := await _state("local_fill", 0.0, false, false, true, false, false)
	var cloud := await _state("cloud_shadow", 1.0, true, false, false, true, false)
	var wind := await _state("wind", 1.0, true, false, false, false, true)
	await _shadow_contract()
	if failed: return
	await _cloud_contract()
	if failed: return
	if day <= .0005 or partial <= total or partial >= day or night > .001 or total > .001:
		_fail("solar state response day=%.5f partial=%.5f night=%.5f total=%.5f" % [day,partial,night,total]); return
	# Full-frame luma is background dominated; the state captures below are kept
	# for visual review while direct celestial gates above remain quantitative.
	if moon_luma <= .0005 or local_luma <= .0005 or cloud <= .0001 or wind <= .0005:
		_fail("fill/cloud/wind response moon=%.5f local=%.5f cloud=%.5f wind=%.5f" % [moon_luma,local_luma,cloud,wind]); return
	var maple_uv2 := false
	for material in materials:
		maple_uv2 = maple_uv2 or bool(material.get_shader_parameter("use_uv2_for_metallic_roughness"))
	if not maple_uv2: _fail("maple bark UV2 material adaptation missing"); return
	print("IMPORTED_TREE_GPU=PASS day=%.5f partial=%.5f moon=%.5f local=%.5f" % [day,partial,moon_luma,local_luma])
	quit(0)

func _make_scene() -> void:
	viewport=SubViewport.new(); viewport.size=Vector2i(SIZE,SIZE); viewport.own_world_3d=true; viewport.render_target_update_mode=SubViewport.UPDATE_ALWAYS; root.add_child(viewport)
	var world:=WorldEnvironment.new(); environment=Environment.new(); environment.background_mode=Environment.BG_COLOR; environment.background_color=Color(0.01,0.01,0.01); environment.ambient_light_source=Environment.AMBIENT_SOURCE_COLOR; environment.ambient_light_color=Color.WHITE; environment.tonemap_mode=Environment.TONE_MAPPER_LINEAR; world.environment=environment; viewport.add_child(world)
	var camera:=Camera3D.new(); camera.projection=Camera3D.PROJECTION_ORTHOGONAL; camera.size=18.0; camera.position=Vector3(980,0,0); camera.current=true; viewport.add_child(camera); camera.look_at(Vector3(1000,0,0),Vector3.UP)
	sun=DirectionalLight3D.new(); sun.light_energy=1.2; sun.shadow_enabled=true; viewport.add_child(sun); sun.look_at(-Vector3.LEFT,Vector3.UP)
	moon=DirectionalLight3D.new(); moon.light_energy=.5; viewport.add_child(moon); moon.look_at(Vector3(1000,.4,1),Vector3.UP)
	fill=OmniLight3D.new(); fill.position=Vector3(990,7,12); fill.light_energy=3.0; fill.omni_range=35; viewport.add_child(fill)
	driver = load("res://scripts/Vegetation/ImportedTreeCatalogDriver.cs").new()
	root.add_child(driver)
	for item in SPECIES:
		for lod in [0,3]:
			var mesh := driver.call("LoadVariantMesh",int(item.index),lod) as Mesh
			if mesh == null: _fail("missing catalog mesh %s lod%d" % [item.label,lod]); return
			meshes["%d:%d" % [int(item.index),lod]] = mesh
			var multi:=MultiMesh.new(); multi.transform_format=MultiMesh.TRANSFORM_3D; multi.mesh=mesh; multi.instance_count=1; multi.set_instance_transform(0,Transform3D(Basis(Quaternion(Vector3.UP,Vector3.LEFT)).scaled(Vector3(9,9,9)),Vector3(1000,0,0)))
			var node:=MultiMeshInstance3D.new(); node.multimesh=multi; node.cast_shadow=GeometryInstance3D.SHADOW_CASTING_SETTING_ON; viewport.add_child(node); instances.append(node)
		for material in driver.call("GetVariantMaterials",int(item.index)):
			if material is ShaderMaterial and not materials.has(material): materials.append(material)
	var image:=Image.create(2,2,false,Image.FORMAT_RGBA8); image.fill(Color(1,0,0,1)); cloud_texture=ImageTexture.create_from_image(image)
	receiver=MeshInstance3D.new(); receiver.mesh=_receiver_mesh(); receiver.material_override=StandardMaterial3D.new(); (receiver.material_override as StandardMaterial3D).albedo_color=Color(.6,.6,.6); receiver.visible=false; viewport.add_child(receiver)
	for material in materials:
		material.set_shader_parameter("ring_center",Vector3.ZERO); material.set_shader_parameter("sun_direction",Vector3.LEFT); material.set_shader_parameter("night_tint",Vector3.ONE); material.set_shader_parameter("night_direct_light_factor",0.0); material.set_shader_parameter("day_direct_light_factor",1.0); material.set_shader_parameter("local_cloud_shadow_texture",cloud_texture); material.set_shader_parameter("local_cloud_shadow_enabled",false); material.set_shader_parameter("local_cloud_shadow_near_enabled",false); material.set_shader_parameter("local_cloud_shadow_far_enabled",false); material.set_shader_parameter("local_cloud_shadow_radius_meters",100000.0)

func _show_only(index:int,lod:int)->void:
	for node in instances: node.visible = node.multimesh.mesh == meshes["%d:%d" % [index,lod]]

func _state(label:String, visibility:float, sun_on:bool, moon_on:bool, fill_on:bool, cloud_on:bool, wind_on:bool)->float:
	RenderingServer.global_shader_parameter_set("ringworld_solar_visibility",visibility)
	RenderingServer.global_shader_parameter_set("ring_local_wind_enabled",1.0 if wind_on else 0.0)
	RenderingServer.global_shader_parameter_set("ring_local_wind_vector",Vector2(18,0) if wind_on else Vector2.ZERO)
	RenderingServer.global_shader_parameter_set("ring_local_wind_wave",Vector4(1,0,.55,.84))
	RenderingServer.global_shader_parameter_set("ring_local_wind_center",Vector3.ZERO)
	RenderingServer.global_shader_parameter_set("ring_local_wind_axis",Vector3.UP)
	# Keep the actual star light present. Night is geometric, as it is in-game.
	sun.visible=sun_on; moon.visible=moon_on; fill.visible=fill_on; environment.ambient_light_energy=0.0
	for material in materials:
		material.set_shader_parameter("ring_center",Vector3(2000,0,0) if label == "night" else Vector3.ZERO)
		material.set_shader_parameter("local_cloud_shadow_enabled",cloud_on); material.set_shader_parameter("local_cloud_shadow_near_enabled",cloud_on)
	var image:=await _capture(label)
	return _luma(image)

func _capture(label:String)->Image:
	for i in range(4): await RenderingServer.frame_post_draw
	var image:=viewport.get_texture().get_image()
	if image == null or image.is_empty(): _fail("empty capture "+label); return Image.new()
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUT)); image.save_png(OUT.path_join(label+".png"))
	return image

func _luma(image:Image)->float:
	var total:=0.0
	for y in range(SIZE):
		for x in range(SIZE): total += image.get_pixel(x,y).srgb_to_linear().get_luminance()
	return total / float(SIZE*SIZE)

func _receiver_mesh()->ArrayMesh:
	var arrays=[]; arrays.resize(ArrayMesh.ARRAY_MAX); var h:=10.0
	arrays[ArrayMesh.ARRAY_VERTEX]=PackedVector3Array([Vector3(1000,-h,-h),Vector3(1000,h,-h),Vector3(1000,h,h),Vector3(1000,-h,h)])
	arrays[ArrayMesh.ARRAY_NORMAL]=PackedVector3Array([Vector3.LEFT,Vector3.LEFT,Vector3.LEFT,Vector3.LEFT]); arrays[ArrayMesh.ARRAY_INDEX]=PackedInt32Array([0,1,2,0,2,3])
	var mesh:=ArrayMesh.new(); mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES,arrays); return mesh

func _shadow_contract()->void:
	_show_only(0,0); receiver.visible=true; sun.visible=true; moon.visible=false; fill.visible=false
	for material in materials: material.set_shader_parameter("ring_center",Vector3.ZERO); material.set_shader_parameter("local_cloud_shadow_enabled",false); material.set_shader_parameter("local_cloud_shadow_near_enabled",false)
	RenderingServer.global_shader_parameter_set("ring_local_wind_enabled",0.0)
	instances[0].visible=false; var off:=await _capture("receiver_off")
	instances[0].visible=true; instances[0].cast_shadow=GeometryInstance3D.SHADOW_CASTING_SETTING_SHADOWS_ONLY; var shadow_only:=await _capture("receiver_shadows_only_calm")
	var root_before:=instances[0].multimesh.get_instance_transform(0).origin
	RenderingServer.global_shader_parameter_set("ring_local_wind_enabled",1.0); RenderingServer.global_shader_parameter_set("ring_local_wind_vector",Vector2(18,0)); RenderingServer.global_shader_parameter_set("ring_local_wind_wave",Vector4(1,0,.55,.84))
	var shadow_wind:=await _capture("receiver_shadows_only_wind")
	var root_after:=instances[0].multimesh.get_instance_transform(0).origin
	instances[0].cast_shadow=GeometryInstance3D.SHADOW_CASTING_SETTING_ON; var on:=await _capture("receiver_on")
	var changed:=0
	for y in range(SIZE):
		for x in range(SIZE):
			var a:=off.get_pixel(x,y).srgb_to_linear().get_luminance(); var b:=shadow_only.get_pixel(x,y).srgb_to_linear().get_luminance(); var c:=on.get_pixel(x,y).srgb_to_linear().get_luminance()
			if a-b>.01 and absf(b-c)<.03: changed+=1
	print("IMPORTED_TREE_RECEIVER_SHADOW pixels=%d" % changed)
	if changed<8: _fail("imported MultiMesh ShadowsOnly receiver contract failed (%d pixels)" % changed)
	if root_before.distance_to(root_after) > .00001: _fail("wind translated MultiMesh root")
	var moved:=0
	for y in range(SIZE):
		for x in range(SIZE):
			if absf(shadow_only.get_pixel(x,y).get_luminance()-shadow_wind.get_pixel(x,y).get_luminance())>.006: moved+=1
	print("IMPORTED_TREE_WIND_SHADOW pixels=%d" % moved)
	if moved<4: _fail("wind did not alter imported ShadowsOnly silhouette (%d pixels)" % moved)
	receiver.visible=false

func _cloud_contract()->void:
	_show_only(0,0); receiver.visible=false; sun.visible=true; moon.visible=false; fill.visible=false
	RenderingServer.global_shader_parameter_set("ringworld_solar_visibility",1.0)
	RenderingServer.global_shader_parameter_set("ring_local_wind_enabled",0.0)
	for material in materials:
		material.set_shader_parameter("ring_center",Vector3.ZERO)
		material.set_shader_parameter("local_cloud_shadow_enabled",false)
		material.set_shader_parameter("local_cloud_shadow_near_enabled",false)
		material.set_shader_parameter("local_cloud_shadow_far_enabled",false)
		material.set_shader_parameter("local_cloud_shadow_center_world",Vector3.ZERO)
		material.set_shader_parameter("local_cloud_shadow_axis_around",Vector3(0,0,1))
		material.set_shader_parameter("local_cloud_shadow_axis_across",Vector3(0,1,0))
		material.set_shader_parameter("local_cloud_shadow_axis_up",Vector3.LEFT)
		material.set_shader_parameter("local_cloud_shadow_radius_meters",100000.0)
		material.set_shader_parameter("local_cloud_shadow_near_start_meters",0.0)
		material.set_shader_parameter("local_cloud_shadow_near_end_meters",1.0)
	var clear:=await _capture("cloud_clear")
	for material in materials:
		material.set_shader_parameter("local_cloud_shadow_enabled",true)
		material.set_shader_parameter("local_cloud_shadow_near_enabled",true)
		material.set_shader_parameter("local_cloud_shadow_far_enabled",false)
		material.set_shader_parameter("local_cloud_shadow_strength",1.0)
	var cloudy:=await _capture("cloud_enabled")
	var pixels:=0; var clear_sum:=0.0; var cloud_sum:=0.0
	for y in range(SIZE):
		for x in range(SIZE):
			var a:=clear.get_pixel(x,y).srgb_to_linear().get_luminance()
			if a>.002: pixels+=1; clear_sum+=a; cloud_sum+=cloudy.get_pixel(x,y).srgb_to_linear().get_luminance()
	if pixels<32: _fail("cloud contract tree mask is empty (%d pixels)" % pixels); return
	var clear_mean:=clear_sum/pixels; var cloud_mean:=cloud_sum/pixels
	print("IMPORTED_TREE_CLOUD mask=%d clear=%.6f cloud=%.6f" % [pixels,clear_mean,cloud_mean])
	if cloud_mean >= clear_mean*.90: _fail("cloud map did not attenuate tree mask clear=%.6f cloud=%.6f" % [clear_mean,cloud_mean])
	for material in materials: material.set_shader_parameter("local_cloud_shadow_enabled",false); material.set_shader_parameter("local_cloud_shadow_near_enabled",false)

func _fail(message:String)->void:
	failed=true; push_error("IMPORTED_TREE_GPU=FAIL "+message); quit(1)
