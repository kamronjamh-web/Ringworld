extends RefCounted
# RingworldTechDemo extension: driver helper of the megastorm strip map (docs/megastorm-design.md 3.2 / 3.6), owned by SunshineClouds.gd.
# Same snapshot / slice / front-back scheme as the storm profile map (RingStormProfileMap.gd): RingMegastormStripBuild.glsl writes the
# BACK buffer one slice per render callback and the buffers swap only after the last slice. The ray-march and shadow computes read the
# front buffer through params_buffer (bound at main 31/32, shadow 15/16).
#
# Texture: Texture2DArray RGBA16F, 4 layers = buffer * 2 + {0 heights, 1 aux}. It stays a 1x1 placeholder until the strip is first
# enabled (configure(true, ...)); the next ensure_resources replaces it with the full CAPACITY_X x CAPACITY_Y texture (texture_generation
# increments so the owner rebuilds the descriptor sets that reference it). Once real it stays real for the session.
# The strip is a moving-frame map: x = distance behind the reference line (X_MIN .. X_MAX), across = 0 .. ring width.

const CAPACITY_X := 1024
const CAPACITY_Y := 1024
const X_MIN := -72000.0
const X_MAX := 56000.0
const PARAM_BYTES := 80
const BUILD_PARAM_BYTES := 80
const RETIRE_CALLBACKS := 8
const SHADER_PATH := "res://addons/SunshineClouds2/RingMegastormStripBuild.glsl"

var texture : RID = RID()
var params_buffer : RID = RID()
var build_params_buffer : RID = RID()
var shader : RID = RID()
var pipeline : RID = RID()
var build_set : RID = RID()
var unavailable : bool = false
var compute_shader : RDShaderFile

# Bumped whenever the texture RID is replaced (placeholder -> real); the owner rebuilds every descriptor set that binds it.
var texture_generation : int = 0
var real_texture : bool = false
var _retiring_texture : RID = RID()
var _retiring_countdown : int = 0

# Requested configuration (configure()); a build snapshots it when it begins.
var requested : bool = false
var ever_enabled : bool = false
var config : Dictionary = {}
# Front (published) state.
var valid : bool = false
var front_buffer : int = 0
var front_config : Dictionary = {}
var x_texel : float = (X_MAX - X_MIN) / float(CAPACITY_X)
var across_texel : float = 0.0
var params_dirty : bool = true
# Storm-light constants of the haze path rule (block member c): (shadow-fade start, shadow-fade end, k_min_eff, lightning flash). The bridge pushes them
# every frame while the strip exists; the haze apply (ring_cloud_atmosphere_apply_storm_path) reads them in the march and the interleave fill.
var haze_light : Vector4 = Vector4(0.35, 0.7, 0.06, 0.0)
# In-flight build.
var build : Dictionary = {}
var build_active : bool = false
var build_start_usec : int = 0
# Diagnostics (read by the bridge / harness).
var builds_completed : int = 0
var slices_dispatched : int = 0

# enabled = false switches the strip off (the shaders skip the strip fetch). t = weather clock seconds, level / strength / seed from
# the megastorm frame, width = ring width m, bow m, meander amplitude / wavelength m and phase rad, ceiling = column top cap m,
# datum = altitude datum m.
func configure(enabled : bool, t : float, level : float, strength : float, seed : float, width : float, bow : float,
		meander_amp : float, meander_wavelength : float, meander_phase : float, ceiling : float, datum : float,
		slices : int, refresh_seconds : float) -> void:
	requested = enabled
	if enabled:
		ever_enabled = true
	config = {
		"t": t, "level": level, "strength": strength, "seed": seed, "width": width, "bow": bow, "meander_amp": meander_amp,
		"meander_wavelength": meander_wavelength, "meander_phase": meander_phase, "ceiling": ceiling, "datum": datum,
		"slices": maxi(slices, 1), "refresh_usec": int(maxf(refresh_seconds, 0.02) * 1000000.0),
	}
	params_dirty = true # block members d / e (the haze path's slanted-ray shade) follow the requested config

func ensure_resources(rd : RenderingDevice) -> void:
	if not rd or unavailable:
		return
	if not params_buffer.is_valid():
		var zeros := PackedByteArray()
		zeros.resize(PARAM_BYTES)
		params_buffer = rd.uniform_buffer_create(PARAM_BYTES, zeros)
		params_dirty = true
	if texture.is_valid() and ever_enabled and not real_texture:
		# Replace the placeholder. Descriptor sets that bind it are dropped by the owner (texture_generation); the old texture is freed a
		# few callbacks later so those sets are released first.
		_release_build_objects(rd)
		_retiring_texture = texture
		_retiring_countdown = RETIRE_CALLBACKS
		texture = RID()
		valid = false
		params_dirty = true
	if not texture.is_valid():
		var texture_format := RDTextureFormat.new()
		texture_format.texture_type = RenderingDevice.TEXTURE_TYPE_2D_ARRAY
		texture_format.format = RenderingDevice.DATA_FORMAT_R16G16B16A16_SFLOAT
		var full := ever_enabled
		texture_format.width = CAPACITY_X if full else 1
		texture_format.height = CAPACITY_Y if full else 1
		texture_format.array_layers = 4
		texture_format.usage_bits = RenderingDevice.TEXTURE_USAGE_STORAGE_BIT | RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT \
			| RenderingDevice.TEXTURE_USAGE_CAN_COPY_FROM_BIT
		texture = rd.texture_create(texture_format, RDTextureView.new())
		real_texture = full
		texture_generation += 1
		valid = false
		params_dirty = true
	if not params_buffer.is_valid() or not texture.is_valid():
		printerr("Megastorm strip resources could not be created; the megastorm wall body is not drawn.")
		unavailable = true

func _release_build_objects(rd : RenderingDevice) -> void:
	build_active = false
	for rid in [build_set, pipeline, shader, build_params_buffer]:
		if rid is RID and rid.is_valid():
			rd.free_rid(rid)
	build_set = RID()
	pipeline = RID()
	shader = RID()
	build_params_buffer = RID()

func _ensure_pipeline(rd : RenderingDevice) -> bool:
	if pipeline.is_valid() and build_set.is_valid():
		return true
	if unavailable or not rd or not texture.is_valid() or not real_texture:
		return false
	if not compute_shader:
		compute_shader = ResourceLoader.load(SHADER_PATH)
	if not compute_shader:
		printerr("Megastorm strip shader resource was not found; the megastorm wall body is not drawn.")
		unavailable = true
		return false
	shader = rd.shader_create_from_spirv(compute_shader.get_spirv())
	if shader.is_valid():
		pipeline = rd.compute_pipeline_create(shader)
	if not pipeline.is_valid():
		printerr("Megastorm strip shader failed to compile; the megastorm wall body is not drawn.")
		unavailable = true
		return false
	build_params_buffer = rd.uniform_buffer_create(BUILD_PARAM_BYTES)
	var uniforms : Array[RDUniform] = []
	var image_uniform := RDUniform.new()
	image_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE
	image_uniform.binding = 0
	image_uniform.add_id(texture)
	uniforms.append(image_uniform)
	var build_uniform := RDUniform.new()
	build_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_UNIFORM_BUFFER
	build_uniform.binding = 1
	build_uniform.add_id(build_params_buffer)
	uniforms.append(build_uniform)
	build_set = rd.uniform_set_create(uniforms, shader, 0)
	if not build_set.is_valid() or not build_params_buffer.is_valid():
		printerr("Megastorm strip descriptor set could not be created; the megastorm wall body is not drawn.")
		unavailable = true
		return false
	return true

# Megastorm prewarm (SunshineClouds._megastorm_prewarm): the build shader and pipeline, once the real texture exists, so the first build
# after the wall comes near does not compile them.
func prewarm(rd : RenderingDevice) -> void:
	if rd and real_texture and not unavailable:
		_ensure_pipeline(rd)

func set_haze_light(light : Vector4) -> void:
	if light != haze_light:
		haze_light = light
		params_dirty = true

func _write_params(rd : RenderingDevice) -> void:
	params_dirty = false
	if not params_buffer.is_valid():
		return
	var bytes := PackedByteArray()
	bytes.resize(PARAM_BYTES)
	var values := [X_MIN, x_texel, across_texel, float(front_buffer * 2),
		float(CAPACITY_X), float(CAPACITY_Y), 1.0 if valid else 0.0, 0.0,
		haze_light.x, haze_light.y, haze_light.z, haze_light.w,
		float(config.get("width", 0.0)), float(config.get("bow", 0.0)), float(config.get("meander_amp", 0.0)), float(config.get("meander_wavelength", 1.0)),
		float(config.get("meander_phase", 0.0)), float(config.get("level", 0.0)), 0.0, 0.0]
	for i in range(values.size()):
		bytes.encode_float(i * 4, values[i])
	rd.buffer_update(params_buffer, 0, PARAM_BYTES, bytes)

# Runs once per render callback, right after the storm profile map update and before the shadow and cloud dispatches: starts a build
# when the refresh interval has elapsed, dispatches exactly one slice of the in-flight build and publishes the back buffer atomically
# after its last slice.
func update(rd : RenderingDevice, stamp_begin : String, stamp_end : String) -> void:
	if _retiring_texture.is_valid():
		_retiring_countdown -= 1
		if _retiring_countdown <= 0 and rd:
			rd.free_rid(_retiring_texture)
			_retiring_texture = RID()
	if not requested or unavailable or config.is_empty() or not real_texture:
		# Off (or not yet allocated): the shaders skip the strip fetch.
		if valid or build_active:
			valid = false
			build_active = false
			params_dirty = true
		if params_dirty:
			_write_params(rd)
		return
	if not texture.is_valid() or not _ensure_pipeline(rd):
		return
	if not build_active:
		var now := Time.get_ticks_usec()
		var due : bool = not valid or now - build_start_usec >= int(config["refresh_usec"])
		if not due:
			if params_dirty:
				_write_params(rd)
			return
		_begin_build(now)
	_dispatch_slice(rd, stamp_begin, stamp_end)
	if params_dirty:
		_write_params(rd)

func _begin_build(now_usec : int) -> void:
	var cfg := config
	across_texel = float(cfg["width"]) / float(CAPACITY_Y)
	var groups_y := int((CAPACITY_Y + 7) / 8)
	build = {
		"buffer": 1 - front_buffer, "slice": 0, "slices": mini(int(cfg["slices"]), groups_y), "groups_y": groups_y,
		"config": cfg,
	}
	build_active = true
	build_start_usec = now_usec

func _dispatch_slice(rd : RenderingDevice, stamp_begin : String, stamp_end : String) -> void:
	var slices : int = build["slices"]
	var slice_index : int = build["slice"]
	var groups_y : int = build["groups_y"]
	var first_group := slice_index * groups_y / slices
	var last_group := (slice_index + 1) * groups_y / slices
	var cfg : Dictionary = build["config"]
	if last_group > first_group:
		var bytes := PackedByteArray()
		bytes.resize(BUILD_PARAM_BYTES)
		var values := [X_MIN, x_texel, across_texel, float(build["buffer"]),
			float(CAPACITY_X), float(CAPACITY_Y), float(first_group * 8), cfg["datum"],
			cfg["t"], cfg["level"], cfg["strength"], cfg["seed"],
			cfg["width"], cfg["bow"], cfg["meander_amp"], cfg["meander_wavelength"],
			cfg["meander_phase"], cfg["ceiling"], 0.0, 0.0]
		for i in range(values.size()):
			bytes.encode_float(i * 4, values[i])
		rd.buffer_update(build_params_buffer, 0, BUILD_PARAM_BYTES, bytes)
		var stamped := stamp_begin != "" and stamp_end != ""
		if stamped:
			rd.capture_timestamp(stamp_begin)
		var compute_list := rd.compute_list_begin()
		rd.compute_list_bind_compute_pipeline(compute_list, pipeline)
		rd.compute_list_bind_uniform_set(compute_list, build_set, 0)
		rd.compute_list_dispatch(compute_list, int((CAPACITY_X + 7) / 8), last_group - first_group, 1)
		rd.compute_list_end()
		if stamped:
			rd.capture_timestamp(stamp_end)
		slices_dispatched += 1
	if slice_index < slices - 1:
		build["slice"] = slice_index + 1
		return
	# Last slice: publish the completed back buffer. Receivers (the cloud and shadow dispatches later in this callback) read it.
	front_buffer = build["buffer"]
	front_config = cfg.duplicate()
	valid = true
	params_dirty = true
	build_active = false
	builds_completed += 1

# Every RID the helper owns, for the owner's deferred release. The build set is released with the sets (build_set).
func owned_rids() -> Array[RID]:
	return [pipeline, shader, texture, params_buffer, build_params_buffer, _retiring_texture]
