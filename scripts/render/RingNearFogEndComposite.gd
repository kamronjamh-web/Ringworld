extends CompositorEffect
## Near ground fog, fog end (NF-4; docs/sun-flare-shafts-near-fog-plan.md, NF-1 design N5). POST_TRANSPARENT, attached by RingNearFog only while the fog is active, ordered after Sunshine, the
## lightning channels, the containment field and the rainbow and before shock distortion, rain streaks, rain haze and the lens effects (RingNearFog.EnsureCompositorSlot).
## Two compute passes in one list from materials/ring_near_fog_end.comp (compiled here at run time from the source text RingNearFog hands over, includes already expanded):
## pass A at 1/8 resolution (T_end and S_end through the fog window, the froxels' density and lighting), pass B at full resolution applied to the resolved HDR colour.
## Main view only (more than one view = XR: skipped). No dispatch at all while inactive. No CPU readback in production (request_state_readback is a test hook).
## Payload (156 floats, RingNearFog.UpdateEnd): frame matrix [0..15], then vec4s: sun (xyz toward the sun, w anisotropy), radiance (rgb, w albedo), ambient (rgb A, the ambient in-scatter radiance), drive (xyz, w amount x gate),
## layer (H, L, Leff, debug sigma), noise offset, shape, fields params, T0 bounds, ref world, ref frame, water frame, flags (x main-empty pixels, y cloud correction, z paint the main-empty mask, w perf hook: skip pass B),
## then the NF-5 lighting inputs [68..155] (haze0, haze1, light0, light1, light2 = sun colour x energy x pi x volumetric energy + g, the eight height-shadow globals, the cloud-shadow cascade block of seven vec4s, then the planet light direction and radiance). Keep in sync with the .comp.
const PAYLOAD_FLOATS := 156
const UBO_FLOATS := 216   # proj, inv_proj, cam (48), size (4), payload (156), camera - ref (4), Sunshine min / max step (4)
const UBO_SIZE := UBO_FLOATS * 4
const BLOCK := 8
var _mutex := Mutex.new()
var _payload := PackedFloat32Array()
var _active := false
var _t0 := RID()
var _basin := RID()
var _water := RID()
var _hs_l0 := RID()
var _hs_l1 := RID()
var _cloud_near := RID()
var _src_a := ""
var _src_b := ""
var _src_c := ""
var shader_c: RID
var pipeline_c: RID
var snap_pre: RID
var snap_post: RID
var snap_size := Vector2i.ZERO
var _sunshine_ref: WeakRef
var _readback_requested := false
var _last_end := PackedFloat32Array()
var _last_frames := {}
var ready := false
var dispatches := 0
var last_error := ""
var rd: RenderingDevice
var shader_a: RID
var shader_b: RID
var pipeline_a: RID
var pipeline_b: RID
var linear_sampler: RID
var nearest_sampler: RID
var ubo_buffer: RID
var fog_end_tex: RID
var fog_end_size := Vector2i.ZERO
var fallback_tex: RID
var fallback_array: RID
var _compiled_a := ""
var _compiled_b := ""

func _init() -> void:
	effect_callback_type = CompositorEffect.EFFECT_CALLBACK_TYPE_POST_TRANSPARENT
	access_resolved_color = true
	access_resolved_depth = true

## Shader source texts (includes expanded, #version first line), set once by RingNearFog; the PASS_A / PASS_B define is injected here.
func set_sources(source: String) -> void:
	_mutex.lock()
	_src_a = source.replace("#version 450\n", "#version 450\n#define PASS_A\n")
	_src_b = source.replace("#version 450\n", "#version 450\n#define PASS_B\n")
	_src_c = source.replace("#version 450\n", "#version 450\n#define PASS_COPY\n")
	_mutex.unlock()

## lattice0 / lattice1: the height-shadow arrays (RingTerrainHeightShadow.GetGlobalsSnapshot), cloud_near: the Sunshine near cloud-shadow cascade; invalid ones bind a harmless fallback (the payload says they are absent).
func publish_frame(payload: PackedFloat32Array, active: bool, t0: RID, basin: RID, water: RID, lattice0: RID = RID(), lattice1: RID = RID(), cloud_near: RID = RID()) -> void:
	_mutex.lock()
	_payload = payload; _active = active; _t0 = t0; _basin = basin; _water = water
	_hs_l0 = lattice0; _hs_l1 = lattice1; _cloud_near = cloud_near
	_mutex.unlock()

func set_sunshine(effect: Object) -> void:
	_mutex.lock()
	_sunshine_ref = weakref(effect) if effect != null else null
	_mutex.unlock()

## Test hook: the next dispatched frame reads back (a render-thread stall, two compute lists instead of one): pass A's image (4 floats per texel, S rgb then T, row-major, get_last_end),
## and the resolved colour (RGBA16F bytes) before and after pass B (get_last_frames: {pre, post, size, end_size}).
func request_state_readback() -> void:
	_mutex.lock()
	_readback_requested = true; _last_end = PackedFloat32Array(); _last_frames = {}
	_mutex.unlock()

func get_last_end() -> PackedFloat32Array:
	_mutex.lock()
	var out := _last_end
	_mutex.unlock()
	return out

func get_last_frames() -> Dictionary:
	_mutex.lock()
	var out := _last_frames
	_mutex.unlock()
	return out

func _notification(what: int) -> void:
	if what != NOTIFICATION_PREDELETE or rd == null: return
	var device := rd
	var resources: Array[RID] = [pipeline_a, pipeline_b, shader_a, shader_b, linear_sampler, nearest_sampler, ubo_buffer, fog_end_tex, fallback_tex, fallback_array]
	RenderingServer.call_on_render_thread(func():
		for rid in resources:
			if rid.is_valid(): device.free_rid(rid))
	ready = false

func _compile(source: String, label: String) -> RID:
	var src := RDShaderSource.new()
	src.language = RenderingDevice.SHADER_LANGUAGE_GLSL
	src.source_compute = source
	var spirv := rd.shader_compile_spirv_from_source(src)
	if spirv.compile_error_compute != "":
		last_error = label + " compile: " + spirv.compile_error_compute; push_error("near fog end " + last_error); return RID()
	var rid := rd.shader_create_from_spirv(spirv)
	if not rid.is_valid(): last_error = label + " shader create failed"
	return rid

func _ensure_resources(src_a: String, src_b: String) -> bool:
	if src_a == "" or src_b == "": return false
	if not (pipeline_a.is_valid() and pipeline_b.is_valid()) or src_a != _compiled_a or src_b != _compiled_b:
		if shader_a.is_valid(): rd.free_rid(shader_a)   # frees the pipeline with it
		if shader_b.is_valid(): rd.free_rid(shader_b)
		pipeline_a = RID(); pipeline_b = RID()
		_compiled_a = src_a; _compiled_b = src_b
		shader_a = _compile(src_a, "pass A")
		if not shader_a.is_valid(): return false
		shader_b = _compile(src_b, "pass B")
		if not shader_b.is_valid(): return false
		pipeline_a = rd.compute_pipeline_create(shader_a)
		pipeline_b = rd.compute_pipeline_create(shader_b)
	if not linear_sampler.is_valid():
		var linear := RDSamplerState.new(); linear.min_filter = RenderingDevice.SAMPLER_FILTER_LINEAR; linear.mag_filter = RenderingDevice.SAMPLER_FILTER_LINEAR
		linear.repeat_u = RenderingDevice.SAMPLER_REPEAT_MODE_CLAMP_TO_EDGE; linear.repeat_v = RenderingDevice.SAMPLER_REPEAT_MODE_CLAMP_TO_EDGE
		linear_sampler = rd.sampler_create(linear)
		nearest_sampler = rd.sampler_create(RDSamplerState.new())
		var format := RDTextureFormat.new()
		format.format = RenderingDevice.DATA_FORMAT_R32G32B32A32_SFLOAT; format.width = 1; format.height = 1
		format.usage_bits = RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT | RenderingDevice.TEXTURE_USAGE_CAN_UPDATE_BIT
		var zero := PackedByteArray(); zero.resize(16)
		fallback_tex = rd.texture_create(format, RDTextureView.new(), [zero])
		var array_format := RDTextureFormat.new()
		array_format.format = RenderingDevice.DATA_FORMAT_R32G32B32A32_SFLOAT; array_format.width = 1; array_format.height = 1
		array_format.texture_type = RenderingDevice.TEXTURE_TYPE_2D_ARRAY; array_format.array_layers = 2
		array_format.usage_bits = RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT | RenderingDevice.TEXTURE_USAGE_CAN_UPDATE_BIT
		fallback_array = rd.texture_create(array_format, RDTextureView.new(), [zero, zero])
		ubo_buffer = rd.uniform_buffer_create(UBO_SIZE)
	return pipeline_a.is_valid() and pipeline_b.is_valid() and ubo_buffer.is_valid()

func _ensure_fog_end(size: Vector2i) -> bool:
	if fog_end_tex.is_valid() and fog_end_size == size: return true
	if fog_end_tex.is_valid(): rd.free_rid(fog_end_tex)
	var format := RDTextureFormat.new()
	format.format = RenderingDevice.DATA_FORMAT_R16G16B16A16_SFLOAT; format.width = size.x; format.height = size.y
	format.usage_bits = RenderingDevice.TEXTURE_USAGE_STORAGE_BIT | RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT | RenderingDevice.TEXTURE_USAGE_CAN_COPY_FROM_BIT
	fog_end_tex = rd.texture_create(format, RDTextureView.new())
	fog_end_size = size
	return fog_end_tex.is_valid()

func _sampled(rid: RID) -> bool:
	return rid.is_valid() and rd.texture_is_valid(rid) and (rd.texture_get_format(rid).usage_bits & RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT) != 0

func _cloud_rids(sunshine: Object) -> Array:
	if sunshine == null or not bool(sunshine.get("enabled")): return []
	var textures = sunshine.get("accumulation_textures")
	if textures == null or textures.size() < 2: return []
	var data: RID = textures[0]; var color: RID = textures[1]
	if not _sampled(color) or not _sampled(data): return []
	return [color, data]

func _uniform(type: int, binding: int, ids: Array) -> RDUniform:
	var u := RDUniform.new(); u.uniform_type = type; u.binding = binding
	for id in ids: u.add_id(id)
	return u

func _render_callback(_type: int, render_data: RenderData) -> void:
	_mutex.lock()
	var payload := _payload; var active := _active
	var t0 := _t0; var basin := _basin; var water := _water
	var hs_l0 := _hs_l0; var hs_l1 := _hs_l1; var cloud_near := _cloud_near
	var src_a := _src_a; var src_b := _src_b; var src_c := _src_c
	var sunshine: Object = _sunshine_ref.get_ref() if _sunshine_ref != null else null
	var readback := _readback_requested
	_mutex.unlock()
	if not active or payload.size() != PAYLOAD_FLOATS: return
	if rd == null: rd = RenderingServer.get_rendering_device()
	if rd == null: return
	if not _ensure_resources(src_a, src_b): return
	var buffers := render_data.get_render_scene_buffers() as RenderSceneBuffersRD
	var scene := render_data.get_render_scene_data() as RenderSceneData
	if buffers == null or scene == null or buffers.get_view_count() != 1: return   # main view only (XR is skipped)
	var size := buffers.get_internal_size()
	if size.x <= 0 or size.y <= 0: return
	var color := buffers.get_color_layer(0, false)
	var depth := buffers.get_depth_layer(0, false)
	if not color.is_valid() or not depth.is_valid(): return
	if not (_sampled(t0) and _sampled(basin)): return   # the producer's fields are not up (or were recreated): the froxels see nothing either
	var water_rid := water if _sampled(water) else fallback_tex
	var l0_rid := hs_l0 if _sampled(hs_l0) else fallback_array
	var l1_rid := hs_l1 if _sampled(hs_l1) else fallback_array
	var cloud_rid := cloud_near if _sampled(cloud_near) else fallback_tex
	var asize := Vector2i((size.x + BLOCK - 1) / BLOCK, (size.y + BLOCK - 1) / BLOCK)
	if not _ensure_fog_end(asize): return
	ready = true
	var projection: Projection = scene.get_cam_projection()
	var inverse: Projection = projection.inverse()
	var camera: Transform3D = scene.get_cam_transform()
	var values := PackedFloat32Array(); values.resize(UBO_FLOATS)
	var k := 0
	for matrix in [projection, inverse]:
		for column in [matrix.x, matrix.y, matrix.z, matrix.w]:
			values[k] = column.x; values[k + 1] = column.y; values[k + 2] = column.z; values[k + 3] = column.w; k += 4
	for column in [camera.basis.x, camera.basis.y, camera.basis.z]:
		values[k] = column.x; values[k + 1] = column.y; values[k + 2] = column.z; values[k + 3] = 0.0; k += 4
	values[k] = camera.origin.x; values[k + 1] = camera.origin.y; values[k + 2] = camera.origin.z; values[k + 3] = 1.0; k += 4
	values[k] = float(size.x); values[k + 1] = float(size.y); values[k + 2] = float(asize.x); values[k + 3] = float(asize.y); k += 4
	for i in PAYLOAD_FLOATS:
		values[k + i] = payload[i]
	k += PAYLOAD_FLOATS
	# payload offsets: ref world = 52..55
	values[k] = camera.origin.x - payload[52]; values[k + 1] = camera.origin.y - payload[53]; values[k + 2] = camera.origin.z - payload[54]; values[k + 3] = 0.0
	k += 4
	if sunshine != null:
		values[k] = float(sunshine.get("min_step_distance")); values[k + 1] = maxf(float(sunshine.get("max_step_distance")), 1.0)
	var grid := float(ProjectSettings.get_setting("rendering/environment/volumetric_fog/volume_size", 64))
	values[k + 2] = grid; values[k + 3] = grid
	rd.buffer_update(ubo_buffer, 0, UBO_SIZE, values.to_byte_array())
	var cloud := _cloud_rids(sunshine) if payload[65] > 0.5 else []
	var ubo_a := _uniform(RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER, 0, [ubo_buffer])
	var img_a := _uniform(RenderingDevice.UNIFORM_TYPE_IMAGE, 1, [fog_end_tex])
	var set_a := UniformSetCacheRD.get_cache(shader_a, 0, [ubo_a, img_a,
		_uniform(RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE, 2, [nearest_sampler, t0]),
		_uniform(RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE, 3, [nearest_sampler, basin]),
		_uniform(RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE, 4, [linear_sampler, water_rid]),
		_uniform(RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE, 5, [nearest_sampler, l0_rid]),
		_uniform(RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE, 6, [nearest_sampler, l1_rid]),
		_uniform(RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE, 7, [linear_sampler, cloud_rid])])
	var cloud_color: RID = cloud[0] if cloud.size() == 2 else fallback_tex
	var cloud_data: RID = cloud[1] if cloud.size() == 2 else fallback_tex
	var set_b := UniformSetCacheRD.get_cache(shader_b, 0, [ubo_a,
		_uniform(RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE, 1, [linear_sampler, fog_end_tex]),
		_uniform(RenderingDevice.UNIFORM_TYPE_IMAGE, 2, [color]),
		_uniform(RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE, 3, [nearest_sampler, depth]),
		_uniform(RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE, 4, [linear_sampler, cloud_color]),
		_uniform(RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE, 5, [linear_sampler, cloud_data])])
	if not set_a.is_valid() or not set_b.is_valid(): return
	var list := rd.compute_list_begin()
	rd.compute_list_bind_compute_pipeline(list, pipeline_a)
	rd.compute_list_bind_uniform_set(list, set_a, 0)
	rd.compute_list_dispatch(list, (asize.x + 7) / 8, (asize.y + 7) / 8, 1)
	var pre := PackedByteArray()
	if readback:
		rd.compute_list_end()
		if not _snapshot(color, size, src_c, true): readback = false
		list = rd.compute_list_begin()
	else:
		rd.compute_list_add_barrier(list)
	if payload[67] < 0.5:   # (perf hook: flags.w skips the apply pass)
		rd.compute_list_bind_compute_pipeline(list, pipeline_b)
		rd.compute_list_bind_uniform_set(list, set_b, 0)
		rd.compute_list_dispatch(list, (size.x + 7) / 8, (size.y + 7) / 8, 1)
	rd.compute_list_end()
	dispatches += 1
	if readback:
		_snapshot(color, size, src_c, false)
		pre = rd.texture_get_data(snap_pre, 0)
		var raw := rd.texture_get_data(fog_end_tex, 0)
		var post := rd.texture_get_data(snap_post, 0)
		_mutex.lock()
		_last_end = _half_to_float(raw); _last_frames = {"pre": pre, "post": post, "size": size, "end_size": asize}; _readback_requested = false
		_mutex.unlock()

## Test hook: copies the resolved colour into snap_pre / snap_post (the colour buffer has no copy-from usage; the copy is a compute pass).
func _snapshot(color: RID, size: Vector2i, source: String, pre: bool) -> bool:
	if not (shader_c.is_valid() and pipeline_c.is_valid()):
		if source == "": return false
		shader_c = _compile(source, "copy pass")
		if not shader_c.is_valid(): return false
		pipeline_c = rd.compute_pipeline_create(shader_c)
	if not snap_pre.is_valid() or snap_size != size:
		for rid in [snap_pre, snap_post]:
			if rid.is_valid(): rd.free_rid(rid)
		var format := RDTextureFormat.new()
		format.format = RenderingDevice.DATA_FORMAT_R16G16B16A16_SFLOAT; format.width = size.x; format.height = size.y
		format.usage_bits = RenderingDevice.TEXTURE_USAGE_STORAGE_BIT | RenderingDevice.TEXTURE_USAGE_CAN_COPY_FROM_BIT
		snap_pre = rd.texture_create(format, RDTextureView.new())
		snap_post = rd.texture_create(format, RDTextureView.new())
		snap_size = size
	var target := snap_pre if pre else snap_post
	var set_c := UniformSetCacheRD.get_cache(shader_c, 0, [_uniform(RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER, 0, [ubo_buffer]), _uniform(RenderingDevice.UNIFORM_TYPE_IMAGE, 1, [color]), _uniform(RenderingDevice.UNIFORM_TYPE_IMAGE, 2, [target])])
	if not set_c.is_valid(): return false
	var list := rd.compute_list_begin()
	rd.compute_list_bind_compute_pipeline(list, pipeline_c)
	rd.compute_list_bind_uniform_set(list, set_c, 0)
	rd.compute_list_dispatch(list, (size.x + 7) / 8, (size.y + 7) / 8, 1)
	rd.compute_list_end()
	return true

## RGBA16F bytes -> floats.
func _half_to_float(raw: PackedByteArray) -> PackedFloat32Array:
	var out := PackedFloat32Array(); out.resize(raw.size() / 2)
	for i in out.size():
		out[i] = raw.decode_half(i * 2)
	return out
