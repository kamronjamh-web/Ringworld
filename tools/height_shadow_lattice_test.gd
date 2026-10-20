extends SceneTree

## Shadow revamp S4a: lattice math, CPU clipmap fill, recentre strips, bands, aperture carve and fill timing (docs/shadow-revamp-plan.md S4).
## Headless-safe:  godot --headless --path . --script res://tools/height_shadow_lattice_test.gd
## Prints HEIGHT_SHADOW_LATTICE=PASS plus HEIGHT_SHADOW_FILL_TIMING / HEIGHT_SHADOW_APERTURE lines. Timing differs Debug vs Release builds.

const LatticeTest := preload("res://tools/HeightShadowLatticeTest.cs")

func _init() -> void:
	var passed: bool = LatticeTest.new().Run()
	quit(0 if passed else 1)
