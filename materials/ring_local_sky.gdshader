shader_type sky;

#include "res://materials/ring_atmosphere.gdshaderinc"

uniform vec3 local_surface_up = vec3(-1.0, 0.0, 0.0);
uniform vec3 ring_axis_direction = vec3(0.0, 1.0, 0.0);
uniform float camera_radial_ratio = 1.0;
uniform float camera_across_ratio = 0.0;
uniform float ring_half_width_ratio = 0.002;
uniform float ring_interior_factor : hint_range(0.0, 1.0) = 1.0;
uniform float day_start_dot = -0.2;
uniform float day_full_dot = 0.35;
uniform vec3 sky_top_color : source_color = vec3(0.12, 0.28, 0.52);
uniform vec3 sky_horizon_color : source_color = vec3(0.50, 0.60, 0.69);
uniform vec3 space_color : source_color = vec3(0.0);
uniform float atmosphere_factor : hint_range(0.0, 1.0) = 1.0;
uniform float horizon_width : hint_range(0.05, 1.0) = 0.52;
uniform vec3 sun_direction = vec3(1.0, 0.0, 0.0);
uniform vec3 sun_color : source_color = vec3(1.0, 0.96, 0.86);
uniform float sun_disk_angular_radius_degrees : hint_range(0.05, 2.0) = 0.265;
uniform float sun_disk_intensity : hint_range(0.0, 10.0) = 2.55;
uniform float sun_halo_intensity : hint_range(0.0, 2.0) = 0.20;
uniform float atmosphere_sun_forward_scattering : hint_range(0.0, 2.0) = 0.30;
uniform float exterior_day_scattering_boost : hint_range(0.0, 8.0) = 1.0;
uniform float exterior_night_scattering_boost : hint_range(0.0, 8.0) = 1.8;
// Eclipse hub sun (spec C.2, E.1): a wide glow around the local zenith, scaled by the hub level. hub_level 0 draws nothing.
uniform vec3 hub_direction = vec3(-1.0, 0.0, 0.0);
uniform float hub_level : hint_range(0.0, 2.5) = 0.0;
uniform vec3 hub_color : source_color = vec3(0.92, 0.97, 1.0);
uniform float hub_halo_intensity : hint_range(0.0, 2.0) = 0.15;
// Strength of the hub in-scatter phase in the sky (EclipseHubBodySettings.SkyInScatter); weighted below by the hub level and hub day.
uniform float hub_in_scatter : hint_range(0.0, 4.0) = 1.0;
// Saturated Rayleigh blue of the hub-lit air (the palette lean toward the cool hub tint desaturates the sky; this puts the blue back).
uniform vec3 hub_scatter_color : source_color = vec3(0.04, 0.20, 0.55);

uniform bool stars_enabled = true;
uniform float star_visibility : hint_range(0.0, 1.0) = 0.0;
uniform float star_cell_density : hint_range(32.0, 1024.0) = 420.0;
uniform float star_density : hint_range(0.0, 0.25) = 0.045;
uniform float star_brightness : hint_range(0.0, 8.0) = 2.2;
uniform vec3 star_color : source_color = vec3(0.76, 0.84, 1.0);
// RingDayNightCycle's authoritative wrapped ring angle. It is deliberately
// supplied by script instead of TIME so a paused or restored simulation is stable.
uniform float starfield_rotation_radians : hint_range(0.0, 6.2831853) = 0.0;

uniform bool planet_enabled = true;
// Evaluated by the deterministic celestial clock; no longer inferred as anti-sun.
uniform vec3 giant_direction = vec3(-1.0, 0.0, 0.0);
// A compact, CPU-derived offset avoids precision-dependent large float hashes.
uniform vec3 celestial_seed_offset = vec3(0.0);
uniform float planet_angular_radius_degrees : hint_range(0.0, 89.9) = 30.8242334;
uniform float eclipse_coverage : hint_range(0.0, 1.0) = 0.0;
uniform float planet_brightness : hint_range(0.0, 4.0) = 1.15;
uniform float planet_detail_scale : hint_range(0.5, 24.0) = 7.0;
// Gas-giant controls intentionally sit beside the legacy planet palette below,
// so older scenes retain their serialized material parameters.
uniform vec3 gas_giant_light_band_color : source_color = vec3(0.83, 0.68, 0.45);
uniform vec3 gas_giant_dark_band_color : source_color = vec3(0.38, 0.20, 0.12);
uniform vec3 gas_giant_cream_color : source_color = vec3(0.94, 0.83, 0.62);
uniform vec3 gas_giant_storm_color : source_color = vec3(0.78, 0.28, 0.13);
uniform float gas_giant_band_count : hint_range(4.0, 32.0) = 13.0;
uniform float gas_giant_axial_tilt_radians : hint_range(-1.57, 1.57) = 0.0546;
uniform float gas_giant_spin_radians : hint_range(0.0, 6.2831853) = 0.0;
uniform vec3 planet_ocean_color : source_color = vec3(0.035, 0.13, 0.28);
uniform vec3 planet_land_color : source_color = vec3(0.25, 0.34, 0.20);
uniform vec3 planet_highland_color : source_color = vec3(0.48, 0.40, 0.27);
uniform vec3 planet_cloud_color : source_color = vec3(0.78, 0.84, 0.88);
uniform vec3 planet_atmosphere_color : source_color = vec3(0.18, 0.52, 1.0);
uniform float planet_atmosphere_rim_brightness : hint_range(0.0, 4.0) = 1.35;
uniform vec3 planet_light_color : source_color = vec3(0.62, 0.72, 0.90);
uniform float planet_haze_scattering_energy : hint_range(0.0, 2.0) = 0.18;
uniform bool planet_haze_enabled = true;
uniform float planet_haze_horizon_start_dot : hint_range(-0.25, 0.25) = -0.02;
uniform float planet_haze_horizon_full_dot : hint_range(0.01, 0.8) = 0.24;
uniform float planet_haze_twilight_end_day_factor : hint_range(0.05, 0.8) = 0.32;

float hash21(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

vec2 hash22(vec2 p) {
	float n = hash21(p);
	return vec2(n, hash21(p + vec2(19.19, 7.73)));
}

float hash31(vec3 p) {
	p = fract(p * 0.1031);
	p += dot(p, p.zyx + 31.32);
	return fract((p.x + p.y) * p.z);
}

float value_noise_3d(vec3 p) {
	vec3 cell = floor(p);
	vec3 local = fract(p);
	local = local * local * (3.0 - 2.0 * local);
	float n000 = hash31(cell + vec3(0.0, 0.0, 0.0));
	float n100 = hash31(cell + vec3(1.0, 0.0, 0.0));
	float n010 = hash31(cell + vec3(0.0, 1.0, 0.0));
	float n110 = hash31(cell + vec3(1.0, 1.0, 0.0));
	float n001 = hash31(cell + vec3(0.0, 0.0, 1.0));
	float n101 = hash31(cell + vec3(1.0, 0.0, 1.0));
	float n011 = hash31(cell + vec3(0.0, 1.0, 1.0));
	float n111 = hash31(cell + vec3(1.0, 1.0, 1.0));
	float lower = mix(mix(n000, n100, local.x), mix(n010, n110, local.x), local.y);
	float upper = mix(mix(n001, n101, local.x), mix(n011, n111, local.x), local.y);
	return mix(lower, upper, local.z);
}

float planet_fbm(vec3 p) {
	float result = 0.0;
	float amplitude = 0.55;
	for (int octave = 0; octave < 4; octave++) {
		result += value_noise_3d(p) * amplitude;
		p = p * 2.03 + vec3(7.1, 13.7, 19.3);
		amplitude *= 0.48;
	}
	return result;
}

vec2 octahedral_sky_uv(vec3 direction) {
	vec3 d = normalize(direction);
	vec2 uv = d.xz / max(abs(d.x) + abs(d.y) + abs(d.z), 0.0001);
	if (d.y < 0.0) {
		vec2 nonzero_sign = vec2(uv.x >= 0.0 ? 1.0 : -1.0, uv.y >= 0.0 ? 1.0 : -1.0);
		uv = (1.0 - abs(uv.yx)) * nonzero_sign;
	}
	return uv * 0.5 + 0.5;
}

float procedural_stars(vec3 direction) {
	vec2 cells = octahedral_sky_uv(direction) * max(star_cell_density, 1.0);
	vec2 cell = floor(cells);
	vec2 local = fract(cells);
	vec2 star_point = mix(vec2(0.16), vec2(0.84), hash22(cell));
	float selector = hash21(cell + vec2(41.7, 83.1));
	float radius = mix(0.025, 0.070, pow(hash21(cell + vec2(11.3, 57.9)), 8.0));
	float point_star = 1.0 - smoothstep(radius, radius * 2.6, length(local - star_point));
	float populated = step(1.0 - clamp(star_density, 0.0, 0.25), selector);
	float magnitude = mix(0.32, 1.0, pow(selector, 18.0));
	return point_star * populated * magnitude;
}

vec3 rotate_around_axis(vec3 direction, vec3 axis, float angle) {
	vec3 normalized_axis = normalize(axis);
	float sine = sin(angle);
	float cosine = cos(angle);
	return direction * cosine
		+ cross(normalized_axis, direction) * sine
		+ normalized_axis * dot(normalized_axis, direction) * (1.0 - cosine);
}

vec3 render_planet(vec3 view_direction, vec3 to_sun, out float mask) {
	// This is a deterministic inertial direction supplied by the celestial clock.
	// It never depends on local surface coordinates or floating-origin transforms.
	if (!planet_enabled || planet_angular_radius_degrees <= 0.0001) {
		mask = 0.0;
		return vec3(0.0);
	}
	vec3 planet_direction = normalize(giant_direction);
	float radius_radians = radians(clamp(planet_angular_radius_degrees, 0.0001, 89.9));
	float alignment = dot(view_direction, planet_direction);
	// Keep the solid limb much sharper than the star's 0.265-degree radius.
	// The old 3% transition made the grazing contact translucent and disagreed
	// with the analytic covered-area result used by lighting.
	float outer_cos = cos(radius_radians * 1.001);
	float inner_cos = cos(radius_radians * 0.999);
	mask = smoothstep(outer_cos, inner_cos, alignment);
	if (mask <= 0.0001) {
		return vec3(0.0);
	}

	// Treat the apparent angular radius as a sphere at unit center distance.
	// The former orthographic disk normal is visibly incorrect at this 30.8-degree
	// radius: it bends bands and illumination toward the limb.  Intersect the view
	// ray with that sphere instead, retaining the same finite angular silhouette.
	float intersection_distance = alignment - sqrt(max(0.0,
		alignment * alignment - cos(radius_radians) * cos(radius_radians)));
	vec3 surface_normal = normalize(view_direction * intersection_distance - planet_direction);
	float facing = max(0.0, -dot(surface_normal, view_direction));

	// Build the planet-fixed frame from the authoritative ring frame rather than
	// the viewing tangent, so bands do not swim as the camera or giant moves.
	vec3 ring_axis = normalize(ring_axis_direction);
	vec3 epoch_reference = abs(ring_axis.y) < 0.94 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
	vec3 epoch_equator = normalize(cross(ring_axis, epoch_reference));
	vec3 apparent_equator = rotate_around_axis(epoch_equator, ring_axis, starfield_rotation_radians);
	vec3 gas_axis = rotate_around_axis(ring_axis, apparent_equator, gas_giant_axial_tilt_radians);
	vec3 prime_meridian = normalize(cross(gas_axis, apparent_equator));
	vec3 gas_east = rotate_around_axis(prime_meridian, gas_axis, gas_giant_spin_radians);
	vec3 gas_north = normalize(cross(gas_east, gas_axis));
	float latitude = asin(clamp(dot(surface_normal, gas_axis), -1.0, 1.0));
	float longitude = atan(dot(surface_normal, gas_east), dot(surface_normal, gas_north));
	float latitude_unit = latitude / 3.14159265 + 0.5;
	vec3 gas_local_normal = vec3(
		dot(surface_normal, gas_east), dot(surface_normal, gas_axis), dot(surface_normal, gas_north));
	float turbulent_edge = planet_fbm(gas_local_normal * planet_detail_scale * 1.25 + celestial_seed_offset * 9.0);
	float band_wave = sin(latitude * gas_giant_band_count * 3.14159265
		+ sin(longitude * 2.0 + latitude * 9.0) * 0.34 + turbulent_edge * 1.4);
	float dark_band = smoothstep(-0.92, -0.20, band_wave);
	float cream_zone = smoothstep(0.20, 0.92, band_wave);
	vec3 albedo = mix(gas_giant_light_band_color, gas_giant_dark_band_color, dark_band);
	albedo = mix(albedo, gas_giant_cream_color, cream_zone * 0.72);
	// The Great Red Spot is deliberately broad enough to read at eclipse scale.
	float storm_longitude = atan(sin(longitude + celestial_seed_offset.x * 6.2831853),
		cos(longitude + celestial_seed_offset.x * 6.2831853));
	float storm_latitude = latitude - (-0.28 + (celestial_seed_offset.y - 0.5) * 0.10);
	float storm = 1.0 - smoothstep(0.055, 0.145,
		length(vec2(storm_longitude * 0.42, storm_latitude * 1.65)));
	float storm_swirl = 0.5 + 0.5 * sin(atan(storm_latitude, storm_longitude) * 4.0
		+ length(vec2(storm_longitude, storm_latitude)) * 38.0);
	albedo = mix(albedo, gas_giant_storm_color, storm * mix(0.48, 0.92, storm_swirl));
	float polar_haze = smoothstep(0.72, 0.96, abs(latitude_unit * 2.0 - 1.0));
	albedo = mix(albedo, gas_giant_cream_color, polar_haze * 0.30);
	float diffuse = max(dot(surface_normal, normalize(to_sun)), 0.0);
	// Conjunction/eclipses see the unlit hemisphere. Keep the tiny ambient term
	// and rim tied to illumination so it reads as an occulting silhouette.
	float lighting = 0.008 + diffuse * 0.992;
	vec3 surface_color = albedo * lighting;
	float rim = pow(1.0 - facing, 3.2) * planet_atmosphere_rim_brightness * (0.05 + diffuse * 0.95);
	return (surface_color + planet_atmosphere_color * rim) * planet_brightness;
}

void sky() {
	vec3 up = normalize(local_surface_up);
	vec3 view_direction = normalize(EYEDIR);
	float local_elevation = dot(view_direction, up);
	// Geometry should cover the lower hemisphere. Holding any remaining pixels
	// at the horizon colour avoids the fixed global-Y ground/sky split that a
	// conventional planetary procedural sky produces inside a ring.
	float zenith_blend = smoothstep(0.0, max(horizon_width, 0.01), max(local_elevation, 0.0));
	zenith_blend = pow(zenith_blend, 0.72);
	vec3 atmospheric_sky = mix(sky_horizon_color, sky_top_color, zenith_blend);
	if (hub_level > 0.0001 && hub_in_scatter > 0.0) {
		// Hub in-scatter (docs/eclipse-false-sun-spec.md E.1, phase E4c): the hub lights the air from the zenith, so the sky gets the same
		// Rayleigh-like phase the real sun's forward scatter uses, taken toward the hub direction, in a saturated blue. This is what
		// keeps the hub-lit sky blue instead of grey. Weighted by the hub level and the hub day; nothing is added at hub level 0.
		float hub_cos = dot(view_direction, normalize(hub_direction));
		float hub_phase = 0.35 + 0.65 * hub_cos * hub_cos;
		atmospheric_sky += hub_scatter_color * hub_phase * hub_in_scatter * min(hub_level, 1.5) * clamp(ringworld_hub_day, 0.0, 1.0);
	}

	vec3 to_sun = normalize(sun_direction);
	float local_solar_day = ringworld_effective_solar_day(smoothstep(
		day_start_dot,
		max(day_start_dot + 0.0001, day_full_dot),
		dot(up, to_sun)));
	// Start celestial composition in vacuum. The CPU-authored local atmospheric
	// palette is added after the planet mask, so it also veils the giant.
	vec3 color = space_color;
	float sun_alignment = clamp(dot(view_direction, to_sun), -1.0, 1.0);
	float inner_cos = cos(radians(max(sun_disk_angular_radius_degrees, 0.01)));
	float outer_cos = cos(radians(max(sun_disk_angular_radius_degrees * 1.35, 0.02)));
	float sun_disk = smoothstep(outer_cos, inner_cos, sun_alignment);
	float sun_halo = pow(max(sun_alignment, 0.0), 256.0) * atmosphere_factor;
	// The actual giant disk handles geometric occultation. Eclipse dimming is
	// local-day-only, so a conjunction never changes the night-side sky.
	// The giant mask below already removes the covered photosphere. Keep the
	// uncovered crescent at full surface brightness and attenuate only its halo;
	// scaling both would count the same occulted area twice.
	color += sun_color * (
		sun_disk * sun_disk_intensity
		+ sun_halo * sun_halo_intensity * local_solar_day);

	if (hub_level > 0.0001) {
		float hub_alignment = max(dot(view_direction, normalize(hub_direction)), 0.0);
		// Forward-scatter lobe around the hub plus a faint wide skirt, in front of the vacuum and behind the far arch.
		float hub_halo = pow(hub_alignment, 24.0) + 0.25 * pow(hub_alignment, 4.0);
		color += hub_color * hub_halo * hub_halo_intensity * min(hub_level, 1.5) * atmosphere_factor;
	}

	if (stars_enabled && star_visibility > 0.0001 && !AT_CUBEMAP_PASS) {
		// The clock advances visible celestial directions by +phase around the
		// ring axis.  Sampling the fixed star domain with -phase puts its features
		// at +phase, matching that convention without rotating camera/origin data.
		vec3 inertial_star_direction = rotate_around_axis(
			view_direction,
			ring_axis_direction,
			-starfield_rotation_radians);
		float stars = procedural_stars(inertial_star_direction);
		color += star_color * stars * star_brightness * star_visibility;
	}

	if (planet_enabled) {
		float planet_mask = 0.0;
		vec3 planet_color = render_planet(view_direction, to_sun, planet_mask);
		color = mix(color, planet_color, planet_mask);
	}
	// Do not fold solar visibility in here: CPU supplies day/night palette data.
	// Adding this after the planet mask keeps the integrated local sky radiance
	// in front of every celestial source even before the physical shell pass.
	color += (atmospheric_sky - space_color) * clamp(atmosphere_factor, 0.0, 1.0);

	vec3 axis = normalize(ring_axis_direction);
	vec3 outward = -normalize(local_surface_up);
	vec3 normalized_camera_position = outward * max(camera_radial_ratio, 0.000001)
		+ axis * camera_across_ratio;
	vec4 curved_air = sample_ring_sky_atmosphere(
		normalized_camera_position,
		view_direction,
		axis,
		max(ring_half_width_ratio, 0.000001),
		to_sun,
		day_start_dot,
		day_full_dot,
		exterior_day_scattering_boost,
		exterior_night_scattering_boost,
		planet_haze_enabled,
		planet_light_color,
		planet_haze_scattering_energy,
		planet_haze_horizon_start_dot,
		planet_haze_horizon_full_dot,
		planet_haze_twilight_end_day_factor,
		atmosphere_sun_forward_scattering);
	curved_air *= ring_interior_factor;
	// Composite integrated in-scattering in front of every celestial source.
	// This physically transmits the already-rendered giant/stars through local
	// air rather than painting a separate giant rim or overwriting its silhouette.
	color = color * (1.0 - curved_air.a) + curved_air.rgb;
	COLOR = color;
}
