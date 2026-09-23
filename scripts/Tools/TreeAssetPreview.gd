extends Node3D

## Isolated reviewer for the LOLIPOP tree packs.  It deliberately does not
## participate in the production vegetation, wind, lighting, or biome systems.

const PACKS := {
	"Fir": {"scene": "res://resources/trees/lolipop/fir/scene.gltf", "variant": "Christmas tree", "position": Vector3(-9.0, 0.0, 0.0)},
	"Pine": {"scene": "res://resources/trees/lolipop/pine/scene.gltf", "variant": "Pine_medium_1", "position": Vector3(0.0, 0.0, 0.0)},
	"Maple": {"scene": "res://resources/trees/lolipop/maple/scene.gltf", "variant": "Acer_medium_1", "position": Vector3(9.0, 0.0, 0.0)},
}
const LOD_ROOT_PATTERN := "^(.+?)(?:_Billboard)?_LOD([0-3])$"

var _lod := 0
var _yaw := PI * 0.5
var _pitch := 0.12
var _distance := 24.0
var _saved_mesh_lod_threshold := 1.0
@onready var _camera: Camera3D = $Camera3D
@onready var _status: Label = $CanvasLayer/Status
@onready var _target: Node3D = $CameraTarget

func _ready() -> void:
	# The scene intentionally has no Sky resource.  Use its explicit color fill
	# rather than the otherwise-black sky ambient source.
	var environment: Environment = $WorldEnvironment.environment
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	var viewport: Viewport = get_viewport()
	_saved_mesh_lod_threshold = viewport.mesh_lod_threshold
	# Manual author LOD selection must not be obscured by renderer mesh LODs.
	viewport.mesh_lod_threshold = 0.0
	for species in PACKS:
		_add_specimen(species, PACKS[species])
	_update_camera()
	_update_status()

func _exit_tree() -> void:
	get_viewport().mesh_lod_threshold = _saved_mesh_lod_threshold
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE

func _add_specimen(species: String, definition: Dictionary) -> void:
	var packed := load(definition.scene) as PackedScene
	if packed == null:
		push_error("Could not load %s tree pack: %s" % [species, definition.scene])
		return
	var source := packed.instantiate() as Node3D
	var specimen := Node3D.new()
	specimen.name = "%sSpecimen" % species
	specimen.position = definition.position
	$Specimens.add_child(specimen)
	var lod_roots := _find_lod_roots(source, definition.variant)
	if lod_roots.size() != 4:
		push_error("%s has %d LOD roots; expected four" % [species, lod_roots.size()])
	for lod_root in lod_roots:
		var copy := lod_root.duplicate() as Node3D
		# Preserve the source's composed orientation/scale, but remove the demo-layout
		# translation so each author LOD shares this gallery specimen's ground origin.
		var composed := _composed_transform(lod_root)
		composed.origin = Vector3.ZERO
		copy.transform = composed
		copy.position = Vector3.ZERO
		specimen.add_child(copy)
	source.queue_free()
	_hide_source_meshes(specimen)
	_select_lod(specimen, _lod)
	_apply_foliage_cutouts(specimen)

func _find_lod_roots(source: Node, variant: String) -> Array[Node3D]:
	var roots: Array[Node3D] = []
	var matcher := RegEx.new()
	matcher.compile(LOD_ROOT_PATTERN)
	for node in _descendants(source):
		var node_name := str(node.name)
		var match := matcher.search(node_name)
		if node is Node3D and match != null and match.get_string(1) == variant:
			roots.append(node as Node3D)
	return roots

func _composed_transform(node: Node3D) -> Transform3D:
	var chain: Array[Node3D] = []
	var current: Node = node
	while current is Node3D:
		chain.append(current as Node3D)
		current = current.get_parent()
	var result := Transform3D.IDENTITY
	for index in range(chain.size() - 1, -1, -1): result = result * chain[index].transform
	return result

func _hide_source_meshes(specimen: Node) -> void:
	# Author reference planes/checkers and every unselected variant remain hidden.
	for node in _descendants(specimen):
		if node is MeshInstance3D:
			(node as MeshInstance3D).visible = false

func _select_lod(specimen: Node, selected_lod: int) -> void:
	for node in _descendants(specimen):
		if node is MeshInstance3D:
			var mesh_instance := node as MeshInstance3D
			mesh_instance.visible = _mesh_is_selected_lod(mesh_instance, specimen.name, selected_lod)

func _mesh_is_selected_lod(mesh_instance: MeshInstance3D, specimen_name: String, selected_lod: int) -> bool:
	var node: Node = mesh_instance
	var is_lod := false
	while node != null:
		if node.name.contains("LOD%d" % selected_lod): is_lod = true
		node = node.get_parent()
	return is_lod

func _apply_foliage_cutouts(specimen: Node) -> void:
	for node in _descendants(specimen):
		if not node is MeshInstance3D:
			continue
		var mesh_instance := node as MeshInstance3D
		if mesh_instance.mesh == null:
			continue
		for surface in mesh_instance.mesh.get_surface_count():
			var material := mesh_instance.get_active_material(surface)
			if material is BaseMaterial3D and (material as BaseMaterial3D).transparency != BaseMaterial3D.TRANSPARENCY_DISABLED:
				var cutout := (material as BaseMaterial3D).duplicate() as BaseMaterial3D
				cutout.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA_SCISSOR
				# Fir's authored MASK cutoff is preserved exactly; blend foliage
				# receives a practical preview-only cutout threshold.
				if (material as BaseMaterial3D).transparency == BaseMaterial3D.TRANSPARENCY_ALPHA_SCISSOR:
					cutout.alpha_scissor_threshold = (material as BaseMaterial3D).alpha_scissor_threshold
				else:
					cutout.alpha_scissor_threshold = 0.25
				cutout.cull_mode = BaseMaterial3D.CULL_DISABLED
				mesh_instance.set_surface_override_material(surface, cutout)

func _descendants(node: Node) -> Array[Node]:
	var result: Array[Node] = []
	for child in node.get_children():
		result.append(child)
		result.append_array(_descendants(child))
	return result

func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventKey and event.pressed and not event.echo:
		if event.keycode == KEY_ESCAPE:
			Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
			return
		if event.keycode >= KEY_1 and event.keycode <= KEY_4:
			_lod = event.keycode - KEY_1
			for specimen in $Specimens.get_children():
				_select_lod(specimen, _lod)
			_update_status()
		elif event.keycode == KEY_F:
			_focus_species("Fir")
		elif event.keycode == KEY_P:
			_focus_species("Pine")
		elif event.keycode == KEY_M:
			_focus_species("Maple")
		elif event.keycode == KEY_R:
			$CameraTarget.position = Vector3.ZERO
			_distance = 24.0
			_yaw = PI * 0.5
			_pitch = 0.12
			_update_camera()
	if event is InputEventMouseButton and event.button_index == MOUSE_BUTTON_LEFT:
		Input.mouse_mode = Input.MOUSE_MODE_CAPTURED if event.pressed else Input.MOUSE_MODE_VISIBLE
	if event is InputEventMouseMotion and Input.mouse_mode == Input.MOUSE_MODE_CAPTURED:
		_yaw -= event.relative.x * 0.008
		_pitch = clampf(_pitch - event.relative.y * 0.008, 0.03, 1.15)
		_update_camera()
	if event is InputEventMouseButton and event.pressed:
		if event.button_index == MOUSE_BUTTON_WHEEL_UP:
			_distance = maxf(6.0, _distance - 1.5)
			_update_camera()
		elif event.button_index == MOUSE_BUTTON_WHEEL_DOWN:
			_distance = minf(70.0, _distance + 1.5)
			_update_camera()

func _notification(what: int) -> void:
	if what == NOTIFICATION_WM_WINDOW_FOCUS_OUT:
		Input.mouse_mode = Input.MOUSE_MODE_VISIBLE

func _process(delta: float) -> void:
	var movement := Vector3.ZERO
	if Input.is_key_pressed(KEY_W): movement.z -= 1.0
	if Input.is_key_pressed(KEY_S): movement.z += 1.0
	if Input.is_key_pressed(KEY_A): movement.x -= 1.0
	if Input.is_key_pressed(KEY_D): movement.x += 1.0
	if movement != Vector3.ZERO:
		var forward: Vector3 = -_camera.global_transform.basis.z.slide(Vector3.UP).normalized()
		var right: Vector3 = _camera.global_transform.basis.x.slide(Vector3.UP).normalized()
		var target: Vector3 = _target.position
		target += (forward * -movement.z + right * movement.x) * delta * 8.0
		_target.position = target
		_update_camera()

func _update_camera() -> void:
	var target: Vector3 = _target.global_position
	_camera.global_position = target + Vector3(cos(_yaw) * cos(_pitch), sin(_pitch), sin(_yaw) * cos(_pitch)) * _distance
	_camera.look_at(target + Vector3.UP * 4.0, Vector3.UP)

func _focus_species(species: String) -> void:
	_target.position = PACKS[species].position
	_distance = 14.0
	_update_camera()

func _update_status() -> void:
	_status.text = "LOLIPOP tree preview  |  LOD%d active (one representation per specimen)\n1–4: LOD0–LOD3 billboard   •   drag LMB: orbit   •   wheel: zoom   •   WASD: pan   •   F/P/M: focus species   •   R: reset\nFir (left) • Pine (center) • Maple (right)" % _lod
