extends CompositorEffect
## Screen-space contact shadows (docs/shadow-revamp-plan.md S6). Runs after the depth prepass (PRE_OPAQUE) of the MAIN 3D view only and writes one
## R8 visibility texel per internal-resolution pixel; RingContactShadows.cs publishes the Texture2DRD global that receivers sample.
## Callbacks of other views that share the main world (minimap, capture SubViewports) are skipped by target size and camera transform.
var _mutex := Mutex.new()
var _enabled := false
var _sun_world := Vector3.UP
var _length := 1.5
var _thickness := 0.25
var _steps := 16
var _fade := 150.0
var _temporal := false
## Diagnostics: false keeps the march undithered even under FSR2 / TAA.
var dither_enabled := true
var _frame := 0
var _target_size := Vector2i.ZERO
var _camera_origin := Vector3.ZERO
var _camera_basis := Basis.IDENTITY
var _texture: Texture2DRD
var _reported_size := Vector2i.ZERO
var _reported_generation := 0
var _published_image := RID()
var _derived_far := 0.0
@export var gpu_timestamp_diagnostics_enabled := false
@export var gpu_timestamp_begin_marker := ""
@export var gpu_timestamp_end_marker := ""
var timestamp_scopes := 0
var ready := false
var dispatches := 0
var skipped_views := 0
var derived_far := 0.0 # |z/w| of NDC z = 0 under the inverse projection of the main view: the main-view fingerprint receivers compare against (the engine projection is infinite-far reverse-Z, so this is NOT Camera3D.far)
var failed := false
var texture_format := RenderingDevice.DATA_FORMAT_R8_UNORM
var rd: RenderingDevice
var shader: RID
var pipeline: RID
var buffer: RID
var sampler: RID
var image: RID
var image_size := Vector2i.ZERO
var set_rid: RID
var set_depth := RID()
var set_image := RID()
var retired: Array[Dictionary] = []

func _init() -> void:
	effect_callback_type = CompositorEffect.EFFECT_CALLBACK_TYPE_PRE_OPAQUE
	access_resolved_depth = true

func bind_texture(texture: Texture2DRD) -> void:
	_mutex.lock()
	_texture = texture
	_mutex.unlock()

## Called every frame from the main thread. camera is the main Camera3D global transform; target_size is the main viewport size in pixels.
func publish_frame(enabled: bool, sun_world: Vector3, length: float, thickness: float, steps: int, fade: float, temporal: bool, frame: int, target_size: Vector2i, camera: Transform3D) -> void:
	_mutex.lock()
	_enabled = enabled; _sun_world = sun_world; _length = length; _thickness = thickness; _steps = steps; _fade = fade
	_temporal = temporal; _frame = frame; _target_size = target_size
	_camera_origin = camera.origin; _camera_basis = camera.basis.orthonormalized()
	_mutex.unlock()

func publish_gpu_markers(enabled: bool, begin: String, end: String) -> void:
	_mutex.lock()
	gpu_timestamp_diagnostics_enabled = enabled
	gpu_timestamp_begin_marker = begin
	gpu_timestamp_end_marker = end
	_mutex.unlock()

## Internal size of the view the last dispatch wrote (zero until the first dispatch). The C# side publishes this as the receivers' key.
func get_reported_size() -> Vector2i:
	_mutex.lock()
	var size := _reported_size
	_mutex.unlock()
	return size

## The R8 image the last dispatch wrote. The host (main thread) binds it to the Texture2DRD global from a render-thread callable that runs between
## frames: replacing the texture inside this callback would invalidate the material uniform sets after they were resolved for the frame (draw errors).
func get_image_rid() -> RID:
	_mutex.lock()
	var rid := _published_image
	_mutex.unlock()
	return rid

## Far-plane distance recovered from the main view's inverse projection (the value the receivers recompute from INV_PROJECTION_MATRIX).
func get_derived_far() -> float:
	_mutex.lock()
	var far := _derived_far
	_mutex.unlock()
	return far

func get_reported_generation() -> int:
	_mutex.lock()
	var generation := _reported_generation
	_mutex.unlock()
	return generation

func get_texture_format() -> int:
	return texture_format

func _notification(what: int) -> void:
	if what != NOTIFICATION_PREDELETE or rd == null: return
	var device := rd
	var resources: Array[RID] = [set_rid, image, buffer, sampler, pipeline, shader]
	for item in retired:
		resources.append(item.get("set", RID())); resources.append(item.get("image", RID()))
	var texture := _texture
	RenderingServer.call_on_render_thread(func():
		if texture != null: texture.texture_rd_rid = RID()
		for rid in resources:
			if rid.is_valid(): device.free_rid(rid))
	retired.clear(); ready = false

func _retire(rid_set: RID, rid_image: RID) -> void:
	if rid_set.is_valid() or rid_image.is_valid():
		retired.append({"set": rid_set, "image": rid_image, "frames": max(2, rd.get_frame_delay() + 2)})

func _create_pipeline() -> bool:
	var source := RDShaderSource.new()
	source.language = RenderingDevice.SHADER_LANGUAGE_GLSL
	source.source_compute = FileAccess.get_file_as_string("res://materials/shadows/ring_contact_shadow.comp")
	if source.source_compute == "":
		failed = true; push_error("Contact shadows: shader source missing"); return false
	var spirv := rd.shader_compile_spirv_from_source(source)
	if spirv.compile_error_compute != "":
		failed = true; push_error("Contact shadows: compile error: " + spirv.compile_error_compute); return false
	shader = rd.shader_create_from_spirv(spirv)
	if not shader.is_valid():
		failed = true; push_error("Contact shadows: shader creation failed"); return false
	pipeline = rd.compute_pipeline_create(shader)
	if not pipeline.is_valid():
		failed = true; rd.free_rid(shader); shader = RID(); return false
	buffer = rd.uniform_buffer_create(176)
	sampler = rd.sampler_create(RDSamplerState.new())
	var usage := RenderingDevice.TEXTURE_USAGE_STORAGE_BIT | RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT | RenderingDevice.TEXTURE_USAGE_CAN_COPY_FROM_BIT
	if not rd.texture_is_format_supported_for_usage(RenderingDevice.DATA_FORMAT_R8_UNORM, usage):
		texture_format = RenderingDevice.DATA_FORMAT_R16_SFLOAT
	return true

func _create_image(size: Vector2i) -> RID:
	var format := RDTextureFormat.new()
	format.texture_type = RenderingDevice.TEXTURE_TYPE_2D
	format.format = texture_format
	format.width = size.x; format.height = size.y
	format.array_layers = 1; format.mipmaps = 1
	format.usage_bits = RenderingDevice.TEXTURE_USAGE_STORAGE_BIT | RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT | RenderingDevice.TEXTURE_USAGE_CAN_COPY_FROM_BIT
	return rd.texture_create(format, RDTextureView.new())

func _render_callback(_type: int, render_data: RenderData) -> void:
	if rd == null: rd = RenderingServer.get_rendering_device()
	if rd == null or failed: return
	for index in range(retired.size() - 1, -1, -1):
		retired[index]["frames"] -= 1
		if retired[index]["frames"] <= 0:
			var old_set: RID = retired[index]["set"]
			var old_image: RID = retired[index]["image"]
			if old_set.is_valid() and rd.uniform_set_is_valid(old_set): rd.free_rid(old_set)
			if old_image.is_valid(): rd.free_rid(old_image)
			retired.remove_at(index)
	if not pipeline.is_valid() and not _create_pipeline(): return
	_mutex.lock()
	var enabled := _enabled; var sun_world := _sun_world; var length := _length; var thickness := _thickness
	var steps := _steps; var fade := _fade; var temporal := _temporal; var frame_index := _frame
	var target_size := _target_size; var camera_origin := _camera_origin; var camera_basis := _camera_basis
	var texture := _texture
	var timestamps := gpu_timestamp_diagnostics_enabled
	var timestamp_begin := gpu_timestamp_begin_marker
	var timestamp_end := gpu_timestamp_end_marker
	_mutex.unlock()
	if not enabled or texture == null: return
	var buffers := render_data.get_render_scene_buffers() as RenderSceneBuffersRD
	var scene := render_data.get_render_scene_data() as RenderSceneData
	if buffers == null or scene == null: return
	if buffers.get_view_count() != 1: return
	var size := buffers.get_internal_size()
	if size.x <= 0 or size.y <= 0: return
	# Only the main view: same target size as the main viewport and a camera at the published pose (loose: the render thread may be a frame behind).
	var camera: Transform3D = scene.get_cam_transform()
	if buffers.get_target_size() != target_size or camera.origin.distance_to(camera_origin) > 8.0 or camera.basis.z.dot(camera_basis.z) < 0.999:
		skipped_views += 1
		return
	var depth := buffers.get_depth_layer(0, false)
	if not depth.is_valid(): return
	if size != image_size or not image.is_valid():
		_retire(set_rid, image)
		set_rid = RID(); set_depth = RID(); set_image = RID()
		image = _create_image(size)
		image_size = size
		if not image.is_valid(): return
		_mutex.lock(); _reported_size = Vector2i.ZERO; _published_image = image; _mutex.unlock()
	# Uniform data (std140): projection, inverse projection, sun (view space) + length, params, size.
	var projection: Projection = scene.get_cam_projection()
	var inverse: Projection = projection.inverse()
	derived_far = absf(inverse.w.z / inverse.w.w)
	_mutex.lock(); _derived_far = derived_far; _mutex.unlock()
	var sun_view: Vector3 = (camera.basis.inverse() * sun_world).normalized()
	var max_pixels: float = 0.1 * size.y
	var data := PackedByteArray(); data.resize(176)
	var offset := 0
	for matrix in [projection, inverse]:
		for column in [matrix.x, matrix.y, matrix.z, matrix.w]:
			for value in [column.x, column.y, column.z, column.w]: data.encode_float(offset, value); offset += 4
	for value in [sun_view.x, sun_view.y, sun_view.z, length, thickness, float(steps), fade, float(frame_index % 64) if (temporal and dither_enabled) else -1.0, float(size.x), float(size.y), max_pixels, 0.0]:
		data.encode_float(offset, value); offset += 4
	rd.buffer_update(buffer, 0, 176, data)
	if not set_rid.is_valid() or not rd.uniform_set_is_valid(set_rid) or set_depth != depth or set_image != image:
		_retire(set_rid, RID())
		var uniforms: Array[RDUniform] = []
		var target := RDUniform.new(); target.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE; target.binding = 0; target.add_id(image); uniforms.append(target)
		var d := RDUniform.new(); d.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE; d.binding = 1; d.add_id(sampler); d.add_id(depth); uniforms.append(d)
		var b := RDUniform.new(); b.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER; b.binding = 2; b.add_id(buffer); uniforms.append(b)
		set_rid = rd.uniform_set_create(uniforms, shader, 0)
		set_depth = depth; set_image = image
	if not set_rid.is_valid(): return
	var timestamp_started := false
	if timestamps and timestamp_begin != "" and timestamp_end != "":
		rd.capture_timestamp(timestamp_begin)
		timestamp_started = true
	var list := rd.compute_list_begin()
	rd.compute_list_bind_compute_pipeline(list, pipeline)
	rd.compute_list_bind_uniform_set(list, set_rid, 0)
	rd.compute_list_dispatch(list, (size.x + 7) / 8, (size.y + 7) / 8, 1)
	rd.compute_list_end()
	if timestamp_started:
		rd.capture_timestamp(timestamp_end)
		timestamp_scopes += 1
	dispatches += 1
	ready = true
	_mutex.lock(); _reported_size = size; _reported_generation += 1; _mutex.unlock()
