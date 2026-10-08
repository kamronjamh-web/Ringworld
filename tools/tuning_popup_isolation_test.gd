extends SceneTree

# Correctness regression for tuning-owned embedded UI windows. Inherited
# BG_CANVAS + AgX can process the eyedropper's frozen screen image a second time.
# Run with Godot .NET --path . --rendering-method forward_plus --script
# res://tools/tuning_popup_isolation_test.gd. Captures are correctness artifacts;
# this fixture measures neither FPS nor GPU savings.

const DRIVER := preload("res://tools/TuningPopupIsolationTestDriver.cs")
const OUTPUT := "res://artifacts/tuning-popup-isolation/"
var failed := false
var driver: RefCounted

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null: fail("Forward+ RenderingDevice required"); return
	create_timer(30.0).timeout.connect(func(): fail("correctness fixture timed out"))
	call_deferred("run")

func run() -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT))
	DisplayServer.window_set_size(Vector2i(1000, 700))
	var environment := Environment.new()
	environment.background_mode = Environment.BG_CANVAS
	environment.background_canvas_max_layer = 0
	environment.tonemap_mode = Environment.TONE_MAPPER_AGX
	environment.tonemap_exposure = 1.5
	var world := WorldEnvironment.new(); world.environment = environment; root.add_child(world)
	var root_world := root.find_world_3d()
	var background := ColorRect.new(); background.color = Color(.4, .7, .2)
	background.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT); root.add_child(background)
	var layer := CanvasLayer.new(); layer.layer = 1; root.add_child(layer)
	var owner := Control.new(); owner.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT); layer.add_child(owner)
	var seeded := PopupPanel.new(); owner.add_child(seeded)
	driver = DRIVER.new(); driver.call("Attach", owner)
	if not require(isolated(seeded, root_world), "preexisting popup was not seeded"): return
	var option := OptionButton.new(); option.position = Vector2(700, 20); option.size = Vector2(200, 40); option.add_item("First"); option.add_item("Second"); owner.add_child(option)
	var option_popup := option.get_popup()
	if not require(isolated(option_popup, root_world), "engine-created OptionButton popup was not isolated synchronously"): return
	option_popup.popup(Rect2i(700, 60, 200, 90))
	await capture("dropdown")
	var selected := [-1]
	option.item_selected.connect(func(index: int): selected[0] = index)
	# OptionButton connects PopupMenu.index_pressed to its selection handler.
	option_popup.emit_signal("index_pressed", 1)
	if not require(selected[0] == 1 and option.selected == 1, "isolated dropdown selection stopped working"): return
	option_popup.hide()
	var confirmation := AcceptDialog.new(); owner.add_child(confirmation)
	if not require(isolated(confirmation, root_world), "owned AcceptDialog was not isolated"): return
	var preserved_preview := PopupPanel.new(); preserved_preview.set_meta("tuning_preserve_3d_world", true); owner.add_child(preserved_preview)
	if not require(not preserved_preview.disable_3d and preserved_preview.find_world_3d() == root_world, "explicit preview opt-out was ignored"): return
	var preserved_nested := PopupPanel.new(); preserved_preview.add_child(preserved_nested)
	if not require(not preserved_nested.disable_3d, "preview opt-out did not preserve nested world boundary"): return
	var nested := PopupPanel.new(); option_popup.add_child(nested)
	if not require(isolated(nested, root_world), "nested popup was not isolated"): return
	# A deliberate 3D Window and any SubViewport stay outside this policy.
	var preview := Window.new(); preview.visible = false; owner.add_child(preview)
	if not require(preview.find_world_3d() == root_world and not preview.disable_3d, "deliberate standalone Window preview was isolated"): return
	var far := SubViewport.new(); owner.add_child(far)
	var preview_popup := PopupPanel.new(); far.add_child(preview_popup)
	if not require(not preview_popup.disable_3d, "SubViewport preview descendant was isolated"): return
	var unowned := PopupPanel.new(); root.add_child(unowned)
	if not require(not unowned.disable_3d and unowned.find_world_3d() == root_world, "unowned popup was isolated"): return
	var tooltip_button := Button.new(); tooltip_button.position = Vector2(700, 620); tooltip_button.size = Vector2(200, 40); tooltip_button.text = "Hover tooltip"; tooltip_button.tooltip_text = "Owned automatic tooltip"; owner.add_child(tooltip_button)
	# Settle popup hiding and layout, then drive the viewport's real hover
	# dispatch directly; OS pointer position is irrelevant to the fixture.
	await process_frame
	await process_frame
	root.notify_mouse_entered()
	var hover := InputEventMouseMotion.new(); hover.position = tooltip_button.get_global_rect().get_center()
	root.push_input(hover, true)
	if not require(root.gui_get_hovered_control() == tooltip_button, "tooltip control did not receive viewport hover: " + str(root.gui_get_hovered_control())): return
	await create_timer(1.5).timeout
	var tooltip_windows := windows_under(tooltip_button)
	if not require(not tooltip_windows.is_empty(), "automatic tooltip was not created"): return
	for tooltip_window in tooltip_windows:
		if not require(isolated(tooltip_window, root_world), "automatic tooltip inherited game world"): return
	await capture("tooltip")
	var leave := InputEventMouseMotion.new(); leave.position = Vector2(20, 20); root.push_input(leave, true)
	await process_frame
	var button := ColorPickerButton.new(); button.position = Vector2(700, 140); button.size = Vector2(100, 40); button.edit_alpha = false; owner.add_child(button)
	var picker := button.get_picker()
	var popup := button.get_popup()
	if not require(isolated(popup, root_world), "ColorPicker popup was not isolated synchronously"): return
	var original := await capture("before_picker")
	popup.popup(Rect2i(550, 100, 400, 480))
	var opened := await capture("picker")
	var sample_point := Vector2i(150, 500)
	var displayed := opened.get_pixelv(sample_point)
	if not require(original.get_pixelv(sample_point) == displayed, "opening picker changed game colors"): return
	var sampler_button := find_sampler(picker)
	if not require(sampler_button != null, "eyedropper button not found"): return
	sampler_button.emit_signal("pressed")
	var sampler: Window
	for candidate in windows_under(picker):
		if candidate.visible: sampler = candidate; break
	if not require(sampler != null and isolated(sampler, root_world), "nested eyedropper was not isolated before first capture"): return
	var eyedropper := await capture("eyedropper")
	if not require(eyedropper.get_pixelv(sample_point) == displayed, "eyedropper screenshot was processed twice: original=%s overlay=%s" % [displayed, eyedropper.get_pixelv(sample_point)]): return
	# Embedded ColorPicker uses its frozen-image TextureRect.gui_input.
	# Motion resolves picker_color in texture-local coordinates; left release
	# commits it. Sending Window.window_input alone bypasses that handler.
	var sample_texture: TextureRect
	for child in sampler.get_children(true):
		if child is TextureRect: sample_texture = child; break
	if not require(sample_texture != null, "eyedropper screenshot TextureRect not found"): return
	var local_point := Vector2(sample_point) - Vector2(sampler.position) - sample_texture.position
	var motion := InputEventMouseMotion.new(); motion.position = local_point; motion.global_position = Vector2(root.position + sample_point)
	sample_texture.emit_signal("gui_input", motion)
	var click := InputEventMouseButton.new(); click.button_index = MOUSE_BUTTON_LEFT; click.pressed = true; click.position = local_point; click.global_position = motion.global_position
	sample_texture.emit_signal("gui_input", click)
	var release := click.duplicate() as InputEventMouseButton; release.pressed = false
	sample_texture.emit_signal("gui_input", release)
	await process_frame
	if not require(color_distance(picker.color, displayed) < .005, "sampled color differs from displayed pixel: sampled=%s displayed=%s" % [picker.color, displayed]): return
	if not require(root.find_world_3d() == root_world and root_world.environment == environment and environment.background_mode == Environment.BG_CANVAS and environment.tonemap_mode == Environment.TONE_MAPPER_AGX and is_equal_approx(environment.tonemap_exposure, 1.5) and not root.disable_3d, "popup isolation modified root world/exposure"): return
	driver.call("Stop"); driver.call("Stop")
	var after_dispose := PopupPanel.new(); owner.add_child(after_dispose)
	if not require(not after_dispose.disable_3d and after_dispose.find_world_3d() == root_world, "NodeAdded handler survived disposal"): return
	owner.queue_free(); unowned.queue_free(); world.queue_free(); background.queue_free(); layer.queue_free()
	await process_frame
	print("TUNING_POPUP_ISOLATION=PASS tooltip/dropdown/picker/eyedropper, exact displayed pixel, sampling, scope, cleanup")
	quit(0)

func isolated(window: Window, game_world: World3D) -> bool:
	return window.disable_3d and window.find_world_3d() != game_world and window.find_world_3d().environment == null

func windows_under(node: Node) -> Array[Window]:
	var found: Array[Window] = []
	for child in node.get_children(true):
		if child is Window: found.append(child)
		found.append_array(windows_under(child))
	return found

func find_sampler(node: Node) -> Button:
	if node is Button and "Pick a color from" in node.tooltip_text: return node
	for child in node.get_children(true):
		var found := find_sampler(child)
		if found != null: return found
	return null

func color_distance(a: Color, b: Color) -> float:
	return absf(a.r-b.r) + absf(a.g-b.g) + absf(a.b-b.b)

func capture(label: String) -> Image:
	for frame in range(4): await RenderingServer.frame_post_draw
	var image := root.get_texture().get_image()
	image.save_png(OUTPUT + label + ".png")
	return image

func require(condition: bool, message: String) -> bool:
	if condition: return true
	fail(message); return false

func fail(message: String) -> void:
	if failed: return
	failed = true
	if driver != null: driver.call("Stop")
	push_error("TUNING_POPUP_ISOLATION=FAIL " + message)
	quit(1)
