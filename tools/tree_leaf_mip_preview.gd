extends SceneTree

## TR-7c-PV (preview only, nothing shipped): the tree models with today's leaf mips beside the distance-stable mips built by tools/prepare_tree_leaf_mips.gd (res://.godot/tr7c/preview).
## The match test's scene and lens (haze off, white ambient or noon sun, magenta measuring background; 8.3 degrees over 128 px at 325 m), materials duplicated in the driver, never the catalog's own.
## Looks: today (as shipped), stable (preview texture, tool-local shader with the isotropic leaf alpha sampler), ship (preview texture, shipped shader), ref (shipped textures at mip 0; authored LOD2, 512 px).
## The model is what the shipped defaults draw: LOD0 at 20 m, the authored LOD2 at 80 m, LOD2b (authored where the render LOD resource keeps it) from 325 m (TR-5 mid fade 130 to 150 m).
## Stages (environment TREE_LEAF_MIP_STAGES, default all): gate, sheets, numbers, dolly. Set (TREE_LEAF_MIP_SET): "_cl" (closed loop, default) or "" (open loop).
## Outputs under res://.godot/tr7c/preview/: tr7c_preview_sheet.png, tr7c_preview_detail.png, preview_table.csv, preview_report.json, dolly/<variant>/{today,stable,both}/frame########.png.
## Run: tools/run_tests.ps1 -Tests tree_leaf_mip_preview -Label tr7c
const DIR := "res://.godot/tr7c/preview"
const FOV_BASE := 8.3
const MAGENTA := Color(1.0, 0.0, 1.0, 1.0)
const GREY := Color(0.55, 0.55, 0.55, 1.0)
const AMBIENT := -1
const NOON := 0
const MIXED := 1
const UPV := Vector3(0, 0, 1)
const SPECIES := ["maple", "fir", "pine"]
const SHEET_VARIANTS := [3, 13, 19]
const AZ := [15.0, 45.0, 75.0, 105.0, 135.0, 165.0, 195.0, 225.0, 255.0, 285.0, 315.0, 345.0]

var failed := false
var done := false
var viewport: SubViewport
var environment: Environment
var camera: Camera3D
var sun: DirectionalLight3D
var driver: Node
var instance: MeshInstance3D
var report := {"gates": {}, "failures": []}
var stages := "gate,sheets,numbers,dolly"

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice required")
		return
	call_deferred("_run")

func _run() -> void:
	create_timer(3400.0).timeout.connect(func(): _fail("watchdog timeout"))
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	var env_stages := OS.get_environment("TREE_LEAF_MIP_STAGES")
	if env_stages != "": stages = env_stages
	var set_suffix := OS.get_environment("TREE_LEAF_MIP_SET")
	if set_suffix == "" and not OS.get_environment("TREE_LEAF_MIP_SET_OPEN") == "1": set_suffix = "_cl"
	_make_scene()
	var error: String = driver.call("Init", DIR, set_suffix)
	if error != "":
		_fail("chain gate: " + error)
		return
	report["gates"]["chains_load_with_built_levels"] = true
	report["set"] = set_suffix if set_suffix != "" else "open"
	var out_abs := ProjectSettings.globalize_path(DIR)
	if "gate" in stages:
		await _level0_gate()
		if failed: return
	if "sheets" in stages:
		await _sheets()
		if failed: return
	if "numbers" in stages:
		await _numbers()
		if failed: return
	if "dolly" in stages:
		await _dolly()
		if failed: return
	var file := FileAccess.open(DIR.path_join("preview_report.json"), FileAccess.WRITE)
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	if report["failures"].size() > 0:
		_fail("gate failures: %s" % [report["failures"]])
		return
	print("TREE_LEAF_MIP_PREVIEW=PASS set=%s stages=%s out=%s" % [report["set"], stages, out_abs])
	done = true
	quit(0)

func _fail(message: String) -> void:
	if done: return
	failed = true
	done = true
	push_error("TREE_LEAF_MIP_PREVIEW=FAIL " + message)
	quit(1)

# --- scene -------------------------------------------------------------------------------------------------------------------------------------------------

func _make_scene() -> void:
	viewport = SubViewport.new()
	viewport.size = Vector2i(128, 128)
	viewport.own_world_3d = true
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)
	environment = Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = MAGENTA
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	environment.ambient_light_color = Color(1, 1, 1)
	environment.ambient_light_energy = 0.0
	environment.reflected_light_source = Environment.REFLECTION_SOURCE_DISABLED
	environment.tonemap_mode = Environment.TONE_MAPPER_LINEAR
	var world := WorldEnvironment.new()
	world.environment = environment
	viewport.add_child(world)
	camera = Camera3D.new()
	camera.fov = FOV_BASE
	camera.near = 1.0
	camera.far = 5000.0
	camera.current = true
	viewport.add_child(camera)
	sun = DirectionalLight3D.new()
	sun.light_energy = 1.0
	sun.shadow_enabled = false
	viewport.add_child(sun)
	driver = load("res://tools/TreeLeafMipPreviewDriver.cs").new()
	root.add_child(driver)
	instance = MeshInstance3D.new()
	instance.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	viewport.add_child(instance)
	RenderingServer.global_shader_parameter_set("ringworld_solar_visibility", 1.0)
	RenderingServer.global_shader_parameter_set("ring_local_wind_enabled", 0.0)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_ray_frame_valid", true)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_camera_world_position", Vector3.ZERO)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_camera_height_meters", 0.0)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_outward_world", Vector3.RIGHT)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_axis_world", Vector3.UP)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_sidewall_negative_meters", 1000000.0)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_sidewall_positive_meters", 1000000.0)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_coordinate_scale", 1.0)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_to_sun", Vector3.LEFT)

func _height(variant: int) -> float:
	return 18.0 if variant < 12 else (11.0 if variant < 15 else 20.0)

func _species(variant: int) -> int:
	return 0 if variant < 12 else (1 if variant < 15 else 2)

func _fov_for(size: int) -> float:
	return rad_to_deg(2.0 * atan(float(size) / 128.0 * tan(deg_to_rad(FOV_BASE * 0.5))))

## One capture. lighting AMBIENT (white ambient only), NOON (sun overhead only) or MIXED (dolly: ambient 0.35 and a 50 degree sun).
func _shot(variant: int, kind: String, look: String, vsize: Vector2i, dist: float, fov: float, lighting: int, az_deg: float, tilt_deg: float, frac: float, bg: Color) -> Image:
	if viewport.size != vsize:
		viewport.size = vsize
		for i in 6: await RenderingServer.frame_post_draw
	var elevation := deg_to_rad(90.0 if lighting == NOON or lighting == AMBIENT else 50.0)
	var sun_direction := Vector3(cos(elevation), 0.0, sin(elevation))
	sun.transform = Transform3D(Basis.looking_at(-sun_direction, Vector3.UP), Vector3.ZERO)
	sun.visible = lighting != AMBIENT
	environment.ambient_light_energy = 1.0 if lighting == AMBIENT else (0.35 if lighting == MIXED else 0.0)
	environment.background_color = bg
	driver.call("SetSun", sun_direction)
	driver.call("SetModel", instance, variant, kind, look)
	instance.transform = Transform3D(Basis(Vector3.RIGHT, PI * 0.5).scaled(Vector3.ONE * _height(variant)), Vector3.ZERO)
	camera.projection = Camera3D.PROJECTION_PERSPECTIVE
	camera.fov = fov
	var az := deg_to_rad(az_deg)
	var tilt := deg_to_rad(tilt_deg)
	var middle := UPV * _height(variant) * frac
	var position := middle + Vector3(cos(az) * cos(tilt), sin(az) * cos(tilt), sin(tilt)) * dist
	camera.transform = Transform3D(Basis.looking_at(middle - position, UPV), position)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_camera_world_position", position)
	for i in 3: await RenderingServer.frame_post_draw
	return viewport.get_texture().get_image()

func _stats(image: Image) -> Array:
	var s: PackedFloat32Array = driver.call("MeasureForeground", image, MAGENTA)
	return [s[0] / float(image.get_width() * image.get_height()), s[1], s[2], s[3], s[4]]

# --- level-0 gate --------------------------------------------------------------------------------------------------------------------------------------------

## The stable chain's level 0 is the import's: a 1:1 texel render (orthographic, 64 texels over 64 px) of one window of each atlas, today against stable, in ambient light, must agree
## (same covered pixels, colour within 2/255). It would catch a texture that is not read as sRGB the way the import is.
func _level0_gate() -> void:
	viewport.size = Vector2i(64, 64)
	for i in 6: await RenderingServer.frame_post_draw
	var results := {}
	for atlas: String in driver.call("AtlasNames"):
		var w: PackedFloat32Array = driver.call("FindWindow", atlas)
		var mesh: Mesh = driver.call("BuildQuad", w[0], w[1], w[2])
		var images := {}
		for look in ["today", "stable"]:
			var material: ShaderMaterial = driver.call("AtlasMaterial", atlas, look)
			instance.mesh = mesh
			instance.set_surface_override_material(0, material)
			instance.transform = Transform3D.IDENTITY
			environment.ambient_light_energy = 1.0
			environment.background_color = MAGENTA
			sun.visible = false
			camera.projection = Camera3D.PROJECTION_ORTHOGONAL
			camera.size = 64.0
			camera.transform = Transform3D(Basis.looking_at(Vector3.LEFT, UPV), Vector3(100, 0, 0))
			for i in 4: await RenderingServer.frame_post_draw
			images[look] = viewport.get_texture().get_image()
		var c: PackedFloat32Array = driver.call("CompareImages", images["today"], images["stable"], MAGENTA)
		var ok: bool = c[0] > 200 and c[1] <= c[0] * 0.01 and c[2] <= 2.0
		results[atlas] = {"covered_both": c[0], "covered_one_only": c[1], "max_channel_diff_255": c[2], "pass": ok}
		if not ok: report["failures"].append("level-0 gate %s: both %d, one only %d, max diff %.1f/255" % [atlas, c[0], c[1], c[2]])
	report["gates"]["level0_render_equal"] = results
	camera.projection = Camera3D.PROJECTION_PERSPECTIVE
	print("TREE_LEAF_MIP_PREVIEW level-0 gate: %s" % [results])

# --- sheets ----------------------------------------------------------------------------------------------------------------------------------------------------

func _enlarged(image: Image, factor: int) -> Image:
	var copy := image.duplicate() as Image
	copy.resize(image.get_width() * factor, image.get_height() * factor, Image.INTERPOLATE_NEAREST)
	return copy

## Far tiles are drawn at their real pixel size (128, 64 and 46 px viewports with the match lens; 20 m and 80 m: a 256 px window at the same pixel scale) and enlarged with nearest filtering.
func _sheets() -> void:
	const CELL := 276
	const LABEL := 20
	const ROWLAB := 96
	const HEAD := 26
	var cols := [[20.0, "lod0", 256, 1], [80.0, "lod2", 256, 1], [325.0, "lod2b", 128, 2], [650.0, "lod2b", 64, 4], [900.0, "lod2b", 46, 6]]
	var items := []
	var row := 0
	for variant: int in SHEET_VARIANTS:
		for lighting in [AMBIENT, NOON]:
			var y := HEAD + row * (CELL + LABEL)
			items.append({"text": "v%d\n%s\n%s" % [variant, SPECIES[_species(variant)], "ambient" if lighting == AMBIENT else "noon sun"], "pos": Vector2(4, y + LABEL)})
			var col := 0
			for c: Array in cols:
				for look in ["today", "stable"]:
					var dist: float = c[0]
					var size: int = c[2]
					var near: bool = dist < 100.0
					var d := dist if near else 325.0
					var fov := _fov_for(size) if near else FOV_BASE
					var frac := 0.55 if near else 0.5
					var image: Image = await _shot(variant, c[1], look, Vector2i(size, size), d, fov, lighting, 30.0, 0.0, frac, GREY)
					var tile := _enlarged(image, c[3])
					var x := ROWLAB + col * CELL
					items.append({"text": "%d m %s (%d px)" % [int(dist), look, size], "pos": Vector2(x, y)})
					items.append({"image": tile, "pos": Vector2(x + (CELL - tile.get_width()) / 2, y + LABEL + (CELL - tile.get_height()) / 2)})
					col += 1
			row += 1
	await _layout(Vector2i(ROWLAB + 10 * CELL, HEAD + row * (CELL + LABEL)), items, DIR.path_join("tr7c_preview_sheet.png"),
		"TR-7c preview: today (shipped mips) beside stable mips; the shipped defaults' model at each distance (LOD0, authored LOD2, LOD2b); far tiles at real pixel size, enlarged x2 / x4 / x6")
	# Detail sheet: at 325 m and 900 m today, stable, stable with the shipped (anisotropic) shader, and the reference model (authored LOD2 at mip 0, 512 px).
	items = []
	row = 0
	for variant: int in SHEET_VARIANTS:
		for dist in [325, 900]:
			for lighting in [AMBIENT, NOON]:
				var y := HEAD + row * (CELL + LABEL)
				items.append({"text": "v%d\n%s\n%d m\n%s" % [variant, SPECIES[_species(variant)], dist, "ambient" if lighting == AMBIENT else "noon sun"], "pos": Vector2(4, y + LABEL)})
				var size := 128 if dist == 325 else 46
				var col := 0
				for look in ["today", "stable", "ship", "ref"]:
					var tile: Image
					if look == "ref":
						var image: Image = await _shot(variant, "lod2", "ref", Vector2i(512, 512), 325.0, FOV_BASE, lighting, 30.0, 0.0, 0.5, GREY)
						tile = image.duplicate() as Image
						tile.resize(CELL - 20, CELL - 20, Image.INTERPOLATE_LANCZOS)
					else:
						var image: Image = await _shot(variant, "lod2b", look, Vector2i(size, size), 325.0, FOV_BASE, lighting, 30.0, 0.0, 0.5, GREY)
						tile = _enlarged(image, 2 if size == 128 else 6)
					var x := ROWLAB + col * CELL
					var names := {"today": "today", "stable": "stable (isotropic alpha)", "ship": "stable, shipped shader", "ref": "reference model (mip 0, 512 px)"}
					items.append({"text": "%s" % names[look], "pos": Vector2(x, y)})
					items.append({"image": tile, "pos": Vector2(x + (CELL - tile.get_width()) / 2, y + LABEL + (CELL - tile.get_height()) / 2)})
					col += 1
				row += 1
	await _layout(Vector2i(ROWLAB + 4 * CELL, HEAD + row * (CELL + LABEL)), items, DIR.path_join("tr7c_preview_detail.png"), "TR-7c detail (325 m = 128 px, 900 m = 46 px): today, stable, stable with the shipped shader, reference model")

## Draws labels and images into a SubViewport and saves it.
func _layout(size: Vector2i, items: Array, path: String, title: String) -> void:
	var sheet := SubViewport.new()
	sheet.size = size
	sheet.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(sheet)
	var background := ColorRect.new()
	background.color = Color(0.16, 0.17, 0.19)
	background.size = Vector2(size)
	sheet.add_child(background)
	var heading := Label.new()
	heading.text = title
	heading.position = Vector2(4, 2)
	heading.add_theme_font_size_override("font_size", 14)
	sheet.add_child(heading)
	for item: Dictionary in items:
		if item.has("image"):
			var rect := TextureRect.new()
			var texture := ImageTexture.create_from_image(item["image"])
			rect.texture = texture
			rect.position = item["pos"]
			rect.size = Vector2(item["image"].get_width(), item["image"].get_height())
			rect.stretch_mode = TextureRect.STRETCH_KEEP
			rect.texture_filter = CanvasItem.TEXTURE_FILTER_NEAREST
			sheet.add_child(rect)
		else:
			var label := Label.new()
			label.text = item["text"]
			label.position = item["pos"]
			label.add_theme_font_size_override("font_size", 13)
			sheet.add_child(label)
	for i in 5: await RenderingServer.frame_post_draw
	sheet.get_texture().get_image().save_png(ProjectSettings.globalize_path(path))
	sheet.queue_free()
	print("TREE_LEAF_MIP_PREVIEW sheet: %s" % ProjectSettings.globalize_path(path))

# --- numbers ---------------------------------------------------------------------------------------------------------------------------------------------------

func _numbers() -> void:
	# configs: [name, kind, look, size, tilt]
	var configs := [
		["today_128", "lod2b", "today", 128, 0.0], ["stable_128", "lod2b", "stable", 128, 0.0], ["ship_128", "lod2b", "ship", 128, 0.0],
		["today_128_top", "lod2b", "today", 128, 60.0], ["stable_128_top", "lod2b", "stable", 128, 60.0], ["ship_128_top", "lod2b", "ship", 128, 60.0],
		["stable_lod2_128", "lod2", "stable", 128, 0.0],
		["today_64", "lod2b", "today", 64, 0.0], ["stable_64", "lod2b", "stable", 64, 0.0], ["ship_64", "lod2b", "ship", 64, 0.0],
		["today_46", "lod2b", "today", 46, 0.0], ["stable_46", "lod2b", "stable", 46, 0.0], ["ship_46", "lod2b", "ship", 46, 0.0],
		["ref_512", "lod2", "ref", 512, 0.0], ["ref_512_top", "lod2", "ref", 512, 60.0]]
	var table := {} # "name|lighting" -> array per variant of [cov, r, g, b, lum]
	for c: Array in configs:
		for lighting in [AMBIENT, NOON]:
			var per_variant := []
			for variant in 30:
				var sum := [0.0, 0.0, 0.0, 0.0, 0.0]
				for az: float in AZ:
					var image: Image = await _shot(variant, c[1], c[2], Vector2i(c[3], c[3]), 325.0, FOV_BASE, lighting, az, c[4], 0.5, MAGENTA)
					var s := _stats(image)
					for k in 5: sum[k] += s[k]
				for k in 5: sum[k] /= AZ.size()
				per_variant.append(sum)
			table["%s|%d" % [c[0], lighting]] = per_variant
		print("TREE_LEAF_MIP_PREVIEW numbers: %s done" % c[0])
	var csv := FileAccess.open(DIR.path_join("preview_table.csv"), FileAccess.WRITE)
	csv.store_line("set,lighting,variant,covered_fraction,r,g,b,luminance (mean of 12 azimuths)")
	for key: String in table:
		var parts := key.split("|")
		for variant in 30:
			var v: Array = table[key][variant]
			csv.store_line("%s,%s,%d,%.6f,%.5f,%.5f,%.5f,%.5f" % [parts[0], "ambient" if int(parts[1]) == AMBIENT else "noon", variant, v[0], v[1], v[2], v[3], v[4]])
	csv.close()
	# species means of the per-variant ratios
	var vs_ref := {}
	for look in ["today", "stable", "ship"]:
		for size in [128, 64, 46]:
			var entry := []
			for sp in 3:
				entry.append(_ref_metrics(table, "%s_%d" % [look, size], "ref_512", sp))
			vs_ref["%s_%d" % [look, size]] = entry
		var top := []
		for sp in 3: top.append(_ref_metrics(table, "%s_128_top" % look, "ref_512_top", sp))
		vs_ref["%s_128_top" % look] = top
	report["vs_reference_model"] = vs_ref
	# expectations (reported, not tuned): stable against the reference model, species mean
	var covers := {128: 0.10, 64: 0.15, 46: 0.20}
	var misses := []
	for size: int in covers:
		for sp in 3:
			var m: Dictionary = vs_ref["stable_%d" % size][sp]
			if absf(m["cov"] - 1.0) > covers[size]: misses.append("%s %d px: covered area ratio %.3f outside %.0f %%" % [SPECIES[sp], size, m["cov"], covers[size] * 100.0])
			if absf(m["amb_lum"] - 1.0) > 0.05: misses.append("%s %d px: ambient luminance ratio %.3f outside 5 %%" % [SPECIES[sp], size, m["amb_lum"]])
			for ch in ["amb_dr", "amb_dg", "amb_db"]:
				if m[ch] > 0.08: misses.append("%s %d px: %s %.3f of the reference luminance, over 8 %%" % [SPECIES[sp], size, ch, m[ch]])
			if absf(m["noon_lum"] - 1.0) > 0.10: misses.append("%s %d px: noon luminance ratio %.3f outside 10 %%" % [SPECIES[sp], size, m["noon_lum"]])
	report["expectation_misses"] = misses
	# anisotropic against isotropic alpha: ship / stable covered area, species mean
	var aniso := {}
	var all_within := true
	for name in ["128", "64", "46", "128_top"]:
		var rows := []
		for sp in 3:
			var ratios := []
			for lighting in [AMBIENT, NOON]:
				for variant in range(30):
					if _species(variant) != sp: continue
					ratios.append(table["ship_%s|%d" % [name, lighting]][variant][0] / maxf(table["stable_%s|%d" % [name, lighting]][variant][0], 1e-9))
			var mean := _mean(ratios)
			rows.append(mean)
			if absf(mean - 1.0) > 0.05: all_within = false
		aniso[name] = rows
	report["aniso_over_iso_covered_area_species_mean"] = aniso
	report["aniso_within_5_percent_everywhere"] = all_within
	var lod := []
	for sp in 3:
		var cov := []; var lum := []
		for lighting in [AMBIENT, NOON]:
			for variant in range(30):
				if _species(variant) != sp: continue
				cov.append(table["stable_128|%d" % lighting][variant][0] / maxf(table["stable_lod2_128|%d" % lighting][variant][0], 1e-9))
				lum.append(table["stable_128|%d" % lighting][variant][4] / maxf(table["stable_lod2_128|%d" % lighting][variant][4], 1e-9))
		lod.append({"cov": _mean(cov), "lum": _mean(lum)})
	report["lod2b_over_authored_lod2_stable_128"] = lod
	print("TREE_LEAF_MIP_PREVIEW stable vs reference (species mean maple/fir/pine) 128 px cov %s; 64 px cov %s; 46 px cov %s" % [_col(vs_ref["stable_128"], "cov"), _col(vs_ref["stable_64"], "cov"), _col(vs_ref["stable_46"], "cov")])
	print("TREE_LEAF_MIP_PREVIEW today vs reference 128 px cov %s; stable ambient lum %s; stable noon lum %s" % [_col(vs_ref["today_128"], "cov"), _col(vs_ref["stable_128"], "amb_lum"), _col(vs_ref["stable_128"], "noon_lum")])
	print("TREE_LEAF_MIP_PREVIEW expectation misses: %d %s" % [misses.size(), misses])
	print("TREE_LEAF_MIP_PREVIEW aniso/iso covered area %s; within 5 %% everywhere: %s; LOD2b/LOD2 stable 128 px %s" % [aniso, all_within, lod])

func _col(entry: Array, key: String) -> String:
	return "/".join(PackedStringArray(entry.map(func(m): return "%.3f" % m[key])))

func _mean(values: Array) -> float:
	var t := 0.0
	for v: float in values: t += v
	return t / maxf(values.size(), 1)

## Species mean over the variants of the ratios of one look to the reference model: covered area, ambient luminance, noon luminance, and the ambient channel differences as a share of the reference luminance.
func _ref_metrics(table: Dictionary, name: String, ref: String, species: int) -> Dictionary:
	var cov := []; var al := []; var nl := []; var dr := []; var dg := []; var db := []
	for variant in range(30):
		if _species(variant) != species: continue
		var a: Array = table["%s|%d" % [name, AMBIENT]][variant]
		var ra: Array = table["%s|%d" % [ref, AMBIENT]][variant]
		var n: Array = table["%s|%d" % [name, NOON]][variant]
		var rn: Array = table["%s|%d" % [ref, NOON]][variant]
		cov.append(a[0] / maxf(ra[0], 1e-9))
		al.append(a[4] / maxf(ra[4], 1e-9))
		nl.append(n[4] / maxf(rn[4], 1e-9))
		dr.append(absf(a[1] - ra[1]) / maxf(ra[4], 1e-9))
		dg.append(absf(a[2] - ra[2]) / maxf(ra[4], 1e-9))
		db.append(absf(a[3] - ra[3]) / maxf(ra[4], 1e-9))
	return {"cov": _mean(cov), "amb_lum": _mean(al), "noon_lum": _mean(nl), "amb_dr": _mean(dr), "amb_dg": _mean(dg), "amb_db": _mean(db)}

# --- dolly -------------------------------------------------------------------------------------------------------------------------------------------------------

## v3 and v13, 15 m to 420 m in 300 frames at the 1080p scale (70 degree vertical lens over 1080 px; each half 540 x 1080), today beside stable. The model changes at 32.5 m (LOD0 to LOD2) and 140 m (to LOD2b), as the
## shipped fades' midpoints; the cards and the far quad are not part of this preview.
func _dolly() -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(DIR.path_join("dolly")))
	var strip_items := [{"text": "TODAY (shipped leaf mips)", "pos": Vector2(8, 4)}, {"text": "STABLE (preview mips)", "pos": Vector2(548, 4)}]
	await _layout(Vector2i(1080, 30), strip_items, DIR.path_join("dolly/strip.png"), "")
	var strip := Image.load_from_file(ProjectSettings.globalize_path(DIR.path_join("dolly/strip.png")))
	strip.convert(Image.FORMAT_RGBA8)
	var frames := 300
	for variant: int in [3, 13]:
		var base := DIR.path_join("dolly/v%d" % variant)
		for sub in ["today", "stable", "both"]: DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(base.path_join(sub)))
		for i in frames:
			var t := float(i) / float(frames - 1)
			var dist := 15.0 * pow(420.0 / 15.0, t)
			var kind := "lod0" if dist < 32.5 else ("lod2" if dist < 140.0 else "lod2b")
			var halves := []
			for look in ["today", "stable"]:
				var half: Image = await _shot(variant, kind, look, Vector2i(540, 1080), dist, 70.0, MIXED, 120.0, 0.0, 0.5, Color(0.62, 0.66, 0.72))
				half.convert(Image.FORMAT_RGBA8)
				halves.append(half)
			var both := Image.create(1080, 1110, false, Image.FORMAT_RGBA8)
			both.fill(Color(0.1, 0.1, 0.1))
			both.blit_rect(strip, Rect2i(0, 0, 1080, 30), Vector2i(0, 0))
			both.blit_rect(halves[0], Rect2i(0, 0, 540, 1080), Vector2i(0, 30))
			both.blit_rect(halves[1], Rect2i(0, 0, 540, 1080), Vector2i(540, 30))
			var name := "frame%08d.png" % i
			halves[0].save_png(ProjectSettings.globalize_path(base.path_join("today/" + name)))
			halves[1].save_png(ProjectSettings.globalize_path(base.path_join("stable/" + name)))
			both.save_png(ProjectSettings.globalize_path(base.path_join("both/" + name)))
		print("TREE_LEAF_MIP_PREVIEW dolly v%d: %d frames in %s" % [variant, frames, ProjectSettings.globalize_path(base)])
	report["dolly"] = {"variants": [3, 13], "frames": frames, "distance_m": [15, 420]}
