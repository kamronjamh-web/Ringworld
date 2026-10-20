extends RefCounted
# Shared StandardMaterial3D -> POI ShaderMaterial converter (materials/ring_poi_architecture*.gdshader).
# Used by convert_poi_materials.gd (in-place conversion of the saved .tres), merge_poi_scenes.gd and build_weather_lung.gd,
# so regeneration stays haze-ready. The shader carries the ring haze per fragment; with no ray frame it is identical to
# the StandardMaterial3D it replaces (tools/poi_haze_gpu_test.gd).

const SHADER_CULL_BACK := "res://materials/ring_poi_architecture.gdshader"
const SHADER_DOUBLE_SIDED := "res://materials/ring_poi_architecture_double_sided.gdshader"

static func is_poi_material(m: Material) -> bool:
	if not (m is ShaderMaterial):
		return false
	var sh := (m as ShaderMaterial).shader
	return sh != null and (sh.resource_path == SHADER_CULL_BACK or sh.resource_path == SHADER_DOUBLE_SIDED)

## Returns "" when m converts exactly, otherwise a reason string.
static func unsupported_reason(m: BaseMaterial3D) -> String:
	if m.cull_mode != BaseMaterial3D.CULL_BACK and m.cull_mode != BaseMaterial3D.CULL_DISABLED:
		return "cull_mode %d" % m.cull_mode
	if m.transparency != BaseMaterial3D.TRANSPARENCY_DISABLED:
		return "transparency %d" % m.transparency
	if m.shading_mode != BaseMaterial3D.SHADING_MODE_PER_PIXEL:
		return "shading_mode %d" % m.shading_mode
	if m.diffuse_mode != BaseMaterial3D.DIFFUSE_BURLEY:
		return "diffuse_mode %d" % m.diffuse_mode
	if m.specular_mode != BaseMaterial3D.SPECULAR_SCHLICK_GGX:
		return "specular_mode %d" % m.specular_mode
	if m.vertex_color_use_as_albedo:
		return "vertex_color_use_as_albedo"
	if m.normal_enabled or m.rim_enabled or m.clearcoat_enabled or m.anisotropy_enabled or m.heightmap_enabled or m.subsurf_scatter_enabled or m.backlight_enabled or m.refraction_enabled or m.ao_enabled or m.detail_enabled:
		return "unsupported feature enabled"
	for tex in [BaseMaterial3D.TEXTURE_ALBEDO, BaseMaterial3D.TEXTURE_METALLIC, BaseMaterial3D.TEXTURE_ROUGHNESS, BaseMaterial3D.TEXTURE_EMISSION]:
		if m.get_texture(tex) != null:
			return "texture slot %d" % tex
	if m.emission_operator != BaseMaterial3D.EMISSION_OP_ADD:
		return "emission_operator"
	if m.uv1_scale != Vector3.ONE or m.uv1_offset != Vector3.ZERO:
		return "uv1 transform"
	return ""

static func to_shader_material(m: BaseMaterial3D) -> ShaderMaterial:
	var why := unsupported_reason(m)
	if why != "":
		push_error("PoiMaterialUtil: %s cannot convert: %s" % [m.resource_name, why])
		return null
	var out := ShaderMaterial.new()
	out.shader = load(SHADER_DOUBLE_SIDED if m.cull_mode == BaseMaterial3D.CULL_DISABLED else SHADER_CULL_BACK)
	out.resource_name = m.resource_name
	out.set_shader_parameter("albedo_color", m.albedo_color)
	out.set_shader_parameter("metallic_value", m.metallic)
	out.set_shader_parameter("roughness_value", m.roughness)
	out.set_shader_parameter("specular_value", m.metallic_specular)
	out.set_shader_parameter("emission_color", m.emission if m.emission_enabled else Color(0, 0, 0, 1))
	out.set_shader_parameter("emission_energy", m.emission_energy_multiplier)
	return out

## Shader parameter with the shader default as fallback (the .tres saver omits values equal to the default).
static func param(m: ShaderMaterial, name: StringName) -> Variant:
	var v: Variant = m.get_shader_parameter(name)
	if v == null and m.shader != null:
		v = RenderingServer.shader_get_parameter_default(m.shader.get_rid(), name)
	return v

const KEPT_PARAMS := ["albedo_color", "metallic_value", "roughness_value", "specular_value", "emission_color", "emission_energy"]

## ResourceSaver writes every uniform of the included haze/cloud-shadow code (explicit defaults when windowed, `= null` when headless).
## Those are runtime-owned (RingAtmosphereModel.ConfigureMaterial / RingPoiHaze) and would go stale in the file, so keep only the
## surface parameters. Returns true when the file was rewritten.
static func strip_runtime_params(path: String) -> bool:
	var text := FileAccess.get_file_as_string(path)
	var kept := PackedStringArray()
	var changed := false
	for line in text.split("\n"):
		if line.begins_with("shader_parameter/"):
			var pname := line.substr(len("shader_parameter/")).get_slice(" =", 0)
			if not KEPT_PARAMS.has(pname):
				changed = true
				continue
		kept.append(line)
	if not changed:
		return false
	var f := FileAccess.open(path, FileAccess.WRITE)
	f.store_string("\n".join(kept))
	f.close()
	return true
