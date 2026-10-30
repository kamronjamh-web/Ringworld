shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, skip_vertex_transform;
// Gust front dust and spray (docs/storm-revamp-s3-storm-arrival-design.md WO-D1): soft camera-facing puffs, dust (tan) on bare and dry ground, spray (white) over water.
// Motion and placement: ring_gust_debris_common.gdshaderinc. Drawn unshaded but lit by hand with the same terms every receiver uses (sun colour and shadow come from the
// directional light the CPU copies in, storm shade, cloud shadow, sampled per vertex) and the per-fragment physical haze, so they take regular haze and lighting without a lit-transparent pass.
#include "res://materials/ring_atmosphere.gdshaderinc"
#include "res://materials/ring_cloud_shadow.gdshaderinc"
#include "res://materials/ring_gust_debris_common.gdshaderinc"
#include "res://materials/ring_gust_debris_light.gdshaderinc"

uniform sampler2D depth_tex : hint_depth_texture, repeat_disable, filter_nearest;
uniform vec3 deb_sun_color = vec3(1.0);     // direct sun radiance (colour x energy), 0 at night
uniform vec3 deb_sky_color = vec3(0.3, 0.38, 0.5);   // ambient sky radiance
uniform float deb_dust_alpha = 0.20;
uniform float deb_spray_alpha = 0.16;

varying vec3 v_world;
varying vec4 v_color;      // rgb lit albedo before haze, a alpha
varying vec2 v_uv;
varying float v_day;
varying float v_view_z;

void vertex() {
	DebPlace d = deb_place(INSTANCE_ID, 0x51ed27u, MODEL_MATRIX[3].xyz);
	float dust_w = d.kinds.y, spray_w = d.kinds.z;
	float total = dust_w + spray_w;
	float biome_draw = fract(d.rnd.x * 5.17 + d.rnd.w * 2.31);
	bool spray = d.kind_select < spray_w / max(total, 1.0e-4);
	float present = (total > 0.001 && biome_draw < min(total, 1.0)) ? 1.0 : 0.0;
	float grow = 0.75 + 0.5 * sin(deb_time * 0.7 + d.rnd.y * 40.0);
	float size = spray ? mix(0.35, 0.8, d.rnd.y) * grow : mix(0.9, 2.3, d.rnd.y) * grow * mix(1.0, 0.6, d.snow);   // spindrift puffs are smaller
	float hover_m = spray ? 0.1 + d.hover * 0.7 : 0.25 + d.hover * 2.4 * (0.5 + 0.5 * deb_level);
	vec3 centre = d.rel + deb_up * hover_m;
	vec3 right = INV_VIEW_MATRIX[0].xyz, upv = INV_VIEW_MATRIX[1].xyz;
	vec3 world = MODEL_MATRIX[3].xyz + centre + (right * VERTEX.x + upv * VERTEX.y) * size;
	float alpha = d.alive * present * (spray ? deb_spray_alpha : deb_dust_alpha) * (0.6 + 0.4 * d.rnd.z);
	alpha *= smoothstep(2.0, 7.0, length(centre));   // no big blobs in the face
	if (alpha <= 0.002) world = MODEL_MATRIX[3].xyz;
	vec3 dust_color = mix(vec3(0.50, 0.42, 0.32), vec3(0.86, 0.90, 0.96), d.snow), spray_color = vec3(0.85, 0.9, 0.95);
	v_world = world;
	v_day = deb_day_factor(world);
	float storm = ring_storm_level(world);
	float storm_ambient = ring_storm_ambient(storm);
	float cloud_shadow = sample_local_cloud_shadow(world) * v_day;
	float sun_vis = v_day * (1.0 - ring_storm_shadow_fade(storm)) * (1.0 - cloud_shadow);
	// Hand lighting: ambient sky (limited by the storm ambient) plus the sun (puffs are diffuse, no direction; the diffuse scale is in deb_sun_color, set by the CPU).
	vec3 lit = (deb_sky_color * storm_ambient * mix(0.04, 1.0, v_day) + deb_sun_color * sun_vis) * (spray ? spray_color : dust_color);
	v_color = vec4(lit, alpha);
	v_uv = UV;
	vec3 view_pos = (VIEW_MATRIX * vec4(world, 1.0)).xyz;
	v_view_z = view_pos.z;
	VERTEX = view_pos;
}

void fragment() {
	vec2 c = v_uv * 2.0 - 1.0;
	float r = length(c);
	float shape = pow(clamp(1.0 - r, 0.0, 1.0), 1.5);
	// Soft particle: fade where the puff meets opaque geometry.
	float depth = texture(depth_tex, SCREEN_UV).r;
	vec3 ndc = vec3(SCREEN_UV * 2.0 - 1.0, depth);
	vec4 view = INV_PROJECTION_MATRIX * vec4(ndc, 1.0);
	view.xyz /= view.w;
	float soft = clamp((-view.z + v_view_z) / 0.6, 0.0, 1.0);
	RingHaze haze = ring_haze_sample(ring_atmosphere_camera_world_position, v_world, ring_center, v_day);
	ALBEDO = v_color.rgb * haze.T + ring_haze_inscatter(haze);
	ALPHA = clamp(v_color.a * shape * soft, 0.0, 1.0);
}
