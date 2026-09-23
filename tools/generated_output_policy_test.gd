extends SceneTree

const GeneratedOutputPaths := preload("res://tools/generated_output_paths.gd")
const SOURCE_ROOTS := ["res://scripts", "res://tools", "res://addons"]
const SOURCE_EXTENSIONS := ["cs", "gd", "ps1"]
const FORBIDDEN_LITERALS := [
	"res://artifacts",
	"res://captures",
	"user://%s.png",
]


func _initialize() -> void:
	var failures: Array[String] = []
	if ProjectSettings.get_setting("debug/file_logging/log_path", "") != "user://generated/logs/godot.log":
		failures.append("Godot's runtime log is not routed below user://generated/logs.")
	for barrier in ["res://artifacts/.gdignore", "res://captures/.gdignore"]:
		if not FileAccess.file_exists(barrier):
			failures.append("Missing Godot scan barrier: %s" % barrier)

	var probe_path := GeneratedOutputPaths.smoke_capture_path("generated_output_policy_probe.tmp")
	var generated_root := ProjectSettings.globalize_path("user://generated").simplify_path()
	if probe_path.is_empty() or not probe_path.simplify_path().begins_with(generated_root + "/"):
		failures.append("Generated smoke path escaped user://generated: %s" % probe_path)
	else:
		var probe := FileAccess.open(probe_path, FileAccess.WRITE)
		if probe == null:
			failures.append("Generated output probe could not be created: %s" % probe_path)
		else:
			probe.store_string("disposable")
			probe.close()
			DirAccess.remove_absolute(probe_path)

	for source_root in SOURCE_ROOTS:
		_scan_directory(source_root, failures)

	if failures.is_empty():
		print("GENERATED_OUTPUT_POLICY_TEST: PASS")
		quit(0)
		return
	for failure in failures:
		push_error(failure)
	print("GENERATED_OUTPUT_POLICY_TEST: FAIL (%d violations)" % failures.size())
	quit(1)


func _scan_directory(directory_path: String, failures: Array[String]) -> void:
	var directory := DirAccess.open(directory_path)
	if directory == null:
		failures.append("Could not scan source directory: %s" % directory_path)
		return
	directory.list_dir_begin()
	var entry := directory.get_next()
	while not entry.is_empty():
		if entry != "." and entry != "..":
			var entry_path := directory_path.path_join(entry)
			if directory.current_is_dir():
				_scan_directory(entry_path, failures)
			elif entry.get_extension().to_lower() in SOURCE_EXTENSIONS:
				_scan_source_file(entry_path, failures)
		entry = directory.get_next()
	directory.list_dir_end()


func _scan_source_file(file_path: String, failures: Array[String]) -> void:
	if file_path == "res://tools/generated_output_policy_test.gd":
		return
	var source := FileAccess.get_file_as_string(file_path)
	for forbidden in FORBIDDEN_LITERALS:
		if source.contains(forbidden):
			failures.append("Disposable output path '%s' remains in %s." % [forbidden, file_path])
