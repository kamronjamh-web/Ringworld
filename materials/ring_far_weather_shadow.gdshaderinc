#ifndef RING_FAR_WEATHER_SHADOW_GDSHADERINC
#define RING_FAR_WEATHER_SHADOW_GDSHADERINC
// Far cloud shadow from the real whole-ring weather (docs/shadow-revamp-plan.md
// current-state row 9). The receiver projects along the sun ray to the far
// cloud shell's mid-band altitude and reads the shell's own cloud amount there:
// RingScaledFarViewport bakes ring_far_weather_cloud_amount (the formula the
// visible far shell draws) over the whole ring from the same weather atlas,
// wind clock and Sunshine noise resources (materials/ring_far_weather_cloud_bake.gdshader).
// The Sunshine near/far projected maps stay authoritative where they exist; this
// term fills the rest through their coverage weight
// (ring_cloud_shadow_occlusion_with_far_weather). Include after
// ring_cloud_shadow.gdshaderinc.
//
// Globals (scripts/RingFarWeatherShadowGlobals.cs):
//   ring_far_weather_cloud_map  R = far shell cloud amount; U around (repeats), V across
//   ring_far_weather_frame      x circumference m, y ring width m (x = 0 until the first bake: no far shadow),
//                               z Sunshine altitude crossfade (the projected maps are weighted by 1 - z)
//   ring_far_weather_shadow     x strength (0 = off), y extinction per metre, z/w background band floor/ceiling m
global uniform sampler2D ring_far_weather_cloud_map : filter_linear, repeat_enable;
global uniform vec4 ring_far_weather_frame;
global uniform vec4 ring_far_weather_shadow;

// Metres per world unit of this receiver's viewport: 1 in the main view, 1000
// in the 1:1000 scaled far viewport.
uniform float far_weather_shadow_meters_per_unit = 1.0;

// Optical-depth scale of the baked (band-limited, mid-band slice) cloud amount
// relative to the band thickness, so the far term reproduces the projected
// maps' column optical depth. Calibrated in game (RTX 4070, 2026-10-22) against
// the Sunshine maps over the same ground (see RingFarWeatherShadowGlobals).
const float RING_FAR_WEATHER_SHADOW_BAND_FILL = 1.7;

// Cubic B-spline reconstruction of the ~2 km baked map from four bilinear taps.
// Plain bilinear shows each texel's straight edges as a stair-stepped grid on
// the ground from altitude; the B-spline is smooth (C2) at texel boundaries.
float ring_far_weather_cloud_map_bspline(vec2 uv) {
	vec2 size = vec2(textureSize(ring_far_weather_cloud_map, 0));
	vec2 texel = uv * size - 0.5;
	vec2 base = floor(texel);
	vec2 f = texel - base;
	vec2 f2 = f * f;
	vec2 f3 = f2 * f;
	vec2 w0 = (1.0 - 3.0 * f + 3.0 * f2 - f3) / 6.0;
	vec2 w1 = (4.0 - 6.0 * f2 + 3.0 * f3) / 6.0;
	vec2 w2 = (1.0 + 3.0 * f + 3.0 * f2 - 3.0 * f3) / 6.0;
	vec2 w3 = f3 / 6.0;
	vec2 g0 = w0 + w1;
	vec2 g1 = w2 + w3;
	vec2 h0 = (base - 0.5 + w1 / g0) / size;
	vec2 h1 = (base + 1.5 + w3 / g1) / size;
	float t00 = textureLod(ring_far_weather_cloud_map, vec2(h0.x, h0.y), 0.0).r;
	float t10 = textureLod(ring_far_weather_cloud_map, vec2(h1.x, h0.y), 0.0).r;
	float t01 = textureLod(ring_far_weather_cloud_map, vec2(h0.x, h1.y), 0.0).r;
	float t11 = textureLod(ring_far_weather_cloud_map, vec2(h1.x, h1.y), 0.0).r;
	return g0.y * (g0.x * t00 + g1.x * t10) + g1.y * (g0.x * t01 + g1.x * t11);
}

// Direct-sun occlusion (0 lit .. strength) of the far weather cloud shell over
// a receiver. Positions are in this viewport's world units; toward_sun points
// toward the sun.
float ring_far_weather_cloud_occlusion(vec3 receiver_world, vec3 receiver_ring_center, vec3 toward_sun) {
	float strength = ring_far_weather_shadow.x;
	float circumference = ring_far_weather_frame.x;
	float width = ring_far_weather_frame.y;
	if (strength <= 0.0 || circumference <= 1.0 || width <= 1.0) return 0.0;
	vec3 radial = receiver_world - receiver_ring_center;
	float radial_length = length(radial.xz);
	if (radial_length <= 0.0) return 0.0;
	vec3 up = vec3(-radial.x, 0.0, -radial.z) / radial_length;
	vec3 sun = normalize(toward_sun);
	float sun_up = dot(sun, up);
	if (sun_up <= 0.0) return 0.0;
	// Same grazing limit as the projected maps (RingSunshineCloudBridge skips
	// cascades below 0.04); the receiver day factor fades this band out.
	sun_up = max(sun_up, 0.04);
	float meters_per_unit = far_weather_shadow_meters_per_unit;
	float radius_meters = circumference / 6.28318530718;
	vec2 band = ring_far_weather_shadow.zw;
	if (band.y <= band.x + 1.0) band = vec2(2000.0, 6000.0);
	float slice_altitude = (band.x + band.y) * 0.5;
	float receiver_altitude = clamp(radius_meters - radial_length * meters_per_unit, -2000.0, slice_altitude);
	float rise = (slice_altitude - receiver_altitude) / sun_up;
	vec3 tangent = vec3(-radial.z, 0.0, radial.x) / radial_length;
	float angle = atan(radial.z, radial.x);
	float around = angle * radius_meters + dot(sun, tangent) * rise;
	float across = radial.y * meters_per_unit + width * 0.5 + sun.y * rise;
	if (across < 0.0 || across > width) return 0.0;
	float cloud = ring_far_weather_cloud_map_bspline(vec2(fract(around / circumference), across / width));
	float optical_depth = ring_far_weather_shadow.y * cloud * (band.y - band.x)
		* RING_FAR_WEATHER_SHADOW_BAND_FILL / sun_up;
	return (1.0 - exp(-optical_depth)) * strength;
}

// Cloud occlusion for a far-weather receiver: the Sunshine projected maps over
// their coverage weight, the far weather shell everywhere they do not reach.
float ring_cloud_shadow_occlusion_with_far_weather(
		vec3 receiver_world, vec3 receiver_ring_center, vec3 toward_sun, bool far_weather_enabled) {
	vec2 local_shadow = sample_local_cloud_shadow_coverage(receiver_world)
		* (1.0 - clamp(ring_far_weather_frame.z, 0.0, 1.0));
	float far_shadow = 0.0;
	if (far_weather_enabled && local_shadow.y < 0.999) {
		far_shadow = ring_far_weather_cloud_occlusion(receiver_world, receiver_ring_center, toward_sun);
	}
	return local_shadow.x + far_shadow * (1.0 - local_shadow.y);
}
#endif
