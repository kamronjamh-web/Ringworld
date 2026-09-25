extends SceneTree

const REGION_SIDE := 64
const FIRST_HEIGHT := 37.25
const SECOND_HEIGHT := 12.5
const DATA_DIRECTORY := "res://.godot/terrain3d-addon-smoke"


func _init() -> void:
	var terrain := Terrain3D.new()
	if terrain == null:
		_fail("Terrain3D native class could not be instantiated")
		return

	var make_directory_error := DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(DATA_DIRECTORY))
	if make_directory_error != OK:
		_fail("Could not create Terrain3D smoke data directory: %s" % error_string(make_directory_error))
		return
	terrain.free_editor_textures = false

	var camera := Camera3D.new()
	camera.current = true
	root.add_child(camera)
	terrain.set_camera(camera)
	root.add_child(terrain)
	# SceneTree._init runs before native nodes receive their ready notification.
	await process_frame
	terrain.change_region_size(REGION_SIDE)
	if terrain.get_region_size() != REGION_SIDE:
		_fail("Terrain3D did not apply the requested region size: expected=%s actual=%s" % [REGION_SIDE, terrain.get_region_size()])
		return
	terrain.data_directory = DATA_DIRECTORY
	terrain.data.load_directory(DATA_DIRECTORY)
	await process_frame
	var data: Terrain3DData = terrain.data
	if data == null:
		_fail("Terrain3D did not initialize its Terrain3DData resource")
		return

	var before := data.get_height(Vector3.ZERO)
	var height_image := Image.create(REGION_SIDE, REGION_SIDE, false, Image.FORMAT_RF)
	height_image.fill(Color(FIRST_HEIGHT, 0.0, 0.0, 1.0))

	var images: Array[Image] = []
	images.resize(Terrain3DRegion.TYPE_MAX)
	images[Terrain3DRegion.TYPE_HEIGHT] = height_image
	data.import_images(images, Vector3.ZERO, 0.0, 1.0)

	var first_import_height := data.get_height(Vector3.ZERO)
	if is_nan(first_import_height) or is_equal_approx(first_import_height, before):
		_fail("Terrain3DData height did not change after first RF height import; before=%s after=%s" % [before, first_import_height])
		return
	if not is_equal_approx(first_import_height, FIRST_HEIGHT):
		_fail("Terrain3DData returned an unexpected first imported height: expected=%s actual=%s" % [FIRST_HEIGHT, first_import_height])
		return

	height_image.fill(Color(SECOND_HEIGHT, 0.0, 0.0, 1.0))
	data.import_images(images, Vector3.ZERO, 0.0, 1.0)
	var second_import_height := data.get_height(Vector3.ZERO)
	if is_nan(second_import_height) or is_equal_approx(second_import_height, first_import_height):
		_fail("Terrain3DData height did not change after second RF height import; first=%s second=%s" % [first_import_height, second_import_height])
		return
	if not is_equal_approx(second_import_height, SECOND_HEIGHT):
		_fail("Terrain3DData returned an unexpected second imported height: expected=%s actual=%s" % [SECOND_HEIGHT, second_import_height])
		return

	print("Terrain3D native addon smoke: PASS (height %s -> %s -> %s)" % [before, first_import_height, second_import_height])
	quit(0)


func _fail(message: String) -> void:
	push_error(message)
	quit(1)
