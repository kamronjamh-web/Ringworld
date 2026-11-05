shader_type spatial;
render_mode cull_disabled, diffuse_lambert, specular_disabled, skip_vertex_transform;
// Gust front leaves (docs/storm-revamp-s3-storm-arrival-design.md WO-D1): tumbling alpha-scissor leaf quads, opaque, depth writing and lit like every other receiver
// (sun, storm shade, cloud shadow, per-fragment physical haze). Motion and placement: ring_gust_debris_common.gdshaderinc. No shadow casting (cast_shadow OFF on the renderer).
#include "res://materials/ring_atmosphere.gdshaderinc"
#include "res://materials/ring_cloud_shadow.gdshaderinc"
#include "res://materials/ring_gust_debris_common.gdshaderinc"
#include "res://materials/ring_gust_debris_light.gdshaderinc"

varying vec3 v_world;
varying vec4 v_color;      // rgb albedo, a alive
varying vec2 v_uv;
varying float v_day;
varying float v_storm_fade;

vec3 deb_rotate(vec3 v, vec3 axis, float angle) {
	float c = cos(angle), s = sin(angle);
	return v * c + cross(axis, v) * s + axis * dot(axis, v) * (1.0 - c);
}

void vertex() {
	DebPlace d = deb_place(INSTANCE_ID, 0u, MODEL_MATRIX[3].xyz);
	// Leaves: green to olive to brown, drier ones where the dust share is high.
	float dry = clamp(d.kinds.y * 2.0, 0.0, 1.0);
	vec3 green = vec3(0.09, 0.20, 0.035), olive = vec3(0.17, 0.20, 0.04), brown = vec3(0.30, 0.17, 0.07);   // linear albedo like the tree foliage around (the old 0.03-0.08 read black)
	float pick = d.rnd.z;
	vec3 albedo = pick < 0.5 - 0.3 * dry ? mix(green, olive, pick * 2.0) : mix(olive, brown, clamp((pick - 0.4) * 1.7 + 0.5 * dry, 0.0, 1.0));
	float size = mix(0.08, 0.17, d.rnd.y) * d.alive;   // grows in with alive: no binary pop
	float lift = pow(d.hover, 1.5) * (0.4 + 0.6 * deb_level);
	float hover_m = 0.12 + lift * 3.2 + 0.18 * sin(deb_time * (2.0 + 3.0 * d.rnd.w) + d.rnd.x * 40.0);
	vec3 axis = normalize(vec3(d.rnd.x - 0.5, d.rnd.y - 0.5, d.rnd.w - 0.5) * 2.0 + vec3(0.001, 0.0, 0.0));
	float spin = mix(2.0, 9.0, d.rnd.z) * (0.5 + 0.8 * deb_level + 0.05 * length(deb_wind));
	float angle = deb_time * spin + d.rnd.w * 6.2831853;
	vec3 centre = d.rel + deb_up * hover_m;
	// A leaf never fills the view: it shrinks to nothing inside 0.8 m of the camera (before the quad corners are built from size).
	size *= smoothstep(0.8, 3.0, length(centre));
	vec3 local = vec3((UV.x - 0.5) * 2.0 * size, (UV.y - 0.5) * 2.0 * size * 1.3, 0.0);
	vec3 normal = deb_rotate(vec3(0.0, 0.0, 1.0), axis, angle);
	vec3 world = MODEL_MATRIX[3].xyz + centre + deb_rotate(local, axis, angle);
	if (d.alive <= 0.001) world = MODEL_MATRIX[3].xyz;
	// Biome: a leaf exists where the cell's leaf share covers this particle's biome draw.
	float biome_draw = fract(d.rnd.x * 7.31 + d.rnd.y * 3.17);
	if (biome_draw >= d.kinds.x) world = MODEL_MATRIX[3].xyz;
	v_world = world;
	v_color = vec4(albedo, d.alive);
	v_uv = UV;
	v_day = deb_day_factor(world);
	v_storm_fade = ring_storm_shadow_fade(ring_storm_level(world));
	VERTEX = (VIEW_MATRIX * vec4(world, 1.0)).xyz;
	NORMAL = normalize((VIEW_MATRIX * vec4(normal, 0.0)).xyz);
}

void fragment() {
	// Leaf outline: a pointed ellipse along v with a short stalk.
	float t = clamp(v_uv.y, 0.0, 1.0);
	float half_width = 0.5 * pow(sin(3.14159265 * t), 0.75) * (0.55 + 0.45 * (1.0 - t));
	float mask = abs(v_uv.x - 0.5) < half_width ? 1.0 : 0.0;
	ALPHA = mask * step(0.001, v_color.a);
	ALPHA_SCISSOR_THRESHOLD = 0.5;
	vec3 shaded_albedo, emission;
	float ao;
	deb_shade(v_world, v_day, v_color.rgb * (0.82 + 0.18 * v_uv.y), shaded_albedo, emission, ao);
	ALBEDO = shaded_albedo;
	EMISSION = emission;
	ROUGHNESS = 0.85;
	SPECULAR = 0.0;
	AO = ao;
	AO_LIGHT_AFFECT = 1.0;
}

void light() {
	if (LIGHT_IS_DIRECTIONAL) {
		// Leaves are thin: lit from both sides, the sun fades with the local day and the storm shade, like every receiver.
		float n_dot_l = abs(dot(NORMAL, LIGHT));
		float visibility = clamp(ATTENUATION, 0.0, 1.0) * v_day * (1.0 - v_storm_fade);
		DIFFUSE_LIGHT += (LIGHT_COLOR / PI) * n_dot_l * visibility;
	} else {
		DIFFUSE_LIGHT += (LIGHT_COLOR / PI) * abs(dot(NORMAL, LIGHT)) * ATTENUATION;
	}
}
