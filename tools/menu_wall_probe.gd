extends SceneTree
## Diagnostic (D1 follow-up): does the live view show the same horizon (rim wall) as the boot snapshot, and when? Boots the menu on an
## ISOLATED phase_c_live APPDATA whose live slot holds a snapshot, then saves world-only presented frames (menu hidden) at several times
## after the reveal plus a forced-draw capture, with the top-35% luma difference against the snapshot. Output: res://.godot/phase_c_live/d1/wall_*.png
const MENU := preload("res://scenes/StartMenu.tscn")
const SNAPSHOT := preload("res://scripts/ui/menu_snapshot.gd")
const OUT := "res://.godot/phase_c_live/d1"

func _initialize() -> void:
	call_deferred("_run")

func _top_diff(a: Image, b: Image) -> float:
	var x := a.duplicate() as Image
	var y := b.duplicate() as Image
	x.convert(Image.FORMAT_RGB8); y.convert(Image.FORMAT_RGB8)
	x.resize(128, 72); y.resize(128, 72)
	var total := 0.0
	var rows := int(72 * 0.35)
	for j in rows:
		for i in 128:
			total += absf(x.get_pixel(i, j).get_luminance() - y.get_pixel(i, j).get_luminance())
	return total / float(rows * 128)

func _world_frame() -> Image:
	await process_frame
	await RenderingServer.frame_post_draw
	await RenderingServer.frame_post_draw
	return root.get_texture().get_image()

func _run() -> void:
	var user_dir := ProjectSettings.globalize_path("user://").replace("\\", "/").to_lower()
	if not user_dir.contains("phase_c_live"):
		push_error("not an isolated profile"); quit(2); return
	var session := root.get_node("WorldSession")
	for slot in range(1, 4):
		if FileAccess.file_exists(ProjectSettings.globalize_path(str(session.get_menu_snapshot_paths(slot)["json"]))):
			session.call("_touch_slot", slot)
	var live: int = session.get_live_menu_slot()
	var menu: Control = MENU.instantiate()
	root.add_child(menu)
	current_scene = menu
	var warm: Node = root.get_node("RingWarmWorld")
	while not bool(warm.reveal_complete):
		await process_frame
	print("WALL reveal_complete at ", Time.get_ticks_msec())
	await create_timer(1.5).timeout
	SNAPSHOT.wait_pending()
	var snap := Image.load_from_file(ProjectSettings.globalize_path(str(session.get_menu_snapshot_paths(live)["png"])))
	print("WALL snapshot loaded: ", snap != null and not snap.is_empty(), " used_snapshot=", warm.placement_used_snapshot())
	if snap != null: snap.save_png(ProjectSettings.globalize_path(OUT).path_join("wall_snapshot.png"))
	var t0 := Time.get_ticks_msec()
	var next := 0.0
	for stamp in [0.0, 4.0, 8.0, 16.0, 30.0]:
		while float(Time.get_ticks_msec() - t0) / 1000.0 < stamp:
			await process_frame
		menu.visible = false
		var image: Image = await _world_frame()
		menu.visible = true
		image.save_png(ProjectSettings.globalize_path(OUT).path_join("wall_t%02d.png" % int(stamp)))
		print("WALL t=%.0f top35_diff_vs_snapshot=%.4f" % [stamp, _top_diff(image, snap)])
	menu.visible = false
	RenderingServer.force_draw(false, 0.0)
	var forced := root.get_texture().get_image()
	forced.save_png(ProjectSettings.globalize_path(OUT).path_join("wall_forced.png"))
	print("WALL forced top35_diff_vs_snapshot=%.4f" % _top_diff(forced, snap))
	menu.visible = true
	await warm.teardown()
	quit(0)
