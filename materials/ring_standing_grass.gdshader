shader_type spatial;
// The project runtime's historical default is Lambert; state it explicitly so
// the custom direct-light path and engine reference remain stable by renderer.
render_mode cull_disabled, depth_draw_opaque, diffuse_lambert, specular_schlick_ggx;
uniform vec3 grass_base_color : source_color = vec3(.11, .20, .065);
uniform vec3 grass_tip_color : source_color = vec3(.38, .49, .19);
#include "res://materials/ring_grass_appearance.gdshaderinc"
// The streaming layer enables this only when it has installed the paired near
// and intermediate MultiMeshes. Keeping it off preserves the established
// single-mesh appearance for gameplay and the meadow benchmark.
uniform bool grass_lod_enabled = false;
uniform float grass_lod_near_distance = 48.0;
uniform float grass_lod_far_distance = 115.0;
uniform float grass_lod_transition_width = 12.0;
uniform float grass_lod_near_density = 1.0;
uniform float grass_lod_far_density = .45;
uniform float grass_lod_max_width_scale = 1.35;
// Set these on each MultiMeshInstance3D with SetInstanceShaderParameter, so
// paired renderers can keep their own pass/count while sharing this shader.
instance uniform int grass_lod_pass = 0;
instance uniform float grass_instance_count = 1.0;
// GPU compaction has already selected its roots against their original source
// indices. Keep the established CPU vertex-LOD path intact for every fallback.
instance uniform bool grass_gpu_culled = false;
#include "res://materials/ring_cloud_shadow.gdshaderinc"
#include "res://materials/ring_local_wind.gdshaderinc"
#include "res://materials/ring_grass_interaction.gdshaderinc"
#include "res://materials/ring_grass_debug.gdshaderinc"
#include "res://materials/ring_grass_lighting.gdshaderinc"
varying float grass_cloud_occlusion;
varying float grass_tint_variation;
varying float grass_lod_root_hash;
varying float grass_lod_instance_rank;
varying float grass_lod_handoff;
varying float grass_lod_final_fade;
varying vec2 grass_debug_deformation;
// light() cannot read fragment output SPECULAR; retain the debug override
// through an interpolant consumed by the custom direct-light path.
varying float grass_light_specular;

float phased_wave(vec2 clock, float phase) {
	float a = phase * 6.28318530718;
	return clock.x * cos(a) + clock.y * sin(a);
}

// z/w are periodic around/across anchors, approximately one cycle per 48m.
// Around uses an integral wave count across the full ring. Only integer harmonics
// are used, so both fields agree at every tile boundary and ring seam.
float grass_gust(vec4 custom_data, float present) {
	float direction_length = length(ring_local_wind_vector);
	if (ring_local_wind_enabled < .5 || direction_length < .0001) return 0.0;
	vec2 signed_direction = ring_local_wind_vector / direction_length;
	vec2 direction = abs(signed_direction);
	float broad = direction.x * phased_wave(ring_local_wind_wave.xy, -sign(signed_direction.x) * custom_data.z)
		+ direction.y * phased_wave(ring_local_wind_wave.xy, -sign(signed_direction.y) * custom_data.w);
	float ripple = direction.x * phased_wave(ring_local_wind_wave.zw, -sign(signed_direction.x) * fract(custom_data.z * 4.0))
		+ direction.y * phased_wave(ring_local_wind_wave.zw, -sign(signed_direction.y) * fract(custom_data.w * 3.0));
	return present * (broad * .28 + ripple * .10);
}

float grass_lod_hash(vec2 canonical_seed, float instance_id) {
	// Placement packs deterministic canonical seeds in x/y. Unlike local world
	// positions, they survive floating-origin shifts and frame reorientation.
	return fract(sin(dot(canonical_seed, vec2(12.9898, 78.233)) + instance_id * .12345) * 43758.5453);
}

void vertex() {
	float t = clamp(VERTEX.y, 0.0, 1.0);
	float present = step(.0001, dot(abs(INSTANCE_CUSTOM), vec4(1.0)));
	float curve = mix(.5, clamp(INSTANCE_CUSTOM.x, 0.0, 1.0), present);
	grass_tint_variation = mix(.5, clamp(INSTANCE_CUSTOM.y, 0.0, 1.0), present);
	vec3 root = MODEL_MATRIX[3].xyz;
	mat3 basis = mat3(MODEL_MATRIX);
	vec3 lengths2 = max(vec3(.000001), vec3(dot(basis[0],basis[0]),dot(basis[1],basis[1]),dot(basis[2],basis[2])));
	float height = clamp(length(MODEL_MATRIX[1]), .05, 20.0);
	vec3 side = normalize(basis[0]);
	vec3 up = normalize(basis[1]);
	vec3 face = normalize(basis[2]);
	float root_distance = distance(root, CAMERA_POSITION_WORLD);
	float transition_width = max(grass_lod_transition_width, .001);
	float handoff = smoothstep(grass_lod_near_distance - transition_width * .5,
		grass_lod_near_distance + transition_width * .5, root_distance);
	float density_fraction = mix(clamp(grass_lod_near_density, 0.0, 1.0),
		clamp(grass_lod_far_density, 0.0, 1.0), handoff);
	float instance_rank = (float(INSTANCE_ID) + .5) / max(grass_instance_count, 1.0);
	float lod_hash = grass_lod_hash(INSTANCE_CUSTOM.xy, float(INSTANCE_ID));
	float final_fade = smoothstep(grass_lod_far_distance - transition_width * .5,
		grass_lod_far_distance + transition_width * .5, root_distance);
	// This is a deterministic root-level dither: paired passes select opposite
	// roots at the handoff, then the same selection fades into terrain.
	float lod_visible = 1.0;
	if (grass_lod_enabled && !grass_gpu_culled) {
		lod_visible *= step(instance_rank, density_fraction);
		lod_visible *= grass_lod_pass == 0 ? step(handoff, lod_hash) : step(lod_hash, handoff);
		lod_visible *= step(final_fade, lod_hash);
	}
	grass_lod_root_hash = lod_hash;
	grass_lod_instance_rank = instance_rank;
	grass_lod_handoff = handoff;
	grass_lod_final_fade = final_fade;
	// Widen only away from the root and only when the ribbon becomes edge-on.
	// The camera view is projected onto the tangent plane so looking down does
	// not create a false edge-on boost. The material cap is bounded at 2x.
	vec3 view_flat = CAMERA_POSITION_WORLD - root;
	view_flat -= up * dot(view_flat, up);
	float view_flat_length = length(view_flat);
	float edge_on = view_flat_length > .0001
		? 1.0 - abs(dot(view_flat / view_flat_length, face)) : 0.0;
	float width_target = grass_lod_enabled && grass_lod_pass != 0
		? mix(1.0, clamp(grass_lod_max_width_scale, 1.0, 2.0), handoff * edge_on) : 1.0;
	float width_k = width_target - 1.0;
	float local_width = 1.0 + width_k * t * t;
	float original_x = VERTEX.x;
	VERTEX.x = original_x * local_width;
	// Inverse-transpose for x' = x * (1 + k*y^2), then the bend Jacobian below.
	NORMAL.x /= local_width;
	NORMAL.y -= NORMAL.x * (2.0 * width_k * t * original_x);
	if (lod_visible < .5) VERTEX = vec3(0.0);
	// Grass deliberately amplifies the shared weather motion, then bounds it
	// below two thirds of blade height. Curve is height-scaled, never width-scaled.
	float wind_strength = clamp(length(ring_local_wind_vector) / 20.0, 0.0, 1.0);
	float flexible = mix(.82, 1.22, curve);
	vec3 tip_wind = ring_local_wind_displacement(root, 1.0, height);
	vec3 lateral = tip_wind * (4.8 + grass_gust(INSTANCE_CUSTOM, present) * 3.0 * grass_wind_gust_multiplier) * grass_wind_strength_multiplier * flexible;
	float lateral_limit = height * mix(.20, .58, wind_strength);
	if (length(lateral) > lateral_limit) lateral *= lateral_limit / length(lateral);
	vec3 curve_world = side * ((curve - .5) * height * .34);
	// Each dense ribbon starts with a real height-scaled lean through its face
	// plane. Tint supplies a deterministic signed variation without new payload.
	float lean_sign = grass_tint_variation < .5 ? -1.0 : 1.0;
	float rest_lean = height * mix(.16, .36, curve) * lean_sign * mix(.88, 1.08, grass_tint_variation);
	vec3 interaction_lateral = grass_interaction_lateral(root, up, height);
	grass_debug_deformation = vec2(clamp(length(lateral) / max(height * .58, .001), 0.0, 1.0), clamp(length(interaction_lateral) / max(height * .65, .001), 0.0, 1.0));
	vec3 total_lateral = lateral + curve_world + face * rest_lean + interaction_lateral;
	// Arc-length shortening avoids a stretched vertical ribbon at strong gusts.
	float short_coeff = min(height * .20, dot(total_lateral, total_lateral) / max(height * 1.45, .05));
	vec3 world_offset = total_lateral * t * t - up * short_coeff * t * t * t;
	VERTEX += transpose(basis) * world_offset / lengths2;

	// Inverse-transpose of the deformation Jacobian I + dLocal * ey^T.
	// It preserves each input mesh normal, including the legacy cards' rounded
	// edge normals, while tracking the exact t^2/t^3 deformation derivative.
	vec3 d_world = 2.0 * total_lateral * t - 3.0 * up * short_coeff * t * t;
	vec3 d_local = transpose(basis) * d_world / lengths2;
	float normal_y_scale = max(.4, 1.0 + d_local.y);
	NORMAL.y = (NORMAL.y - dot(NORMAL.xz, d_local.xz)) / normal_y_scale;
	NORMAL = normalize(NORMAL);
	grass_cloud_occlusion = sample_local_cloud_shadow((MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz);
}

void fragment() {
	if (grass_lod_enabled && !grass_gpu_culled) {
		float density = mix(clamp(grass_lod_near_density, 0.0, 1.0), clamp(grass_lod_far_density, 0.0, 1.0), grass_lod_handoff);
		if (grass_lod_instance_rank > density || (grass_lod_pass == 0 && grass_lod_root_hash < grass_lod_handoff) || (grass_lod_pass != 0 && grass_lod_root_hash >= grass_lod_handoff) || grass_lod_root_hash < grass_lod_final_fade) discard;
	}
	float blade = clamp(UV.y, 0.0, 1.0);
	vec3 albedo = mix(grass_base_color, grass_tip_color, blade) * mix(.86, 1.14, grass_tint_variation);
	vec3 debug = grass_debug_color(clamp(grass_lod_instance_rank, 0.0, 1.0), grass_debug_deformation.x, grass_debug_deformation.y, grass_lod_pass);
	albedo = grass_appearance_albedo(albedo, COLOR, grass_lod_final_fade);
	float cloud = grass_cloud_occlusion;
	ALBEDO = albedo * (1.0 - cloud * .32);
	ROUGHNESS = 1.0;
	SPECULAR = .05;
	grass_light_specular = .05;
	// Darker roots ground the meadow while leaving tips responsive to the sun.
	AO = mix(.72, 1.0, blade) * (1.0 - cloud * .70);
	AO_LIGHT_AFFECT = 1.0;
	BACKLIGHT = albedo * mix(.07, .20, blade) * (1.0 - cloud * .55);
	if (debug.x >= 0.0) {
		ALBEDO = vec3(0.0);
		EMISSION = debug;
		SPECULAR = 0.0;
		grass_light_specular = 0.0;
		AO = 1.0;
		BACKLIGHT = vec3(0.0);
	}
}

void light() {
	// Keep this in the stage entry point: Godot's LIGHT, ATTENUATION and output
	// accumulators are stage-local shader built-ins.
	vec3 sun_view = normalize((VIEW_MATRIX * vec4(grass_primary_sun_direction, 0.0)).xyz);
	float attenuation = ATTENUATION * grass_primary_sun_visibility(LIGHT_IS_DIRECTIONAL, LIGHT, sun_view);
	float c_n_dot_l = max(min(dot(NORMAL, LIGHT), 1.0), 0.0);
	float c_n_dot_v = max(dot(NORMAL, VIEW), 1e-4);
	vec3 half_vector = normalize(VIEW + LIGHT);
	float c_l_dot_h = clamp(dot(LIGHT, half_vector), 0.0, 1.0);
	float diffuse_brdf_nl = c_n_dot_l * (1.0 / PI);
	DIFFUSE_LIGHT += LIGHT_COLOR * diffuse_brdf_nl * attenuation;
	DIFFUSE_LIGHT += LIGHT_COLOR * ((1.0 / PI) - diffuse_brdf_nl) * BACKLIGHT * attenuation;
	float c_n_dot_h = clamp(dot(NORMAL, half_vector), 0.0, 1.0);
	float d = grass_d_ggx(c_n_dot_h, 1.0);
	float g = grass_v_ggx(c_n_dot_l, c_n_dot_v, 1.0);
	float f0 = .16 * grass_light_specular * grass_light_specular;
	// Godot evaluates dot(vec3(f0), vec3(50.0 * .33)), hence three terms.
	float f90 = clamp(f0 * (3.0 * 50.0 * .33), 0.0, 1.0);
	float fresnel = f0 + (f90 - f0) * grass_schlick_fresnel(c_l_dot_h);
	SPECULAR_LIGHT += vec3(c_n_dot_l * d * fresnel * g) * LIGHT_COLOR * attenuation * SPECULAR_AMOUNT;
}
