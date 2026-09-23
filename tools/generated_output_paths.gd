## Disposable test captures live below user://generated, never beside saves.
## The generated subtree may be removed between runs or before a OneDrive sync.
extends RefCounted

const SMOKE_CAPTURE_DIRECTORY := "user://generated/captures/smoke"


static func smoke_capture_path(file_name: String) -> String:
	if file_name.get_file() != file_name or file_name.is_empty():
		push_error("Generated capture file name must not contain a directory: %s" % file_name)
		return ""
	var directory := ProjectSettings.globalize_path(SMOKE_CAPTURE_DIRECTORY)
	var error := DirAccess.make_dir_recursive_absolute(directory)
	if error != OK:
		push_error("Could not create generated capture directory: %s" % directory)
		return ""
	return directory.path_join(file_name)
