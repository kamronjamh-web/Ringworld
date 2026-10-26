extends RefCounted
# RingworldTechDemo extension: driver helper of the storm profile map (docs/storm-revamp-s4-sculpt-v2-design.md, WO-V1), owned by
# SunshineClouds.gd. Same snapshot / slice / front-back scheme as the macro weather map: RingStormProfileBake.glsl writes the BACK
# buffer one slice per render callback, every slice reading one snapshot of the weather-systems block, and the buffers swap only after
# the last slice. The ray-march and shadow computes read the front buffer through params_buffer (bound at main 24/25, shadow 11/12).
#
# Texture: Texture2DArray RGBA16F, CAPACITY x CAPACITY, 4 layers = buffer * 2 + {0 heights, 1 aux}; allocated once. The map is a
# square centred on the camera (the macro map spans the whole ring width instead).

const CAPACITY := 1024
const PARAM_BYTES := 48
const BUILD_PARAM_BYTES := 80
const SNAPSHOT_PAD_TEXELS := 2
const MIN_TEXEL_METERS := 250.0
const SHADER_PATH := "res://addons/SunshineClouds2/RingStormProfileBake.glsl"

var texture : RID = RID()
var params_buffer : RID = RID()
var build_params_buffer : RID = RID()
var snapshot_buffer : RID = RID()
var shader : RID = RID()
var pipeline : RID = RID()
var build_set : RID = RID()
var unavailable : bool = false
var compute_shader : RDShaderFile

# Requested configuration (configure()); a build snapshots it when it begins.
var requested : bool = false
var config : Dictionary = {}
# Front (published) state.
var valid : bool = false
var front_buffer : int = 0
var front_origin : Vector2 = Vector2.ZERO
var front_texel : float = MIN_TEXEL_METERS
var front_size : Vector2i = Vector2i.ZERO
var front_circumference : float = 0.0
var front_wind : Vector3 = Vector3.ZERO # downwind cos, sin, lean metres
var params_dirty : bool = true
# In-flight build.
var build : Dictionary = {}
var build_active : bool = false
var build_start_usec : int = 0
var snapshot_revision : int = -1
# Diagnostics (read by the bridge / harness).
var builds_completed : int = 0
var slices_dispatched : int = 0

# enabled = false switches the map off (the shaders return to the analytic storm loop). center = camera canonical (around, across);
# half_extent the half size of the square (handoff end + light reach + margin, the macro map's half_around); shape = (pillar radius m,
# anvil radius multiplier, anvil start band fraction, shear 0..1); mammatus 0..1; wind = (downwind cos, sin, lean metres at the band top).
func configure(enabled : bool, center : Vector2, half_extent : float, shape : Vector4, mammatus : float, wind : Vector3,
		slices : int, refresh_seconds : float) -> void:
	requested = enabled
	config = {
		"center": center, "half": maxf(half_extent, 1000.0), "shape": shape, "mammatus": mammatus, "wind": wind,
		"slices": maxi(slices, 1), "refresh_usec": int(maxf(refresh_seconds, 0.02) * 1000000.0),
	}

func ensure_resources(rd : RenderingDevice) -> void:
	if not rd or unavailable:
		return
	if not params_buffer.is_valid():
		var zeros := PackedByteArray()
		zeros.resize(PARAM_BYTES)
		params_buffer = rd.uniform_buffer_create(PARAM_BYTES, zeros)
		params_dirty = true
	if not texture.is_valid():
		var texture_format := RDTextureFormat.new()
		texture_format.texture_type = RenderingDevice.TEXTURE_TYPE_2D_ARRAY
		texture_format.format = RenderingDevice.DATA_FORMAT_R16G16B16A16_SFLOAT
		texture_format.width = CAPACITY
		texture_format.height = CAPACITY
		texture_format.array_layers = 4
		texture_format.usage_bits = RenderingDevice.TEXTURE_USAGE_STORAGE_BIT | RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT \
			| RenderingDevice.TEXTURE_USAGE_CAN_COPY_FROM_BIT
		texture = rd.texture_create(texture_format, RDTextureView.new())
		valid = false
		params_dirty = true
	if not params_buffer.is_valid() or not texture.is_valid():
		printerr("Storm profile map resources could not be created; storms use the analytic shape.")
		unavailable = true

func _ensure_pipeline(rd : RenderingDevice, weather_systems_bytes_size : int) -> bool:
	if pipeline.is_valid() and build_set.is_valid():
		return true
	if unavailable or not rd or not texture.is_valid():
		return false
	if not compute_shader:
		compute_shader = ResourceLoader.load(SHADER_PATH)
	if not compute_shader:
		printerr("Storm profile map shader resource was not found; storms use the analytic shape.")
		unavailable = true
		return false
	shader = rd.shader_create_from_spirv(compute_shader.get_spirv())
	if shader.is_valid():
		pipeline = rd.compute_pipeline_create(shader)
	if not pipeline.is_valid():
		printerr("Storm profile map shader failed to compile; storms use the analytic shape.")
		unavailable = true
		return false
	var zeros := PackedByteArray()
	zeros.resize(weather_systems_bytes_size)
	snapshot_buffer = rd.uniform_buffer_create(weather_systems_bytes_size, zeros)
	build_params_buffer = rd.uniform_buffer_create(BUILD_PARAM_BYTES)
	var uniforms : Array[RDUniform] = []
	var image_uniform := RDUniform.new()
	image_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE
	image_uniform.binding = 0
	image_uniform.add_id(texture)
	uniforms.append(image_uniform)
	var systems_uniform := RDUniform.new()
	systems_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER
	systems_uniform.binding = 1
	systems_uniform.add_id(snapshot_buffer)
	uniforms.append(systems_uniform)
	var build_uniform := RDUniform.new()
	build_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER
	build_uniform.binding = 2
	build_uniform.add_id(build_params_buffer)
	uniforms.append(build_uniform)
	build_set = rd.uniform_set_create(uniforms, shader, 0)
	if not build_set.is_valid() or not snapshot_buffer.is_valid() or not build_params_buffer.is_valid():
		printerr("Storm profile map descriptor set could not be created; storms use the analytic shape.")
		unavailable = true
		return false
	return true

func _write_params(rd : RenderingDevice) -> void:
	params_dirty = false
	if not params_buffer.is_valid():
		return
	var bytes := PackedByteArray()
	bytes.resize(PARAM_BYTES)
	var values := [front_origin.x, front_origin.y, front_texel, float(front_buffer),
		float(front_size.x), float(front_size.y), front_circumference, 1.0 if valid else 0.0,
		front_wind.x, front_wind.y, front_wind.z, 0.0]
	for i in range(values.size()):
		bytes.encode_float(i * 4, values[i])
	rd.buffer_update(params_buffer, 0, PARAM_BYTES, bytes)

# Runs once per render callback, right after the macro map update and before the shadow and cloud dispatches: starts a build when the
# refresh interval has elapsed (or the system structure changed), dispatches exactly one slice of the in-flight build and publishes
# the back buffer atomically after its last slice. weather_systems_bytes is the uploaded system block (header0.y > 0.5 = enabled).
func update(rd : RenderingDevice, weather_systems_bytes : PackedByteArray, weather_systems_revision : int, weather_frame : Vector4,
		ring_radius : float, stamp_begin : String, stamp_end : String) -> void:
	var snapshot_ready := weather_systems_bytes.size() > 64 and weather_systems_bytes.decode_float(4) > 0.5
	if not requested or unavailable or not snapshot_ready or config.is_empty():
		# Off (or nothing to build from): the shaders take the analytic storm loop.
		if valid or build_active:
			valid = false
			build_active = false
			params_dirty = true
		if params_dirty:
			_write_params(rd)
		return
	if not texture.is_valid() or not _ensure_pipeline(rd, weather_systems_bytes.size()):
		return
	if not build_active:
		var now := Time.get_ticks_usec()
		var due : bool = not valid or now - build_start_usec >= int(config["refresh_usec"]) \
			or snapshot_revision != weather_systems_revision
		if not due:
			if params_dirty:
				_write_params(rd)
			return
		_begin_build(rd, now, weather_systems_bytes, weather_systems_revision, weather_frame, ring_radius)
	_dispatch_slice(rd, stamp_begin, stamp_end)
	if params_dirty:
		_write_params(rd)

func _begin_build(rd : RenderingDevice, now_usec : int, weather_systems_bytes : PackedByteArray, revision : int,
		weather_frame : Vector4, ring_radius : float) -> void:
	var cfg := config
	var circumference := weather_frame.x if weather_frame.x > 1.0 else ring_radius * TAU
	var half : float = cfg["half"]
	var usable := float(CAPACITY - SNAPSHOT_PAD_TEXELS - 1)
	var texel : float = maxf(MIN_TEXEL_METERS, 2.0 * half / usable)
	var count := mini(ceili(2.0 * half / texel) + SNAPSHOT_PAD_TEXELS, CAPACITY)
	var center : Vector2 = cfg["center"]
	# Origin snapped to whole texels (unwrapped canonical metres, then wrapped to the ring) so the grid is world-anchored.
	var origin_around : float = floorf((center.x - half) / texel) * texel
	if circumference > 1.0:
		origin_around = fposmod(origin_around, circumference)
	var origin_across : float = floorf((center.y - half) / texel) * texel
	var groups_y := int((count + 7) / 8)
	rd.buffer_update(snapshot_buffer, 0, weather_systems_bytes.size(), weather_systems_bytes)
	build = {
		"origin": Vector2(origin_around, origin_across), "texel": texel, "size": Vector2i(count, count),
		"circumference": circumference, "shift": Vector2(weather_frame.y, weather_frame.z), "buffer": 1 - front_buffer,
		"slice": 0, "slices": mini(int(cfg["slices"]), groups_y), "groups_y": groups_y,
		"shape": cfg["shape"], "mammatus": cfg["mammatus"], "wind": cfg["wind"],
	}
	build_active = true
	build_start_usec = now_usec
	snapshot_revision = revision

func _dispatch_slice(rd : RenderingDevice, stamp_begin : String, stamp_end : String) -> void:
	var slices : int = build["slices"]
	var slice_index : int = build["slice"]
	var groups_y : int = build["groups_y"]
	var first_group := slice_index * groups_y / slices
	var last_group := (slice_index + 1) * groups_y / slices
	var size : Vector2i = build["size"]
	if last_group > first_group:
		var shape : Vector4 = build["shape"]
		var bytes := PackedByteArray()
		bytes.resize(BUILD_PARAM_BYTES)
		var values := [build["origin"].x, build["origin"].y, build["texel"], float(build["buffer"]),
			float(size.x), float(size.y), float(first_group * 8), build["circumference"],
			build["shift"].x, build["shift"].y, 0.0, 0.0,
			shape.x, shape.y, shape.z, shape.w,
			build["mammatus"], 0.0, 0.0, 0.0]
		for i in range(values.size()):
			bytes.encode_float(i * 4, values[i])
		rd.buffer_update(build_params_buffer, 0, BUILD_PARAM_BYTES, bytes)
		var stamped := stamp_begin != "" and stamp_end != ""
		if stamped:
			rd.capture_timestamp(stamp_begin)
		var compute_list := rd.compute_list_begin()
		rd.compute_list_bind_compute_pipeline(compute_list, pipeline)
		rd.compute_list_bind_uniform_set(compute_list, build_set, 0)
		rd.compute_list_dispatch(compute_list, int((size.x + 7) / 8), last_group - first_group, 1)
		rd.compute_list_end()
		if stamped:
			rd.capture_timestamp(stamp_end)
		slices_dispatched += 1
	if slice_index < slices - 1:
		build["slice"] = slice_index + 1
		return
	# Last slice: publish the completed back buffer. Receivers (the cloud and shadow dispatches later in this callback) read it.
	front_buffer = build["buffer"]
	front_origin = build["origin"]
	front_texel = build["texel"]
	front_size = size
	front_circumference = build["circumference"]
	front_wind = build["wind"]
	valid = true
	params_dirty = true
	build_active = false
	builds_completed += 1

# Every RID the helper owns, for the owner's deferred release.
func owned_rids() -> Array[RID]:
	return [pipeline, shader, texture, params_buffer, build_params_buffer, snapshot_buffer]
