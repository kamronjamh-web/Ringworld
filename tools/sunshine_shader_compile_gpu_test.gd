extends SceneTree

# Compiles fresh source trees in memory; it does not rewrite imports or .res files.
const SOURCES := [
	"res://addons/SunshineClouds2/SunshineCloudsCompute.glsl",
	"res://addons/SunshineClouds2/SunshineCloudsInterleaveFill.glsl",
	"res://addons/SunshineClouds2/SunshineCloudsPreCompute.glsl",
	"res://addons/SunshineClouds2/SunshineCloudsPostCompute.glsl",
	"res://addons/SunshineClouds2/SunshineCloudsPostCompute.msaa.glsl",
	"res://addons/SunshineClouds2/SunshineCloudShadowCompute.glsl",
	"res://addons/SunshineClouds2/SunshineCloudsDisplay.glsl",
	"res://addons/SunshineClouds2/SunshineCloudsDisplay.msaa.glsl",
	"res://scripts/render/ring_rain_haze_composite.glsl",
	"res://materials/ring_terrain_cloud_sun_generate.glsl",
]

func _initialize() -> void:
	var rd: RenderingDevice = RenderingServer.create_local_rendering_device()
	if rd == null:
		_fail("Local RenderingDevice unavailable; run from a normal display session.")
		return
	for source_path in SOURCES:
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
		var imported := ResourceLoader.load(source_path) as RDShaderFile
		if imported == null:
			rd.free()
			_fail("%s imported shader missing" % source_path)
			return
		var imported_spirv: RDShaderSPIRV = imported.get_spirv()
		if imported_spirv == null:
			rd.free()
			_fail("%s imported SPIR-V missing" % source_path)
			return
		var stages := [RenderingDevice.SHADER_STAGE_VERTEX, RenderingDevice.SHADER_STAGE_FRAGMENT] if is_raster else [RenderingDevice.SHADER_STAGE_COMPUTE]
		for stage in stages:
			if imported_spirv.get_stage_bytecode(stage) != spirv.get_stage_bytecode(stage):
				rd.free()
				_fail("%s imported stage %d differs from fresh source; reimport the shader" % [source_path, stage])
				return
		print("Sunshine import/source bytecode match: %s" % source_path)
		var shader: RID = rd.shader_create_from_spirv(spirv)
		if not shader.is_valid():
			rd.free()
			_fail("%s produced an invalid shader RID" % source_path)
			return
		if not is_raster:
			var pipeline: RID = rd.compute_pipeline_create(shader)
			if not pipeline.is_valid():
				rd.free_rid(shader)
				rd.free()
				_fail("%s produced an invalid compute pipeline" % source_path)
				return
			rd.free_rid(pipeline)
		rd.free_rid(shader)
		print("Sunshine source compile PASS: %s" % source_path)
	rd.free()
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
			var child: String = path.get_base_dir().path_join(relative).simplify_path()
			var included: String = _expand_includes(child, next_stack)
			if included.is_empty():
				return ""
			result += included + "\n"
		else:
			result += line + "\n"
	return result

func _fail(message: String) -> void:
	push_error(message)
	quit(1)
