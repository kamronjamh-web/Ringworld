shader_type canvas_item;
render_mode blend_premul_alpha;

#include "menu_flyover_cloud_common.gdshaderinc"

uniform vec3 camera_origin_km;
uniform vec3 camera_basis_x;
uniform vec3 camera_basis_y;
uniform vec3 camera_basis_z;
uniform vec3 sun_direction = vec3(-0.4, 0.75, -0.5);
uniform vec2 canonical_anchor_km;
uniform float camera_fov_y = 1.0;
uniform float camera_aspect = 1.777;
uniform float ring_radius_km = 1000.0;
uniform float ring_width_km = 100.0;
uniform float seed = 1337.0;
uniform float time_seconds;
uniform sampler2D height_texture : filter_linear, repeat_enable;
uniform vec4 terrain_info;
uniform int terrain_row_offset;
uniform int terrain_row_count;
uniform int terrain_columns;

float sample_ground(vec3 point) {
    float distance_km = ring_radius_km * atan(-point.z, ring_radius_km - point.y);
    float logical_row = (distance_km - terrain_info.x) / max(0.0001, terrain_info.z);
    float column = (point.x - terrain_info.y) / max(0.0001, terrain_info.z);
    if (logical_row < 0.0 || logical_row >= float(terrain_row_count - 1) || column < 0.0 || column >= float(terrain_columns - 1)) return 0.0;
    float row = mod(logical_row + float(terrain_row_offset), float(terrain_row_count));
    return texture(height_texture, vec2((column + 0.5) / float(terrain_columns), (row + 0.5) / float(terrain_row_count))).r;
}

float cylinder_exit(vec3 ro, vec3 rd, float radius) {
    vec2 o = ro.yz - vec2(ring_radius_km, 0.0);
    float a = dot(rd.yz, rd.yz);
    if (a < 0.000001) return -1.0;
    float b = dot(o, rd.yz);
    float c = dot(o, o) - radius * radius;
    float discriminant = b * b - a * c;
    if (discriminant < 0.0) return -1.0;
    return (-b + sqrt(discriminant)) / a;
}

void fragment() {
    vec2 ndc = UV * 2.0 - 1.0;
    vec3 ray_direction = normalize(camera_basis_x * ndc.x * camera_aspect * tan(camera_fov_y * 0.5) - camera_basis_y * ndc.y * tan(camera_fov_y * 0.5) - camera_basis_z);
    vec3 ray_origin = camera_origin_km;
    float start_distance = cylinder_exit(ray_origin, ray_direction, ring_radius_km - 6.0);
    float end_distance = min(160.0, cylinder_exit(ray_origin, ray_direction, ring_radius_km - 2.2));
    vec3 color = vec3(0.0);
    float alpha = 0.0;
    if (start_distance >= 0.0 && end_distance > start_distance) {
        float step_length = (end_distance - start_distance) / 32.0;
        for (int sample_index = 0; sample_index < 32; ++sample_index) {
            vec3 point = ray_origin + ray_direction * (start_distance + (float(sample_index) + 0.5) * step_length);
            float inward_altitude = ring_radius_km - length(vec2(point.y - ring_radius_km, point.z));
            vec2 canonical = canonical_anchor_km + vec2(ring_radius_km * atan(-point.z, ring_radius_km - point.y), point.x);
            if (canonical.y < 0.0 || canonical.y > ring_width_km) continue;
            if (inward_altitude <= sample_ground(point)) break;
            float layer = smoothstep(2.15, 2.65, inward_altitude) * (1.0 - smoothstep(5.5, 6.1, inward_altitude));
            float weather = menu_weather(canonical, ring_radius_km, seed, time_seconds);
            // Two inexpensive 3D reads turn the weather column into altitude-varying billows.
            vec2 moved = canonical + MENU_WIND_KM_PER_SECOND * time_seconds;
            float angle = moved.x / ring_radius_km;
            vec3 billow_position = vec3(sin(angle) * ring_radius_km / 3.0,
                cos(angle) * ring_radius_km / 3.0 + inward_altitude * 0.7,
                moved.y / 3.0 + inward_altitude * 1.6);
            float billow = 0.62 * menu_noise(billow_position) + 0.38 * menu_noise(billow_position * 2.17 + 17.0);
            // A thresholded billow carves gaps through the broad weather layer;
            // the small low-density floor keeps cloud edges soft instead of noisy.
            float billow_shape = smoothstep(0.46, 0.70, billow);
            float density = menu_cloud_coverage(weather) * mix(0.06, 1.0, billow_shape) * layer;
            density *= 1.0 - smoothstep(120.0, 160.0, start_distance + (float(sample_index) + 0.5) * step_length);
            // One forward weather sample approximates self-shadowing toward the sun.
            vec2 sun_canonical = canonical + vec2(-sun_direction.z * 2.5, sun_direction.x * 2.5);
            float sun_density = menu_cloud_coverage(menu_weather(sun_canonical, ring_radius_km, seed, time_seconds));
            float sun_transmittance = exp(-sun_density * max(0.0, 6.0 - inward_altitude) * 0.6 / max(0.2, sun_direction.y));
            float sample_alpha = 1.0 - exp(-density * step_length * 1.1);
            float light = 0.25 + 0.75 * sun_transmittance * max(0.0, dot(normalize(vec3(0.0, ring_radius_km - point.y, -point.z)), normalize(sun_direction)));
            color += (1.0 - alpha) * mix(vec3(0.32, 0.38, 0.48), vec3(1.0), light) * sample_alpha;
            alpha += (1.0 - alpha) * sample_alpha;
            if (alpha > 0.985) break;
        }
    }
    COLOR = vec4(color, alpha);
}
