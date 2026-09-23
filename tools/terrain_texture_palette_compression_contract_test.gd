extends SceneTree

const TEXTURE_PATHS := [
	"res://materials/third_party/voxelgame/grass_albedo.png",
	"res://materials/biomes/desert_sand_albedo.png",
	"res://materials/biomes/forest_floor_albedo.png",
	"res://materials/biomes/tundra_ground_albedo.png",
	"res://materials/biomes/alpine_rock_albedo.png",
]
const VISUALS_SOURCE := "res://scripts/Voxel/Phase3/RingTerrainVisuals.cs"

func _init() -> void:
	var source := FileAccess.get_file_as_string(VISUALS_SOURCE)
	if not source.contains("image.IsCompressed() && image.Decompress() != Error.Ok"):
		_fail("Terrain palette loader no longer decompresses compressed CPU images before sampling")
		return
	if not source.contains("image.Dispose()"):
		_fail("Terrain palette loader no longer releases its temporary Image copy")
		return

	for path in TEXTURE_PATHS:
		var texture := load(path) as Texture2D
		if texture == null:
			_fail("Could not load terrain palette texture: %s" % path)
			return
		var image := texture.get_image()
		if image == null or image.is_empty():
			_fail("Could not obtain CPU image for terrain palette texture: %s" % path)
			return
		if image.is_compressed() and image.decompress() != OK:
			_fail("Could not decompress terrain palette texture: %s" % path)
			return
		if image.is_compressed():
			_fail("Terrain palette image remains compressed before GetPixel: %s" % path)
			return
		var pixel := image.get_pixel(image.get_width() / 2, image.get_height() / 2)
		if not is_finite(pixel.r) or not is_finite(pixel.g) or not is_finite(pixel.b):
			_fail("Terrain palette image returned a non-finite pixel: %s" % path)
			return
		image = null

	print("Terrain texture palette compression contract passed")
	quit(0)

func _fail(message: String) -> void:
	push_error(message)
	quit(1)
