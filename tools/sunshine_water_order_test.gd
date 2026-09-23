extends SceneTree

const TEST_SCENE := preload("res://addons/SunshineClouds2/ExampleScene.tscn")
const GeneratedOutputPaths := preload("res://tools/generated_output_paths.gd")
const CAPTURE_FILE := "sunshine_water_order.png"

var _scene: Node3D
var _camera: Camera3D
var _frame := 0
var _capture_path := ""


func _initialize() -> void:
	_capture_path = GeneratedOutputPaths.smoke_capture_path(CAPTURE_FILE)
	_scene = TEST_SCENE.instantiate()
	root.add_child(_scene)
	_camera = _scene.get_node("Camera3D") as Camera3D
	_camera.position = Vector3(0.0, 30000.0, 0.0)
	_camera.rotation_degrees = Vector3(-80.0, 0.0, 0.0)
	_add_depth_writing_water()


func _process(_delta: float) -> bool:
	_frame += 1
	if _frame == 120:
		var image := root.get_texture().get_image()
		var error := image.save_png(_capture_path)
		if error != OK:
			push_error("Sunshine water-order capture failed: %s (%s)" % [_capture_path, error])
			quit(1)
		else:
			print("Sunshine water-order test complete: %s" % _capture_path)
			quit(0)
	return false


func _add_depth_writing_water() -> void:
	var water_mesh := PlaneMesh.new()
	water_mesh.size = Vector2(1000000.0, 1000000.0)
	var water_material := StandardMaterial3D.new()
	water_material.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	water_material.depth_draw_mode = BaseMaterial3D.DEPTH_DRAW_ALWAYS
	water_material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	water_material.albedo_color = Color(0.015, 0.11, 0.17, 0.97)
	water_mesh.material = water_material
	var water := MeshInstance3D.new()
	water.name = "DepthWritingWaterRegressionSurface"
	water.mesh = water_mesh
	_scene.add_child(water)
