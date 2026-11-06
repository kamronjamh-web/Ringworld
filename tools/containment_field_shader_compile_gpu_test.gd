extends SceneTree
# Containment field shaders compile (WO3/WO4): the RenderingDevice raster passes (post-Sunshine near patch and ripple patches) are compiled fresh from
# source with their includes expanded, and the spatial .gdshader materials must parse (a shader that fails to parse reports no uniforms).
# gpu-local (needs a RenderingDevice). Prints CONTAINMENT_FIELD_SHADER_COMPILE=PASS.

const RD_SOURCES := [
	"res://scripts/render/ring_containment_field_post.glsl",
	"res://scripts/render/ring_containment_field_copy.glsl",
	"res://scripts/render/ring_containment_ripple_post.glsl",
]
# Spatial shaders and one uniform each must declare (proves the whole file, includes and all, parsed).
const SPATIAL := {
	"res://materials/ring_containment_field.gdshader": "cf_shimmer_strength",
	"res://materials/ring_containment_ripple.gdshader": "cf_ripple_strength",
	"res://materials/ring_containment_strike_far.gdshader": "cf_far_fade",
}

func _initialize() -> void:
	var rd: RenderingDevice = RenderingServer.create_local_rendering_device()
	if rd == null:
		_fail("Local RenderingDevice unavailable; run from a normal display session.")
		return
	var compiled := 0
	for source_path in RD_SOURCES:
		if not FileAccess.file_exists(source_path):
			print("CONTAINMENT_FIELD_SHADER_COMPILE skip (not present yet): %s" % source_path)
			continue
		var expanded: String = _expand_includes(source_path, [])
		if expanded.is_empty():
			rd.free()
			return
		var shader_source := RDShaderSource.new()
		var is_raster: bool = expanded.contains("#[vertex]") or expanded.contains("#[fragment]")
		if is_raster:
			shader_source.source_vertex = _stage_source(expanded, "vertex")
			shader_source.source_fragment = _stage_source(expanded, "fragment")
		else:
			shader_source.source_compute = expanded.replace("#[compute]", "")
		var spirv: RDShaderSPIRV = rd.shader_compile_spirv_from_source(shader_source)
		var compile_error: String = spirv.compile_error_vertex + spirv.compile_error_fragment if is_raster else spirv.compile_error_compute
		if compile_error != "":
			rd.free()
			_fail("%s compile failed: %s" % [source_path, compile_error])
			return
		var shader: RID = rd.shader_create_from_spirv(spirv)
		if not shader.is_valid():
			rd.free()
			_fail("%s produced an invalid shader RID" % source_path)
			return
		rd.free_rid(shader)
		var imported := ResourceLoader.load(source_path) as RDShaderFile
		if imported == null or imported.get_spirv() == null:
			rd.free()
			_fail("%s imported shader missing (reimport)" % source_path)
			return
		var imported_spirv: RDShaderSPIRV = imported.get_spirv()
		var imported_error: String = imported_spirv.compile_error_vertex + imported_spirv.compile_error_fragment if is_raster else imported_spirv.compile_error_compute
		if imported_error != "":
			rd.free()
			_fail("%s imported SPIR-V has errors (reimport): %s" % [source_path, imported_error])
			return
		compiled += 1
		print("CONTAINMENT_FIELD_SHADER_COMPILE rd ok: %s" % source_path)
	rd.free()
	var parsed := 0
	for path in SPATIAL.keys():
		if not FileAccess.file_exists(path):
			print("CONTAINMENT_FIELD_SHADER_COMPILE skip (not present yet): %s" % path)
			continue
		var shader := load(path) as Shader
		if shader == null:
			_fail("%s did not load" % path)
			return
		var names: Array[String] = []
		for u in shader.get_shader_uniform_list(true):
			names.append(String(u.get("name", "")))
		if not names.has(String(SPATIAL[path])):
			_fail("%s did not parse (uniform %s missing; %d uniforms reported)" % [path, SPATIAL[path], names.size()])
			return
		parsed += 1
		print("CONTAINMENT_FIELD_SHADER_COMPILE spatial ok: %s (%d uniforms)" % [path, names.size()])
	print("CONTAINMENT_FIELD_SHADER_COMPILE=PASS rd=%d spatial=%d" % [compiled, parsed])
	quit(0)

func _stage_source(expanded: String, stage: String) -> String:
	var marker := "#[%s]" % stage
	var start := expanded.find(marker)
	if start < 0:
		_fail("missing %s stage" % stage)
		return ""
	var other := "#[fragment]" if stage == "vertex" else "#[vertex]"
	var end := expanded.find(other, start + marker.length())
	if end < 0:
		end = expanded.length()
	return expanded.substr(start + marker.length(), end - start - marker.length())

func _expand_includes(path: String, stack: Array[String]) -> String:
	if path in stack:
		_fail("recursive include: %s" % path)
		return ""
	var text: String = FileAccess.get_file_as_string(path)
	if text.is_empty():
		_fail("could not read shader source: %s" % path)
		return ""
	var result := ""
	var next_stack: Array[String] = stack.duplicate()
	next_stack.append(path)
	for line in text.split("\n"):
		var stripped: String = line.strip_edges()
		if stripped.begins_with("#include"):
			var first_quote: int = stripped.find("\"")
			var last_quote: int = stripped.rfind("\"")
			if first_quote < 0 or last_quote <= first_quote:
				_fail("malformed include in %s: %s" % [path, line])
				return ""
			var relative: String = stripped.substr(first_quote + 1, last_quote - first_quote - 1)
			var child: String = relative if relative.begins_with("res://") else path.get_base_dir().path_join(relative).simplify_path()
			var included: String = _expand_includes(child, next_stack)
			if included.is_empty():
				return ""
			result += included + "\n"
		else:
			result += line + "\n"
	return result

func _fail(message: String) -> void:
	push_error("CONTAINMENT_FIELD_SHADER_COMPILE=FAIL " + message)
	quit(1)
