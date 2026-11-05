@tool
extends SceneTree

## Explicit authoring step: glTF imports depend on scene.bin as well as scene.gltf.
## Run with --headless --editor --path . --script res://tools/prepare_tree_assets_editor.gd.
const SOURCES := [
	"res://resources/trees/lolipop/fir/scene.gltf",
	"res://resources/trees/lolipop/maple/scene.gltf",
	"res://resources/trees/lolipop/pine/scene.gltf",
]

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	create_timer(300.0).timeout.connect(func():
		push_error("PREPARE_TREE_IMPORTS=FAIL timeout")
		quit(1))
	if not Engine.is_editor_hint():
		push_error("PREPARE_TREE_IMPORTS=FAIL requires --editor")
		quit(1)
		return
	var filesystem := EditorInterface.get_resource_filesystem()
	while filesystem == null or filesystem.is_scanning() or filesystem.is_importing():
		await process_frame
		filesystem = EditorInterface.get_resource_filesystem()
	filesystem.reimport_files(PackedStringArray(SOURCES))
	while filesystem.is_scanning() or filesystem.is_importing():
		await process_frame
	for path in SOURCES:
		# A previous .scn can survive a failed reimport. Consult the updated import result,
		# not only whether that older resource can still be loaded.
		var directory := filesystem.get_filesystem_path(path.get_base_dir())
		var index := directory.find_file_index(path.get_file()) if directory != null else -1
		if index < 0 or not directory.get_file_import_is_valid(index):
			push_error("PREPARE_TREE_IMPORTS=FAIL invalid import " + path)
			quit(1)
			return
		if ResourceLoader.load(path, "PackedScene", ResourceLoader.CACHE_MODE_IGNORE) == null:
			push_error("PREPARE_TREE_IMPORTS=FAIL missing imported scene " + path)
			quit(1)
			return
	print("PREPARE_TREE_IMPORTS=PASS")
	quit(0)
