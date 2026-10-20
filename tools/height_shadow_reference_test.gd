extends SceneTree

## Shadow revamp S4b: GPU height-shadow march (ring_height_shadow_build.comp) against a double-precision CPU reference
## (RingTerrainShadowGeometry.SampleStraightRay + analytic structural walls) on the same lattice heights; fixtures, low-sun curvature tips,
## seam equivalence and build timing. Local RenderingDevice, so it needs a real Vulkan renderer (do not pass --headless):
##   godot --path . --script res://tools/height_shadow_reference_test.gd [-- --only=ridge]

const ReferenceTest := preload("res://tools/HeightShadowReferenceTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var passed: bool = ReferenceTest.new().Run()
	quit(0 if passed else 1)
