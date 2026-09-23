extends SceneTree

# Local-RD A/B guard for the native four-tap blur. Run with a display GPU.
func _init() -> void:
	var post := FileAccess.get_file_as_string("res://addons/SunshineClouds2/SunshineCloudsPostCompute.comp")
	var begin := post.find("float w0")
	var end := post.find("vec4 radialBlurData", begin)
	var filter_begin := post.find("if (blurPower > 0.0 && genericData.data.temporal_controls.z <= 0.5)")
	var filter_end := post.find("// Convert screen coordinates", filter_begin)
	if begin < 0 or end < begin or filter_begin < 0 or filter_end < filter_begin:
		fail("production blur helper or bypass condition changed; update this A/B fixture")
		return
	var rd := RenderingServer.create_local_rendering_device()
	if rd == null:
		fail("Local RenderingDevice unavailable; run from a normal display session.")
		return
	var helper := post.substr(begin, end - begin)
	var production_filter := post.substr(filter_begin, filter_end - filter_begin)
	production_filter = production_filter.replace("genericData.data.temporal_controls.z", "testRawMode")
	production_filter = production_filter.replace("genericData.data.menu_optimization_controls.x", "testMenuFast")
	production_filter = production_filter.replace("genericData.data.blurQuality", "1.0")
	production_filter = production_filter.replace("input_color_image", "inputTex")
	production_filter = production_filter.replace("input_data_image", "inputTex")
	production_filter = production_filter.replace("lowres_sizefloat", "size")
	# The fixture intentionally compiles the exact production bicubic/radial helper.
	# Cases use off-axis low-res UV phases to exercise the actual upscale path.
	var src := "#version 450\n" + helper + "\nlayout(local_size_x=8,local_size_y=8,local_size_z=1)in; layout(set=0,binding=0)uniform sampler2D inputTex; layout(set=0,binding=1,std430)buffer O{vec4 o[];}; void main(){uvec3 id=gl_GlobalInvocationID; if(id.x>=64u||id.y>=64u||id.z>=7u)return; vec2 size=vec2(64); int resolutionScale=(id.z==1u||id.z==2u||id.z==4u)?2:(id.z==3u?4:1); vec2 accumUV=((vec2(id.xy)+vec2(.25,.75))/size); vec4 start=texture(inputTex,accumUV); vec4 currentAccumilation=start; float blurPower=id.z==4u?.5:(id.z==6u?0.:.1); float testMenuFast=(id.z==2u||id.z==3u||id.z==4u)?1.:0.; float testRawMode=id.z==5u?1.:0.; vec4 old=radialBlurColor(start,inputTex,inputTex,accumUV,size,4.,max(blurPower,.1)/size.y,max(blurPower,.1)/size.x,1.);" + production_filter + "uint n=id.z*4096u+id.y*64u+id.x; o[n*3u]=start;o[n*3u+1u]=old;o[n*3u+2u]=currentAccumilation;}\n"
	var ss := RDShaderSource.new()
	ss.source_compute = src
	var sp := rd.shader_compile_spirv_from_source(ss)
	if sp.compile_error_compute != "":
		rd.free()
		fail(sp.compile_error_compute)
		return
	var pixels:=PackedFloat32Array()
	for y in 64:
		for x in 64:
			var v:float=sin(float(x)*.2)*.25+sin(float(y)*.13)*.25+(1.0 if x>31 else 0.0)
			pixels.append_array([v,v*.7,v*.4,1.])
	var fmt:=RDTextureFormat.new();fmt.width=64;fmt.height=64;fmt.format=RenderingDevice.DATA_FORMAT_R32G32B32A32_SFLOAT;fmt.usage_bits=RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT
	var tex:=rd.texture_create(fmt,RDTextureView.new(),[pixels.to_byte_array()]);var state:=RDSamplerState.new();state.min_filter=RenderingDevice.SAMPLER_FILTER_LINEAR;state.mag_filter=RenderingDevice.SAMPLER_FILTER_LINEAR;state.repeat_u=RenderingDevice.SAMPLER_REPEAT_MODE_CLAMP_TO_EDGE;state.repeat_v=RenderingDevice.SAMPLER_REPEAT_MODE_CLAMP_TO_EDGE;var samp:=rd.sampler_create(state)
	var shader := rd.shader_create_from_spirv(sp); var out := rd.storage_buffer_create(64*64*7*3*16)
	var u0:=RDUniform.new();u0.uniform_type=RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE;u0.binding=0;u0.add_id(samp);u0.add_id(tex)
	var u1:=RDUniform.new();u1.uniform_type=RenderingDevice.UNIFORM_TYPE_STORAGE_BUFFER;u1.binding=1;u1.add_id(out)
	var set:=rd.uniform_set_create([u0,u1],shader,0); var pipe:=rd.compute_pipeline_create(shader)
	var cl:=rd.compute_list_begin(); rd.compute_list_bind_compute_pipeline(cl,pipe); rd.compute_list_bind_uniform_set(cl,set,0); rd.compute_list_dispatch(cl,8,8,7); rd.compute_list_end(); rd.submit(); rd.sync()
	var values:=rd.buffer_get_data(out).to_float32_array(); rd.free_rid(pipe);rd.free_rid(set);rd.free_rid(out);rd.free_rid(samp);rd.free_rid(tex);rd.free_rid(shader);rd.free()
	for case_index in 7:
		var old_new_total:float = 0.0
		var old_new_maximum:float = 0.0
		var start_new_total:float = 0.0
		var start_new_maximum:float = 0.0
		for pixel in 4096:
			for channel in 4:
				var index:int = (case_index * 4096 + pixel) * 12 + channel
				var start:float = values[index]
				var old:float = values[index + 4]
				var fresh:float = values[index + 8]
				if not is_finite(start) or not is_finite(old) or not is_finite(fresh):
					fail("nonfinite A/B output")
					return
				var expected:float = start if case_index in [5, 6] else old
				if case_index in [1, 4, 5, 6] and absf(fresh - expected) >= 0.000001:
					fail("incorrect filter result for case %d" % case_index)
					return
				var old_new_delta:float = absf(old - fresh)
				var start_new_delta:float = absf(start - fresh)
				old_new_total += old_new_delta
				old_new_maximum = maxf(old_new_maximum, old_new_delta)
				start_new_total += start_new_delta
				start_new_maximum = maxf(start_new_maximum, start_new_delta)
		if case_index in [0, 2, 3] and old_new_maximum >= 0.01:
			fail("native cheap reconstruction differs from radial reference by >= .01")
			return
		if case_index < 5 and start_new_maximum <= 0.000001:
			fail("blur kernel produced no measurable delta for case %d" % case_index)
			return
		print("Blur case %d: old-new mean=%f max=%f; start-new mean=%f max=%f" % [case_index, old_new_total / (4096.0 * 4.0), old_new_maximum, start_new_total / (4096.0 * 4.0), start_new_maximum])
	print("Sunshine blur GPU fixture PASS")
	quit()
func fail(s:String)->void: push_error(s);quit(1)
