extends SceneTree

## Shadow revamp S4a probe: do sampler2DArray shader globals fed by Texture2DArrayRD work in this Godot build?
## Creates a 3-layer RG16F RD texture array, wraps it in Texture2DArrayRD, assigns it to the project global ringworld_height_shadow_l0, renders a quad that
## samples each layer through `global uniform sampler2DArray`, reads the pixels back and compares. Also checks texture_create_shared_from_slice
## (per-layer storage views, needed to write one layer while another is sampled) and that a rewrite of the RD texture is seen through the global.
## Requires a real renderer:  godot --path . --script res://tools/height_shadow_texture_array_probe.gd

const SHADER := """shader_type canvas_item;
global uniform sampler2DArray ringworld_height_shadow_l0;
uniform float layer = 0.0;
void fragment() {
	vec4 t = texture(ringworld_height_shadow_l0, vec3(UV, layer));
	COLOR = vec4(t.rg, 0.0, 1.0);
}
"""

var _viewport: SubViewport
var _rect: ColorRect
var _material: ShaderMaterial
var _failed := false

func _init() -> void:
	call_deferred("_run")

func _fail(message: String) -> void:
	_failed = true
	push_error("TEXTURE_ARRAY_PROBE=FAIL " + message)

func _run() -> void:
	var rd := RenderingServer.get_rendering_device()
	if rd == null:
		_fail("no RenderingDevice")
		quit(1)
		return
	var layers := 3
	var size := 4
	var format := RDTextureFormat.new()
	format.texture_type = RenderingDevice.TEXTURE_TYPE_2D_ARRAY
	format.format = RenderingDevice.DATA_FORMAT_R32G32_SFLOAT
	format.width = size
	format.height = size
	format.array_layers = layers
	format.usage_bits = RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT | RenderingDevice.TEXTURE_USAGE_STORAGE_BIT | RenderingDevice.TEXTURE_USAGE_CAN_UPDATE_BIT | RenderingDevice.TEXTURE_USAGE_CAN_COPY_FROM_BIT
	var layer_data: Array[PackedByteArray] = []
	for layer in layers:
		var floats := PackedFloat32Array()
		for i in size * size:
			floats.append(float(layer) + 0.25)
			floats.append(float(i) * 0.5)
		layer_data.append(floats.to_byte_array())
	var texture := rd.texture_create(format, RDTextureView.new(), layer_data)
	if not texture.is_valid():
		_fail("texture_create (2D array) failed")
		quit(1)
		return
	var wrapper := Texture2DArrayRD.new()
	wrapper.texture_rd_rid = texture
	RenderingServer.global_shader_parameter_set("ringworld_height_shadow_l0", wrapper)
	var slice := rd.texture_create_shared_from_slice(RDTextureView.new(), texture, 1, 0, 1, RenderingDevice.TEXTURE_SLICE_2D)
	print("TEXTURE_ARRAY_PROBE slice_view_valid=", slice.is_valid())
	if not slice.is_valid():
		_fail("texture_create_shared_from_slice failed")

	_viewport = SubViewport.new()
	_viewport.size = Vector2i(size, size)
	_viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	_viewport.transparent_bg = false
	root.add_child(_viewport)
	_rect = ColorRect.new()
	_rect.size = Vector2(size, size)
	var shader := Shader.new()
	shader.code = SHADER
	_material = ShaderMaterial.new()
	_material.shader = shader
	_rect.material = _material
	_viewport.add_child(_rect)

	for layer in layers:
		_material.set_shader_parameter("layer", float(layer))
		var pixel := await _read_pixel(1, 1)
		var expected := Vector2(float(layer) + 0.25, 5.0 * 0.5)
		print("TEXTURE_ARRAY_PROBE layer=", layer, " rg=", pixel, " expected=", expected)
		# 8-bit LDR target: values above 1 clamp; compare in a normalised way below.
	# The viewport is 8-bit, so use small values for the exact check: rewrite layer 2 with fractions and read back.
	var small := PackedFloat32Array()
	for i in size * size:
		small.append(0.5)
		small.append(0.25)
	rd.texture_update(texture, 2, small.to_byte_array())
	_material.set_shader_parameter("layer", 2.0)
	var rewritten := await _read_pixel(2, 2)
	print("TEXTURE_ARRAY_PROBE rewritten layer2 rg=", rewritten, " expected (0.5, 0.25)")
	if absf(rewritten.x - 0.5) > 0.01 or absf(rewritten.y - 0.25) > 0.01:
		_fail("layer 2 rewrite not visible through the sampler2DArray global")
	_material.set_shader_parameter("layer", 1.0)
	var untouched := await _read_pixel(2, 2)
	print("TEXTURE_ARRAY_PROBE layer1 after rewrite rg=", untouched, " (layer 1 saturates: x=1.25->1, y=7.5*0.5->1 expected 1,1)")
	if untouched.x < 0.99 or untouched.y < 0.99:
		_fail("layer 1 changed or the layer index is not honoured")
	if not _failed:
		print("TEXTURE_ARRAY_PROBE=PASS Texture2DArrayRD + sampler2DArray global work")
	wrapper.texture_rd_rid = RID()
	quit(1 if _failed else 0)

func _read_pixel(x: int, y: int) -> Vector2:
	for i in 4:
		await process_frame
	var image := _viewport.get_texture().get_image()
	var c := image.get_pixel(x, y)
	return Vector2(c.r, c.g)
