extends SceneTree

# Run on a normal GPU-capable display session. This deliberately creates a
# local RenderingDevice and fails clearly when that is unavailable.
const CASES := 4096

func _init() -> void:
	var rd := RenderingServer.create_local_rendering_device()
	if rd == null:
		fail("Local RenderingDevice unavailable; run this GPU fixture from a normal display session.")
		return
	var helper := FileAccess.get_file_as_string("res://addons/SunshineClouds2/SunshineCloudsEntrySkip.glslinc")
	if helper.is_empty():
		rd.free()
		fail("could not read production entry-skip helper")
		return
	var source := "#version 450\n" + helper + "\nlayout(local_size_x=1) in;\n" + \
		"layout(set=0,binding=0,std430) readonly buffer In { vec4 d[]; } input_data;\n" + \
		"layout(set=0,binding=1,std430) writeonly buffer Out { int s[]; } output_data;\n" + \
		"float old_alt(vec3 p,vec3 ar,vec3 up,float r){return r-length(vec2(dot(p,ar),r-dot(p,up)));}\nvoid main(){ uint i=gl_GlobalInvocationID.x; vec4 a=input_data.d[i*6u]; vec4 b=input_data.d[i*6u+1u]; vec4 c=input_data.d[i*6u+2u]; vec4 e=input_data.d[i*6u+3u]; vec4 up=input_data.d[i*6u+4u]; vec4 ar=input_data.d[i*6u+5u]; int n=int(c.y); int skip=cloud_entry_skip_steps(a.xyz,b.xyz,ar.xyz,up.xyz,a.w,c.w,e.x,b.w,c.x,n,e.y); int bad=(skip<0||skip>=n)?1:0; int stop=n; float d=b.w; for(int k=0;k<n;k++,d+=c.x){ float h=old_alt(a.xyz+b.xyz*d,ar.xyz,up.xyz,a.w); float hn=old_alt(a.xyz+b.xyz*(d+c.x),ar.xyz,up.xyz,a.w); bool exit=(h>e.x&&hn>h)||(h<c.w&&hn<h); if(d>e.y||exit){stop=k;break;} if(k<skip && h>=c.w && h<=e.x) bad=1; } if(skip>stop) bad=1; output_data.s[i*3u]=skip; output_data.s[i*3u+1u]=bad; output_data.s[i*3u+2u]=stop; }\n"
	var shader_source := RDShaderSource.new()
	shader_source.source_compute = source
	var spirv := rd.shader_compile_spirv_from_source(shader_source)
	if spirv.compile_error_compute != "":
		rd.free()
		fail("entry helper GPU compile failed: " + spirv.compile_error_compute)
		return
	var shader := rd.shader_create_from_spirv(spirv)
	var input := PackedFloat32Array()
	# Six vec4s: origin/radius, direction/jitter, step/count/altitude/floor,
	# ceiling/depth, up basis, and around basis.
	var rng := RandomNumberGenerator.new()
	rng.seed = 918273
	for i in CASES:
		var radius := 80000.0 if i % 2 == 0 else 5000000.0
		var altitude := rng.randf_range(-2000.0, 4000.0)
		var direction := Vector3(rng.randf_range(-.2,.2), rng.randf_range(-1.,1.), rng.randf_range(-.2,.2)).normalized()
		var jitter := rng.randf_range(0., 1000.)
		if i == 0: altitude = 499.; direction = Vector3.UP; jitter = 400. # jitter-crossing control
		if i == 1: altitude = -5000.; direction = Vector3.UP; jitter = 0. # positive skip control
		if i == 2: altitude = 1000.; direction = Vector3.UP # in-band control
		if i == 3: direction = Vector3(0,0,1) # axis/tangent control
		var angle := rng.randf_range(-PI, PI)
		var around := Vector3(cos(angle), 0, sin(angle))
		var up := Vector3.UP
		var count := 1.0 if i == 4 else float(32 + (i % 96))
		var depth := 300.0 if i == 5 else rng.randf_range(2000., 1000000.)
		input.append_array([0.0, altitude, 0.0, radius, direction.x, direction.y, direction.z, jitter, 500.0 + float(i % 4) * 500.0, count, altitude, 500.0, 2000.0, depth, 0.0, 0.0, up.x, up.y, up.z, 0.0, around.x, around.y, around.z, 0.0])
	var in_buffer := rd.storage_buffer_create(input.size() * 4, input.to_byte_array())
	var out_buffer := rd.storage_buffer_create(CASES * 12)
	var u0 := RDUniform.new(); u0.uniform_type = RenderingDevice.UNIFORM_TYPE_STORAGE_BUFFER; u0.binding = 0; u0.add_id(in_buffer)
	var u1 := RDUniform.new(); u1.uniform_type = RenderingDevice.UNIFORM_TYPE_STORAGE_BUFFER; u1.binding = 1; u1.add_id(out_buffer)
	var set := rd.uniform_set_create([u0, u1], shader, 0)
	var pipeline := rd.compute_pipeline_create(shader)
	var list := rd.compute_list_begin(); rd.compute_list_bind_compute_pipeline(list, pipeline); rd.compute_list_bind_uniform_set(list, set, 0); rd.compute_list_dispatch(list, CASES, 1, 1); rd.compute_list_end(); rd.submit(); rd.sync()
	var data := rd.buffer_get_data(out_buffer).to_int32_array()
	rd.free_rid(pipeline); rd.free_rid(set); rd.free_rid(in_buffer); rd.free_rid(out_buffer); rd.free_rid(shader); rd.free()
	var skipped := false
	for i in CASES:
		var value := data[i * 3]
		if value < 0 or value > 127: fail("GPU skip outside budget at %d" % i); return
		if data[i * 3 + 1] != 0: fail("GPU helper skipped original in-band/terminal lattice sample at %d" % i); return
		if value > 0: skipped = true
	if data[0] != 0: fail("jitter crossing control skipped an already in-band first sample"); return
	if data[3] <= 0: fail("positive control did not skip"); return
	if not skipped: fail("vacuous fixture: no case skipped"); return
	print("Sunshine entry-skip GPU fixture: PASS (%d cases)" % CASES)
	quit(0)

func fail(message: String) -> void:
	push_error(message)
	quit(1)
