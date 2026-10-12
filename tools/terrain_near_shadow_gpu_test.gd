extends SceneTree

# Real Forward+ directional shadows, production receiver/caster shaders, and
# paired caster-hidden controls. No scene preferences or weather nodes needed.
const SIZE := Vector2i(512, 384)
var viewport: SubViewport
var camera: Camera3D
var receiver: MeshInstance3D
var caster: MeshInstance3D
var output := ""
var failed := false
var failures: Array[String] = []

func _initialize() -> void:
    if RenderingServer.get_rendering_device() == null:
        abort("Forward+ required"); return
    call_deferred("run")

func run() -> void:
    create_timer(90.0).timeout.connect(func(): abort("watchdog expired"))
    output = "res://.godot/terrain_near_shadow_%d_%d" % [OS.get_process_id(), Time.get_ticks_usec()]
    DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(output))
    setup()
    for path: String in ["res://materials/voxel_ring_terrain.gdshader", "res://materials/ring_terrain_proxy.gdshader"]:
        var material := terrain_material(path)
        receiver.material_override = material
        for cloud: float in [0.0, .4, .8]:
            material.set_shader_parameter("local_cloud_shadow_texture", solid_texture(cloud))
            caster.visible = false
            var clear := await capture("%s_cloud%d_control" % [path.get_file().get_basename(), int(cloud * 100)])
            caster.visible = true
            var shadow := await capture("%s_cloud%d_shadow" % [path.get_file().get_basename(), int(cloud * 100)])
            var lit := sample_ground(clear)
            var dark := sample_ground(shadow)
            if cloud == 0.0 and lit <= .025:
                viewport.queue_free()
                abort("clear-sky direct-light control is black; fixture lighting/hemisphere invalid for " + path.get_file()); return
            print("TERRAIN_NEAR_SHADOW receiver=%s cloud=%.2f control=%.6f shadow=%.6f ratio=%.6f" % [path.get_file(), cloud, lit, dark, dark / maxf(lit, .00001)])
            check(lit > .025, "direct-light control is unavailable for " + path.get_file())
            check(dark <= lit * .10 + .005, "hard terrain shadow regained direct light under cloud %.2f in %s" % [cloud, path.get_file()])
    # The same composition helper must preserve an opaque macro terrain mask.
    var macro := terrain_material("res://materials/ring_terrain_proxy.gdshader")
    receiver.material_override = macro
    caster.visible = false
    macro.set_shader_parameter("terrain_macro_shadow_texture", solid_texture(1.0))
    macro.set_shader_parameter("terrain_macro_shadow_strength", 1.0)
    macro.set_shader_parameter("terrain_macro_shadow_fade_start_meters", 0.0)
    macro.set_shader_parameter("terrain_macro_shadow_fade_end_meters", 1.0)
    for cloud: float in [0.0, .4, .8]:
        macro.set_shader_parameter("local_cloud_shadow_texture", solid_texture(cloud))
        macro.set_shader_parameter("terrain_macro_shadow_enabled", false)
        var lit := sample_ground(await capture("macro_cloud%d_control" % int(cloud * 100)))
        macro.set_shader_parameter("terrain_macro_shadow_enabled", true)
        var masked := sample_ground(await capture("macro_cloud%d_masked" % int(cloud * 100)))
        print("TERRAIN_MACRO_SHADOW cloud=%.2f control=%.6f masked=%.6f" % [cloud, lit, masked])
        check(lit > .025 and masked <= lit * .10 + .005, "opaque macro terrain mask regained direct light under cloud %.2f" % cloud)
    # Isolate caster handoff from lighting composition with zero cloud.
    var baseline_material := terrain_material("res://materials/voxel_ring_terrain.gdshader")
    receiver.material_override = baseline_material
    baseline_material.set_shader_parameter("local_cloud_shadow_texture", solid_texture(0.0))
    caster.visible = false
    var control := sample_ground(await capture("caster_control"))
    caster.visible = true
    var visible_material := terrain_material("res://materials/ring_terrain_proxy.gdshader")
    var shadow_material := ShaderMaterial.new()
    shadow_material.shader = load("res://materials/ring_terrain_proxy_shadow.gdshader")
    shadow_material.set_shader_parameter("geomorph_enabled", false)
    for material: ShaderMaterial in [visible_material, shadow_material]:
        material.set_shader_parameter("coverage_atlas", solid_texture(0.0))
    caster.set_instance_shader_parameter("coverage_atlas_rect", Vector4(0, 0, 1, 1))
    caster.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_SHADOWS_ONLY
    for mode: String in ["visible", "near_cutout", "far_cutout", "shadow_only", "visible_hole", "shadow_hole"]:
        var use_shadow := mode.begins_with("shadow")
        var material: ShaderMaterial = shadow_material if use_shadow else visible_material
        visible_material.set_shader_parameter("near_fade_enabled", mode == "near_cutout")
        visible_material.set_shader_parameter("fade_start_meters", 1000.0)
        visible_material.set_shader_parameter("fade_end_meters", 1100.0)
        visible_material.set_shader_parameter("far_fade_enabled", mode == "far_cutout")
        visible_material.set_shader_parameter("fade_out_start_meters", 0.0)
        visible_material.set_shader_parameter("fade_out_end_meters", 1.0)
        material.set_shader_parameter("coverage_atlas", solid_texture(1.0 if mode.ends_with("hole") else 0.0))
        caster.material_override = material
        var value := sample_ground(await capture("caster_" + mode))
        var should_cast := mode in ["visible", "shadow_only"]
        print("TERRAIN_CASTER mode=%s control=%.6f receiver=%.6f" % [mode, control, value])
        if should_cast:
            check(value <= control * .10 + .005, "opaque caster failed: " + mode)
        else:
            check(absf(value - control) <= maxf(.01, control * .05), "discarded coverage/cutout still casts: " + mode)
    viewport.queue_free()
    for frame in range(3): await process_frame
    if not failures.is_empty():
        abort("; ".join(failures)); return
    print("TERRAIN_NEAR_SHADOW_GPU=PASS cloud=0,.4,.8 receivers=voxel,proxy caster=visible,near,far,shadow_only,matching_atlas_holes output=" + output)
    quit(0)

func setup() -> void:
    viewport = SubViewport.new(); viewport.size = SIZE; viewport.own_world_3d = true
    viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
    root.add_child(viewport)
    var scene := Node3D.new(); viewport.add_child(scene)
    var environment := Environment.new()
    environment.background_mode = Environment.BG_COLOR
    environment.background_color = Color.BLACK
    environment.ambient_light_source = Environment.AMBIENT_SOURCE_DISABLED
    environment.ambient_light_energy = 0.0
    environment.reflected_light_source = Environment.REFLECTION_SOURCE_DISABLED
    environment.tonemap_mode = Environment.TONE_MAPPER_LINEAR
    environment.fog_enabled = false
    var owner := WorldEnvironment.new(); owner.environment = environment; scene.add_child(owner)
    camera = Camera3D.new(); camera.projection = Camera3D.PROJECTION_ORTHOGONAL
    camera.size = 16; camera.near = .1; camera.far = 60
    scene.add_child(camera); camera.position = Vector3(0, 20, 0)
    camera.look_at(Vector3.ZERO, Vector3.FORWARD); camera.make_current()
    var sun := DirectionalLight3D.new(); scene.add_child(sun)
    sun.light_energy = .25; sun.shadow_enabled = true
    sun.directional_shadow_max_distance = 50; sun.shadow_bias = .02; sun.shadow_normal_bias = .1
    sun.look_at(Vector3(1, -1, 0), Vector3.UP)
    receiver = MeshInstance3D.new(); receiver.mesh = PlaneMesh.new()
    receiver.mesh.size = Vector2(24, 24)
    receiver.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
    scene.add_child(receiver)
    caster = MeshInstance3D.new(); caster.mesh = BoxMesh.new()
    caster.mesh.size = Vector3(2, 4, 6); caster.position = Vector3(-4, 2, 0)
    scene.add_child(caster)

func terrain_material(path: String) -> ShaderMaterial:
    var material := ShaderMaterial.new(); material.shader = load(path)
    # Ring hemisphere math uses the XZ radial plane. A far -X center gives
    # radial +X and inward -X up. Dot((-1,0,0),sun)=+.707, fully daylight;
    # the actual plane normal +Y independently receives +.707 direct light.
    var parameters := {
        "ring_center": Vector3(-1000000, 0, 0), "camera_world_position": Vector3(0,20,0),
        "sun_direction": Vector3(-1,1,0).normalized(), "ring_atmosphere_enabled": false,
        "geomorph_enabled": false, "analytic_cloud_shadow_enabled": false,
        "terrain_macro_shadow_enabled": false, "terrain_planet_shadow_enabled": false,
        "local_cloud_shadow_enabled": true, "local_cloud_shadow_near_enabled": true,
        "local_cloud_shadow_far_enabled": false, "local_cloud_shadow_strength": 1.0,
        "local_cloud_shadow_far_strength": 1.0, "local_cloud_shadow_radius_meters": 1000.0,
        "local_cloud_shadow_axis_around": Vector3.RIGHT, "local_cloud_shadow_axis_across": Vector3.FORWARD,
        "local_cloud_shadow_axis_up": Vector3.UP, "local_cloud_shadow_near_start_meters": -100.0,
        "local_cloud_shadow_near_end_meters": -99.0, "local_cloud_shadow_texture": solid_texture(0.0),
        "shadow_floor": 0.0, "debug_color_enabled": true, "debug_color": Vector3(.5,.5,.5)
    }
    for key: String in parameters: material.set_shader_parameter(key, parameters[key])
    return material

func solid_texture(value: float) -> ImageTexture:
    var image := Image.create(2, 2, false, Image.FORMAT_RGBA8)
    image.fill(Color(value, 0, 0, 1))
    return ImageTexture.create_from_image(image)

func capture(label: String) -> Image:
    for frame in range(8): await process_frame
    await RenderingServer.frame_post_draw
    var image := viewport.get_texture().get_image()
    if image.is_empty(): abort("empty capture " + label); return image
    var save_error := image.save_png(output + "/" + label + ".png")
    if save_error != OK: abort("capture save failed: " + error_string(save_error))
    return image

func sample_ground(image: Image) -> float:
    if image.is_empty(): return INF
    var pixel := Vector2i(camera.unproject_position(Vector3.ZERO))
    var total := 0.0
    for y in range(pixel.y - 2, pixel.y + 3):
        for x in range(pixel.x - 2, pixel.x + 3):
            var color := image.get_pixel(x, y)
            total += color.r * .2126 + color.g * .7152 + color.b * .0722
    return total / 25.0

func check(condition: bool, message: String) -> void:
    if not condition: failures.append(message)

func abort(message: String) -> void:
    if failed: return
    failed = true; push_error("TERRAIN_NEAR_SHADOW_GPU=FAIL " + message); quit(1)
