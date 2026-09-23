extends SceneTree

# Forward+ HDR regression. Production helper output is compared against an
# independently assembled parent-triangle reference over the whole UV tile.
const OUT := "res://.godot/water_fft_lod_seam_gpu"
const SIZE := Vector2i(160, 160)
# Keep the synthetic +/-1.3 m field positive while resolving below 2 mm per
# half-float output channel; an 8 m encoding quantizes each step to 3.9 mm.
const SCALE := 4.0
var fft: Texture2DArray

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null: _fail("Forward+ RenderingDevice required"); return
	call_deferred("_run")

func _run() -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUT)); fft = _fft()
	# start=100, end=300: production's guarded endpoint is 198 m.
	var cases := [[2.,4.,0.,Vector3.ZERO,"near"],[2.,4.,198.,Vector3.ZERO,"2_4"],[4.,8.,198.,Vector3.ZERO,"4_8"],[8.,0.,198.,Vector3.ZERO,"8_0"],[2.,4.,198.,Vector3(17000,-800,4000),"rebase"]]
	var images: Array[Image] = []
	for c: Array in cases:
		var im := await _render(c)
		if not _finite(im): _fail("non-finite or empty capture " + c[4]); return
		images.append(im)
	var error := 0.0
	for im: Image in images: error=maxf(error,_error(im))
	var actual_base := await _render(cases[1], 1)
	var actual_rebase := await _render(cases[4], 1)
	var own_2 := await _render(cases[0], 2)
	var own_4 := await _render([4.,8.,0.,Vector3.ZERO,"near4"], 2)
	if not _finite(actual_base) or not _finite(actual_rebase) or not _finite(own_2) or not _finite(own_4): _fail("non-finite positive-control capture"); return
	var rebase := _delta(actual_base,actual_rebase)
	var amplitude := _amplitude(own_2)
	var spacing_delta := _delta(own_2,own_4)
	print("WATER_FFT_LOD_SEAM_GPU_METRICS max_error_m=%.7f phase_rebase_m=%.7f amplitude_m=%.7f spacing_delta_m=%.7f" % [error,rebase,amplitude,spacing_delta])
	if error > .004 or rebase > .004 or amplitude < .05 or spacing_delta < .01: _fail("parent mismatch or inactive synthetic FFT positive control") ; return
	print("WATER_FFT_LOD_SEAM_GPU_TEST=PASS assertions=near,2to4,4to8,8toflat,both_diagonals,edges,halfway_edges,rebase"); quit()

func _render(c: Array, mode := 0) -> Image:
	var shader:=Shader.new(); shader.code=_source(); var vp:=SubViewport.new(); vp.size=SIZE; vp.use_hdr_2d=true; vp.render_target_update_mode=SubViewport.UPDATE_ALWAYS; root.add_child(vp)
	var rect:=ColorRect.new(); rect.size=SIZE; var m:=ShaderMaterial.new(); m.shader=shader; rect.material=m; vp.add_child(rect)
	for p: Array in [["fft_displacement",fft],["fft_ready",true],["fft_cascade_count",2],["fft_period_1",Vector2(79,79)],["fft_resolution",128.],["fft_geometry_filter_meters",2.],["fft_geometry_spacing_meters",c[0]],["fft_geometry_parent_spacing_meters",c[1]],["proxy_sector_size_meters",128.],["fft_geometry_distance_meters",5000.],["fft_detail_distance_meters",5000.],["fft_anchor_world",c[3]],["camera_world_position",c[3]],["fft_around_axis",Vector3(1,0,0)],["fft_across_axis",Vector3(0,1,0)],["ring_radius_meters",5000001.5],["test_distance",c[2]],["test_output_mode",mode],["geomorph_enabled",true],["morph_start_meters",100.],["morph_end_meters",300.]]: m.set_shader_parameter(p[0],p[1])
	for f: int in 4: await process_frame
	await RenderingServer.frame_post_draw; var im:=vp.get_texture().get_image(); im.save_png(OUT+"/"+c[4]+"_mode"+str(mode)+".png"); vp.queue_free(); await process_frame; return im

func _source() -> String:
	var src:=FileAccess.get_file_as_string("res://materials/ring_ocean_proxy.gdshader"); var a:=src.find("float ring_fft_outgoing_handoff"); var b:=src.find("void vertex()",a); var helpers:=src.substr(a,b-a)
	return """shader_type canvas_item;
uniform float detail_filtering_strength=1.; uniform vec3 camera_world_position; uniform float ring_radius_meters; uniform float test_distance; uniform int test_output_mode; uniform bool geomorph_enabled; uniform float morph_start_meters; uniform float morph_end_meters;
#include "res://materials/ring_water_fft.gdshaderinc"
vec2 ring_fft_distant_ocean_factors(vec2 uv){return vec2(1.);}
"""+helpers+"""
vec3 ref_corner(vec3 s,vec3 u,vec2 o,vec2 uv){if(fft_geometry_parent_spacing_meters<=.001)return vec3(0);float r=max(1.,ring_radius_meters);vec3 t=normalize(cross(fft_across_axis,u));float a=o.x/r;vec3 cs=s+t*(r*sin(a))+u*(r*2.*sin(a*.5)*sin(a*.5))+fft_across_axis*o.y;vec3 cu=normalize(u*cos(a)-t*sin(a));vec2 sh=ring_fft_shoreline_factors(ring_fft_canonical(cs),ring_fft_distant_ocean_factors(uv));vec3 d=ring_fft_geometry_displacement_for_spacing(ring_fft_canonical(cs),fft_geometry_parent_spacing_meters)*vec3(sh.y,sh.x,sh.y);return(fft_around_axis*d.x+cu*d.y+fft_across_axis*d.z)*ring_fft_geometry_fade(distance(cs,camera_world_position));}
vec3 ref_parent(vec3 s,vec3 u,vec2 uv){if(fft_geometry_parent_spacing_meters<=.001)return vec3(0);float q=fft_geometry_parent_spacing_meters;vec2 x=uv*proxy_sector_size_meters,lo=floor(x/q)*q,f=clamp((x-lo)/q,vec2(0),vec2(1)),a=lo-x,b=a+vec2(q,0),c=a+vec2(0,q),d=a+vec2(q),k=vec2(1./proxy_sector_size_meters);if(f.x+f.y<=1.)return ref_corner(s,u,a,uv+a*k)*(1.-f.x-f.y)+ref_corner(s,u,b,uv+b*k)*f.x+ref_corner(s,u,c,uv+c*k)*f.y;return ref_corner(s,u,d,uv+d*k)*(f.x+f.y-1.)+ref_corner(s,u,c,uv+c*k)*(1.-f.x)+ref_corner(s,u,b,uv+b*k)*(1.-f.y);}
void fragment() {
	// Exactly include lattice corners and halfway edge vertices, rather than
	// relying on pixel-centre UVs to happen to touch a shared edge.
	vec2 mesh_uv = floor(UV * 64.0 + vec2(0.5)) / 64.0;
	vec3 u = vec3(0, 0, 1);
	vec3 s = fft_anchor_world + fft_around_axis * (100.0 + mesh_uv.x * proxy_sector_size_meters)
		+ fft_across_axis * (50.0 + mesh_uv.y * proxy_sector_size_meters);
	vec2 sh = ring_fft_shoreline_factors(ring_fft_canonical(s), vec2(1));
	vec3 d = ring_fft_geometry_displacement(ring_fft_canonical(s)) * vec3(sh.y, sh.x, sh.y);
	vec3 own = (fft_around_axis * d.x + u * d.y + fft_across_axis * d.z)
		* ring_fft_geometry_fade(distance(s, camera_world_position));
	float h = ring_fft_outgoing_handoff(test_distance);
	// Expected endpoint is independent of the helper under test.
	float expected_h = smoothstep(100.0, 198.0, test_distance);
	vec3 actual = mix(own, ring_fft_parent_triangle_displacement_world(s, u, mesh_uv), h);
	vec3 expect = mix(own, ref_parent(s, u, mesh_uv), expected_h);
	vec3 outv = test_output_mode == 1 ? actual : (test_output_mode == 2 ? own : actual - expect);
	COLOR = vec4(outv / """+str(SCALE)+""" + vec3(0.5), 1.0);
}
"""

func _fft() -> Texture2DArray:
	var images:Array[Image]=[]
	for l: int in 2:
		var im:=Image.create(128,128,true,Image.FORMAT_RGBAF)
		for y: int in 128:
			for x: int in 128:
				var u:=TAU*x/128.;var v:=TAU*y/128.;im.set_pixel(x,y,Color(.7*sin(7*u+3*v),1.4*cos(11*u-5*v)+.6*sin(23*u+17*v),.65*cos(9*v-4*u),1))
		im.generate_mipmaps();images.append(im)
	var t:=Texture2DArray.new();return t if t.create_from_images(images)==OK else null

func _finite(im:Image)->bool:
	if im==null or im.is_empty(): return false
	for y:int in range(0,im.get_height(),8):
		for x:int in range(0,im.get_width(),8):
			var p:=im.get_pixel(x,y)
			if is_nan(p.r)or is_nan(p.g)or is_nan(p.b)or is_inf(p.r)or is_inf(p.g)or is_inf(p.b): return false
	return true
func _error(im:Image)->float:return _delta(im,_flat(im))
func _amplitude(im:Image)->float:return _error(im)
func _flat(im:Image)->Image:
	var x:=Image.create(im.get_width(),im.get_height(),false,im.get_format());x.fill(Color(.5,.5,.5,1));return x
func _delta(a:Image,b:Image)->float:
	var r:=0.
	for y:int in a.get_height():
		for x:int in a.get_width():
			r=maxf(r,(Vector3(a.get_pixel(x,y).r,a.get_pixel(x,y).g,a.get_pixel(x,y).b)-Vector3(b.get_pixel(x,y).r,b.get_pixel(x,y).g,b.get_pixel(x,y).b)).length()*SCALE)
	return r
func _fail(s:String)->void:push_error("WATER_FFT_LOD_SEAM_GPU_TEST=FAIL "+s);quit(1)
