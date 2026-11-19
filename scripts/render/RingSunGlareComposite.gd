extends CompositorEffect
## Sun veiling glare (LF-1; docs/sun-flare-shafts-near-fog-plan.md section A). POST_TRANSPARENT, ordered LAST ("lens effects last", RingLocalWeatherEffects.EnsureLensEffectsLast):
## the rain streaks and rain haze are in the scene the lens sees. Two compute passes in one list: ring_sun_visibility.glsl (one 64-thread group: visible sun fraction F from main depth, the
## scaled far viewport's depth and Sunshine's live accumulation alpha, plus the displayed disc radiance L measured from the resolved HDR colour) and ring_sun_glare.glsl (the CIE 1999
## glare function drawn additively in a square window around the sun). Main view only (more than one view = XR: skipped). No dispatch at all while inactive (strength 0, sun off-screen
## or absent); no CPU readback in production (request_state_readback is a test hook).
## Params (16 floats, RingSunGlare.cs): [0..2] sun direction (render frame), [3] disc radius (rad), [4..6] fallback sun radiance, [8] eclipse coverage, [9] storm_hide, [10] enclosure gate,
## [11] camera cloud transmittance, [12] strength, [13] source gain, [14] window half-angle theta_max (rad). UBO (std140) 224 bytes: proj, inv_proj, viewport, sun_view, fallback, scalars, draw.
## State buffer (80 bytes, persistent): result (F, F_taps, clean disc, clean annulus), hold (L_hold rgb, init), debug, meas, oracle (test image oracle). Keep in sync with the two shaders.
const PARAM_FLOATS := 16
const UBO_SIZE := 224
const STATE_BYTES := 80
var _mutex := Mutex.new()
var _params := PackedFloat32Array()
var _active := false
var _sunshine_ref: WeakRef
var _readback_requested := false
var _last_state := PackedFloat32Array()
var _last_window := Rect2i()
var ready := false
var dispatches := 0
var last_error := ""
var rd: RenderingDevice
var vis_shader: RID
var vis_pipeline: RID
var glare_shader: RID
var glare_pipeline: RID
var depth_sampler: RID
var linear_sampler: RID
var ubo_buffer: RID
var state_buffer: RID
var fallback_tex: RID

func _init() -> void:
	effect_callback_type = CompositorEffect.EFFECT_CALLBACK_TYPE_POST_TRANSPARENT
	access_resolved_color = true
	access_resolved_depth = true

func publish_frame(params: PackedFloat32Array, active: bool) -> void:
	_mutex.lock()
	_params = params; _active = active
	_mutex.unlock()

func set_sunshine(effect: Object) -> void:
	_mutex.lock()
	_sunshine_ref = weakref(effect) if effect != null else null
	_mutex.unlock()

## Test hook: the next dispatched frame reads the state buffer back (a render-thread stall). Never used in production.
func request_state_readback() -> void:
	_mutex.lock()
	_readback_requested = true; _last_state = PackedFloat32Array()
	_mutex.unlock()

## 20 floats of the state buffer (result, hold, debug, meas, oracle), empty until a requested readback arrived.
func get_last_state() -> PackedFloat32Array:
	_mutex.lock()
	var state := _last_state
	_mutex.unlock()
	return state

## The pixel window the draw pass last covered (position, size); zero size when the draw was skipped.
func get_last_window() -> Rect2i:
	_mutex.lock()
	var window := _last_window
	_mutex.unlock()
	return window

func _notification(what: int) -> void:
	if what != NOTIFICATION_PREDELETE or rd == null: return
	var device := rd
	var resources: Array[RID] = [vis_pipeline, vis_shader, glare_pipeline, glare_shader, depth_sampler, linear_sampler, ubo_buffer, state_buffer, fallback_tex]
	RenderingServer.call_on_render_thread(func():
		for rid in resources:
			if rid.is_valid(): device.free_rid(rid))
	ready = false

func _load_compute(path: String) -> RID:
	var file: RDShaderFile = load(path)
	if file == null: last_error = "shader file missing: " + path; return RID()
	var spirv := file.get_spirv()
	if spirv.compile_error_compute != "":
		last_error = "compile: " + spirv.compile_error_compute; push_error(path.get_file() + " " + last_error); return RID()
	var rid := rd.shader_create_from_spirv(spirv)
	if not rid.is_valid(): last_error = "shader create failed: " + path
	return rid

func _ensure_resources() -> bool:
	if vis_pipeline.is_valid() and glare_pipeline.is_valid(): return true
	vis_shader = _load_compute("res://scripts/render/ring_sun_visibility.glsl")
	if not vis_shader.is_valid(): return false
	glare_shader = _load_compute("res://scripts/render/ring_sun_glare.glsl")
	if not glare_shader.is_valid(): return false
	vis_pipeline = rd.compute_pipeline_create(vis_shader)
	glare_pipeline = rd.compute_pipeline_create(glare_shader)
	depth_sampler = rd.sampler_create(RDSamplerState.new())
	var linear := RDSamplerState.new(); linear.min_filter = RenderingDevice.SAMPLER_FILTER_LINEAR; linear.mag_filter = RenderingDevice.SAMPLER_FILTER_LINEAR
	linear_sampler = rd.sampler_create(linear)
	var format := RDTextureFormat.new()
	format.format = RenderingDevice.DATA_FORMAT_R32G32B32A32_SFLOAT; format.width = 1; format.height = 1
	format.usage_bits = RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT | RenderingDevice.TEXTURE_USAGE_CAN_UPDATE_BIT
	var zero := PackedByteArray(); zero.resize(16)
	fallback_tex = rd.texture_create(format, RDTextureView.new(), [zero])
	ubo_buffer = rd.uniform_buffer_create(UBO_SIZE)
	var state := PackedByteArray(); state.resize(STATE_BYTES)   # zero: hold.w = 0 means "initialise L_hold from the fallback"
	state_buffer = rd.storage_buffer_create(STATE_BYTES, state)
	return vis_pipeline.is_valid() and glare_pipeline.is_valid() and ubo_buffer.is_valid() and state_buffer.is_valid()

func _far_depth_rid(sunshine: Object) -> RID:
	if sunshine == null: return RID()
	var tex = sunshine.get("far_opaque_depth_texture")
	if tex == null or not (tex is Texture2DRD): return RID()
	var rid: RID = (tex as Texture2DRD).texture_rd_rid
	if not rid.is_valid() or not rd.texture_is_valid(rid): return RID()
	return rid

func _cloud_color_rid(sunshine: Object, view: int) -> RID:
	if sunshine == null or not bool(sunshine.get("enabled")): return RID()
	var textures = sunshine.get("accumulation_textures")
	if textures == null or textures.size() < view * 7 + 2: return RID()
	var color: RID = textures[view * 7 + 1]
	if not color.is_valid() or not rd.texture_is_valid(color): return RID()
	if not (rd.texture_get_format(color).usage_bits & RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT): return RID()
	return color

func _render_callback(_type: int, render_data: RenderData) -> void:
	_mutex.lock()
	var params := _params; var active := _active
	var sunshine: Object = _sunshine_ref.get_ref() if _sunshine_ref != null else null
	var readback := _readback_requested
	_mutex.unlock()
	if not active or params.size() != PARAM_FLOATS: return
	if rd == null: rd = RenderingServer.get_rendering_device()
	if rd == null: return
	if not _ensure_resources(): return
	ready = true
	var buffers := render_data.get_render_scene_buffers() as RenderSceneBuffersRD
	var scene := render_data.get_render_scene_data() as RenderSceneData
	if buffers == null or scene == null or buffers.get_view_count() != 1: return   # main view only (XR is skipped)
	var size := buffers.get_internal_size()
	if size.x <= 0 or size.y <= 0: return
	var color := buffers.get_color_layer(0, false)
	var depth := buffers.get_depth_layer(0, false)
	if not color.is_valid() or not depth.is_valid(): return
	var projection: Projection = scene.get_cam_projection()
	var inverse: Projection = projection.inverse()
	var camera: Transform3D = scene.get_cam_transform()
	var sun_world := Vector3(params[0], params[1], params[2]).normalized()
	var sun_view := Vector3(sun_world.dot(camera.basis.x), sun_world.dot(camera.basis.y), sun_world.dot(camera.basis.z))
	var bytes := PackedByteArray(); bytes.resize(UBO_SIZE)
	var offset := 0
	for matrix in [projection, inverse]:
		for column in [matrix.x, matrix.y, matrix.z, matrix.w]:
			for value in [column.x, column.y, column.z, column.w]: bytes.encode_float(offset, value); offset += 4
	for value in [float(size.x), float(size.y), 0.0, 0.0, sun_view.x, sun_view.y, sun_view.z, params[3], params[4], params[5], params[6], 0.0,
			params[8], params[9], params[10], params[11], params[12], params[13], params[14], 0.0]:
		bytes.encode_float(offset, value); offset += 4
	rd.buffer_update(ubo_buffer, 0, UBO_SIZE, bytes)
	# Draw window: a square around the sun's pixel, half-size = tan(theta_max) in pixels (the larger of the two focal lengths), clamped to the viewport.
	var window := Rect2i()
	var clip := projection * Vector4(sun_view.x, sun_view.y, sun_view.z, 1.0)
	if sun_view.z < 0.0 and clip.w > 1.0e-6:
		var centre := Vector2((clip.x / clip.w * 0.5 + 0.5) * size.x, (clip.y / clip.w * 0.5 + 0.5) * size.y)
		var focal := maxf(absf(projection.x.x) * size.x * 0.5, absf(projection.y.y) * size.y * 0.5)
		var half := minf(tan(minf(params[14], 1.5)) * focal, 16384.0)
		var low := Vector2i((centre - Vector2(half, half)).floor())
		var high := Vector2i((centre + Vector2(half, half)).ceil())
		low = low.clamp(Vector2i.ZERO, size); high = high.clamp(Vector2i.ZERO, size)
		window = Rect2i(low, high - low)
	_mutex.lock()
	_last_window = window
	_mutex.unlock()
	var ubo := RDUniform.new(); ubo.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER; ubo.binding = 0; ubo.add_id(ubo_buffer)
	var d := RDUniform.new(); d.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE; d.binding = 1; d.add_id(depth_sampler); d.add_id(depth)
	var cloud := _cloud_color_rid(sunshine, 0)
	var c := RDUniform.new(); c.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE; c.binding = 2; c.add_id(linear_sampler); c.add_id(cloud if cloud.is_valid() else fallback_tex)
	var far_rid := _far_depth_rid(sunshine)
	var f := RDUniform.new(); f.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE; f.binding = 3; f.add_id(depth_sampler); f.add_id(far_rid if far_rid.is_valid() else fallback_tex)
	var img := RDUniform.new(); img.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE; img.binding = 4; img.add_id(color)
	var st := RDUniform.new(); st.uniform_type = RenderingDevice.UNIFORM_TYPE_STORAGE_BUFFER; st.binding = 5; st.add_id(state_buffer)
	var vis_set := UniformSetCacheRD.get_cache(vis_shader, 0, [ubo, d, c, f, img, st])
	var gubo := RDUniform.new(); gubo.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER; gubo.binding = 0; gubo.add_id(ubo_buffer)
	var gimg := RDUniform.new(); gimg.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE; gimg.binding = 1; gimg.add_id(color)
	var gst := RDUniform.new(); gst.uniform_type = RenderingDevice.UNIFORM_TYPE_STORAGE_BUFFER; gst.binding = 2; gst.add_id(state_buffer)
	var glare_set := UniformSetCacheRD.get_cache(glare_shader, 0, [gubo, gimg, gst])
	if not vis_set.is_valid() or not glare_set.is_valid(): return
	var list := rd.compute_list_begin()
	rd.compute_list_bind_compute_pipeline(list, vis_pipeline)
	rd.compute_list_bind_uniform_set(list, vis_set, 0)
	rd.compute_list_dispatch(list, 1, 1, 1)
	if window.size.x > 0 and window.size.y > 0:
		rd.compute_list_add_barrier(list)
		var push := PackedInt32Array([window.position.x, window.position.y, window.size.x, window.size.y]).to_byte_array()
		rd.compute_list_bind_compute_pipeline(list, glare_pipeline)
		rd.compute_list_bind_uniform_set(list, glare_set, 0)
		rd.compute_list_set_push_constant(list, push, push.size())
		rd.compute_list_dispatch(list, (window.size.x + 7) / 8, (window.size.y + 7) / 8, 1)
	rd.compute_list_end()
	dispatches += 1
	if readback:
		var raw := rd.buffer_get_data(state_buffer)
		_mutex.lock()
		_last_state = raw.to_float32_array(); _readback_requested = false
		_mutex.unlock()
