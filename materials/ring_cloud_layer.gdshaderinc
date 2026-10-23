// Shared body of the whole-ring far cloud shell (ring_cloud_layer.gdshader, scaled far viewport)
// and the main-view cloud layer (ring_cloud_layer_main.gdshader, RingCloudLayer).
#ifdef RING_CLOUD_LAYER_MAIN_VIEW
// The main view draws this layer only over its own ground. Where nothing was rendered (reverse-Z 0) the
// scaled far viewport is composited instead, with its own far clouds, so drawing there would stack two layers.
uniform sampler2D main_view_depth : hint_depth_texture, filter_nearest;
// Sunshine altitude crossfade (0..1): closes the Sunshine handoff hole as Sunshine fades out.
uniform float main_view_handoff_crossfade = 0.0;
#endif
// Sunshine-matched column shading. The layer stands for the whole cloud band: its opacity is the band's Beer-Lambert
// column along the view ray (same extinction, band and optical-depth fill as the far weather cloud shadow), and it is
// lit with the lights and ambient Sunshine marches with (RingSunshineCloudBridge.PublishFarCloudLighting): a relief
// normal from the column height, self-shadowing through the column, dark bases from below, forward-scatter silver
// lining toward the sun and a powder darkening of thin edges. x strength, y extinction / m, z/w band floor/ceiling.
global uniform vec4 ring_far_weather_shadow;
global uniform mat4 ring_far_cloud_lights;
global uniform vec4 ring_far_cloud_ambient;
const float FAR_CLOUD_BAND_FILL = 1.7; // RING_FAR_WEATHER_SHADOW_BAND_FILL
uniform float far_cloud_relief = 0.3;
uniform float far_cloud_sun_extinction = 0.5;
uniform float far_cloud_sun_gain = 0.34;
uniform float far_cloud_ambient_gain = 1.0;
uniform float far_cloud_silver_lining = 0.6;
uniform float far_cloud_edge_softness = 0.5;
uniform float far_cloud_billow = 0.5;
uniform float terrain_cloud_receiver_height_meters = 0.0;
#include "res://materials/ring_terrain_cloud_sun_receiver.gdshaderinc"

uniform vec3 ring_center = vec3(0.0);
uniform vec3 camera_world_position = vec3(0.0);
uniform bool far_space_enabled = true;
uniform float far_space_start_meters = 850000.0;
uniform float far_space_real_end_meters = 10500000.0;
uniform float far_space_render_end_meters = 1150000.0;
uniform vec3 sun_direction = vec3(1.0, 0.0, 0.0);
uniform float day_start_dot = -0.2;
uniform float day_full_dot = 0.35;
uniform vec3 day_cloud_color : source_color = vec3(0.88, 0.93, 1.0);
uniform vec3 day_cloud_shadow_color : source_color = vec3(0.36, 0.43, 0.53);
uniform vec3 night_cloud_color : source_color = vec3(0.055, 0.085, 0.15);
uniform float opacity : hint_range(0.0, 1.0) = 0.72;
uniform float coverage : hint_range(0.0, 1.0) = 0.50;
uniform float softness : hint_range(0.01, 0.4) = 0.18;
uniform bool local_volumetric_handoff_enabled = false;
uniform float local_handoff_start_meters = 7000.0;
uniform float local_handoff_end_meters = 12000.0;
// A bounded, world-seeded macro-weather atlas is shared with the camera-local
// far canopy. This keeps cloud banks and storm flashes coherent around the
// entire ring instead of evaluating unrelated per-pixel fields.
uniform sampler2D weather_atlas : hint_default_white, filter_linear_mipmap, repeat_enable;
uniform bool weather_atlas_enabled = false;
uniform float weather_time_seconds = 0.0;
uniform vec2 weather_wind_uv_per_second = vec2(0.0);
uniform vec4 far_lightning_event_a = vec4(0.0);
uniform vec4 far_lightning_event_b = vec4(0.0);
uniform float far_lightning_event_a_id = 0.0;
uniform float far_lightning_event_b_id = 0.0;
// A/B payloads stay raw source pulses; renderer ownership is a separate pair.
uniform vec2 far_lightning_event_weights = vec2(0.0);
uniform float far_clipmap_handoff_start_meters = 140000.0;
uniform float far_clipmap_handoff_end_meters = 180000.0;
uniform float ring_circumference_km = 31415.9;
uniform float ring_width_km = 318.0;
#include "res://materials/ring_atmosphere.gdshaderinc"
#include "res://materials/ring_far_cloud_density.gdshaderinc"
#include "res://materials/ring_cloud_weather_shared.gdshaderinc"
#include "res://materials/ring_far_weather_cloud.gdshaderinc"

#ifdef RING_CLOUD_LAYER_MAIN_VIEW
// The cloud body (large and medium shapes, no small detail erosion) of far_density_core with the extra-large mask
// passed in, for the column-extrusion taps: the outline at another height, at 2 of the core's 5 fetches.
float ring_cloud_layer_extrusion_body(vec3 meters, float coverage_threshold, float extra) {
	float band = max(far_density_background_altitude.y - far_density_background_altitude.x, 1.0);
	float h = clamp((meters.y - far_density_background_altitude.x) / band, 0.0, 1.0);
	vec4 gradient = texture(far_density_height_gradient, vec2(h, 0.5));
	float swept_height = 1.0 - clamp(h / max(far_density_wind_swept_range, 0.0001), 0.0, 1.0);
	vec3 swept = meters + vec3(far_density_wind_direction.x, 0.0, far_density_wind_direction.z)
		* far_density_wind_swept_strength * swept_height * swept_height;
	float large = texture(far_density_large_scale_noise, (swept - far_density_large_position) / max(far_density_large_scale, 1.0)).r * extra * gradient.b;
	float large_shape = smoothstep(1.0 - coverage_threshold, 1.1 - coverage_threshold, large * gradient.r);
	float medium_shape = 1.0 - texture(far_density_medium_scale_noise, (swept - far_density_medium_position) / max(far_density_medium_scale, 1.0)).b;
	float shape = clamp((medium_shape - (1.0 - large_shape)) / max(large_shape, 0.0001), 0.0, 1.0);
	float edge_fade = smoothstep(0.0, 0.1, h) * (1.0 - smoothstep(0.9, 1.0, h));
	return shape * edge_fade * far_density_clouds_density;
}
#endif

varying vec3 world_position;
varying float ring_day_factor;
varying vec3 inhabited_surface_up;

vec2 far_lightning_glow(vec2 canonical_uv, vec4 event_data) {
        if (event_data.w <= 0.0 || event_data.z <= 0.0) return vec2(0.0);
        vec2 delta = vec2(fract(canonical_uv.x - event_data.x + 0.5) - 0.5,
                (canonical_uv.y - event_data.y) * ring_width_km / max(ring_circumference_km, 1.0));
        float distance_normalized = length(delta) / event_data.z;
	// Use unwrapped canonical metric derivatives; wrapped deltas are discontinuous
	// at the physical seam and must never determine pixel footprint.
	vec2 metric_uv = vec2(canonical_uv.x,
		canonical_uv.y * ring_width_km / max(ring_circumference_km, 1.0));
	float pixel_metric = max(length(dFdx(metric_uv)), length(dFdy(metric_uv)));
	float core_radius = max(0.24, min(0.75, 1.5 * pixel_metric / event_data.z));
        float broad = (1.0 - smoothstep(0.35, 1.55, distance_normalized)) * event_data.w;
        float core = (1.0 - smoothstep(0.0, core_radius, distance_normalized)) * event_data.w;
        return vec2(core, broad);
}

void vertex() {
	POSITION = PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
	world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	if (far_space_enabled) {
		vec3 view_position = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
		float real_distance = length(view_position);
		if (real_distance > far_space_start_meters) {
			float distance_fraction = clamp(
				(real_distance - far_space_start_meters)
				/ max(far_space_real_end_meters - far_space_start_meters, 1.0),
				0.0,
				1.0);
			float render_distance = mix(far_space_start_meters, far_space_render_end_meters, distance_fraction);
			view_position *= render_distance / max(real_distance, 1.0);
			POSITION = PROJECTION_MATRIX * vec4(view_position, 1.0);
		}
	}
	vec3 radial = world_position - ring_center;
	radial.y = 0.0;
	vec3 radial_out = length(radial) > 0.0001 ? normalize(radial) : vec3(1.0, 0.0, 0.0);
	inhabited_surface_up = -radial_out;
	ring_day_factor = ringworld_effective_solar_day(smoothstep(day_start_dot, day_full_dot, dot(inhabited_surface_up, normalize(sun_direction))));
}

void fragment() {
#ifdef RING_CLOUD_LAYER_MAIN_VIEW
	if (textureLod(main_view_depth, SCREEN_UV, 0.0).r <= 0.0) discard;
	// Inside the (closing) Sunshine handoff hole nothing is drawn: skip the density work.
	float main_view_handoff = 1.0;
	if (local_volumetric_handoff_enabled) {
		main_view_handoff = mix(smoothstep(local_handoff_start_meters, local_handoff_end_meters,
			distance(world_position, camera_world_position)), 1.0, clamp(main_view_handoff_crossfade, 0.0, 1.0));
	}
	if (main_view_handoff <= 0.0005) discard;
#endif
	// The duplicated mesh vertex at U=1 is the same physical point as U=0.
	// Canonicalise it before sampling so the cloud shell cannot show a
	// different last column at the ring seam.
	vec2 weather_uv = vec2(fract(UV.x), clamp(UV.y, 0.0, 1.0));
	weather_uv.x = fract(weather_uv.x - weather_wind_uv_per_second.x * weather_time_seconds);
	weather_uv.y = clamp(weather_uv.y - weather_wind_uv_per_second.y * weather_time_seconds, 0.0, 1.0);
	vec4 weather = texture(weather_atlas, weather_uv);
	vec3 canonical_meters = vec3(fract(UV.x) * ring_circumference_km * 1000.0, 0.0, UV.y * ring_width_km * 1000.0);
	// Until the whole-ring weather atlas exists the shell stays clear: there is
	// no second, unrelated weather field to show instead. The far cloud shadow
	// (ring_far_weather_shadow.gdshaderinc) evaluates this same amount.
	vec2 resolved_weather = vec2(0.0);
	float cloud = weather_atlas_enabled
		? ring_far_weather_cloud_amount_resolved(weather, canonical_meters, coverage, softness, resolved_weather)
		: 0.0;
	float resolved_coverage = resolved_weather.x;
#ifdef RING_CLOUD_LAYER_MAIN_VIEW
	// Column extrusion: the layer lies at the band middle, but a view ray crosses the whole band. Where it is above the
	// middle (toward the camera from above, beyond the pixel from below) it can meet the upper part of a cloud, and
	// below the middle the lower part, so tilted views show cloud sides and tops beyond the mid-plane outline, as
	// Sunshine does. Sample the density where the ray crosses the upper and lower band (world offsets mapped to
	// canonical metres through the screen derivatives).
	{
		vec2 band_x = ring_far_weather_shadow.zw;
		if (band_x.y <= band_x.x + 1.0) band_x = vec2(2000.0, 6000.0);
		vec3 up_x = normalize(inhabited_surface_up);
		vec3 ray = normalize(world_position - camera_world_position);
		float ray_up = dot(ray, up_x);
		float scale_x = max(ring_atmosphere_coordinate_scale, 0.000001);
		vec3 ex_dpdx = dFdx(world_position);
		vec3 ex_dpdy = dFdy(world_position);
		vec2 ex_dcdx = dFdx(canonical_meters.xz);
		vec2 ex_dcdy = dFdy(canonical_meters.xz);
		vec3 ex_r1 = cross(ex_dpdy, up_x);
		vec3 ex_r2 = cross(up_x, ex_dpdx);
		float ex_det = dot(ex_dpdx, ex_r1);
		float half_band = 0.5 * (band_x.y - band_x.x);
		// Horizontal ray displacement (world units) per metre above the middle, capped at grazing angles.
		vec3 per_meter = (ray - ray_up * up_x) / (ray_up >= 0.0 ? max(ray_up, 0.25) : -max(-ray_up, 0.25)) * scale_x;
		vec3 world_offset = per_meter * (0.5 * half_band); // where the ray is half way up the upper half
		vec2 canonical_offset = abs(ex_det) > 1e-20
			? (dot(world_offset, ex_r1) * ex_dcdx + dot(world_offset, ex_r2) * ex_dcdy) / ex_det
			: vec2(0.0);
		// Bound it (grazing views, and the canonical wrap where the derivative jumps for one pixel).
		canonical_offset *= min(1.0, 2.0 * half_band / max(length(canonical_offset), 0.001));
		if (weather_atlas_enabled && far_density_enabled) {
			float threshold = mix(0.70, 0.90, resolved_coverage);
			float mid = 0.5 * (band_x.x + band_x.y);
			// The extra-large mask (320 km) is the same at both taps; canonical metres stay unwrapped within the patch.
			float extra = texture(far_density_extra_large_noise_patterns,
				(canonical_meters.xz - far_density_extra_large_position.xz) / max(far_density_extra_large_scale, 1.0)).a;
			float upper = ring_cloud_layer_extrusion_body(vec3(canonical_meters.x + canonical_offset.x,
				mid + 0.5 * half_band, canonical_meters.z + canonical_offset.y), threshold, extra);
			float lower = ring_cloud_layer_extrusion_body(vec3(canonical_meters.x - canonical_offset.x,
				mid - 0.5 * half_band, canonical_meters.z - canonical_offset.y), threshold, extra);
			// Each stands for only part of the column: thinner than the full mid-plane body.
			cloud = max(cloud, 0.75 * max(upper, lower) * resolved_weather.y * far_density_strength);
		}
	}
#endif
	float handoff_factor = 1.0;
#ifdef RING_CLOUD_LAYER_MAIN_VIEW
	handoff_factor = main_view_handoff;
#else
	if (local_volumetric_handoff_enabled) {
		float camera_distance = distance(world_position, camera_world_position);
		handoff_factor *= smoothstep(local_handoff_start_meters, local_handoff_end_meters, camera_distance);
		// The global shell owns the horizon beyond the curved local clipmap.
		// Keeping a short overlap avoids a visible radial seam without drawing
		// two fully opaque cloud layers through the same mid-distance pixels.
		handoff_factor *= smoothstep(far_clipmap_handoff_start_meters, far_clipmap_handoff_end_meters, camera_distance);
	}
#endif
	cloud *= handoff_factor;
	float width_fade = smoothstep(0.0, 0.035, UV.y) * (1.0 - smoothstep(0.965, 1.0, UV.y));
	cloud *= width_fade;
	vec2 flash_a = far_lightning_glow(UV, far_lightning_event_a);
	vec2 flash_b = far_lightning_glow(UV, far_lightning_event_b);
	vec2 raw_flash = min(flash_a + flash_b, vec2(1.15, 1.35));
	vec2 far_flash = min(
		flash_a * far_lightning_event_weights.x + flash_b * far_lightning_event_weights.y,
		vec2(1.15, 1.35));
	// Shared events are already storm-validated on CPU. Keep their bounded
	// carrier through an atlas-clear texel and the short physical ring edge.
	float flash_edge = smoothstep(0.0, 0.0025, UV.y) * (1.0 - smoothstep(0.9975, 1.0, UV.y));
	// Distance ownership already supplies the local/clipmap/shell crossfade.
	// Do not multiply the cloud-body handoff here or flash alpha would square it.
	float flash_carrier = far_flash.y * flash_edge;
	// Derivatives for the relief normal are taken before the discard (uniform control flow).
	vec3 cloud_dpdx = dFdx(world_position);
	vec3 cloud_dpdy = dFdy(world_position);
	// Billowed cloud tops: a band-limited puff term (Sunshine's small detail noise at ~14 km) on the relief height.
	float relief_height = cloud;
	if (far_density_enabled) {
		float billow = texture(far_density_small_scale_noise, vec3(canonical_meters.x, 0.0, canonical_meters.z) / 14000.0).r;
		// Sunshine erodes with this noise, so its low values are the round puffs.
		relief_height += far_cloud_billow * (0.5 - billow) * smoothstep(0.0, 0.5, cloud);
	}
	vec2 cloud_dcloud = vec2(dFdx(relief_height), dFdy(relief_height));
	if (cloud < 0.015 && flash_carrier < 0.001) discard;

	// Determine side physically rather than trusting mesh winding: the scaled
	// and legacy shells have different transforms. A camera toward ring centre
	// sees the sunlit exterior/top; one toward terrain sees the inhabited-side
	// underside. FRONT_FACING only resolves the exact grazing-angle fallback.
	float view_side = dot(normalize(camera_world_position - world_position), inhabited_surface_up);
	float exterior_weight = abs(view_side) > 0.001
		? step(0.0, view_side)
		: (FRONT_FACING ? 1.0 : 0.0);
	// Both faces use the same sky-facing local daylight. Only their brightness
	// differs; radial_out would incorrectly make the top night-coloured at
	// local noon.
	float face_day_factor = ring_day_factor;
	float terrain_sun = ring_terrain_cloud_receiver_visibility(vec3(canonical_meters.x, terrain_cloud_receiver_height_meters, canonical_meters.z));
	vec3 cloud_color;
	float base_alpha;
	{
		vec2 band = ring_far_weather_shadow.zw;
		if (band.y <= band.x + 1.0) band = vec2(2000.0, 6000.0);
		float extinction = ring_far_weather_shadow.y > 0.0 ? ring_far_weather_shadow.y : 0.001;
		vec3 up = normalize(inhabited_surface_up);
		vec3 to_camera = normalize(camera_world_position - world_position);
		float mu_v = max(0.12, abs(view_side));
		float tau0 = extinction * (band.y - band.x) * FAR_CLOUD_BAND_FILL * cloud;
		float view_tau = tau0 / mu_v;
		// Thin margins fade out instead of ending on the density threshold, like Sunshine's soft edges.
		base_alpha = clamp((1.0 - exp(-view_tau)) * smoothstep(0.0, far_cloud_edge_softness, cloud)
			* far_density_opacity_multiplier, 0.0, 1.0);
		// Relief normal of the cloud tops (height = column fraction of the band) from screen derivatives. Faded out
		// where one pixel spans much more than the relief so distant pixels do not sparkle.
		float scale = max(ring_atmosphere_coordinate_scale, 0.000001);
		float height_per_cloud = (band.y - band.x) * far_cloud_relief * scale;
		vec3 dpdx = cloud_dpdx;
		vec3 dpdy = cloud_dpdy;
		vec3 r1 = cross(dpdy, up);
		vec3 r2 = cross(up, dpdx);
		float det = dot(dpdx, r1);
		vec3 surface_gradient = sign(det) * height_per_cloud * (cloud_dcloud.x * r1 + cloud_dcloud.y * r2);
		vec3 n_raw = abs(det) * up - surface_gradient;
		vec3 n = abs(det) > 1e-20 && dot(n_raw, n_raw) > 1e-30 ? normalize(n_raw) : up;
		float footprint = max(length(dpdx), length(dpdy)) / scale;
		// Thin margins keep the top normal: their steep threshold slope would otherwise draw dark rims.
		n = normalize(mix(n, up, max(smoothstep(2000.0, 8000.0, footprint), 1.0 - smoothstep(0.1, 0.45, cloud))));
		vec3 below_n = reflect(-n, up); // the base mirrors the top relief
		// Sunshine's macro lighting-density multiplier (ring_ws_apply_macro): heavy overcast and storms absorb the
		// light march, so dense weather reads dark. Normalised to 1 at a typical 0.5 coverage.
		float heavy = smoothstep(0.62, 1.0, resolved_coverage);
		float lighting_power = mix(0.72, 1.55, max(weather.g, heavy));
		float light_extinction_scale = lighting_power * (1.0 + 3.0 * mix(0.70, 0.90, resolved_coverage) * 1.01) / 2.47;
		// The base is floored: a flash carrier keeps cloud == 0 pixels alive, and pow(0, 0) is NaN on GPUs (a NaN
		// pixel showed as a dark disc and threw auto exposure to its bound for the whole screen).
		float powder = mix(1.0, pow(clamp(cloud, 0.0001, 1.0), max(ring_far_cloud_ambient.w, 0.0) * 0.5), 0.25);
		vec3 sun_radiance = vec3(0.0);
		for (int i = 0; i < 2; i++) {
			vec4 light_direction = ring_far_cloud_lights[i * 2];
			vec3 light_radiance = ring_far_cloud_lights[i * 2 + 1].rgb;
			if (light_direction.w < 0.5 || dot(light_radiance, vec3(1.0)) <= 0.0) continue;
			vec3 L = normalize(light_direction.xyz);
			float mu_s = dot(L, up);
			float light_up = smoothstep(-0.05, 0.12, mu_s);
			if (light_up <= 0.0) continue;
			float mu_s_c = max(mu_s, 0.08);
			float s_ext = far_cloud_sun_extinction * light_extinction_scale;
			float lit;
			if (exterior_weight > 0.5) {
				// Fraction of the visible column that sees the light: front-to-back over depth, attenuated by the
				// column above each depth along the light.
				float k = 1.0 / mu_v + s_ext / mu_s_c;
				lit = (1.0 - exp(-tau0 * k)) / max((1.0 + s_ext * mu_v / mu_s_c) * max(1.0 - exp(-view_tau), 0.0001), 0.0001);
				float facing = clamp(dot(n, L) * 0.5 + 0.5, 0.0, 1.0);
				lit *= facing * facing;
			} else {
				// From below: the light has crossed the whole column above the visible base.
				float k = s_ext / mu_s_c - 1.0 / mu_v;
				float through = abs(k) > 0.0001
					? exp(-tau0 * s_ext / mu_s_c) * (exp(tau0 * k) - 1.0) / (k * mu_v * max(1.0 - exp(-view_tau), 0.0001))
					: exp(-tau0 * s_ext / mu_s_c) * tau0 / (mu_v * max(1.0 - exp(-view_tau), 0.0001));
				lit = clamp(through, 0.0, 1.0) * clamp(dot(below_n, L) * 0.5 + 0.5, 0.25, 1.0);
			}
			// Forward-scatter silver lining: thin parts glow when the view ray points toward the light.
			float cos_theta = dot(-to_camera, L);
			float g = clamp(ring_far_cloud_lights[1].w, 0.0, 0.9);
			float hg = (1.0 - g * g) / pow(1.0 + g * g - 2.0 * g * cos_theta, 1.5);
			float thin = exp(-view_tau) * (1.0 - exp(-view_tau)) / max(1.0 - exp(-view_tau), 0.0001);
			float silver = far_cloud_silver_lining * max(hg - 1.0, 0.0) * thin * smoothstep(0.2, 0.9, cos_theta);
			// Sunshine weights every directional light by the terrain/arch sun visibility (eclipses, arch shadow).
			sun_radiance += light_radiance * light_up * (lit + silver) * terrain_sun;
		}
		vec3 ambient = ring_far_cloud_ambient.rgb;
		float ambient_occlusion = exterior_weight > 0.5
			? mix(0.7, 1.0, clamp(dot(n, up), 0.0, 1.0))
			: mix(1.0, 0.55, 1.0 - exp(-tau0));
		cloud_color = sun_radiance * far_cloud_sun_gain * powder
			+ ambient * far_cloud_ambient_gain * ambient_occlusion;
		// No published lighting yet (no Sunshine bridge, e.g. isolated tests): the authored fallback colours.
		if (dot(ring_far_cloud_lights[1].rgb + ring_far_cloud_lights[3].rgb + ring_far_cloud_ambient.rgb, vec3(1.0)) <= 0.0) {
			vec3 day_color = mix(day_cloud_shadow_color, day_cloud_color, clamp(cloud * 1.35, 0.0, 1.0));
			day_color -= (day_color - day_cloud_shadow_color) * (1.0 - terrain_sun);
			vec3 underside_color = mix(night_cloud_color, day_color * vec3(0.78, 0.83, 0.92), ring_day_factor);
			vec3 exterior_color = mix(night_cloud_color * 0.72, day_color * vec3(1.04, 1.02, 0.98), ring_day_factor);
			cloud_color = mix(underside_color, exterior_color, exterior_weight);
		}
	}
	RingHaze haze = ring_haze_sample(camera_world_position, world_position, ring_center, face_day_factor);
	vec3 atmosphere_transmittance = haze.T;
	// The CPU selects real seeded storm systems and publishes the same bounded
	// events to this whole-ring shell and the camera-local clipmap. This keeps a
	// distant flash coherent instead of breaking it into sub-pixel atlas sparks.
	float storm_gate = 1.0;
	vec3 far_glow = (vec3(0.18, 0.28, 0.46) * far_flash.y * storm_gate * 0.55
		+ vec3(0.82, 0.91, 1.0) * far_flash.x * storm_gate * 2.4) * far_density_opacity_multiplier;
	float flash_mask = flash_edge * opacity;
	float unscaled_body_alpha = clamp(cloud * opacity, 0.0, 1.0);
	float reference_alpha = max(unscaled_body_alpha, raw_flash.y * 0.22 * flash_mask);
	// Raw reference alpha restores the old full-weight response over an opaque
	// body; weighted radiance keeps partial ownership linear.
	vec3 flash_premultiplied = far_glow * reference_alpha;
	// Premultiplied blending lets zero-alpha flash radiance add over a bright
	// far backdrop instead of making a translucent carrier darken it. Physical
	// cloud opacity stays base-only; flash_carrier still bounds discard/radiance.
	// Unshaded: Godot composites ALBEDO only and drops EMISSION, so the haze in-scatter goes into ALBEDO. In EMISSION it
	// was lost and far clouds were only dimmed by the haze, never brightened toward it like the terrain around them.
	ALBEDO = (cloud_color * base_alpha + flash_premultiplied) * atmosphere_transmittance
		+ ring_haze_inscatter(haze) * base_alpha;
	ALPHA = base_alpha;
}
