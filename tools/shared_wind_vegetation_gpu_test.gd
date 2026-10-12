extends "res://tools/vegetation_wind_gpu_test.gd"

var shared_texture: ImageTexture

# Real imported surfaces and packed production LOD data, plus a shader helper
# readback. Run with a RenderingDevice; this is functional evidence, not timing.
func run() -> void:
	if output_path == OUT: output_path = "res://artifacts/shared-wind/vegetation"
	driver = DRIVER.new()
	if not require(bool(driver.call("VerifyTreeWindAttachments")), "production attachment metadata invalid"): return
	setup_view()
	install_shared_field()
	var fixtures: Array = driver.call("LoadSharedWindProductionMeshes")
	fixtures.append({"name": "grass", "mesh": driver.call("LoadGrassMesh")})
	for fixture: Dictionary in fixtures:
		var grass: bool = fixture.name == "grass"
		configure_mesh(fixture.mesh, grass)
		if not grass:
			if fixture.has("mode"):
				material.set_shader_parameter("tree_foliage_mode", fixture.mode)
				material.set_shader_parameter("tree_foliage_attachments", true)
			else: geometry.material_override = fixture.get("material")
			geometry.multimesh.set_instance_custom_data(0, Color(0, 1, .37, 2))
		else:
			material.set_shader_parameter("grass_gust_enabled", true)
			material.set_shader_parameter("grass_gust_strength", 1.0)
			material.set_shader_parameter("grass_gust_base_breeze", 0.0)
		publish_shared(Vector2.ZERO, Vector2(.10, .20))
		var calm := await capture(fixture.name + "_calm")
		publish_shared(Vector2(-35, 0), Vector2(.10, .20))
		var storm := await capture(fixture.name + "_storm")
		if not require(metrics(calm).count > 10 and difference(calm, storm).count > 10,
			"production shared wind invisible: " + fixture.name): return
		var frozen := await capture()
		if not require(difference(storm, frozen).count == 0, "clock drift: " + fixture.name): return
		publish_shared(Vector2(-35, 0), Vector2(.68, .20))
		var passing := await capture(fixture.name + "_passing_gust")
		if not require(difference(storm, passing).count > 5, "moving shared patch invisible: " + fixture.name): return
		print("SHARED_WIND_PRODUCTION ", fixture.name, " storm=", difference(calm, storm), " patch=", difference(storm, passing))
	if not await shared_helper_contract(): return
	if not await leaf_parts_contract(): return
	if not await actual_advected_field_contract(): return
	print("SHARED_WIND_VEGETATION_GPU=PASS imported LOD0/1/2/3, packed LOD3, procedural trunk/canopy, grass, common field, roots, attachments, parts controls, lever limits, lag, freeze, frame")
	quit(0)

func install_shared_field() -> void:
	var image := Image.create(64, 64, false, Image.FORMAT_RF)
	for y in range(64):
		for x in range(64):
			var value := .5 + .45 * sin(TAU * x / 64.0 + .37) * cos(TAU * y / 64.0 - .21)
			image.set_pixel(x, y, Color(value, 0, 0, 1))
	shared_texture = ImageTexture.create_from_image(image)
	RenderingServer.global_shader_parameter_set("ring_wind_gust_texture", shared_texture)
	RenderingServer.global_shader_parameter_set("ring_wind_field_enabled", 1.0)
	RenderingServer.global_shader_parameter_set("ring_wind_gust_strength", 1.0)
	RenderingServer.global_shader_parameter_set("ring_wind_gust_base", .25)
	RenderingServer.global_shader_parameter_set("ring_wind_anchor_world", Vector3.ZERO)
	RenderingServer.global_shader_parameter_set("ring_wind_around_covector", Vector4(-1, 0, 0, .01))
	RenderingServer.global_shader_parameter_set("ring_wind_across_covector", Vector4(0, 0, 1, .01))
	RenderingServer.global_shader_parameter_set("ring_wind_gust_velocity_uv", Vector2(.07, .02))
	RenderingServer.global_shader_parameter_set("ring_wind_phase", Vector4(0, 1, 0, 1))
	RenderingServer.global_shader_parameter_set("ring_tree_wind_response", Vector4(1.7, .28, 2, .6))
	RenderingServer.global_shader_parameter_set("ring_tree_wind_parts", Vector2(.15, 1))

func publish_shared(wind: Vector2, uv: Vector2) -> void:
	set_field(wind, Vector4(.7, .714, .3, .954))
	RenderingServer.global_shader_parameter_set("ring_wind_mean_vector", wind)
	RenderingServer.global_shader_parameter_set("ring_tree_wind_trunk", wind)
	RenderingServer.global_shader_parameter_set("ring_tree_wind_canopy", wind)
	RenderingServer.global_shader_parameter_set("ring_wind_anchor_uv", uv)

func shared_helper_contract() -> bool:
	var sample := SubViewport.new()
	sample.size = Vector2i(8, 8)
	sample.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(sample)
	var rect := ColorRect.new()
	rect.size = Vector2(8, 8)
	var shader := Shader.new()
	shader.code = """
shader_type canvas_item;
#include "res://materials/ring_local_wind.gdshaderinc"
uniform vec3 root_point;
uniform float weight;
uniform int mode;
void fragment(){
 vec3 derivative;
 vec3 offset; mat3 jacobian;
 ring_tree_wind_geometry(root_point,vec3(0.0,weight,0.0),mat3(vec3(6,0,0),vec3(0,6,0),vec3(0,0,6)),vec4(0.0),2,false,offset,jacobian);
 vec3 bend=offset*6.0;
 if(mode==0) COLOR=vec4(vec3(.5)+bend/12.0,1.0);
 else if(mode==1) COLOR=vec4(ring_wind_gust_at_world(root_point,0.0)/3.0,ring_tree_filtered_gust(root_point,.6)/3.0,ring_tree_filtered_gust(root_point,2.0)/3.0,1.0);
 else COLOR=vec4(vec3(length(bend)<=6.0*.60001 ? 1.0:0.0),1.0);
}
"""
	var mat := ShaderMaterial.new()
	mat.shader = shader
	rect.material = mat
	sample.add_child(rect)
	pose = Transform3D.IDENTITY
	publish_shared(Vector2(-35, 0), Vector2(.40, .40))
	mat.set_shader_parameter("weight", 0.0)
	var pinned := await helper_pixel(sample)
	if not require(pinned.distance_to(Vector3(.5, .5, .5)) < .007, "shared root moved"): return false
	mat.set_shader_parameter("weight", 1.0)
	var tip := await helper_pixel(sample)
	if not require(tip.x > .52, "shared storm tip bend too small"): return false
	mat.set_shader_parameter("mode", 1)
	var lag := await helper_pixel(sample)
	if not require(absf(lag.x-lag.y) > .005 and absf(lag.y-lag.z) > .005, "trunk/canopy lag missing"): return false
	var frozen := await helper_pixel(sample)
	if not require(lag.distance_to(frozen) == 0.0, "shared clock not frozen"): return false
	# Translate both the root and authoritative anchor; UV and gust must agree.
	var shift := Vector3(20000, -12000, 8000)
	mat.set_shader_parameter("root_point", shift)
	RenderingServer.global_shader_parameter_set("ring_wind_anchor_world", shift)
	var rebased := await helper_pixel(sample)
	if not require(lag.distance_to(rebased) < .007, "shared rebase changes gust"): return false
	mat.set_shader_parameter("root_point", Vector3.ZERO)
	RenderingServer.global_shader_parameter_set("ring_wind_anchor_world", Vector3.ZERO)
	mat.set_shader_parameter("mode", 2)
	RenderingServer.global_shader_parameter_set("ring_tree_wind_response", Vector4(100, 100, 2, .6))
	RenderingServer.global_shader_parameter_set("ring_tree_wind_trunk", Vector2(-80, 0))
	RenderingServer.global_shader_parameter_set("ring_tree_wind_canopy", Vector2(80, 0))
	for weight in [0.0, .25, .5, .75, 1.0]:
		mat.set_shader_parameter("weight", weight)
		if not require((await helper_pixel(sample)).x > .99, "tree hard limit exceeded"): return false
	return true

func actual_advected_field_contract() -> bool:
	var payload: Dictionary = driver.call("PublishProductionWindFixture",0.0)
	var roots: PackedVector3Array = payload.roots
	var sample := SubViewport.new()
	sample.size=Vector2i(33,1)
	sample.use_hdr_2d=true
	sample.render_target_update_mode=SubViewport.UPDATE_ALWAYS
	root.add_child(sample)
	var rect:=ColorRect.new()
	rect.size=Vector2(33,1)
	var shader:=Shader.new()
	shader.code="""
shader_type canvas_item;
#include "res://materials/ring_local_wind.gdshaderinc"
uniform vec3 roots[33];
void fragment(){
 int index=clamp(int(FRAGCOORD.x),0,32);
 vec3 root=roots[index];
 mat3 basis=mat3(vec3(2,0,0),vec3(0,6,0),vec3(0,0,3));
 vec3 wood;mat3 jwood;vec3 leaf;mat3 jleaf;
 ring_tree_wind_geometry(root,vec3(.1,.7,0),basis,vec4(0,.7,0,5),0,true,wood,jwood);
 ring_tree_wind_geometry(root,vec3(.1,.7,0),basis,vec4(0,.7,0,5),1,true,leaf,jleaf);
 COLOR=vec4(ring_wind_gust_at_world(root,0.0)/4.0,length(basis*wood),length(basis*(leaf-wood)),1.0);
}
"""
	var mat:=ShaderMaterial.new()
	mat.shader=shader
	mat.set_shader_parameter("roots",roots)
	rect.material=mat
	sample.add_child(rect)
	for i in range(4): await RenderingServer.frame_post_draw
	var before:=sample.get_texture().get_image()
	var low:=INF
	var high:=-INF
	var bark_low:=INF
	var bark_high:=-INF
	for i in range(33):
		var pixel:=before.get_pixel(i,0)
		if not require(absf(pixel.r*4.0-float(payload.gusts[i]))<.003,"actual field CPU/GPU mismatch"): return false
		low=minf(low,pixel.r);high=maxf(high,pixel.r)
		bark_low=minf(bark_low,pixel.g);bark_high=maxf(bark_high,pixel.g)
	if not require(high-low>.04 and bark_high-bark_low>.003,"actual nearby identical trees have no spatial response"): return false
	# 12 m/s downwind for .5 s exactly advances the patch by one 6 m root.
	driver.call("PublishProductionWindFixture",.5)
	for i in range(4): await RenderingServer.frame_post_draw
	var after:=sample.get_texture().get_image()
	var downstream_error:=0.0
	var upstream_error:=0.0
	for i in range(1,32):
		downstream_error+=absf(after.get_pixel(i,0).r-before.get_pixel(i-1,0).r)
		upstream_error+=absf(after.get_pixel(i,0).r-before.get_pixel(i+1,0).r)
	if not require(downstream_error<.015 and upstream_error>downstream_error*10+.05,"production gust patches do not propagate downwind"): return false
	for i in range(4): await RenderingServer.frame_post_draw
	if not require(difference(after,sample.get_texture().get_image()).count==0,"production field animates while clock frozen"): return false
	# Simultaneous real production plants, sharing their prototype and art settings.
	# Only planted position differs. Captures support review of patch-shaped motion.
	var fixtures: Array=driver.call("LoadSharedWindProductionMeshes")
	fixtures.append({"name":"near_grass","mesh":driver.call("LoadGrassMesh")})
	for fixture:Dictionary in fixtures:
		var grass:bool=fixture.name=="near_grass"
		configure_mesh(fixture.mesh,grass)
		if grass:
			material.set_shader_parameter("grass_gust_enabled",true)
			material.set_shader_parameter("grass_gust_strength",1.0)
			# Isolate wind on the real near prototype; this wide diagnostic row
			# has no paired LOD renderer or terrain handoff behind its roots.
			material.set_shader_parameter("grass_lod_enabled",false)
		elif fixture.has("mode"):
			material.set_shader_parameter("tree_foliage_mode",fixture.mode)
			material.set_shader_parameter("tree_foliage_attachments",true)
		else: geometry.material_override=fixture.get("material")
		geometry.multimesh.instance_count=33
		for i in range(33):
			geometry.multimesh.set_instance_transform(i,Transform3D(instance_basis,roots[i]))
			geometry.multimesh.set_instance_custom_data(i,Color(.7,.5,.37,2) if grass else Color(0,1,.37,2))
		# Keep the real .75 m grass height while giving its ribbons enough
		# pixels for meaningful debug readback across the full 192 m row.
		vp.size=Vector2i(4096,128) if grass else Vector2i(2048,256)
		camera.size=7.0 if grass else 28.0
		camera.far=500.0
		var camera_height:=.375 if grass else 2.0
		camera.position=Vector3(0,camera_height,20 if grass else 300)
		camera.look_at(Vector3(0,camera_height,0),Vector3.UP)
		for time in [1.0,1.5,2.0]:
			driver.call("PublishProductionWindFixture",time)
			var plants:=await capture(fixture.name+"_actual_perlin_33_plants_t"+str(time))
			if not require(metrics(plants).count>100,"simultaneous production plants invisible: "+fixture.name): return false
		if grass:
			material.set_shader_parameter("grass_debug_mode",3)
			var debug_plants:=await capture("near_grass_actual_perlin_spatial_response")
			var response_low:=INF
			var response_high:=-INF
			for i in range(33):
				var x:=int(camera.unproject_position(roots[i]).x)
				var total:=0.0
				var count:=0
				for yy in range(debug_plants.get_height()):
					for xx in range(maxi(0,x-20),mini(debug_plants.get_width(),x+20)):
						var pixel:=debug_plants.get_pixel(xx,yy).srgb_to_linear()
						if pixel.r+pixel.b>.05:
							total+=pixel.r/(pixel.r+pixel.b);count+=1
				if count>0:
					response_low=minf(response_low,total/count);response_high=maxf(response_high,total/count)
			if not require(response_high-response_low>.03,"identical production grasses lack local Perlin response"): return false
			material.set_shader_parameter("grass_wind_gust_multiplier",0.0)
			material.set_shader_parameter("grass_gust_base_breeze",0.0)
			RenderingServer.global_shader_parameter_set("ring_local_wind_wave",Vector4(1,0,1,0))
			if not require(difference(debug_plants,await capture()).count==0,"hidden legacy controls/global sine alter shared grass"): return false
			material.set_shader_parameter("grass_debug_mode",0)
	driver.call("DisposeProductionWindFixture")
	print("ACTUAL_PERLIN_VEGETATION=PASS roots=33 spatial_range=",high-low," bark_range=",bark_high-bark_low," downwind_error=",downstream_error," upstream_error=",upstream_error)
	return true

func leaf_parts_contract() -> bool:
	var sample := SubViewport.new()
	sample.size = Vector2i(8, 8)
	sample.use_hdr_2d = true
	sample.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(sample)
	var rect := ColorRect.new()
	rect.size = Vector2(8, 8)
	var shader := Shader.new()
	shader.code = """
shader_type canvas_item;
#include "res://materials/ring_local_wind.gdshaderinc"
uniform vec3 source=vec3(.1,.7,0.0);
uniform vec4 attachment=vec4(0.0,.7,0.0,5.0);
uniform int foliage=1;
uniform int output_mode=0;
void fragment(){
 mat3 basis=mat3(vec3(2,0,0),vec3(0,6,0),vec3(0,0,3));
 vec3 offset; mat3 jacobian;
 ring_tree_wind_geometry(vec3(0.0),source,basis,attachment,foliage,true,offset,jacobian);
 vec3 normal=ring_tree_deformed_normal(jacobian,normalize(vec3(.3,.7,.2)));
 if(output_mode==0) COLOR=vec4(vec3(.5)+basis*offset*.5,1.0);
 else if(output_mode==1) COLOR=vec4(vec3(.5)+normal*.5,1.0);
 else {
  vec3 normal_source=normalize(vec3(.3,.7,.2));
  vec3 tangent=normalize(cross(normal_source,vec3(0,0,1)));
  vec3 bitangent=normalize(cross(normal_source,tangent));
  vec3 offset_t;mat3 unused_t;vec3 offset_b;mat3 unused_b;
  float epsilon=.0001;
  ring_tree_wind_geometry(vec3(0.0),source+tangent*epsilon,basis,attachment,foliage,true,offset_t,unused_t);
  ring_tree_wind_geometry(vec3(0.0),source+bitangent*epsilon,basis,attachment,foliage,true,offset_b,unused_b);
  vec3 fd_tangent=tangent+(offset_t-offset)/epsilon;
  vec3 fd_bitangent=bitangent+(offset_b-offset)/epsilon;
  float error=max(abs(dot(normal,normalize(fd_tangent))),abs(dot(normal,normalize(fd_bitangent))));
  COLOR=vec4(vec3(error),1.0);
 }
}
"""
	var mat := ShaderMaterial.new()
	mat.shader = shader
	rect.material = mat
	sample.add_child(rect)
	pose = Transform3D.IDENTITY
	publish_shared(Vector2(-35, 0), Vector2(.4, .4))
	RenderingServer.global_shader_parameter_set("ring_tree_wind_response", Vector4(1.7,.28,2,.6))
	RenderingServer.global_shader_parameter_set("ring_tree_wind_parts", Vector2(.15,0))
	var no_flex := await helper_pixel(sample)
	mat.set_shader_parameter("foliage", 0)
	var wood := await helper_pixel(sample)
	if not require(no_flex.distance_to(wood)<.0001, "canopy zero detaches foliage from common structure"): return false
	RenderingServer.global_shader_parameter_set("ring_tree_wind_parts", Vector2(0,1))
	var static_wood := await helper_pixel(sample)
	if not require(static_wood.distance_to(Vector3(.5,.5,.5))<.0001, "trunk zero still bends bark"): return false
	mat.set_shader_parameter("foliage", 1)
	var leaves := await helper_pixel(sample)
	if not require(leaves.distance_to(static_wood)>.001, "leaves cannot move with stiff trunk"): return false
	# Every vertex is limited by its own lever, including this nonuniform basis.
	var extra := leaves.distance_to(static_wood)*2.0
	if not require(extra<=.32*.2+.0005, "small leaf exceeded world attachment lever cap"): return false
	RenderingServer.global_shader_parameter_set("ring_tree_wind_parts", Vector2(.15,1))
	mat.set_shader_parameter("source", Vector3(0,.7,0))
	var attached_leaf := await helper_pixel(sample)
	mat.set_shader_parameter("foliage", 0)
	var attached_wood := await helper_pixel(sample)
	if not require(attached_leaf.distance_to(attached_wood)<.0001, "attachment does not stay exactly on branch deformation"): return false
	mat.set_shader_parameter("foliage", 1)
	mat.set_shader_parameter("source", Vector3(.1,.7,0))
	mat.set_shader_parameter("output_mode", 1)
	var normal := await helper_pixel(sample)
	if not require(normal.is_finite() and absf((normal-Vector3(.5,.5,.5)).length()-.5)<.003, "deformation normal invalid"): return false
	mat.set_shader_parameter("output_mode", 2)
	var normal_error := await helper_pixel(sample)
	if not require(normal_error.is_finite() and normal_error.x<.005, "analytic foliage normal disagrees with finite difference deformation"): return false
	mat.set_shader_parameter("output_mode", 0)
	var frozen := await helper_pixel(sample)
	if not require(frozen.distance_to(await helper_pixel(sample))==0.0, "leaf clock advances without publisher"): return false
	RenderingServer.global_shader_parameter_set("ring_wind_phase", Vector4(0,1,1,0))
	if not require(frozen.distance_to(await helper_pixel(sample))>.001, "independent leaf phase motion missing"): return false
	print("TREE_LEAF_PARTS_GPU=PASS trunk0/canopy0, exact attachment, nonuniform lever cap, normal, frozen/animated phase")
	return true
