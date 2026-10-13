shader_type spatial;
// The project runtime's historical default is Lambert; state it explicitly so
// the custom direct-light path and engine reference remain stable by renderer.
render_mode cull_disabled, depth_draw_opaque, diffuse_lambert, specular_schlick_ggx;
uniform vec3 grass_base_color : source_color = vec3(.11, .20, .065);
uniform vec3 grass_tip_color : source_color = vec3(.38, .49, .19);
// Optional legacy blade-face shading. Ground-facing illumination is the default.
uniform bool grass_blade_shading_enabled = false;
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
#include "res://materials/ring_grass_gust.gdshaderinc"
#include "res://materials/ring_grass_modifiers.gdshaderinc"
#include "res://materials/ring_grass_interaction.gdshaderinc"
#include "res://materials/ring_grass_debug.gdshaderinc"
#include "res://materials/ring_grass_lighting.gdshaderinc"
varying float grass_cloud_occlusion;
varying float grass_tint_variation;
varying vec4 grass_local_modifier_tint;
varying float grass_lod_root_hash;
varying float grass_lod_instance_rank;
varying float grass_lod_handoff;
varying float grass_lod_final_fade;
varying float grass_terrain_color_blend;
varying float grass_terrain_root_coverage;
varying vec3 grass_terrain_up_view;
// fragment resolves the sanitized adaptation weight consumed by light().
varying float grass_terrain_light_blend;
varying vec2 grass_debug_deformation;
// light() cannot read fragment output SPECULAR; retain the debug override
// through an interpolant consumed by the custom direct-light path.
varying float grass_light_specular;
// Resolve the per-instance LOD pass in fragment(), then carry the decision to
// light(). Near grass always retains its established direct-light specular.
uniform bool grass_simplified_intermediate_shading = false;
varying float grass_light_full_shading;
// Experimental comparison path. Only non-near LOD passes consume it.
uniform bool grass_camera_facing_enabled = false;
uniform float grass_camera_facing_strength = 1.0;
uniform float grass_camera_facing_start = 12.0;
uniform float grass_camera_facing_end = 48.0;

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

// Finish ground lighting and brightness relief convergence before roots disappear.
// Keeping this band separate from final_fade leaves geometry and density selection intact.
float grass_ground_handoff_at_distance(float root_distance, float transition_width, float start_fraction, float end_fraction) {
	if (!grass_appearance_enabled || grass_terrain_handoff_strength <= 0.0
		|| isnan(grass_terrain_handoff_strength) || isinf(grass_terrain_handoff_strength)
		|| isnan(grass_terrain_draw_distance) || isinf(grass_terrain_draw_distance)) return 0.0;
	float cutoff = grass_terrain_draw_distance > 0.0 ? grass_terrain_draw_distance : grass_lod_far_distance;
	if (isnan(cutoff) || isinf(cutoff) || cutoff <= 0.0
		|| isnan(root_distance) || isinf(root_distance)
		|| isnan(transition_width) || isinf(transition_width)
		|| isnan(start_fraction) || isinf(start_fraction)
		|| isnan(end_fraction) || isinf(end_fraction)) return 0.0;
	float requested_start = clamp(start_fraction, 0.0, 1.0) * cutoff;
	float requested_end = clamp(end_fraction, 0.0, 1.0) * cutoff;
	if (requested_end <= requested_start) return 0.0;
	float fade_start = cutoff - transition_width * .5;
	// A transition wider than the entire cutoff has no positive pre-fade
	// range. Retain a finite band inside the cutoff rather than disabling tint.
	float end = min(requested_end, fade_start > 0.0 ? fade_start : cutoff);
	if (end <= 0.0) return 0.0;
	// Ordered 99/100 controls can land beyond the capped end. Pull only the
	// start back to reserve 1% of cutoff (at least .001m), leaving 70/90 intact.
	float minimum_band = min(end, max(.001, cutoff * .01));
	float start = min(requested_start, max(0.0, end - minimum_band));
	if (end <= start) return 0.0;
	return smoothstep(start, end, root_distance);
}

float grass_terrain_blend_at_distance(float root_distance, float transition_width) {
	return grass_ground_handoff_at_distance(root_distance, transition_width,
		grass_terrain_blend_start_fraction, grass_terrain_blend_end_fraction);
}

void vertex() {
	float t = clamp(VERTEX.y, 0.0, 1.0);
	float present = step(.0001, dot(abs(INSTANCE_CUSTOM), vec4(1.0)));
	float curve = mix(.5, clamp(INSTANCE_CUSTOM.x, 0.0, 1.0), present);
	grass_tint_variation = mix(.5, clamp(INSTANCE_CUSTOM.y, 0.0, 1.0), present);
	vec3 root = MODEL_MATRIX[3].xyz;
	grass_local_modifier_tint = grass_modifier_tint(root);
	mat3 basis = mat3(MODEL_MATRIX);
	vec3 lengths2 = max(vec3(.000001), vec3(dot(basis[0],basis[0]),dot(basis[1],basis[1]),dot(basis[2],basis[2])));
	float height = clamp(length(MODEL_MATRIX[1]), .05, 20.0);
	vec3 side = normalize(basis[0]);
	vec3 up = normalize(basis[1]);
	vec3 face = normalize(basis[2]);
	float root_distance = distance(root, CAMERA_POSITION_WORLD);
	float transition_width = max(grass_lod_transition_width, .001);
	grass_terrain_color_blend = grass_terrain_blend_at_distance(root_distance, transition_width);
	grass_terrain_up_view = mat3(VIEW_MATRIX) * up;
	grass_terrain_root_coverage = 0.0;
	if (grass_appearance_enabled && grass_terrain_handoff_strength > 0.0) {
		vec4 terrain_weights = clamp(COLOR, vec4(0.0), vec4(1.0));
		float grassland = max(0.0, 1.0 - dot(terrain_weights, vec4(1.0)));
		float eligibility = clamp(grassland + terrain_weights.y * .55, 0.0, 1.0);
		grass_terrain_root_coverage = ring_terrain_grass_coverage(root, root_distance, 1.0, eligibility);
	}
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
	// Keep clump source roots fixed when the distant width compensation is
	// active. The default mode retains the historical whole-instance origin.
	float width_origin = grass_camera_facing_enabled && grass_lod_pass != 0 ? CUSTOM0.x : 0.0;
	float original_x = VERTEX.x;
	float width_relative_x = original_x - width_origin;
	VERTEX.x = width_origin + width_relative_x * local_width;
	// Inverse-transpose for x' = x * (1 + k*y^2), then the bend Jacobian below.
	NORMAL.x /= local_width;
	NORMAL.y -= NORMAL.x * (2.0 * width_k * t * width_relative_x);
	// CUSTOM0 stores each source's local root x/z and its rest width axis x/z.
	// Both crossed cards of a tuft use the same source data, preserving their
	// crossed silhouette while the whole source turns around its planted root.
	if (grass_camera_facing_enabled && grass_lod_pass != 0) {
		vec3 source_root_local = vec3(CUSTOM0.x, 0.0, CUSTOM0.y);
		vec3 source_root_world = (MODEL_MATRIX * vec4(source_root_local, 1.0)).xyz;
		vec3 camera_flat = MAIN_CAM_INV_VIEW_MATRIX[3].xyz - source_root_world;
		camera_flat -= up * dot(camera_flat, up);
		float camera_flat_length = length(camera_flat);
		vec3 source_side_world = basis * vec3(CUSTOM0.z, 0.0, CUSTOM0.w);
		source_side_world -= up * dot(source_side_world, up);
		float source_side_length = length(source_side_world);
		if (camera_flat_length > .0001 && source_side_length > .0001) {
			vec3 target_side = normalize(cross(up, camera_flat / camera_flat_length));
			vec3 rest_side = source_side_world / source_side_length;
			float signed_angle = atan(dot(up, cross(rest_side, target_side)), dot(rest_side, target_side));
			float facing_t = smoothstep(grass_camera_facing_start, max(grass_camera_facing_start + .001, grass_camera_facing_end), root_distance);
			float angle = signed_angle * clamp(grass_camera_facing_strength, 0.0, 1.0) * facing_t;
			float c = cos(angle);
			float s = sin(angle);
			// Rodrigues rotation around the local tangent-plane normal.
			vec3 local_relative = VERTEX - source_root_local;
			vec3 world_relative = basis * local_relative;
			vec3 rotated_relative = world_relative * c + cross(up, world_relative) * s + up * dot(up, world_relative) * (1.0 - c);
			VERTEX = source_root_local + transpose(basis) * rotated_relative / lengths2;
			// Rest curve and lean are source-frame vectors, so turn them with the
			// card. Wind and interaction remain world-directed below.
			side = side * c + cross(up, side) * s + up * dot(up, side) * (1.0 - c);
			face = face * c + cross(up, face) * s + up * dot(up, face) * (1.0 - c);
			// Convert the local normal through the non-uniform instance basis,
			// rotate it in world space, then return the matching local covector.
			vec3 world_normal = normalize(basis * (NORMAL / lengths2));
			vec3 rotated_normal = world_normal * c + cross(up, world_normal) * s + up * dot(up, world_normal) * (1.0 - c);
			NORMAL = normalize(transpose(basis) * rotated_normal);
		}
	}
	if (lod_visible < .5) VERTEX = vec3(0.0);
	// Grass deliberately amplifies the shared weather motion, then bounds it
	// below two thirds of blade height. Curve is height-scaled, never width-scaled.
	float wind_strength = clamp(length(ring_wind_field_enabled > .5 ? ring_wind_mean_vector : ring_local_wind_vector) / 20.0, 0.0, 1.0);
	float flexible = mix(.82, 1.22, curve);
	vec3 tip_wind = ring_local_wind_displacement(root, 1.0, height);
	vec3 lateral;
	float lateral_limit = height * mix(.20, .58, wind_strength);
	if (ring_wind_field_enabled>.5) {
		float response = length(ring_wind_mean_vector)/20.0 * max(0.0,grass_wind_strength_multiplier)
			* flexible * grass_texture_gust(root);
		lateral = length(tip_wind)>.0001 ? normalize(tip_wind)*lateral_limit*(1.0-exp(-response)) : vec3(0.0);
	} else {
		lateral = tip_wind * (4.8 + grass_gust(INSTANCE_CUSTOM,present)*3.0*grass_wind_gust_multiplier)
			*grass_wind_strength_multiplier*flexible;
		if (length(lateral)>lateral_limit) lateral *= lateral_limit/length(lateral);
		lateral *= grass_texture_gust(root);
	}
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
	// Lower the complete authored tuft into its planted root, including every
	// wind/rest/interaction offset. Legacy color sliders control appearance,
	// including immediate matching at 0/1%; they must never erase nearby grass.
	// Geometry uses its own safe 70--90% band before stochastic retirement.
	// Uniform scaling preserves normals and avoids flattened hovering ribbons.
	if (grass_coordinated_handoff_enabled && grass_appearance_enabled) {
		float collapse = grass_ground_handoff_at_distance(root_distance, transition_width, .70, .90)
			* clamp(grass_terrain_handoff_strength, 0.0, 1.0);
		if (!isnan(collapse) && !isinf(collapse)) VERTEX *= max(.001, 1.0 - collapse);
	}
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
	albedo = grass_appearance_albedo_with_coverage(albedo, COLOR, grass_terrain_color_blend, grass_terrain_root_coverage);
	albedo = mix(albedo, grass_local_modifier_tint.rgb, clamp(grass_local_modifier_tint.a, 0.0, 1.0));
	float terrain_lighting = grass_appearance_enabled
		? grass_terrain_color_blend * clamp(grass_terrain_handoff_strength, 0.0, 1.0)
			* clamp(grass_terrain_lighting_match, 0.0, 1.0) : 0.0;
	if (isnan(terrain_lighting) || isinf(terrain_lighting)) terrain_lighting = 0.0;
	grass_terrain_light_blend = terrain_lighting;
	// Ground-facing diffuse and reduced blade self-shading close the brightness
	// gap at the cutoff in legacy mode. Default mode uses ground normals everywhere.
	if (!grass_blade_shading_enabled || terrain_lighting > 0.0) {
		vec3 ground_normal = grass_terrain_up_view;
		float ground_length2 = dot(ground_normal, ground_normal);
		if (!isnan(ground_length2) && !isinf(ground_length2) && ground_length2 > .000001) {
			ground_normal *= inversesqrt(ground_length2);
			vec3 blended_normal = grass_blade_shading_enabled
				? mix(NORMAL, ground_normal, terrain_lighting) : ground_normal;
			float blended_length2 = dot(blended_normal, blended_normal);
			NORMAL = !isnan(blended_length2) && !isinf(blended_length2) && blended_length2 > .000001
				? blended_normal * inversesqrt(blended_length2) : ground_normal;
		}
	}
	float cloud = grass_cloud_occlusion;
	ALBEDO = albedo * (1.0 - cloud * .32);
	ROUGHNESS = 1.0;
	SPECULAR = grass_blade_shading_enabled ? .05 * (1.0 - terrain_lighting) : 0.0;
	grass_light_specular = SPECULAR;
	grass_light_full_shading = grass_blade_shading_enabled
		&& !(grass_simplified_intermediate_shading && grass_lod_pass != 0) ? 1.0 : 0.0;
	// Darker roots ground the meadow while leaving tips responsive to the sun.
	AO = (grass_blade_shading_enabled ? mix(mix(.72, 1.0, blade), 1.0, terrain_lighting) : 1.0)
		* (1.0 - cloud * .70);
	AO_LIGHT_AFFECT = grass_blade_shading_enabled ? 1.0 - terrain_lighting : 0.0;
	BACKLIGHT = grass_blade_shading_enabled
		? albedo * mix(.07, .20, blade) * (1.0 - cloud * .55) * (1.0 - terrain_lighting) : vec3(0.0);
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
	// Read the planted up vector directly in default mode: neither the visible
	// face nor a renderer backface-normal adjustment can darken one blade side.
	vec3 lighting_normal = grass_blade_shading_enabled ? NORMAL : normalize(grass_terrain_up_view);
	float c_n_dot_l = max(min(dot(lighting_normal, LIGHT), 1.0), 0.0);
	float diffuse_brdf_nl = c_n_dot_l * (1.0 / PI);
	// The production terrain owns a custom ALBEDO-weighted direct-light
	// expression. Approach that exact expression only in the distant band.
	if (grass_terrain_light_blend > 0.0) {
		vec3 diffuse_response = mix(vec3(diffuse_brdf_nl), ALBEDO * c_n_dot_l, grass_terrain_light_blend);
		DIFFUSE_LIGHT += LIGHT_COLOR * diffuse_response * attenuation;
	} else {
		DIFFUSE_LIGHT += LIGHT_COLOR * diffuse_brdf_nl * attenuation;
	}
	DIFFUSE_LIGHT += LIGHT_COLOR * ((1.0 / PI) - diffuse_brdf_nl) * BACKLIGHT * attenuation;
	if (grass_light_full_shading > .5) {
		float c_n_dot_v = max(dot(NORMAL, VIEW), 1e-4);
		vec3 half_vector = normalize(VIEW + LIGHT);
		float c_l_dot_h = clamp(dot(LIGHT, half_vector), 0.0, 1.0);
		float c_n_dot_h = clamp(dot(NORMAL, half_vector), 0.0, 1.0);
		float d = grass_d_ggx(c_n_dot_h, 1.0);
		float g = grass_v_ggx(c_n_dot_l, c_n_dot_v, 1.0);
		float f0 = .16 * grass_light_specular * grass_light_specular;
		// Godot evaluates dot(vec3(f0), vec3(50.0 * .33)), hence three terms.
		float f90 = clamp(f0 * (3.0 * 50.0 * .33), 0.0, 1.0);
		float fresnel = f0 + (f90 - f0) * grass_schlick_fresnel(c_l_dot_h);
		vec3 direct_specular = vec3(c_n_dot_l * d * fresnel * g) * LIGHT_COLOR * attenuation * SPECULAR_AMOUNT;
		if (grass_terrain_light_blend > 0.0) direct_specular *= 1.0 - grass_terrain_light_blend;
		SPECULAR_LIGHT += direct_specular;
	}
}
