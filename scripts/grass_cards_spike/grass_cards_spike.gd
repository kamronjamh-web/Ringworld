extends Node3D
# P0 grass-cards spike scene (docs/grass-cards-redesign.md). Standalone: touches no gameplay systems.
# Run: Godot ... --path . res://scenes/grass_cards_spike/GrassCardsSpike.tscn
# Keys: 1-6 bookmarks, V cycle variant (P0 / A density+colour / B +form / C +atlas / D C+hero layer / E C+small hero layer; F1-F6 direct), R reach (200/300/400), T density, C far-collapse, H hide cards,
# WASD+QE fly (Shift fast), hold RMB to look, F12 screenshot.

const Assets := preload("res://scripts/grass_cards_spike/GrassCardsSpikeAssets.cs")
const CARD_SHADER := preload("res://materials/grass_cards_spike/grass_cards.gdshader")
const HERO_SHADER := preload("res://materials/grass_cards_spike/grass_hero.gdshader")
const GROUND_SHADER := preload("res://materials/grass_cards_spike/spike_ground.gdshader")
const ALBEDO_PATH := "res://materials/third_party/voxelgame/grass_albedo.png"

const REACHES := [200.0, 300.0, 400.0]
const DENSITIES := {"low": 0.5, "med": 1.0, "high": 1.6}
const BOOKMARK_NAMES := ["standing", "feet", "alt20", "alt100", "alt5", "crouch"]

# Variants: P0 = round-1 look; A = density + per-fragment colour; B = A + form/lighting + low layer; C = B + fuller atlas.
const VARIANT_NAMES := ["P0", "A", "B", "C", "D", "E"]
const VARIANT_DEFAULTS := {"mode": 1, "near_density": 0.0, "near_full": 8.0, "near_end": 45.0, "near_width": 1.0, "near_height": 1.0, "lean": 0.0, "low_density": 0.0, "low_full": 8.0, "low_end": 22.0, "tilt_amount": 0.22, "normal_out": 0.0, "normal_face": 0.0, "backlight": 0.0, "atlas": 0}
# P0c hero layer: hero_on, cell size / hysteresis (m), fade start/end (m), tufts per m2, card thinning (min keep, start, end m).
const HERO_DEFAULTS := {"hero_on": 0, "hero_cell": 4.0, "hero_hyst": 0.75, "hero_start": 4.0, "hero_end": 6.0, "hero_dens": 50.0, "hero_hmul": 0.9, "thin_min": 1.0, "thin_start": 3.0, "thin_end": 7.5}
const VARIANTS := [
	{"mode": 0},
	{"near_density": 12.0, "near_width": 0.7, "lean": 0.12},
	{"near_density": 12.0, "near_width": 0.7, "lean": 0.12, "tilt_amount": 0.08, "normal_out": 0.45, "normal_face": 0.25, "backlight": 0.28, "low_density": 18.0},
	{"near_density": 14.0, "near_width": 0.7, "lean": 0.12, "tilt_amount": 0.08, "normal_out": 0.45, "normal_face": 0.25, "backlight": 0.28, "low_density": 18.0, "atlas": 1},
	{"near_density": 14.0, "near_width": 0.7, "lean": 0.12, "tilt_amount": 0.08, "normal_out": 0.45, "normal_face": 0.25, "backlight": 0.28, "low_density": 18.0, "atlas": 1, "hero_on": 1, "hero_cell": 4.0, "hero_hyst": 0.75, "hero_start": 4.0, "hero_end": 6.0, "thin_min": 0.4, "thin_start": 3.0, "thin_end": 7.5},
	{"near_density": 14.0, "near_width": 0.7, "lean": 0.12, "tilt_amount": 0.08, "normal_out": 0.45, "normal_face": 0.25, "backlight": 0.28, "low_density": 18.0, "atlas": 1, "hero_on": 1, "hero_cell": 2.5, "hero_hyst": 0.5, "hero_start": 2.4, "hero_end": 3.8, "thin_min": 0.4, "thin_start": 1.8, "thin_end": 5.0},
]

var assets
var atlas: ImageTexture
var atlas_full: ImageTexture
var variant := 1
var card_mesh: ArrayMesh
var card_material: ShaderMaterial
var ground_material: ShaderMaterial
var camera: Camera3D
var field_root: Node3D
var chunks: Array = []          # {inst, aabb, count}
var total_instances := 0
var reach := 300.0
var density_name := "med"
var collapse_enabled := true
var cards_visible := true
var build_ms := 0.0
var hud: Label
var yaw := 0.0
var pitch := 0.0
var interactive := true
var single_start := 90.0
var single_end := 160.0
var comp_cap := 2.5
var auto_setup := true
# P0c hero layer state (camera-relative cell ring, hysteresis re-anchor; see docs/grass-cards-redesign.md P0c section).
var hero_mesh: ArrayMesh
var hero_material: ShaderMaterial
var hero_root: Node3D
var hero_cells := {}            # Vector2i -> MultiMeshInstance3D
var hero_cfg := HERO_DEFAULTS.duplicate()
var hero_anchor := Vector2i.ZERO
var hero_anchor_valid := false
var hero_cell_builds := 0       # cells generated since last reset (idle test: must not change while stationary)
var hero_reanchors := 0
var hero_instances := 0
var hero_build_ms := 0.0

func _ready() -> void:
	if auto_setup:
		setup(true)

func setup(is_interactive: bool) -> void:
	interactive = is_interactive
	assets = Assets.new()
	var t0 := Time.get_ticks_usec()
	atlas = assets.BuildAtlas(30, 1337)
	print("ATLAS built in %.0f ms hash=%s outline_waste=%.3f" % [(Time.get_ticks_usec() - t0) * 0.001, assets.LastAtlasHash, assets.LastOutlineWasteFraction])
	print(assets.LastCoverageReport)
	atlas_full = assets.BuildAtlasFull(56, 1337, 0.16, 0.24, 1.2)
	print("ATLAS full hash=%s outline_waste=%.3f" % [assets.LastAtlasHash, assets.LastOutlineWasteFraction])
	card_mesh = assets.BuildMesh()
	hero_mesh = assets.BuildHeroTuftMesh(8, 4242, 0.15, 0.013)
	print("HERO tuft mesh triangles=", assets.HeroMeshTriangles)
	var albedo: Texture2D = load(ALBEDO_PATH)

	card_material = ShaderMaterial.new()
	card_material.shader = CARD_SHADER
	card_material.set_shader_parameter("tuft_atlas", atlas)
	card_material.set_shader_parameter("terrain_albedo", albedo)
	card_mesh.surface_set_material(0, card_material)
	hero_material = ShaderMaterial.new()
	hero_material.shader = HERO_SHADER
	hero_material.set_shader_parameter("terrain_albedo", albedo)
	hero_mesh.surface_set_material(0, hero_material)
	ground_material = ShaderMaterial.new()
	ground_material.shader = GROUND_SHADER
	ground_material.set_shader_parameter("terrain_albedo", albedo)

	var ground := MeshInstance3D.new()
	ground.name = "Ground"
	ground.mesh = assets.BuildGround(1500.0, 10.0)
	ground.material_override = ground_material
	ground.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	add_child(ground)

	var sun := DirectionalLight3D.new()
	sun.rotation_degrees = Vector3(-48, 35, 0)
	sun.light_energy = 1.1
	sun.shadow_enabled = false
	add_child(sun)
	var env := Environment.new()
	var sky := Sky.new()
	sky.sky_material = ProceduralSkyMaterial.new()
	env.sky = sky
	env.background_mode = Environment.BG_SKY
	env.ambient_light_source = Environment.AMBIENT_SOURCE_SKY
	env.tonemap_mode = Environment.TONE_MAPPER_FILMIC
	var we := WorldEnvironment.new()
	we.environment = env
	add_child(we)

	camera = Camera3D.new()
	camera.fov = 75.0
	camera.far = 3000.0
	camera.near = 0.05
	add_child(camera)
	camera.current = true
	field_root = Node3D.new()
	field_root.name = "Field"
	add_child(field_root)
	hero_root = Node3D.new()
	hero_root.name = "Hero"
	add_child(hero_root)
	if interactive:
		hud = Label.new()
		hud.position = Vector2(12, 8)
		hud.add_theme_color_override("font_outline_color", Color.BLACK)
		hud.add_theme_constant_override("outline_size", 6)
		var layer := CanvasLayer.new()
		layer.add_child(hud)
		add_child(layer)
		RenderingServer.viewport_set_measure_render_time(get_viewport().get_viewport_rid(), true)
	rebuild(300.0, "med", true)
	set_bookmark(0)

func rebuild(new_reach: float, new_density: String, new_collapse: bool) -> void:
	reach = new_reach
	density_name = new_density
	collapse_enabled = new_collapse
	for c in chunks:
		(c.inst as Node).queue_free()
	chunks.clear()
	total_instances = 0
	var t0 := Time.get_ticks_usec()
	var cfg := VARIANT_DEFAULTS.duplicate()
	cfg.merge(VARIANTS[variant], true)
	var data: Array
	if int(cfg.mode) == 0:
		data = assets.BuildField(reach, DENSITIES[density_name], 20260, 0.0, 0.0, comp_cap)
	else:
		var o := {"reach": reach, "density_scale": DENSITIES[density_name], "seed": 20260, "comp_cap": comp_cap}
		for k in ["near_density", "near_full", "near_end", "near_width", "near_height", "lean", "low_density", "low_full", "low_end"]:
			o[k] = float(cfg[k])
		data = assets.BuildFieldV2(o)
	for k in ["mode", "tilt_amount", "normal_out", "normal_face", "backlight"]:
		card_material.set_shader_parameter(k, cfg[k])
	card_material.set_shader_parameter("tuft_atlas", atlas_full if int(cfg.atlas) == 1 else atlas)
	for d in data:
		var mm := MultiMesh.new()
		mm.transform_format = MultiMesh.TRANSFORM_3D
		mm.use_custom_data = true
		mm.mesh = card_mesh
		mm.instance_count = int(d["count"])
		mm.buffer = d["buffer"]
		var mmi := MultiMeshInstance3D.new()
		mmi.multimesh = mm
		mmi.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
		mmi.custom_aabb = d["aabb"]
		field_root.add_child(mmi)
		chunks.append({"inst": mmi, "aabb": d["aabb"], "count": int(d["count"])})
		total_instances += int(d["count"])
	build_ms = (Time.get_ticks_usec() - t0) * 0.001
	card_material.set_shader_parameter("reach", reach)
	card_material.set_shader_parameter("single_start", single_start if collapse_enabled else 1.0e9)
	card_material.set_shader_parameter("single_end", single_end if collapse_enabled else 2.0e9)
	field_root.visible = cards_visible
	hero_configure(cfg)

func hero_configure(cfg: Dictionary) -> void:
	var nc := HERO_DEFAULTS.duplicate()
	for k in HERO_DEFAULTS.keys():
		if cfg.has(k):
			nc[k] = cfg[k]
	hero_cfg = nc
	hero_clear()
	card_material.set_shader_parameter("thin_min", float(nc.thin_min))
	card_material.set_shader_parameter("thin_start", float(nc.thin_start))
	card_material.set_shader_parameter("thin_end", float(nc.thin_end))
	hero_material.set_shader_parameter("hero_start", float(nc.hero_start))
	hero_material.set_shader_parameter("hero_end", float(nc.hero_end))
	hero_root.visible = cards_visible
	update_hero()

func hero_clear() -> void:
	for k in hero_cells:
		(hero_cells[k] as Node).queue_free()
	hero_cells.clear()
	hero_anchor_valid = false
	hero_instances = 0

# Camera-relative ring: a (2n+1)^2 block of fixed world cells around an anchor cell. Cell contents depend only on the
# cell coordinates (deterministic). The anchor only moves when the camera leaves the anchor cell by more than the
# hysteresis margin, and a re-anchor builds only the newly covered cells (bounded by the swept strip) and drops the rest.
# Stationary camera: no builds, no uploads, no material updates.
func update_hero() -> void:
	if int(hero_cfg.hero_on) == 0 or camera == null:
		if not hero_cells.is_empty():
			hero_clear()
		return
	var cell := float(hero_cfg.hero_cell)
	var c := camera.position
	if hero_anchor_valid:
		var ctr := (Vector2(hero_anchor) + Vector2(0.5, 0.5)) * cell
		var lim := cell * 0.5 + float(hero_cfg.hero_hyst)
		if absf(c.x - ctr.x) <= lim and absf(c.z - ctr.y) <= lim:
			return
	hero_anchor = Vector2i(floori(c.x / cell), floori(c.z / cell))
	hero_anchor_valid = true
	hero_reanchors += 1
	var n := int(ceil((float(hero_cfg.hero_end) + cell * 0.5 + float(hero_cfg.hero_hyst)) / cell - 0.5))
	var t0 := Time.get_ticks_usec()
	var want := {}
	for dz in range(-n, n + 1):
		for dx in range(-n, n + 1):
			want[Vector2i(hero_anchor.x + dx, hero_anchor.y + dz)] = true
	for k in hero_cells.keys():
		if not want.has(k):
			hero_instances -= (hero_cells[k] as MultiMeshInstance3D).multimesh.instance_count
			(hero_cells[k] as Node).queue_free()
			hero_cells.erase(k)
	for k in want.keys():
		if hero_cells.has(k):
			continue
		var d: Dictionary = assets.BuildHeroCell(k.x, k.y, cell, float(hero_cfg.hero_dens), 4242, float(hero_cfg.hero_hmul))
		var mm := MultiMesh.new()
		mm.transform_format = MultiMesh.TRANSFORM_3D
		mm.use_custom_data = true
		mm.mesh = hero_mesh
		mm.instance_count = int(d["count"])
		mm.buffer = d["buffer"]
		var mmi := MultiMeshInstance3D.new()
		mmi.multimesh = mm
		mmi.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
		mmi.custom_aabb = d["aabb"]
		hero_root.add_child(mmi)
		hero_cells[k] = mmi
		hero_instances += int(d["count"])
		hero_cell_builds += 1
	hero_build_ms = (Time.get_ticks_usec() - t0) * 0.001

func set_variant(i: int) -> void:
	variant = clampi(i, 0, VARIANTS.size() - 1)
	rebuild(reach, density_name, collapse_enabled)

func set_cards_visible(v: bool) -> void:
	cards_visible = v
	field_root.visible = v
	hero_root.visible = v

func bookmark_transform(i: int) -> Dictionary:
	var g := float(assets.GroundHeight(0.0, 0.0))
	match i:
		0: return {"pos": Vector3(0, g + 1.7, 0), "yaw": 0.0, "pitch": -2.0}
		1: return {"pos": Vector3(0, g + 1.7, 0), "yaw": 0.0, "pitch": -72.0}
		2: return {"pos": Vector3(0, g + 20.0, 0), "yaw": 0.0, "pitch": -25.0}
		4: return {"pos": Vector3(0, g + 5.0, 0), "yaw": 0.0, "pitch": -12.0}
		5: return {"pos": Vector3(0, g + 0.8, 0), "yaw": 0.0, "pitch": -4.0}
		_: return {"pos": Vector3(0, g + 100.0, 0), "yaw": 0.0, "pitch": -30.0}

func set_bookmark(i: int) -> void:
	var b := bookmark_transform(i)
	camera.position = b.pos
	update_hero()
	yaw = deg_to_rad(b.yaw)
	pitch = deg_to_rad(b.pitch)
	_apply_look()

func _apply_look() -> void:
	camera.rotation = Vector3(pitch, yaw, 0.0)

func visible_instances() -> int:
	var planes := camera.get_frustum()
	var n := 0
	for c in chunks:
		var box: AABB = c.aabb
		var inside := true
		for p in planes:
			var pn: Vector3 = p.normal
			# Godot frustum plane normals point out of the frustum: reject when the innermost corner is outside.
			var pv := Vector3(box.position.x + (0.0 if pn.x > 0.0 else box.size.x), box.position.y + (0.0 if pn.y > 0.0 else box.size.y), box.position.z + (0.0 if pn.z > 0.0 else box.size.z))
			if p.distance_to(pv) > 0.0:
				inside = false
				break
		if inside:
			n += int(c.count)
	return n

func _process(delta: float) -> void:
	if not interactive:
		return
	var speed := 8.0 * (6.0 if Input.is_key_pressed(KEY_SHIFT) else 1.0)
	var dir := Vector3.ZERO
	var basis := camera.global_transform.basis
	if Input.is_key_pressed(KEY_W): dir -= basis.z
	if Input.is_key_pressed(KEY_S): dir += basis.z
	if Input.is_key_pressed(KEY_A): dir -= basis.x
	if Input.is_key_pressed(KEY_D): dir += basis.x
	if Input.is_key_pressed(KEY_E): dir += Vector3.UP
	if Input.is_key_pressed(KEY_Q): dir -= Vector3.UP
	camera.position += dir * speed * delta
	update_hero()
	var gpu := RenderingServer.viewport_get_measured_render_time_gpu(get_viewport().get_viewport_rid())
	hud.text = "variant %s | reach %d m | density %s | collapse %s | cards %s | instances %d (in visible chunks %d) | hero %d tufts (%d cell builds, %d re-anchors) | GPU %.2f ms | %d fps\n1-6 bookmarks (6 = crouch 0.8 m)  V/F1-F6 variant (D=F5 hero, E=F6 small hero)  R reach  T density  C collapse  H hide cards  F12 shot" % [
		VARIANT_NAMES[variant], int(reach), density_name, str(collapse_enabled), str(cards_visible), total_instances, visible_instances(), hero_instances, hero_cell_builds, hero_reanchors, gpu, Engine.get_frames_per_second()]

func _unhandled_input(event: InputEvent) -> void:
	if not interactive:
		return
	if event is InputEventMouseMotion and Input.is_mouse_button_pressed(MOUSE_BUTTON_RIGHT):
		yaw -= event.relative.x * 0.003
		pitch = clamp(pitch - event.relative.y * 0.003, -1.55, 1.55)
		_apply_look()
	elif event is InputEventKey and event.pressed and not event.echo:
		match event.keycode:
			KEY_1: set_bookmark(0)
			KEY_2: set_bookmark(1)
			KEY_3: set_bookmark(2)
			KEY_4: set_bookmark(3)
			KEY_5: set_bookmark(4)
			KEY_V: set_variant((variant + 1) % VARIANTS.size())
			KEY_F1: set_variant(0)
			KEY_F2: set_variant(1)
			KEY_F3: set_variant(2)
			KEY_F4: set_variant(3)
			KEY_R: rebuild(REACHES[(REACHES.find(reach) + 1) % REACHES.size()], density_name, collapse_enabled)
			KEY_T:
				var names := DENSITIES.keys()
				rebuild(reach, names[(names.find(density_name) + 1) % names.size()], collapse_enabled)
			KEY_C: rebuild(reach, density_name, not collapse_enabled)
			KEY_H: set_cards_visible(not cards_visible)
			KEY_F12:
				var img := get_viewport().get_texture().get_image()
				img.save_png("user://grass_cards_spike_%d.png" % Time.get_ticks_msec())
