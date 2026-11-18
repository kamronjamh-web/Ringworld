extends SceneTree

## Forest lighting FL-1 headless coverage: canopy raster builder (single crown, area, gap product, mips, base/top, wrap, frame parity, determinism, 13,000 tree timing) and the crown hull table.
## Headless-safe:  godot --headless --path . --script res://tools/forest_canopy_raster_test.gd

const RasterTest := preload("res://tools/ForestCanopyRasterTest.cs")

func _init() -> void:
	var passed: bool = RasterTest.new().Run()
	quit(0 if passed else 1)
