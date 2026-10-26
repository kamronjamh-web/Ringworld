// Precipitation occlusion map access (docs/storm-revamp-s1-design.md section 1; producer scripts/Weather/RingPrecipitationOcclusion.cs).
// The map is an orthographic top-down depth of everything opaque except grass, trees, the player and weather meshes, stored as metres below
// the map plane (R32F, 0 = nothing drawn). The frame is published in world space every frame, so floating-origin rebases need no re-render.
// Usage: float exposed = ring_precip_exposure(world_position); // 1 = open sky, 0 = sheltered. Always 1 until the map is ready/active.

global uniform sampler2D ring_precip_occlusion : filter_nearest, repeat_disable;
global uniform vec4 ring_precip_occlusion_origin;  // xyz: map plane centre (world), w: far depth below the plane (m)
global uniform vec4 ring_precip_occlusion_around;  // xyz: image up axis (around tangent, world), w: half extent (m)
global uniform vec4 ring_precip_occlusion_across;  // xyz: image right axis (world; up = cross(across, around)), w: texel size (m)
global uniform vec4 ring_precip_occlusion_state;   // x: 1 when published and active, y: resolution (px), z: frame serial, w: reserved

const float RING_PRECIP_TOLERANCE = 0.2;

// Metres of rain-blocking surface above a world point: > 0 means the first opaque surface seen from above is higher than the point.
// Returns false (and cover = 0) when the map is not usable or the point lies outside it.
bool ring_precip_cover(vec3 world, out float cover) {
	cover = 0.0;
	if (ring_precip_occlusion_state.x < 0.5) return false;
	vec3 rel = world - ring_precip_occlusion_origin.xyz;
	vec3 image_x = ring_precip_occlusion_across.xyz;
	vec3 image_y = ring_precip_occlusion_around.xyz;
	vec3 up = cross(image_x, image_y);
	float half_extent = ring_precip_occlusion_around.w;
	vec2 uv = vec2(dot(rel, image_x) / (2.0 * half_extent) + 0.5, 0.5 - dot(rel, image_y) / (2.0 * half_extent));
	if (uv.x < 0.0 || uv.y < 0.0 || uv.x >= 1.0 || uv.y >= 1.0) return false;
	float depth_below_plane = -dot(rel, up);
	if (depth_below_plane > ring_precip_occlusion_origin.w) return false;
	float surface = textureLod(ring_precip_occlusion, uv, 0.0).r;
	cover = surface > 0.0 ? max(0.0, depth_below_plane - surface) : 0.0;
	return true;
}

float ring_precip_exposure(vec3 world) {
	float cover;
	if (!ring_precip_cover(world, cover)) return 1.0;
	return 1.0 - smoothstep(RING_PRECIP_TOLERANCE, RING_PRECIP_TOLERANCE + 0.3, cover);
}
