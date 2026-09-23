extends SceneTree

## Production tree GPU contract. It rejects Dummy rendering, uses the KayKit
## normalization path, and writes diagnostic captures below .godot only.
const SIZE := 128
const X := 1000.0
const OUT := "res://.godot/tree_lighting_shadow"
const LEGACY_SHADER := """
shader_type spatial;
render_mode cull_disabled, depth_draw_opaque;

uniform bool use_albedo_texture = false;
uniform sampler2D tree_albedo_texture : source_color, filter_nearest_mipmap_anisotropic, repeat_disable;
uniform vec3 tree_tint : source_color = vec3(1.0);
uniform float cloud_shadow_albedo_strength : hint_range(0.0, 1.0) = 0.34;
uniform float cloud_shadow_ao_strength : hint_range(0.0, 1.0) = 0.72;

#include "res://materials/ring_cloud_shadow.gdshaderinc"

varying float tree_cloud_occlusion;

void vertex() {
	vec3 tree_world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	// A cloud-map texel covers tens of metres, so evaluating per tree vertex is
	// visually equivalent to per fragment while avoiding foliage overdraw cost.
	tree_cloud_occlusion = sample_local_cloud_shadow(tree_world_position);
}

void fragment() {
	vec3 sampled_color = use_albedo_texture
		? texture(tree_albedo_texture, UV).rgb
		: vec3(1.0);
	float cloud_occlusion = tree_cloud_occlusion;
	ALBEDO = sampled_color * tree_tint
		* (1.0 - cloud_occlusion * cloud_shadow_albedo_strength);
	ROUGHNESS = 0.92;
	SPECULAR = 0.04;
	METALLIC = 0.0;
	AO = 1.0 - cloud_occlusion * cloud_shadow_ao_strength;
	AO_LIGHT_AFFECT = 1.0;
}
"""
var failed := false
var output_dir: String
var vp: SubViewport
var env: Environment
var sun: DirectionalLight3D
var planet: DirectionalLight3D
var omni: OmniLight3D
var tree: MultiMeshInstance3D
var receiver: MeshInstance3D
var tree_mat: ShaderMaterial

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null: _fail("RenderingDevice required; Dummy renderer is invalid"); return
	call_deferred("_run")

func _run() -> void:
	var wind_test := "--tree-wind-shadow" in OS.get_cmdline_user_args()
	output_dir = OUT.path_join("wind" if wind_test else ("before" if "--tree-lighting-before" in OS.get_cmdline_user_args() else "after"))
	_make_scene()
	if failed: return
	if wind_test: _set_fixture_wind(true)
	var day := await _tree_luma("day", Vector3.ZERO, 1.0, true, false, false, 0.0)
	var partial := await _tree_luma("partial", Vector3.ZERO, .45, true, false, false, 0.0)
	# Normal and real sun stay aligned; only ring_center changes geometric day.
	var night := await _tree_luma("night", Vector3(2000, 0, 0), 1.0, true, false, false, 0.0)
	var total := await _tree_luma("totality", Vector3.ZERO, 0.0, true, false, false, 0.0)
	var ambient := await _tree_luma("ambient", Vector3(2000, 0, 0), 1.0, false, false, false, .35)
	var moon := await _tree_luma("moon_night", Vector3(2000, 0, 0), 1.0, false, true, false, 0.0)
	var lightning := await _tree_luma("lightning_night", Vector3(2000, 0, 0), 1.0, false, false, true, 0.0)
	var ambient_total := await _tree_luma("ambient_totality", Vector3.ZERO, 0.0, false, false, false, .35)
	var moon_total := await _tree_luma("moon_totality", Vector3.ZERO, 0.0, false, true, false, 0.0)
	var lightning_total := await _tree_luma("lightning_totality", Vector3.ZERO, 0.0, false, false, true, 0.0)
	print("TREE_LIGHT_VALUES day=%.6f partial=%.6f night=%.6f total=%.6f ambient=%.6f moon=%.6f omni=%.6f" % [day,partial,night,total,ambient,moon,lightning])
	print("TREE_TOTALITY_FILL ambient=%.6f moon=%.6f omni=%.6f" % [ambient_total, moon_total, lightning_total])
	if failed: return
	if day <= .003 or partial <= .001 or not (day > partial and partial > total) or night > .0001 or total > .0001:
		_fail("sun/eclipse gate failed day=%.5f partial=%.5f night=%.5f total=%.5f" % [day, partial, night, total]); return
	if ambient <= .001 or moon <= .001 or lightning <= .001: _fail("ambient, planet directional, or Omni did not survive night"); return
	if ambient_total <= .001 or moon_total <= .001 or lightning_total <= .001: _fail("ambient, planet directional, or Omni did not survive totality"); return
	await _shadow_contract("res://materials/voxel_ring_terrain.gdshader", "voxel")
	if failed: return
	await _shadow_contract("res://materials/ring_terrain_proxy.gdshader", "proxy")
	if failed: return
	print("TREE_LIGHTING_SHADOW_GPU=PASS day=%.5f partial=%.5f night=%.5f total=%.5f" % [day, partial, night, total]); quit(0)

func _make_scene() -> void:
	vp=SubViewport.new(); vp.size=Vector2i(SIZE,SIZE); vp.own_world_3d=true; vp.render_target_update_mode=SubViewport.UPDATE_ALWAYS; root.add_child(vp)
	var we:=WorldEnvironment.new(); env=Environment.new(); env.background_mode=Environment.BG_COLOR; env.background_color=Color.BLACK; env.tonemap_mode=Environment.TONE_MAPPER_LINEAR; we.environment=env; vp.add_child(we)
	var camera:=Camera3D.new(); camera.position=Vector3(X-20,0,0); camera.current=true; vp.add_child(camera); camera.look_at(Vector3(X,0,0),Vector3.UP)
	sun=DirectionalLight3D.new(); sun.light_energy=.25; sun.shadow_enabled=true; sun.directional_shadow_max_distance=100; sun.shadow_bias=.01; sun.shadow_normal_bias=.1; vp.add_child(sun); sun.look_at(-Vector3.LEFT,Vector3.UP)
	planet=DirectionalLight3D.new(); planet.light_energy=.25; vp.add_child(planet); planet.look_at(Vector3(.6,-.8,0),Vector3.UP)
	omni=OmniLight3D.new(); omni.light_energy=3.0; omni.omni_range=30; omni.position=Vector3(X-10,0,0); vp.add_child(omni)
	receiver=MeshInstance3D.new(); receiver.mesh=_inward_quad(); vp.add_child(receiver)
	tree_mat=ShaderMaterial.new(); tree_mat.shader=load("res://materials/ring_tree.gdshader"); tree_mat.set_shader_parameter("tree_tint",Vector3(.3,.7,.25)); tree_mat.set_shader_parameter("ring_center",Vector3.ZERO); tree_mat.set_shader_parameter("sun_direction",Vector3.LEFT); tree_mat.set_shader_parameter("day_start_dot",-.2); tree_mat.set_shader_parameter("day_full_dot",.35); tree_mat.set_shader_parameter("night_tint",Vector3.ONE); tree_mat.set_shader_parameter("night_direct_light_factor",0.0); tree_mat.set_shader_parameter("day_direct_light_factor",1.0)
	if "--tree-lighting-before" in OS.get_cmdline_user_args():
		var old_shader := Shader.new()
		old_shader.code = LEGACY_SHADER
		tree_mat.shader = old_shader
	var loader = load("res://tools/TreeLightingShadowBehaviorTest.cs").new()
	var mesh := loader.call("LoadNormalizedKayKitTree") as Mesh
	if mesh == null:
		_fail("KayKit normalized tree unavailable")
		return
	var mm:=MultiMesh.new(); mm.transform_format=MultiMesh.TRANSFORM_3D; mm.mesh=mesh; mm.instance_count=1
	var basis:=Basis(Quaternion(Vector3.UP,Vector3.LEFT)).scaled(Vector3(6,6,6)); mm.set_instance_transform(0,Transform3D(basis,Vector3(X,0,0)))
	tree=MultiMeshInstance3D.new(); tree.multimesh=mm; tree.material_override=tree_mat; tree.cast_shadow=GeometryInstance3D.SHADOW_CASTING_SETTING_ON; vp.add_child(tree)

func _tree_luma(label:String, center:Vector3, visibility:float, sun_on:bool, moon_on:bool, omni_on:bool, ambient_energy:float)->float:
	RenderingServer.global_shader_parameter_set("ringworld_solar_visibility",visibility); tree_mat.set_shader_parameter("ring_center",center); tree_mat.set_shader_parameter("sun_direction",Vector3.LEFT)
	sun.look_at(-Vector3.LEFT,Vector3.UP); sun.visible=sun_on; planet.visible=moon_on; omni.visible=omni_on; tree.visible=true; tree.cast_shadow=GeometryInstance3D.SHADOW_CASTING_SETTING_OFF; env.ambient_light_source=Environment.AMBIENT_SOURCE_COLOR; env.ambient_light_color=Color.WHITE; env.ambient_light_energy=ambient_energy
	receiver.visible=false
	return await _luma(label,Rect2i(38,22,52,84))

func _shadow_contract(shader_path:String,label:String)->void:
	receiver.visible=true; receiver.material_override=_receiver_material(shader_path); env.ambient_light_energy=0.0; RenderingServer.global_shader_parameter_set("ringworld_solar_visibility",1.0); tree_mat.set_shader_parameter("ring_center",Vector3.ZERO); tree_mat.set_shader_parameter("sun_direction",Vector3(-.6,.7,.2).normalized()); sun.visible=true; sun.look_at(-Vector3(-.6,.7,.2),Vector3.UP); planet.visible=false; omni.visible=false
	var wind_test := "--tree-wind-shadow" in OS.get_cmdline_user_args()
	tree.visible=false; var off:=await _image(label+"_off")
	if failed: return
	var calm_only: Image
	if wind_test:
		_set_fixture_wind(false)
		tree.visible=true; tree.cast_shadow=GeometryInstance3D.SHADOW_CASTING_SETTING_SHADOWS_ONLY
		calm_only=await _image(label+"_wind_calm_only")
		if failed: return
		_set_fixture_wind(true)
	tree.visible=true; tree.cast_shadow=GeometryInstance3D.SHADOW_CASTING_SETTING_SHADOWS_ONLY; var only:=await _image(label+"_only")
	if failed: return
	tree.cast_shadow=GeometryInstance3D.SHADOW_CASTING_SETTING_ON; var on:=await _image(label+"_on")
	if failed: return
	var changed:=0
	for y in range(70,116):
		for x in range(14,114):
			# Count ground darkening that agrees with a shadow-only render, not the visible tree.
			var a:=off.get_pixel(x,y).srgb_to_linear().get_luminance(); var b:=only.get_pixel(x,y).srgb_to_linear().get_luminance(); var c:=on.get_pixel(x,y).srgb_to_linear().get_luminance()
			if a-b>.01 and absf(b-c)<.02: changed+=1
	print("TREE_GROUND_SHADOW receiver=%s pixels=%d" % [label,changed])
	if changed<8: _fail("%s KayKit MultiMesh shadow missing or visible/on disagrees with ShadowsOnly (%d pixels)" % [label,changed])
	if wind_test:
		var moved:=0
		for y in range(70,116):
			for x in range(14,114):
				if absf(calm_only.get_pixel(x,y).get_luminance()-only.get_pixel(x,y).get_luminance())>.006: moved+=1
		print("TREE_WIND_SHADOW receiver=%s changed_pixels=%d" % [label,moved])
		if moved<4: _fail("%s wind did not alter the shadow-only ground silhouette (%d pixels)" % [label,moved])

func _set_fixture_wind(active:bool)->void:
	# Fixture surface is inward -X, ring axis is +Y.  The shared helper maps
	# positive around wind into a Z bend, safely visible on this receiver.
	RenderingServer.global_shader_parameter_set("ring_local_wind_enabled",1.0 if active else 0.0)
	RenderingServer.global_shader_parameter_set("ring_local_wind_vector",Vector2(18.0,0.0) if active else Vector2.ZERO)
	RenderingServer.global_shader_parameter_set("ring_local_wind_wave",Vector4(1.0,0.0,.55,.84))
	RenderingServer.global_shader_parameter_set("ring_local_wind_center",Vector3.ZERO)
	RenderingServer.global_shader_parameter_set("ring_local_wind_axis",Vector3.UP)

func _receiver_material(path:String)->ShaderMaterial:
	var m:=ShaderMaterial.new(); m.shader=load(path); m.set_shader_parameter("ring_center",Vector3.ZERO); m.set_shader_parameter("camera_world_position",Vector3(X-20,0,0)); m.set_shader_parameter("sun_direction",Vector3(-.6,.7,.2).normalized()); m.set_shader_parameter("day_start_dot",-.2); m.set_shader_parameter("day_full_dot",.35); m.set_shader_parameter("night_direct_light_factor",0.0); m.set_shader_parameter("day_direct_light_factor",1.0); m.set_shader_parameter("shadow_floor",0.0); m.set_shader_parameter("ring_atmosphere_enabled",false); m.set_shader_parameter("local_cloud_shadow_enabled",false)
	if path.ends_with("proxy.gdshader"): m.set_shader_parameter("near_fade_enabled",false); m.set_shader_parameter("far_fade_enabled",false); m.set_shader_parameter("geomorph_enabled",false); m.set_shader_parameter("analytic_cloud_shadow_enabled",false)
	return m

func _inward_quad()->ArrayMesh:
	var a=[]; a.resize(ArrayMesh.ARRAY_MAX); var h:=10.0; a[ArrayMesh.ARRAY_VERTEX]=PackedVector3Array([Vector3(X,-h,-h),Vector3(X,h,-h),Vector3(X,h,h),Vector3(X,-h,h)]); a[ArrayMesh.ARRAY_NORMAL]=PackedVector3Array([Vector3.LEFT,Vector3.LEFT,Vector3.LEFT,Vector3.LEFT]); a[ArrayMesh.ARRAY_COLOR]=PackedColorArray([Color(.5,.6,.4),Color(.5,.6,.4),Color(.5,.6,.4),Color(.5,.6,.4)]); a[ArrayMesh.ARRAY_INDEX]=PackedInt32Array([0,1,2,0,2,3]); var mesh:=ArrayMesh.new(); mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES,a); return mesh

func _image(label:String)->Image:
	for i in range(4): await RenderingServer.frame_post_draw
	var image:=vp.get_texture().get_image()
	if image == null or image.is_empty() or image.get_size() != Vector2i(SIZE,SIZE):
		_fail("empty/invalid capture: "+label)
		return Image.new()
	for y in range(SIZE):
		for x in range(SIZE):
			var c := image.get_pixel(x,y)
			if not is_finite(c.r) or not is_finite(c.g) or not is_finite(c.b):
				_fail("non-finite capture: "+label)
				return image
	if DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(output_dir)) != OK or image.save_png(output_dir.path_join(label+".png")) != OK:
		_fail("could not save capture: "+label)
	return image
func _luma(label:String, area:Rect2i)->float:
	var im:=await _image(label); var total:=0.0; var n:=0
	if failed: return 0.0
	for y in range(area.position.y,area.end.y):
		for x in range(area.position.x,area.end.x):
			total+=im.get_pixel(x,y).srgb_to_linear().get_luminance(); n+=1
	return total/max(1,n)
func _fail(s:String)->void:
	failed = true
	push_error("TREE_LIGHTING_SHADOW_GPU=FAIL "+s)
	quit(1)
