extends SceneTree

## Shadow revamp S4a GPU contract: the scatter upload (slot rects, seam-straddling strips) makes the GPU height buffer bit-equal to the CPU master,
## and every max-mip cell is >= the bilinear sample anywhere inside it (mips bit-equal to the CPU reference). Local RenderingDevice.
## Requires a real Vulkan renderer (do not pass --headless):  godot --path . --script res://tools/height_shadow_gpu_test.gd

const LatticeTest := preload("res://tools/HeightShadowLatticeTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var passed: bool = LatticeTest.new().RunGpu()
	quit(0 if passed else 1)
