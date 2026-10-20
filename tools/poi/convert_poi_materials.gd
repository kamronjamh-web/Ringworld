extends SceneTree
# Converts every POI architecture StandardMaterial3D .tres to a POI ShaderMaterial IN PLACE (same path, so the meshes that
# reference the .tres by path need no rewrite). Idempotent: materials that are already POI shaders are skipped.
#   godot --headless --path . --script res://tools/poi/convert_poi_materials.gd
# Expected: 56 materials (33 merged emitter/spire/reactor + 23 Weather Lung). Verifies the saved text and every mesh surface.
const Util := preload("res://tools/poi/poi_material_util.gd")
const DIRS := [
	"res://assets/poi/beam_emitter/merged",
	"res://assets/poi/main_spire/merged",
	"res://assets/poi/reactor/merged",
	"res://assets/poi/main_reactor/merged",
	"res://assets/poi/weather_lung/built",
]
const EXPECTED := 56
var failures := PackedStringArray()

func _initialize() -> void:
	var total := 0
	var converted := 0
	for dir in DIRS:
		var mat_dir: String = dir + "/materials"
		var files := DirAccess.get_files_at(mat_dir)
		files.sort()
		for f in files:
			if not f.ends_with(".tres"):
				continue
			total += 1
			var path: String = mat_dir + "/" + f
			var res := ResourceLoader.load(path, "", ResourceLoader.CACHE_MODE_REPLACE)
			if res is ShaderMaterial and Util.is_poi_material(res):
				if Util.strip_runtime_params(path):
					converted += 1
				continue
			if not (res is BaseMaterial3D):
				failures.append("%s is not a BaseMaterial3D" % path)
				continue
			var sm := Util.to_shader_material(res as BaseMaterial3D)
			if sm == null:
				failures.append("%s failed to convert" % path)
				continue
			var err := ResourceSaver.save(sm, path)
			if err != OK:
				failures.append("%s save error %d" % [path, err])
				continue
			Util.strip_runtime_params(path)
			if not _verify_text(path, sm):
				continue
			converted += 1
	# Every mesh surface in the sibling meshes dirs must now be a POI ShaderMaterial.
	var surfaces := 0
	for dir in DIRS:
		var mesh_dir: String = dir + "/meshes"
		for f in DirAccess.get_files_at(mesh_dir):
			if not f.ends_with(".res"):
				continue
			var mesh := ResourceLoader.load(mesh_dir + "/" + f, "", ResourceLoader.CACHE_MODE_REPLACE) as Mesh
			if mesh == null:
				failures.append("%s/%s did not load as a Mesh" % [mesh_dir, f])
				continue
			for s in mesh.get_surface_count():
				surfaces += 1
				var mat := mesh.surface_get_material(s)
				if mat == null or not Util.is_poi_material(mat):
					failures.append("%s/%s surface %d is %s" % [mesh_dir, f, s, mat])
	print("convert_poi_materials: %d materials (%d converted this run), %d mesh surfaces checked" % [total, converted, surfaces])
	if total != EXPECTED:
		failures.append("expected %d materials, found %d" % [EXPECTED, total])
	if failures.is_empty():
		print("CONVERT_POI_MATERIALS=PASS %d" % total)
		quit(0)
	else:
		for f in failures:
			push_error("CONVERT_POI_MATERIALS=FAIL " + f)
		quit(1)

func _verify_text(path: String, sm: ShaderMaterial) -> bool:
	var text := FileAccess.get_file_as_string(path)
	if not text.contains("ring_poi_architecture"):
		failures.append("%s saved without the POI shader reference" % path)
		return false
	# Every surface parameter is set explicitly by the converter, so each must be in the saved text.
	for p in Util.KEPT_PARAMS:
		if not text.contains("shader_parameter/" + p):
			failures.append("%s lost shader_parameter/%s" % [path, p])
			return false
	return true
