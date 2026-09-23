// Both surfaces evaluate the same camera-ray ground datum for their dither
// handoff. Using their different terrain heights would leave unmatched holes.
uniform float radius_km = 5000.0;
uniform float ring_width_km = 318.0;
uniform vec2 canonical_anchor_km = vec2(0.0, 159.0);
uniform vec3 camera_origin_km = vec3(0.0, 20.0, 0.0);
uniform float rows = 2049.0;
uniform float columns = 1537.0;
uniform float window_start_km = -48.0;
uniform int row_offset = 0;
uniform vec4 terrain_info = vec4(-48.0, -96.0, 0.125, 0.0);
uniform sampler2D availability_texture : filter_nearest, repeat_enable;
uniform bool near_ready = false;
uniform sampler2D cloud_shadow_texture : filter_linear, repeat_disable;
uniform vec2 shadow_center_km = vec2(0.0);
uniform float shadow_extent_km = 256.0;
uniform vec2 shadow_uv_offset = vec2(0.0);
uniform bool shadow_ready = false;
uniform float shadow_strength = 0.65;
uniform float cloud_floor_km = 2.2;
uniform float cloud_ceiling_km = 6.0;
uniform float haze_amount = 0.12;
uniform float haze_start_km = 250.0;
uniform float haze_end_km = 1500.0;

float ring_row_uv(float logical_row) {
    return (mod(logical_row + float(row_offset), rows) + 0.5) / rows;
}

vec2 local_ground_coordinates(vec3 point) {
    return vec2(radius_km * atan(-point.z, radius_km - point.y), point.x);
}

float handoff_hash(vec2 pixel) {
    return fract(52.9829189 * fract(dot(floor(pixel), vec2(0.06711056, 0.00583715))));
}

float near_weight(vec3 surface_point) {
    if (!near_ready) return 0.0;
    vec3 ray = normalize(surface_point - camera_origin_km);
    vec2 radial = camera_origin_km.yz - vec2(radius_km, 0.0);
    float a = dot(ray.yz, ray.yz);
    float b = dot(radial, ray.yz);
    float c = camera_origin_km.y * (camera_origin_km.y - 2.0 * radius_km)
        + camera_origin_km.z * camera_origin_km.z;
    float discriminant = b * b - a * c;
    if (a < 0.000001 || discriminant < 0.0) return 0.0;
    vec3 datum = camera_origin_km + ray * ((-b + sqrt(discriminant)) / a);
    vec2 grid = local_ground_coordinates(datum) - vec2(window_start_km, terrain_info.y);
    vec2 extent = vec2(rows - 1.0, columns - 1.0) * terrain_info.z;
    float edge = min(min(grid.x, grid.y), min(extent.x - grid.x, extent.y - grid.y));
    if (edge <= 0.0) return 0.0;
    float row = grid.x / terrain_info.z;
    float available = min(
        texture(availability_texture, vec2(0.5, ring_row_uv(floor(row)))).r,
        texture(availability_texture, vec2(0.5, ring_row_uv(ceil(row)))).r);
    return smoothstep(0.0, 8.0, edge) * available;
}

float cached_shadow(vec2 canonical_ground_km, float height_km) {
    if (!shadow_ready) return 0.0;
    vec2 delta = canonical_ground_km - shadow_center_km;
    float circumference = TAU * radius_km;
    delta.x = mod(delta.x + 0.5 * circumference, circumference) - 0.5 * circumference;
    // The map already integrates along the sun ray. Do not project it again.
    vec2 uv = delta / shadow_extent_km + vec2(0.5) + shadow_uv_offset;
    float edge = min(min(uv.x, uv.y), min(1.0 - uv.x, 1.0 - uv.y));
    if (edge <= 0.0) return 0.0;
    vec2 channels = texture(cloud_shadow_texture, uv).rg;
    float occlusion = 1.0 - (1.0 - channels.r) * (1.0 - channels.g);
    return occlusion * smoothstep(0.0, 0.015625, edge)
        * (1.0 - smoothstep(mix(cloud_floor_km, cloud_ceiling_km, 2.0 / 3.8), cloud_ceiling_km, height_km));
}

float distant_haze(vec3 point) {
    return haze_amount * smoothstep(haze_start_km, max(haze_end_km, haze_start_km + 0.001), distance(point, camera_origin_km));
}
