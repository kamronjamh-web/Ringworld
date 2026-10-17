shader_type spatial;
render_mode blend_mix, cull_disabled, depth_draw_always, shadows_disabled;

uniform vec3 camera_world_position = vec3(0.0);
uniform bool far_fade_enabled = false;
uniform float fade_out_start_meters = 480.0;
uniform float fade_out_end_meters = 640.0;
uniform bool geomorph_enabled = true;
uniform float morph_start_meters = 480.0;
uniform float morph_end_meters = 640.0;
uniform vec3 ring_center = vec3(0.0);
uniform vec3 sun_direction = vec3(1.0, 0.0, 0.0);
uniform float day_start_dot = -0.2;
uniform float day_full_dot = 0.35;
uniform vec3 night_tint = vec3(0.30, 0.45, 0.70);
uniform float night_direct_light_factor : hint_range(0.0, 1.0) = 0.0;
uniform float day_direct_light_factor : hint_range(0.0, 2.0) = 1.0;
uniform float shadow_floor : hint_range(0.0, 1.0) = 0.0;
uniform float ocean_roughness : hint_range(0.02, 0.5) = 0.16;
uniform float ocean_specular : hint_range(0.0, 1.0) = 0.92;
uniform float ring_width_meters = 20000.0;
// Must remain the first instance uniform: terrain and ocean share one mesh,
// and Godot requires this index to match the terrain material's index zero.
uniform sampler2D coverage_atlas : filter_nearest, repeat_disable;
instance uniform vec4 coverage_atlas_rect = vec4(0.0, 0.0, 0.0, 0.0);
// Outside the finite shoreline field, this vertex-aligned atlas is the
// conservative depth proof for displaced ocean. A tile contains (cells + 1)^2
// samples and is bilinearly interpolated, so adjacent sector edge vertices
// sample their matching canonical depth rather than clamped neighbouring cells.
uniform sampler2D fft_distant_ocean_mask_atlas : filter_linear, repeat_disable;
instance uniform vec4 fft_distant_ocean_mask_rect = vec4(0.0, 0.0, 0.0, 0.0);

#include "res://materials/ring_color_space.gdshaderinc"
#include "res://materials/ring_atmosphere.gdshaderinc"
#include "res://materials/ring_water_common.gdshaderinc"
#include "res://materials/ring_wind_field.gdshaderinc"
#include "res://materials/ring_water_depth.gdshaderinc"
#include "res://materials/ring_water_fft.gdshaderinc"
#include "res://materials/ring_cloud_shadow.gdshaderinc"
#include "res://materials/ring_terrain_macro_shadow.gdshaderinc"
#include "res://materials/ring_far_cloud_field.gdshaderinc"
#include "res://materials/ring_surface_aperture.gdshaderinc"

vec2 ring_fft_distant_ocean_factors(vec2 mesh_uv) {
	if (fft_distant_ocean_mask_rect.z <= 0.0 || fft_distant_ocean_mask_rect.w <= 0.0)
		return vec2(0.0);
	vec2 atlas_size = vec2(textureSize(fft_distant_ocean_mask_atlas, 0));
	if (any(lessThanEqual(atlas_size, vec2(0.0)))) return vec2(0.0);
	vec2 tile_size = max(vec2(2.0), round(fft_distant_ocean_mask_rect.zw * atlas_size));
	vec2 tile_texel = fft_distant_ocean_mask_rect.zw / tile_size;
	vec2 vertex_texel = clamp(mesh_uv, vec2(0.0), vec2(1.0)) * (tile_size - 1.0) + 0.5;
	vec2 uv = fft_distant_ocean_mask_rect.xy + vertex_texel * tile_texel;
	// R stores conservative minimum physical depth in metres. Applying the
	// threshold here means live tuning changes apply immediately, without
	// invalidating any streamed sector atlas.
	float depth = texture(fft_distant_ocean_mask_atlas, uv).r * 510.0;
	float factor = smoothstep(max(0.0, fft_shoreline_shallow_depth_meters),
		max(fft_shoreline_shallow_depth_meters + 0.01, fft_deep_water_wave_depth_meters), depth);
	return vec2(factor);
}

float ring_water_view_depth_at_uv(vec2 uv, mat4 inverse_projection) {
	float raw_depth = texture(water_depth_texture, uv).r;
	if (raw_depth <= 0.0 || raw_depth > 1.0) return -1.0;
	vec4 view_position = inverse_projection * vec4(uv * 2.0 - 1.0, raw_depth, 1.0);
	return view_position.w > 0.0 ? -view_position.z / view_position.w : -1.0;
}

// Returns rgb reflection and a confidence in alpha.  The ray advances in view
// space so it naturally follows displaced water normals without world-space
// assumptions.  A miss leaves the existing grazing/environment fallback intact.
vec4 ring_water_ssr(vec3 surface_view, vec3 normal_view, float roughness, mat4 projection, mat4 inverse_projection, out float step_ratio) {
	step_ratio = 0.0;
	if (!water_ssr_enabled || water_ssr_strength <= 0.001 || roughness >= water_ssr_roughness_cutoff)
		return vec4(0.0);
	float camera_distance = length(surface_view);
	if (camera_distance > water_ssr_camera_distance) return vec4(0.0);
	vec3 ray_direction = normalize(reflect(normalize(surface_view), normal_view));
	if (ray_direction.z >= -0.0001) return vec4(0.0);
	float ray_length = min(water_ssr_max_ray_distance, max(1.0, water_ssr_camera_distance - camera_distance));
	float step_size = ray_length / float(max(water_ssr_coarse_steps, 1));
	float previous_distance = 0.0;
	float previous_signed_delta = 0.0;
	bool previous_valid = false;
	// Seed the bracket at the biased surface so a valid crossing in the first
	// coarse interval is retained instead of being discarded.
	vec4 start_clip = projection * vec4(surface_view, 1.0);
	if (start_clip.w > 0.0) {
		vec2 start_uv = start_clip.xy / start_clip.w * 0.5 + 0.5;
		float start_edge = min(min(start_uv.x, start_uv.y), min(1.0 - start_uv.x, 1.0 - start_uv.y));
		float start_scene_depth = start_edge > 0.0 ? ring_water_view_depth_at_uv(start_uv, inverse_projection) : -1.0;
		if (start_scene_depth > 0.0) {
			previous_signed_delta = -surface_view.z - start_scene_depth;
			previous_valid = true;
		}
	}
	float hit_distance = -1.0;
	vec2 hit_uv = vec2(0.0);
	for (int step_index = 1; step_index <= 32; step_index++) {
		if (step_index > water_ssr_coarse_steps) break;
		float distance_along_ray = float(step_index) * step_size;
		vec3 ray_position = surface_view + ray_direction * distance_along_ray;
		vec4 clip_position = projection * vec4(ray_position, 1.0);
		if (clip_position.w <= 0.0) break;
		vec2 uv = clip_position.xy / clip_position.w * 0.5 + 0.5;
		float edge = min(min(uv.x, uv.y), min(1.0 - uv.x, 1.0 - uv.y));
		if (edge <= 0.0) break;
		float scene_depth = ring_water_view_depth_at_uv(uv, inverse_projection);
		float ray_depth = -ray_position.z;
		if (scene_depth <= 0.0) {
			previous_valid = false;
			continue;
		}
		float signed_delta = ray_depth - scene_depth;
		// A real front-to-behind bracket prevents an arbitrary positive sample from
		// claiming foreground geometry, while retaining thin crossings at coarse steps.
		if (previous_valid && previous_signed_delta < 0.0 && signed_delta >= 0.0) {
			float low = previous_distance;
			float high = distance_along_ray;
			for (int refine = 0; refine < 8; refine++) {
				if (refine >= water_ssr_refinement_steps) break;
				float mid = (low + high) * 0.5;
				vec3 candidate = surface_view + ray_direction * mid;
				vec4 candidate_clip = projection * vec4(candidate, 1.0);
				vec2 candidate_uv = candidate_clip.xy / candidate_clip.w * 0.5 + 0.5;
				float candidate_scene_depth = ring_water_view_depth_at_uv(candidate_uv, inverse_projection);
				float candidate_signed_delta = candidate_scene_depth > 0.0 ? -candidate.z - candidate_scene_depth : -1.0;
				if (candidate_scene_depth > 0.0 && candidate_signed_delta >= 0.0) high = mid;
				else low = mid;
			}
			hit_distance = high;
			step_ratio = float(step_index) / float(max(water_ssr_coarse_steps, 1));
			vec3 hit_position = surface_view + ray_direction * hit_distance;
			vec4 hit_clip = projection * vec4(hit_position, 1.0);
			hit_uv = hit_clip.xy / hit_clip.w * 0.5 + 0.5;
			float refined_scene_depth = ring_water_view_depth_at_uv(hit_uv, inverse_projection);
			float refined_tolerance = mix(water_ssr_depth_tolerance_near, water_ssr_depth_tolerance_far, clamp(hit_distance / max(ray_length, 0.001), 0.0, 1.0));
			if (refined_scene_depth <= 0.0 || abs(-hit_position.z - refined_scene_depth) > refined_tolerance) hit_distance = -1.0;
			break;
		}
		previous_distance = distance_along_ray;
		previous_signed_delta = signed_delta;
		previous_valid = true;
	}
	if (hit_distance < 0.0) return vec4(0.0);
	float edge = min(min(hit_uv.x, hit_uv.y), min(1.0 - hit_uv.x, 1.0 - hit_uv.y));
	float edge_confidence = smoothstep(0.0, water_ssr_edge_fade, edge);
	float distance_confidence = 1.0 - clamp(hit_distance / max(water_ssr_max_ray_distance, 0.001), 0.0, 1.0);
	float camera_confidence = 1.0 - clamp(camera_distance / max(water_ssr_camera_distance, 0.001), 0.0, 1.0);
	float roughness_confidence = 1.0 - smoothstep(water_ssr_roughness_cutoff * 0.55, water_ssr_roughness_cutoff, roughness);
	float final_scene_depth = ring_water_view_depth_at_uv(hit_uv, inverse_projection);
	float final_tolerance = mix(water_ssr_depth_tolerance_near, water_ssr_depth_tolerance_far, clamp(hit_distance / max(ray_length, 0.001), 0.0, 1.0));
	float depth_agreement = 1.0 - clamp(abs(-(surface_view + ray_direction * hit_distance).z - final_scene_depth) / max(final_tolerance, 0.001), 0.0, 1.0);
	float confidence = edge_confidence * distance_confidence * camera_confidence * roughness_confidence * depth_agreement;
	return vec4(textureLod(water_screen_texture, hit_uv, roughness * 4.0).rgb, confidence);
}
uniform bool analytic_cloud_shadow_enabled = true;
uniform float analytic_cloud_shadow_strength : hint_range(0.0, 0.8) = 0.60;
uniform float analytic_cloud_shadow_fade_start_meters = 38000.0;
uniform float analytic_cloud_shadow_fade_end_meters = 50000.0;
// Per-sector ownership proof from the terrain LOD hierarchy. Unlike the
// shared FFT atlas, this remains valid when FFT is disabled or rebuilding.
uniform bool ocean_replacement_coverage_enabled = false;
uniform float ocean_replacement_distance_meters = 0.0;
uniform sampler2D ocean_replacement_coverage_atlas : filter_nearest, repeat_disable;
instance uniform vec4 ocean_replacement_coverage_rect = vec4(0.0, 0.0, 0.0, 0.0);
// A separate atlas certifies a fine ocean proxy. It never represents terrain
// readiness, and is consulted only to hide coarse ocean underlays.
uniform bool fft_ocean_coverage_enabled = false;
uniform float fft_ocean_coverage_distance_meters = 0.0;
uniform sampler2D fft_ocean_coverage_atlas : filter_nearest, repeat_disable;
uniform vec2 fft_ocean_coverage_center_canonical = vec2(0.0);
uniform float fft_ocean_coverage_cell_meters = 2.0;
uniform float fft_surface_base_offset_meters = 0.0;

varying vec3 world_position;
// Always the undistorted source point. FFT sampling must use this coordinate.
varying vec3 fft_source_world_position;
varying float fft_geometry_fade_value;
// The vertex handoff is also used to select a compatible normal footprint.
varying float fft_geometry_lod_handoff_value;
varying float fft_crest_height_value;
varying float ring_day_factor;
varying vec2 proxy_coverage_uv;
varying float water_kind_profile;

float sample_combined_cloud_shadow(vec3 sample_world_position, float day_factor) {
	float distance_to_camera = distance(sample_world_position, camera_world_position);
	float analytic_fade = smoothstep(
		analytic_cloud_shadow_fade_start_meters,
		max(analytic_cloud_shadow_fade_start_meters + 1.0, analytic_cloud_shadow_fade_end_meters),
		distance_to_camera);
	float analytic_shadow = analytic_cloud_shadow_enabled
		? ring_far_cloud_field_world(sample_world_position, ring_center)
			* analytic_cloud_shadow_strength
			* analytic_fade
		: 0.0;
	return min(
		0.80,
		max(analytic_shadow, sample_local_cloud_shadow(sample_world_position))) * day_factor;
}

float ring_fft_outgoing_handoff(float morph_distance) {
	// A zero parent spacing is an intentional 8m -> flat-parent handoff. Only
	// a missing sector size preserves the standalone, legacy displacement path.
	if (!geomorph_enabled || proxy_sector_size_meters <= 0.001)
		return 0.0;
	// Coarse ownership begins at the midpoint. Finish slightly before it so
	// there is never a frame where the fine surface owns a different edge.
	float midpoint = (morph_start_meters + max(morph_end_meters, morph_start_meters + 1.0)) * 0.5;
	float handoff_end = max(morph_start_meters + 0.01, midpoint - 2.0);
	return smoothstep(morph_start_meters, handoff_end, morph_distance);
}

vec3 ring_fft_parent_source_world(vec3 source_world, vec3 source_up_world, vec2 offset) {
	float radius = max(1.0, ring_radius_meters);
	vec3 tangent = normalize(cross(fft_across_axis, source_up_world));
	float angle = offset.x / radius;
	// sin/cos form keeps the curved-ring source stable at small angles; avoid
	// cos(angle)-1, which cancels at this ring's scale.
	return source_world
		+ tangent * (radius * sin(angle))
		+ source_up_world * (radius * 2.0 * sin(angle * 0.5) * sin(angle * 0.5))
		+ fft_across_axis * offset.y;
}

vec3 ring_fft_parent_corner_displacement_world(vec3 source_world, vec3 source_up_world, vec2 offset, vec2 corner_uv) {
	if (fft_geometry_parent_spacing_meters <= 0.001) return vec3(0.0);
	float radius = max(1.0, ring_radius_meters);
	vec3 tangent = normalize(cross(fft_across_axis, source_up_world));
	float angle = offset.x / radius;
	vec3 corner_up = normalize(source_up_world * cos(angle) - tangent * sin(angle));
	vec3 corner_source = ring_fft_parent_source_world(source_world, source_up_world, offset);
	vec2 shoreline = ring_fft_shoreline_factors(ring_fft_canonical(corner_source), ring_fft_distant_ocean_factors(corner_uv));
	vec3 displacement = ring_fft_geometry_displacement_for_spacing(
		ring_fft_canonical(corner_source), fft_geometry_parent_spacing_meters)
		* vec3(shoreline.y, shoreline.x, shoreline.y);
	float fade = ring_fft_geometry_fade(distance(corner_source, camera_world_position));
	return (fft_around_axis * displacement.x + corner_up * displacement.y + fft_across_axis * displacement.z) * fade;
}

vec3 ring_fft_parent_triangle_displacement_world(vec3 source_world, vec3 source_up_world, vec2 mesh_uv) {
	if (fft_geometry_parent_spacing_meters <= 0.001 || proxy_sector_size_meters <= 0.001) return vec3(0.0);
	float spacing = fft_geometry_parent_spacing_meters;
	vec2 lattice = mesh_uv * proxy_sector_size_meters;
	vec2 cell_min = floor(lattice / spacing) * spacing;
	vec2 fraction = clamp((lattice - cell_min) / spacing, vec2(0.0), vec2(1.0));
	vec2 offset00 = cell_min - lattice;
	vec2 offset10 = offset00 + vec2(spacing, 0.0);
	vec2 offset01 = offset00 + vec2(0.0, spacing);
	vec2 offset11 = offset00 + vec2(spacing);
	vec2 uv_scale = vec2(1.0 / proxy_sector_size_meters);
	// Parent sectors use the same i01--i10 diagonal as the generated mesh.
	if (fraction.x + fraction.y <= 1.0) {
		vec3 p00 = ring_fft_parent_corner_displacement_world(source_world, source_up_world, offset00, mesh_uv + offset00 * uv_scale);
		vec3 p10 = ring_fft_parent_corner_displacement_world(source_world, source_up_world, offset10, mesh_uv + offset10 * uv_scale);
		vec3 p01 = ring_fft_parent_corner_displacement_world(source_world, source_up_world, offset01, mesh_uv + offset01 * uv_scale);
		return p00 * (1.0 - fraction.x - fraction.y) + p10 * fraction.x + p01 * fraction.y;
	}
	vec3 p11 = ring_fft_parent_corner_displacement_world(source_world, source_up_world, offset11, mesh_uv + offset11 * uv_scale);
	vec3 p01 = ring_fft_parent_corner_displacement_world(source_world, source_up_world, offset01, mesh_uv + offset01 * uv_scale);
	vec3 p10 = ring_fft_parent_corner_displacement_world(source_world, source_up_world, offset10, mesh_uv + offset10 * uv_scale);
	return p11 * (fraction.x + fraction.y - 1.0) + p01 * (1.0 - fraction.x) + p10 * (1.0 - fraction.y);
}

void vertex() {
	proxy_coverage_uv = UV;
	water_kind_profile = ring_water_kind_from_vertex_alpha(COLOR.a);
	vec3 original_world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec3 original_radial = original_world_position - ring_center;
	original_radial.y = 0.0;
	vec3 surface_up_world = length(original_radial) > 0.0001 ? -normalize(original_radial) : vec3(-1.0, 0.0, 0.0);
	vec3 surface_up_local = normalize(inverse(mat3(MODEL_MATRIX)) * surface_up_world);
	if (geomorph_enabled) {
		float morph_distance = distance(original_world_position, camera_world_position);
		// Water replacement starts two metres before the visible far cutoff.
		// Finish the radial base at the same guarded endpoint as FFT geometry.
		float morph_handoff = max(morph_start_meters + 0.01, (morph_start_meters + max(morph_end_meters, morph_start_meters + 1.0)) * 0.5 - 2.0);
		float morph = smoothstep(morph_start_meters, max(morph_handoff, morph_start_meters + 0.01), morph_distance);
		VERTEX += surface_up_local * UV2.x * morph;
	}
	bool use_fft_surface = ring_fft_ocean_ready(water_kind_profile);
	// Coarse proxy sectors publish zero spacing; they retain FFT shading but
	// must never displace their sparse silhouette.
	bool use_fft_geometry = use_fft_surface && fft_geometry_spacing_meters > 0.001;
	// Restore the fine sector's intentional underlay before deriving the FFT
	// source coordinate or distance. This is not part of the distance fade.
	if (use_fft_geometry) VERTEX += surface_up_local * fft_surface_base_offset_meters;
	fft_source_world_position = use_fft_geometry
		? original_world_position + surface_up_world * fft_surface_base_offset_meters : original_world_position;
	fft_geometry_fade_value = use_fft_geometry
		? ring_fft_geometry_fade(distance(fft_source_world_position, camera_world_position)) : 0.0;
	fft_geometry_lod_handoff_value = use_fft_geometry
		? ring_fft_outgoing_handoff(distance(original_world_position, camera_world_position)) : 0.0;
	fft_crest_height_value = 0.0;
	if (use_fft_geometry && fft_geometry_fade_value > 0.001) {
		// The geometry layer is cascade 1 when available, otherwise cascade 0.
		// Its horizontal components are part of the surface, rather than a normal-only overlay.
		vec2 shoreline = ring_fft_shoreline_factors(ring_fft_canonical(fft_source_world_position), ring_fft_distant_ocean_factors(UV));
		vec3 displacement = ring_fft_geometry_displacement(ring_fft_canonical(fft_source_world_position))
			* vec3(shoreline.y, shoreline.x, shoreline.y);
		vec3 own_world_displacement = (fft_around_axis * displacement.x + surface_up_world * displacement.y + fft_across_axis * displacement.z)
			* fft_geometry_fade_value;
		vec3 final_world_displacement = own_world_displacement;
		if (fft_geometry_lod_handoff_value > 0.001) {
			vec3 parent_world_displacement = ring_fft_parent_triangle_displacement_world(fft_source_world_position, surface_up_world, UV);
			final_world_displacement = mix(own_world_displacement, parent_world_displacement, fft_geometry_lod_handoff_value);
		}
		VERTEX += inverse(mat3(MODEL_MATRIX)) * final_world_displacement;
		fft_crest_height_value = max(0.0, dot(final_world_displacement, surface_up_world));
	} else if (!use_fft_surface && wave_geometry_strength > 0.001) {
		float wave_fade = 1.0 - smoothstep(
			wave_geometry_fade_meters * 0.65,
			max(wave_geometry_fade_meters, 1.0),
			distance(original_world_position, camera_world_position));
		if (wave_fade > 0.001) {
			vec2 wave_coordinates = ring_water_coordinates(original_world_position, ring_center, ring_radius_meters);
			VERTEX += surface_up_local
				* ring_water_profile_height(wave_coordinates, (water_wind_clock_enabled ? 1.0 : TIME), water_kind_profile)
				* wave_fade
				* wave_geometry_strength;
		}
	}
	world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec3 radial = world_position - ring_center;
	radial.y = 0.0;
	vec3 surface_up = length(radial) > 0.0001 ? -normalize(radial) : vec3(-1.0, 0.0, 0.0);
	ring_day_factor = ringworld_effective_solar_day(smoothstep(day_start_dot, day_full_dot, dot(surface_up, normalize(sun_direction))));
}

void fragment() {
	// The atlas certifies terrain replacement only. Do not discard water from
	// terrain readiness: no equivalent fine generated-water surface exists.
	float distance_to_camera = distance(fft_source_world_position, camera_world_position);
	// Form source-coordinate derivatives before any divergent discard/profile path.
	vec2 fft_source_canonical = ring_fft_canonical(fft_source_world_position);
	vec2 fft_source_dx = dFdx(fft_source_canonical);
	vec2 fft_source_dy = dFdy(fft_source_canonical);
	if (ring_surface_aperture_discard(world_position)) discard;
	if (far_fade_enabled) {
		float far_handoff = (fade_out_start_meters + max(fade_out_end_meters, fade_out_start_meters + 0.01)) * 0.5;
		if (distance_to_camera > far_handoff) discard;
	}
	if (ocean_replacement_coverage_enabled && ocean_replacement_distance_meters > 0.0
		&& distance_to_camera < ocean_replacement_distance_meters
		&& water_kind_profile < 1.5
		&& ocean_replacement_coverage_rect.z > 0.0 && ocean_replacement_coverage_rect.w > 0.0) {
		vec2 replacement_uv = ocean_replacement_coverage_rect.xy + proxy_coverage_uv * ocean_replacement_coverage_rect.zw;
		vec2 replacement_texel = 0.5 / vec2(textureSize(ocean_replacement_coverage_atlas, 0));
		replacement_uv = clamp(replacement_uv,
			ocean_replacement_coverage_rect.xy + replacement_texel,
			ocean_replacement_coverage_rect.xy + ocean_replacement_coverage_rect.zw - replacement_texel);
		if (texture(ocean_replacement_coverage_atlas, replacement_uv).r > 0.5) discard;
	}
	if (fft_ocean_coverage_enabled && fft_ready && water_kind_profile < 1.5
		&& distance(fft_source_world_position, camera_world_position) < fft_ocean_coverage_distance_meters - 2.0) {
		vec2 coverage_delta = fft_source_canonical - fft_ocean_coverage_center_canonical;
		float coverage_span = fft_ocean_coverage_cell_meters * 512.0;
		vec2 ocean_coverage_uv = coverage_delta / coverage_span + vec2(0.5);
		if (all(greaterThanEqual(ocean_coverage_uv, vec2(0.0))) && all(lessThan(ocean_coverage_uv, vec2(1.0)))
			&& texture(fft_ocean_coverage_atlas, ocean_coverage_uv).r > 0.5) discard;
	}

	bool use_fft_surface = ring_fft_ocean_ready(water_kind_profile);
	vec2 centre_shoreline = use_fft_surface
		? ring_fft_shoreline_factors(fft_source_canonical, ring_fft_distant_ocean_factors(proxy_coverage_uv))
		: vec2(0.0);
	vec2 wave_coordinates = ring_water_coordinates(world_position, ring_center, ring_radius_meters);
	float water_gust_response = water_spatial_gust_enabled && ring_wind_field_enabled > 0.5 && water_mean_wind_speed > 0.01
        ? 1.0 + clamp(ring_wind_gust_at_world(world_position, 0.0) - 1.0, 0.0, 2.0) * water_gust_response_strength
        : 1.0;
    water_gust_response = clamp(water_gust_response, 1.0, 3.0);
	vec2 wave_coordinate_dx = dFdx(wave_coordinates);
	vec2 wave_coordinate_dy = dFdy(wave_coordinates);
	float wave_distance_fade = 1.0 - smoothstep(
		wave_normal_fade_start_meters,
		max(wave_normal_fade_start_meters + 1.0, wave_normal_fade_end_meters),
		distance_to_camera);
	float wave_normal_detail = use_fft_surface ? 0.0 : wave_distance_fade;
	vec3 radial = world_position - ring_center;
	radial.y = 0.0;
	vec3 smooth_water_normal_world = length(radial) > 0.0001 ? -normalize(radial) : vec3(-1.0, 0.0, 0.0);
	vec3 water_normal_world = smooth_water_normal_world;
	float crest_wave_height = 0.0;
	float wave_slope_magnitude = 0.0;
	vec2 filtered_gradient = vec2(0.0);
	if (wave_normal_detail > 0.001) {
		filtered_gradient = ring_water_profile_gradient_filtered(
			wave_coordinates,
			(water_wind_clock_enabled ? 1.0 : TIME),
			wave_coordinate_dx,
			wave_coordinate_dy,
			water_kind_profile);
        filtered_gradient *= water_mean_response * water_gust_response;
		vec3 detailed_water_normal_world = ring_water_normal_world_from_gradient(
			world_position,
			ring_center,
			filtered_gradient);
		water_normal_world = normalize(mix(smooth_water_normal_world, detailed_water_normal_world, wave_normal_detail));
		crest_wave_height = ring_water_profile_height_filtered(
			wave_coordinates,
			(water_wind_clock_enabled ? 1.0 : TIME),
			wave_coordinate_dx,
			wave_coordinate_dy,
			water_kind_profile);
		wave_slope_magnitude = length(filtered_gradient) * wave_normal_detail;
	}
	vec4 fft_detail = vec4(0.0);
	if (use_fft_surface && fft_geometry_fade_value > 0.001) {
		// Differentiate the complete displaced mapping, including horizontal chop.
		// Source-coordinate derivatives select matching displacement mips; the
		// finite samples themselves stay in physical metres and remain seam-safe.
		vec2 source_canonical = fft_source_canonical;
		vec2 source_dx = fft_source_dx;
		vec2 source_dy = fft_source_dy;
		float normal_parent_spacing = fft_geometry_parent_spacing_meters > 0.001
			? fft_geometry_parent_spacing_meters : fft_geometry_spacing_meters;
		float normal_spacing = mix(fft_geometry_spacing_meters, normal_parent_spacing, fft_geometry_lod_handoff_value);
		float geometry_normal_fade = fft_geometry_parent_spacing_meters > 0.001
			? 1.0 : 1.0 - fft_geometry_lod_handoff_value;
		float step_meters = max(0.25, normal_spacing);
		vec3 scale = vec3(fft_geometry_height_scale * fft_geometry_choppiness, fft_geometry_height_scale, fft_geometry_height_scale * fft_geometry_choppiness);
		float fade_around_positive = ring_fft_geometry_fade(distance(fft_source_world_position + fft_around_axis * step_meters, camera_world_position));
		float fade_around_negative = ring_fft_geometry_fade(distance(fft_source_world_position - fft_around_axis * step_meters, camera_world_position));
		float fade_across_positive = ring_fft_geometry_fade(distance(fft_source_world_position + fft_across_axis * step_meters, camera_world_position));
		float fade_across_negative = ring_fft_geometry_fade(distance(fft_source_world_position - fft_across_axis * step_meters, camera_world_position));
		vec2 distant_shoreline = ring_fft_distant_ocean_factors(proxy_coverage_uv);
		vec2 shore_positive_around = ring_fft_shoreline_factors(source_canonical + vec2(step_meters, 0.0), distant_shoreline);
		vec2 shore_negative_around = ring_fft_shoreline_factors(source_canonical - vec2(step_meters, 0.0), distant_shoreline);
		vec2 shore_positive_across = ring_fft_shoreline_factors(source_canonical + vec2(0.0, step_meters), distant_shoreline);
		vec2 shore_negative_across = ring_fft_shoreline_factors(source_canonical - vec2(0.0, step_meters), distant_shoreline);
		vec3 positive_around = ring_fft_geometry_displacement_grad_for_spacing(source_canonical + vec2(step_meters, 0.0), source_dx, source_dy, normal_spacing) * scale * vec3(shore_positive_around.y, shore_positive_around.x, shore_positive_around.y) * fade_around_positive;
		vec3 negative_around = ring_fft_geometry_displacement_grad_for_spacing(source_canonical - vec2(step_meters, 0.0), source_dx, source_dy, normal_spacing) * scale * vec3(shore_negative_around.y, shore_negative_around.x, shore_negative_around.y) * fade_around_negative;
		vec3 positive_across = ring_fft_geometry_displacement_grad_for_spacing(source_canonical + vec2(0.0, step_meters), source_dx, source_dy, normal_spacing) * scale * vec3(shore_positive_across.y, shore_positive_across.x, shore_positive_across.y) * fade_across_positive;
		vec3 negative_across = ring_fft_geometry_displacement_grad_for_spacing(source_canonical - vec2(0.0, step_meters), source_dx, source_dy, normal_spacing) * scale * vec3(shore_negative_across.y, shore_negative_across.x, shore_negative_across.y) * fade_across_negative;
		vec3 tangent_around = fft_around_axis + (fft_around_axis * (positive_around.x - negative_around.x)
			+ smooth_water_normal_world * (positive_around.y - negative_around.y)
			+ fft_across_axis * (positive_around.z - negative_around.z)) / (2.0 * step_meters);
		vec3 tangent_across = fft_across_axis + (fft_around_axis * (positive_across.x - negative_across.x)
			+ smooth_water_normal_world * (positive_across.y - negative_across.y)
			+ fft_across_axis * (positive_across.z - negative_across.z)) / (2.0 * step_meters);
		vec3 fft_normal = normalize(cross(tangent_across, tangent_around));
		if (dot(fft_normal, smooth_water_normal_world) < 0.0) fft_normal *= -1.0;
		// Offset samples already contain their local geometry fades. Mixing once
		// more by the centre fade would flatten the reconstructed surface twice.
		water_normal_world = normalize(mix(smooth_water_normal_world, fft_normal, centre_shoreline.x * geometry_normal_fade));
		wave_slope_magnitude = length(water_normal_world - smooth_water_normal_world);
		vec4 geometry_normal_foam = ring_fft_normal_foam_grad(source_canonical, ring_fft_geometry_layer(), source_dx, source_dy);
		// Persistent foam is admitted only where the filtered horizontal mapping
		// compresses. This is the same displacement and fade used by the mesh.
		float d_around_around = (positive_around.x - negative_around.x) / (2.0 * step_meters);
		float d_across_around = (positive_around.z - negative_around.z) / (2.0 * step_meters);
		float d_around_across = (positive_across.x - negative_across.x) / (2.0 * step_meters);
		float d_across_across = (positive_across.z - negative_across.z) / (2.0 * step_meters);
		float horizontal_jacobian = (1.0 + d_around_around) * (1.0 + d_across_across) - d_around_across * d_across_around;
		float compression = smoothstep(0.01, 0.18, max(0.0, 1.0 - horizontal_jacobian));
		fft_detail.z = geometry_normal_foam.a * compression * centre_shoreline.x;
		float large_residual = max(0.0, geometry_normal_foam.z - dot(geometry_normal_foam.xy, geometry_normal_foam.xy));
		fft_detail.w = large_residual * fft_geometry_height_scale * fft_geometry_height_scale * fft_geometry_fade_value * fft_geometry_fade_value * centre_shoreline.x;
	}
	// Cascade zero is a single optional micro-normal lookup. It remains useful
	// on coarse/non-displaced water until the independent detail-distance fade.
	float small_shading_fade = use_fft_surface ? ring_fft_shading_fade(distance_to_camera) * centre_shoreline.x : 0.0;
	if (use_fft_surface && fft_cascade_count > 1 && fft_normal_strength > 0.001 && small_shading_fade > 0.001) {
		vec4 small = ring_fft_normal_foam_grad(fft_source_canonical, 0, fft_source_dx, fft_source_dy);
		vec3 small_normal = normalize(water_normal_world - fft_around_axis * small.x * fft_normal_strength * water_gust_response
			- fft_across_axis * small.y * fft_normal_strength * water_gust_response);
		water_normal_world = normalize(mix(water_normal_world, small_normal, small_shading_fade));
		wave_slope_magnitude += length(small.xy) * fft_normal_strength * water_gust_response * small_shading_fade;
		float small_residual = max(0.0, small.z - dot(small.xy, small.xy));
		fft_detail.w += small_residual * fft_normal_strength * fft_normal_strength * water_gust_response * water_gust_response * small_shading_fade * small_shading_fade;
	}
	if (!FRONT_FACING) water_normal_world *= -1.0;
	vec3 water_normal_view = normalize((VIEW_MATRIX * vec4(water_normal_world, 0.0)).xyz);
	NORMAL = water_normal_view;
	vec3 normal_dx = dFdx(water_normal_view);
	vec3 normal_dy = dFdy(water_normal_view);
	float normal_variance = max(dot(normal_dx, normal_dx), dot(normal_dy, normal_dy));
	float specular_aa_roughness = clamp(
		pow(max(normal_variance, 0.0), 0.25) * specular_aa_strength,
		0.0,
		0.65);
	float raw_scene_depth = texture(water_depth_texture, SCREEN_UV).r;
	vec4 scene_view_position = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, raw_scene_depth, 1.0);
	float surface_depth = max(0.0, -VERTEX.z);
	vec2 depth_contact = ring_water_depth_from_scene(scene_view_position, raw_scene_depth, surface_depth);
	float water_thickness = depth_contact.x;
	// RGB Beer-Lambert extinction: red is attenuated fastest and blue slowest.
	vec3 scene_transmission_rgb = exp(-water_thickness * absorption_density * max(absorption_coefficients, vec3(0.001)));
	float scene_transmission = dot(scene_transmission_rgb, vec3(0.2126, 0.7152, 0.0722));
	float absorption = 1.0 - scene_transmission;
	float fresnel = ring_water_schlick_fresnel(
		clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0));
	float reflection_weight = clamp(fresnel * fresnel_strength, 0.0, 1.0);
	// The refracted scene is composed manually and the final alpha is kept nearly
	// opaque below, preventing transparent blending from copying it a second time.
	vec2 refracted_uv = clamp(
		SCREEN_UV + water_normal_view.xy * refraction_strength * mix(0.35, 1.0, absorption),
		vec2(0.001), vec2(0.999));
	vec3 refracted_scene = textureLod(water_screen_texture, refracted_uv, ocean_roughness * 3.0).rgb;
	vec3 seeded_tint = ring_vertex_color_to_linear(COLOR.rgb);
	vec3 water_tint = mix(shallow_water_tint, deep_water_tint, absorption);
	water_tint = mix(water_tint, seeded_tint, 0.20) * mix(night_tint, vec3(1.0), ring_day_factor);
	float shore_foam = depth_contact.y * (1.0 - smoothstep(0.08, max(0.1, shore_foam_depth_meters), water_thickness))
		* shore_foam_strength
		* ring_water_profile_shore_foam_scale(water_kind_profile);
	// Calm periodic crests do not foam. Whitecaps appear only where the filtered
	// surface is both high and physically steep enough to be breaking, with a
	// broad low-frequency breakup so threshold crossings cannot stamp a lattice.
	float profile_wave_height = wave_height_meters * ring_water_profile_height_scale(water_kind_profile);
	float crest_presence = smoothstep(
		profile_wave_height * 0.30,
		max(0.01, profile_wave_height * 0.72),
		crest_wave_height);
	float breaking_wave = smoothstep(
		breaking_foam_slope_start,
		max(breaking_foam_slope_start + 0.01, breaking_foam_slope_end),
		wave_slope_magnitude);
	float foam_breakup = ring_water_foam_patch_mask(wave_coordinates, ring_radius_meters);
	float distant_nonshore_foam = mix(
		1.0,
		distant_foam_visibility,
		smoothstep(90.0 * water_coordinate_scale, 900.0 * water_coordinate_scale, distance_to_camera));
	float crest_foam = crest_presence
		* breaking_wave
		* foam_breakup
		* breaking_foam_strength
		* wave_normal_detail
		* (water_kind_profile < 1.5 ? 1.0 : 0.18)
		* (use_fft_surface ? 0.0 : 1.0);
    float gust_whitecap = smoothstep(0.22, 0.70, wave_slope_magnitude)
        * (water_gust_response - 1.0) * 0.20 * small_shading_fade;
	float fft_foam = (fft_detail.z * water_gust_response + gust_whitecap) * fft_foam_strength * foam_breakup * distant_nonshore_foam;
	crest_foam *= distant_nonshore_foam;
	float foam = clamp(shore_foam + crest_foam + fft_foam, 0.0, 1.0);
	// Retain the established shallow/deep alpha controls as artist-facing volume
	// coverage. They now control how quickly the optical medium replaces the
	// refracted scene, rather than triggering a second hardware alpha blend.
	float volume_coverage = mix(shallow_alpha, deep_alpha, absorption);
	vec3 volume_transmission_rgb = mix(vec3(1.0), scene_transmission_rgb, volume_coverage);
	// RGB transmission directly controls the manually composed scene colour.
	// In-scattering is a luminous shallow-volume cue, never a deep-ocean glow.
	// Remaining scene transmission gates it out for optically deep or invalid
	// depth samples, which must stay fully concealing.
	vec3 shallow_scatter = shallow_water_tint * (1.0 - volume_transmission_rgb)
		* scene_transmission * shallow_inscatter_strength;
	vec3 optical_water = refracted_scene * volume_transmission_rgb
		+ water_tint * (1.0 - volume_transmission_rgb) + shallow_scatter;
	vec3 reflection_color = vec3(0.34, 0.52, 0.68);
	vec3 grazing_color = mix(optical_water, reflection_color, reflection_weight);
	vec4 atmosphere = sample_ring_atmosphere(camera_world_position, world_position, ring_center, ring_day_factor);
	float wave_slope = clamp(1.0 - dot(normalize(water_normal_world), normalize(smooth_water_normal_world)), 0.0, 1.0);
	// FFT water deliberately suppresses the analytic normal path above, but that
	// must not also force the distant-water roughness.  Keep its independently
	// filtered surface detail eligible for the near-ocean SSR roughness cutoff.
	float surface_detail_fade = use_fft_surface ? ring_fft_shading_fade(distance_to_camera) : wave_normal_detail;
	float base_roughness = clamp(
		mix(mix(far_water_roughness, ocean_roughness, surface_detail_fade), 0.48, foam)
			+ wave_slope * 0.10 + sqrt(max(fft_detail.w, 0.0)) * 0.07,
		0.02,
		1.0);
	float ssr_step_ratio;
	vec4 ssr = ring_water_ssr(VERTEX + water_normal_view * water_ssr_ray_bias, water_normal_view, base_roughness, PROJECTION_MATRIX, INV_PROJECTION_MATRIX, ssr_step_ratio);
	float ssr_weight = clamp(ssr.a * water_ssr_strength, 0.0, 1.0);
	if (water_ssr_debug_mode == 1) grazing_color = vec3(ssr.a);
	else if (water_ssr_debug_mode == 2) grazing_color = vec3(ssr.a > 0.001 ? 1.0 : 0.0);
	else if (water_ssr_debug_mode == 3) grazing_color = vec3(ssr_step_ratio);
	else {
		reflection_color = mix(reflection_color, ssr.rgb, ssr_weight);
		grazing_color = mix(optical_water, reflection_color, reflection_weight);
	}
	grazing_color = mix(grazing_color, foam_color, foam);
	ALBEDO = grazing_color * (1.0 - atmosphere.a);
	// The foam self-light floor scales with the eclipse night light (spec D.1); exactly 1 outside an eclipse.
	EMISSION = atmosphere.rgb * atmosphere.a * atmosphere_scattering_energy + foam_color * foam * (0.16 * ringworld_night_light_scale);
	ROUGHNESS = max(base_roughness, specular_aa_roughness);
	SPECULAR = ring_water_dielectric_specular() * ocean_specular;
	METALLIC = 0.0;
	AO = 1.0 - sample_combined_cloud_shadow(world_position, ring_day_factor) * 0.60;
	AO_LIGHT_AFFECT = 0.0;
	// ALBEDO already contains the refracted scene. Near-opacity prevents it being
	// blended with that same scene again; deep water therefore fails closed.
	ALPHA = 0.995;
}

void light() {
	float n_dot_l = max(dot(NORMAL, LIGHT), 0.0);
	float readable_shadow = max(ATTENUATION, shadow_floor);
	vec3 sun_direction_view = normalize((VIEW_MATRIX * vec4(sun_direction, 0.0)).xyz);
	float primary_sun_weight = LIGHT_IS_DIRECTIONAL
		? smoothstep(0.90, 0.995, dot(normalize(LIGHT), sun_direction_view))
		: 0.0;
	float sun_hemisphere_direct = mix(
		night_direct_light_factor,
		day_direct_light_factor,
		ring_day_factor);
	float hemisphere_direct = mix(1.0, sun_hemisphere_direct, primary_sun_weight);
	float terrain_shadow = LIGHT_IS_DIRECTIONAL
		? sample_terrain_macro_shadow(
			world_position,
			ring_center,
			camera_world_position,
			ring_width_meters) * primary_sun_weight * ring_day_factor
		: 0.0;
	float cloud_shadow = LIGHT_IS_DIRECTIONAL
		? sample_combined_cloud_shadow(world_position, ring_day_factor)
		: 0.0;
	float combined_visibility = combine_ring_shadow_visibility(
		readable_shadow,
		cloud_shadow,
		terrain_shadow);
	DIFFUSE_LIGHT += ALBEDO
		* LIGHT_COLOR
		* n_dot_l
		* combined_visibility
		* hemisphere_direct;
	// Crest transmission uses displaced FFT height and back-lit directional sun.
	if (primary_sun_weight > 0.0 && (fft_crest_lighting_strength > 0.001 || crest_transmission_strength > 0.001)) {
		float crest = smoothstep(0.025, 0.30, fft_crest_height_value);
		float back_lit = pow(max(dot(normalize(VIEW), -normalize(LIGHT)), 0.0), 2.0);
		float crest_transmission = max(fft_crest_lighting_strength, crest_transmission_strength);
		vec3 transmission_tint = mix(vec3(0.20, 0.85, 0.78), shallow_water_tint, 0.45);
		DIFFUSE_LIGHT += LIGHT_COLOR * transmission_tint * crest * back_lit * crest_transmission
			* combined_visibility * ring_day_factor * 0.18;
	}
}
