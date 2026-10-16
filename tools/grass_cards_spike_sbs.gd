extends SceneTree
# Side-by-side composer for the spike screenshots (left variant a, right variant b). Headless-safe.
# Usage: -- dir=<screenshot dir> a=P0 b=C,D,E reach=300 shots=standing,feet
func _initialize() -> void:
	var args := {}
	for a in OS.get_cmdline_user_args():
		var kv := a.split("=", true, 1)
		if kv.size() == 2:
			args[kv[0]] = kv[1]
	var dir: String = args.get("dir", "")
	var left: String = args.get("a", "P0")
	var reach: String = args.get("reach", "300")
	for v in String(args.get("b", args.get("variants", "A,B,C"))).split(","):
		for shot in String(args.get("shots", "standing,feet")).split(","):
			var a := Image.load_from_file(dir.path_join("%s_r%s_%s.png" % [left, reach, shot]))
			var b := Image.load_from_file(dir.path_join("%s_r%s_%s.png" % [v, reach, shot]))
			a.convert(Image.FORMAT_RGBA8)
			b.convert(Image.FORMAT_RGBA8)
			a.resize(960, 540, Image.INTERPOLATE_LANCZOS)
			b.resize(960, 540, Image.INTERPOLATE_LANCZOS)
			var o := Image.create(1930, 540, false, Image.FORMAT_RGBA8)
			o.fill(Color.BLACK)
			o.blit_rect(a, Rect2i(0, 0, 960, 540), Vector2i(0, 0))
			o.blit_rect(b, Rect2i(0, 0, 960, 540), Vector2i(970, 0))
			o.save_png(dir.path_join("sbs_%s_vs_%s_%s.png" % [left, v, shot]))
	quit(0)
