extends "res://tools/water_pattern_gpu_test.gd"

# Reuses the existing production-material render fixture, with no timing/FPS measurements.
var _fixture_clock := 1000000000.0
var _fixture_direction := Vector2(1.0, 0.0)
var _fixture_kind := 1
var _fixture_gust := false
const PHASE_RATES := [1.0, -1.173, 1.619, -2.071, 2.513, .071, -.053, .041, -.063, .037, .23, -.13]

func _run() -> void:
    DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT))
    for shader_path: String in [PROXY_SHADER, GLOBAL_SHADER]:
        for kind: int in [1, 2, 3]:
            _fixture_kind = kind
            _fixture_clock = 1000000000.0
            _fixture_direction = Vector2(1.0, 0.0)
            var label := "shared_wind_%s_kind%d" % ["near" if shader_path == PROXY_SHADER else "far", kind]
            var controls := _controls(.65, .65, .20, 1.0)
            var baseline := await _render(shader_path, 100.0, controls, label + "_mean")
            var paused := await _render(shader_path, 100.0, controls, label + "_paused")
            _fixture_direction = Vector2(0.0, 1.0)
            var turned := await _render(shader_path, 100.0, controls, label + "_turned")
            _fixture_clock += 8.0
            var advanced := await _render(shader_path, 100.0, controls, label + "_advanced")
            if not _valid(baseline) or not _valid(paused) or not _valid(turned) or not _valid(advanced):
                _fail("shared wind nonfinite material %s" % label)
                return
            if _mean_luma_delta(baseline, paused) > .000001:
                _fail("paused authoritative clock changed material %s" % label)
                return
            if _mean_luma_delta(baseline, turned) < .00002:
                _fail("mean wind direction did not reach material %s" % label)
                return
            if _mean_luma_delta(turned, advanced) < .00002:
                _fail("long authoritative clock did not animate material %s" % label)
                return
    _fixture_kind = 1
    _fixture_direction = Vector2(1.0, 0.0)
    _fixture_clock = 1000000000.0
    _fixture_gust = true
    var gust_image := Image.create(4, 4, false, Image.FORMAT_RF)
    gust_image.fill(Color(1.0, 0.0, 0.0))
    var gust_texture := ImageTexture.create_from_image(gust_image)
    RenderingServer.global_shader_parameter_set("ring_wind_gust_texture", gust_texture)
    RenderingServer.global_shader_parameter_set("ring_wind_gust_base", 1.0)
    RenderingServer.global_shader_parameter_set("ring_wind_gust_strength", 1.0)
    RenderingServer.global_shader_parameter_set("ring_wind_field_enabled", 0.0)
    var disabled := await _render(PROXY_SHADER, 100.0, _controls(.65, .65, .20, 1.0), "shared_wind_gust_disabled")
    RenderingServer.global_shader_parameter_set("ring_wind_field_enabled", 1.0)
    var gusting := await _render(PROXY_SHADER, 100.0, _controls(.65, .65, .20, 1.0), "shared_wind_gust_enabled")
    RenderingServer.global_shader_parameter_set("ring_wind_field_enabled", 0.0)
    var disabled_again := await _render(PROXY_SHADER, 100.0, _controls(.65, .65, .20, 1.0), "shared_wind_gust_disabled_again")
    if not _valid(disabled) or not _valid(gusting) or _mean_luma_delta(disabled, gusting) < .0001:
        _fail("shared gust atlas made no measurable near water detail/foam change")
        return
    if _mean_luma_delta(disabled, disabled_again) > .000001:
        _fail("disabled shared gust atlas retained water response")
        return
    print("WATER_SHARED_WIND_GPU_TEST=PASS paths=proxy,global kinds=ocean,lake,river checks=finite,pause,bearing,long_clock output=%s" % OUTPUT)
    quit(0)

func _material(shader_path: String, distance_meters: float, controls: Dictionary, translation: Vector3) -> ShaderMaterial:
    var material := super._material(shader_path, distance_meters, controls, translation)
    var phases := PackedVector3Array()
    for rate: float in PHASE_RATES:
        phases.append(Vector3(fposmod(_fixture_clock * 1.35 * rate, TAU), fposmod(_fixture_clock * 1.35 * rate * .42, TAU), fposmod(_fixture_clock * 1.35 * rate * .18, TAU)))
    material.set_shader_parameter("water_wind_clock_enabled", true)
    material.set_shader_parameter("water_wave_phases", phases)
    material.set_shader_parameter("water_mean_wind_direction", _fixture_direction)
    material.set_shader_parameter("water_mean_response", 2.0)
    material.set_shader_parameter("water_far_group_translation", Vector4.ZERO)
    material.set_shader_parameter("water_spatial_gust_enabled", _fixture_gust)
    material.set_shader_parameter("water_mean_wind_speed", 24.0)
    material.set_shader_parameter("water_gust_response_strength", 1.3)
    return material

func _quad(side: float) -> ArrayMesh:
    var mesh := super._quad(side)
    var arrays := mesh.surface_get_arrays(0)
    var colors: PackedColorArray = arrays[Mesh.ARRAY_COLOR]
    for index: int in colors.size():
        colors[index].a = float(_fixture_kind) / 3.0
    arrays[Mesh.ARRAY_COLOR] = colors
    var result := ArrayMesh.new()
    result.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
    return result
