shader_type spatial;
render_mode cull_disabled, depth_draw_opaque;

uniform sampler2D vertex_data : filter_nearest;
uniform sampler2D attachment_data : filter_nearest;
uniform sampler2D normal_data : filter_nearest;
uniform sampler2D uv_data : filter_nearest;
uniform sampler2D tangent_data : filter_nearest;
uniform sampler2D variant_parameters : filter_nearest;
uniform sampler2DArray albedo_layers : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
// TR-7: baked tangent-space shading normals (n * 0.5 + 0.5). The mips are selected, not averaged, so they are sampled without blending between texels or levels.
uniform sampler2DArray normal_layers : hint_normal, filter_nearest_mipmap, repeat_enable;
uniform sampler2DArray metallic_roughness_layers : filter_linear_mipmap_anisotropic, repeat_enable;
uniform vec3 ring_center = vec3(0.0);
uniform vec3 sun_direction = vec3(1.0, 0.0, 0.0);
uniform float day_start_dot = -0.2;
uniform float day_full_dot = 0.35;
uniform vec3 night_tint = vec3(0.30, 0.45, 0.70);
uniform float night_direct_light_factor : hint_range(0.0, 1.0) = 0.0;
uniform float day_direct_light_factor : hint_range(0.0, 2.0) = 1.0;
uniform bool vegetation_wind_enabled = true;
uniform int tree_foliage_mode = 0;
uniform bool tree_foliage_attachments = false;
uniform bool simplified_tree_shading = false;
// TR-6: one camera-facing quad per tree (vertices 0 to 5; TR-6b adds the horizontal card as vertices 6 to 11) instead of the crossed cards. The quad copies the largest LOD3 plane (variant_parameters rows 3 and 4),
// turns about the tree's up axis towards the camera position, and has no wind.
uniform bool far_quad_mode = false;
// Clockwise seen from the camera (x across to the right, y up): Godot's front face is clockwise, and a back face would have its normal reversed.
const vec2 FAR_QUAD_CORNERS[6] = vec2[6](vec2(0.0, 0.0), vec2(1.0, 1.0), vec2(1.0, 0.0), vec2(0.0, 0.0), vec2(0.0, 1.0), vec2(1.0, 1.0));
// TR-6b: sine of the camera elevations (20 and 30 degrees) between which the horizontal card fades in; the same values as RingBiomeDecorationLayer.TreeSteepElevationLow/HighDegrees.
const float FAR_QUAD_TOP_SIN_LOW = 0.3420201;
const float FAR_QUAD_TOP_SIN_HIGH = 0.5;
// Main-viewport only: ray frame comes from global shader parameters.
#define RING_ATMOSPHERE_FRAME_GLOBAL
#include "res://materials/ring_cloud_shadow.gdshaderinc"
#include "res://materials/ring_shadow_compose.gdshaderinc"
#include "res://materials/ring_atmosphere.gdshaderinc"
#include "res://materials/ring_vegetation_haze.gdshaderinc"
#include "res://materials/ring_vegetation_shadow.gdshaderinc"
#include "res://materials/ring_local_wind.gdshaderinc"
#include "res://materials/ring_wetness.gdshaderinc"
#include "res://materials/ring_forest_lighting.gdshaderinc"
#include "res://materials/ring_foliage_tint.gdshaderinc"
varying vec3 tree_model_position; // forest lighting switches on: the card point in model (normalized tree) space
varying float tree_leaf_transmit; // written in fragment() for light(): k_species * facing * crown transmittance towards the primary sun (0 = no translucency); one varying because the shader is at the 32-varying limit
varying flat vec3 tree_model_light; // forest lighting switches on: the unit direction to the sun in model space (vertex(): MODEL_MATRIX in fragment() lacks the MultiMesh instance transform)
varying flat vec3 tree_model_camera; // forest lighting switches on: the camera in model space
varying float tree_cloud_occlusion;
varying float tree_day_factor;
varying float tree_height_term;
varying vec3 tree_world_position;
varying vec3 tree_haze_T;
varying vec3 tree_haze_S;
varying vec2 tree_wet_state; // wet world, per vertex: (darkening amount, gloss)
varying float tree_highlight_roughness; // wet world: the direct highlight keeps the full wet gloss (ROUGHNESS may be held at the SSR floor)
varying flat int tree_variant;
varying vec4 tree_parameters;
varying flat vec2 tree_dither_window; // the pixel is drawn while x <= noise < y (the coverage word of ring_gpu_vegetation_indirect.glsl, decoded in vertex())
varying flat float tree_dither_seed;
varying flat vec2 tree_foliage_tint; // T6-8: written in vertex() only while ring_foliage_tint_on(): (value factor, hue term) of this root, see ring_foliage_tint.gdshaderinc

float tree_dither_noise(vec2 pixel, float seed) {
	return fract(sin(dot(pixel + vec2(seed * 17.0, seed * 43.0), vec2(12.9898, 78.233))) * 43758.5453);
}

void apply_tree_dither(vec2 fragment_coordinate) {
	if (tree_dither_window.x <= 0.0 && tree_dither_window.y >= 1.0) return;
	float noise = tree_dither_noise(floor(fragment_coordinate), tree_dither_seed);
	if (noise < tree_dither_window.x || noise >= tree_dither_window.y) discard;
}

// Model-space direction of a world-space direction (the instance basis is a rotation times one uniform scale).
vec3 far_quad_to_model_direction(mat4 model, float scale, vec3 world_direction) {
	return vec3(dot(world_direction, model[0].xyz), dot(world_direction, model[1].xyz), dot(world_direction, model[2].xyz)) / max(scale, 0.000001);
}

void vertex() {
	float coverage_word = abs(INSTANCE_CUSTOM.w - 2.0) <= 0.25 ? INSTANCE_CUSTOM.y : 1.0;
	vec2 dither_window = vec2(0.0, 1.0);
	if (coverage_word >= 0.0 && coverage_word < 1.0) dither_window.y = coverage_word; // fade in
	else if (coverage_word > -1.0 && coverage_word < 0.0) dither_window.x = 1.0 + coverage_word; // fade out
	// Window (steep card tree at the near fade): the marker is 2 + 0.2 * low and the coverage word is the upper limit.
	dither_window.x = max(dither_window.x, clamp((INSTANCE_CUSTOM.w - 2.0) * 5.0, 0.0, 1.0));
	tree_dither_window = dither_window;
	tree_dither_seed = INSTANCE_CUSTOM.z;
	int variant = clamp(int(round(INSTANCE_CUSTOM.x)), 0, 29);
	tree_variant = variant;
	tree_parameters = texelFetch(variant_parameters, ivec2(variant, 1), 0);
	vec3 root_world = MODEL_MATRIX[3].xyz;
	if (far_quad_mode && VERTEX_ID >= 6) {
		// TR-6b: the variant's horizontal LOD3 card (vertices 6 to 11), fixed in the tree's own frame (no turning, no wind), covering the card's UV rectangle.
		// It is dropped at low angles (all six vertices on one point: no triangles, no pixels) and fades in by screen-door over the same 20 to 30 degrees of
		// camera elevation as the steep-view rule of ring_gpu_vegetation_indirect.glsl (smoothstep of the sine, measured from the tree root along its up axis).
		float model_scale = length(MODEL_MATRIX[1].xyz);
		vec3 tree_up = MODEL_MATRIX[1].xyz / max(model_scale, 0.000001);
		vec3 to_camera = ring_atmosphere_camera_world_position - root_world;
		float steepness = smoothstep(FAR_QUAD_TOP_SIN_LOW, FAR_QUAD_TOP_SIN_HIGH, dot(tree_up, to_camera) / max(length(to_camera), 0.001));
		vec4 top_uv = texelFetch(variant_parameters, ivec2(variant, 5), 0);
		vec4 top_corners = texelFetch(variant_parameters, ivec2(variant, 6), 0);
		vec4 top_extra = texelFetch(variant_parameters, ivec2(variant, 7), 0);
		vec3 corner_00 = vec3(top_corners.x, top_extra.z, top_corners.y);
		vec3 edge_u = vec3(top_corners.z - top_corners.x, 0.0, top_corners.w - top_corners.y);
		vec3 edge_v = vec3(top_extra.x - top_corners.x, 0.0, top_extra.y - top_corners.y);
		// Clockwise seen from above (Godot's front face): swap two corners of each triangle when the picture's (u, v) frame is clockwise from above.
		int corner_index = VERTEX_ID - 6;
		if (edge_u.z * edge_v.x - edge_u.x * edge_v.z < 0.0) corner_index = (corner_index % 3 == 1) ? corner_index + 1 : ((corner_index % 3 == 2) ? corner_index - 1 : corner_index);
		vec2 corner = FAR_QUAD_CORNERS[corner_index];
		bool top_present = top_extra.w >= 0.0 && steepness > 0.0;
		VERTEX = top_present ? corner_00 + edge_u * corner.x + edge_v * corner.y : vec3(0.0);
		dither_window.y = min(dither_window.y, steepness);
		tree_dither_window = dither_window;
		// Normal-layer frame from the frame code (see the upright quad below): the baked frame T0, B0, N0 against the picture's u and v directions, seen from above.
		int top_code = int(round(max(top_extra.w, 0.0)));
		float top_frame_sign = (top_code & 1) != 0 ? 1.0 : -1.0;
		float top_t_sign = (top_code & 2) != 0 ? 1.0 : -1.0;
		float top_b_sign = (top_code & 4) != 0 ? 1.0 : -1.0;
		float top_hand = (top_code & 8) != 0 ? 1.0 : -1.0;
		vec3 u_dir = normalize(edge_u + vec3(0.0, 0.0, 0.000001));
		vec3 v_dir = normalize(edge_v + vec3(0.000001, 0.0, 0.0));
		NORMAL = normalize(cross(u_dir, v_dir)) * (top_frame_sign * top_hand);
		TANGENT = u_dir * (top_frame_sign * top_t_sign);
		BINORMAL = v_dir * (top_frame_sign * top_b_sign);
		UV = vec2(mix(top_uv.x, top_uv.z, corner.x), mix(top_uv.y, top_uv.w, corner.y));
		UV2 = vec2(0.0);
	} else if (far_quad_mode) {
		float model_scale = length(MODEL_MATRIX[1].xyz);
		vec3 tree_up = MODEL_MATRIX[1].xyz / max(model_scale, 0.000001);
		vec4 plane_uv = texelFetch(variant_parameters, ivec2(variant, 3), 0);
		vec4 plane_size = texelFetch(variant_parameters, ivec2(variant, 4), 0);
		float quad_width = plane_size.x * model_scale;
		float quad_height = max((plane_size.z - plane_size.y) * model_scale, 0.0001);
		vec3 quad_centre = root_world + tree_up * (0.5 * (plane_size.y + plane_size.z) * model_scale);
		vec3 to_camera = ring_atmosphere_camera_world_position - quad_centre;
		vec3 camera_dir = to_camera / max(length(to_camera), 0.001);
		float sin_elevation = clamp(dot(tree_up, camera_dir), -1.0, 1.0);
		// Horizontal axis: cross(up, direction). Directly above or below (up . direction beyond 0.999) it blends to the ring-axis direction (world Y)
		// instead of switching, so the picture does not twist.
		vec3 across = cross(tree_up, camera_dir);
		vec3 axis_direction = dot(across, vec3(0.0, 1.0, 0.0)) < 0.0 ? vec3(0.0, -1.0, 0.0) : vec3(0.0, 1.0, 0.0);
		across += axis_direction * (1.0 - smoothstep(0.0, 0.0447, length(across)));
		across -= camera_dir * dot(across, camera_dir);
		across = normalize(across);
		vec3 quad_up = cross(camera_dir, across);
		// Outline of an ellipsoid of the tree's size: the side view from the ground, a W x W patch from straight above, no enlargement.
		float height_scale = sqrt(max(1.0 - sin_elevation * sin_elevation, 0.0) + (quad_width / quad_height) * (quad_width / quad_height) * sin_elevation * sin_elevation);
		vec2 corner = FAR_QUAD_CORNERS[VERTEX_ID % 6];
		bool mirrored = fract(INSTANCE_CUSTOM.z * 7919.0) > 0.5;
		float u_at_left = mirrored ? plane_uv.y : plane_uv.x;
		float u_at_right = mirrored ? plane_uv.x : plane_uv.y;
		vec3 corner_world = quad_centre + across * ((corner.x - 0.5) * quad_width) + quad_up * ((corner.y - 0.5) * quad_height * height_scale);
		vec3 offset = corner_world - root_world;
		VERTEX = vec3(dot(offset, MODEL_MATRIX[0].xyz), dot(offset, MODEL_MATRIX[1].xyz), dot(offset, MODEL_MATRIX[2].xyz)) / max(model_scale * model_scale, 0.000001);
		// Normal-layer frame: the bake's own, not the tilted quad's: across, the tree's up axis, and the horizontal direction to the camera, so top light stays
		// top light seen from above. The card the quad copies was baked with frame (T0, B0, N0) and sign s; row 4's w holds its relation to the picture:
		// bit 0 s = +1, bit 1 T0 along +u, bit 2 B0 along +v. The picture is drawn with u along `across` times u_sign and v along up times v_sign (a mirrored
		// picture flips only the across component, which u_sign does in TANGENT). The baked side always faces the camera, so N0 is the direction to the camera
		// times s and does not depend on the mirroring. The quad is front-facing (see FAR_QUAD_CORNERS), so Godot does not negate the frame; s is applied here.
		int frame_code = int(round(plane_size.w));
		float frame_sign = (frame_code & 1) != 0 ? 1.0 : -1.0;
		float t_sign = (frame_code & 2) != 0 ? 1.0 : -1.0;
		float b_sign = (frame_code & 4) != 0 ? 1.0 : -1.0;
		float u_sign = u_at_right >= u_at_left ? 1.0 : -1.0;
		float v_sign = plane_uv.w >= plane_uv.z ? 1.0 : -1.0;
		vec3 across_flat = normalize(across - tree_up * dot(across, tree_up));
		vec3 toward_camera_flat = cross(across_flat, tree_up);
		NORMAL = far_quad_to_model_direction(MODEL_MATRIX, model_scale, toward_camera_flat * frame_sign);
		TANGENT = far_quad_to_model_direction(MODEL_MATRIX, model_scale, across_flat * (frame_sign * t_sign * u_sign));
		BINORMAL = far_quad_to_model_direction(MODEL_MATRIX, model_scale, tree_up * (frame_sign * b_sign * v_sign));
		UV = vec2(mix(u_at_left, u_at_right, corner.x), mix(plane_uv.z, plane_uv.w, corner.y));
		UV2 = vec2(0.0);
	} else {
	vec4 packed_vertex = texelFetch(vertex_data, ivec2(VERTEX_ID, variant), 0);
	VERTEX = packed_vertex.xyz;
	NORMAL = normalize(texelFetch(normal_data, ivec2(VERTEX_ID, variant), 0).xyz);
	vec4 packed_uv = texelFetch(uv_data, ivec2(VERTEX_ID, variant), 0);
	UV = packed_uv.xy;
	UV2 = packed_uv.zw;
	vec4 packed_tangent = texelFetch(tangent_data, ivec2(VERTEX_ID, variant), 0);
	TANGENT = normalize(packed_tangent.xyz);
	BINORMAL = normalize(cross(NORMAL, TANGENT)) * packed_tangent.w;
	if (vegetation_wind_enabled) {
		mat3 basis = mat3(MODEL_MATRIX);
		vec3 offset; mat3 jacobian;
		vec4 wind_flags = texelFetch(variant_parameters, ivec2(variant, 2), 0);
		ring_tree_wind_geometry(root_world, VERTEX, basis, texelFetch(attachment_data, ivec2(VERTEX_ID, variant), 0), int(round(wind_flags.g)), wind_flags.b > .5, offset, jacobian);
		VERTEX += offset;
		NORMAL = ring_tree_deformed_normal(jacobian, NORMAL);
		if (length(TANGENT) > .0001) TANGENT = normalize(jacobian * TANGENT);
		if (length(BINORMAL) > .0001) BINORMAL = normalize(jacobian * BINORMAL);
	}
	}
	if (ring_forest_translucency_on() || ring_forest_crown_on()) {
		tree_model_camera = ring_crown_model_point(MODEL_MATRIX, ring_atmosphere_camera_world_position);
		tree_model_light = normalize(transpose(mat3(MODEL_MATRIX)) * sun_direction);
		tree_model_position = VERTEX;
	}
	if (ring_foliage_tint_on()) {
		tree_foliage_tint = ring_foliage_tint_terms(root_world, INSTANCE_CUSTOM.z);
		tree_model_position = VERTEX; // the foliage gate of fragment() reads the model height (the forest lighting block above writes the same value)
	}
	vec3 world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	RingVegetationShadowInstance shadow_instance = ring_vegetation_shadow_instance(RING_VEGETATION_SHADOW_TREES, INSTANCE_ID);
	tree_cloud_occlusion = ring_vegetation_shadow_cloud_vertex(shadow_instance, world_position);
	vec3 radial = world_position - ring_center;
	radial.y = 0.0;
	vec3 surface_up = length(radial) > 0.0001 ? -normalize(radial) : vec3(-1.0, 0.0, 0.0);
	tree_day_factor = ringworld_effective_solar_day(smoothstep(day_start_dot, day_full_dot, dot(surface_up, normalize(sun_direction))));
	tree_world_position = world_position;
	// Terrain height-shadow clipmap (S5), per vertex like the cloud term: 1 when inactive. Applied to the primary sun in light().
	tree_height_term = ring_vegetation_shadow_height_vertex(shadow_instance, world_position, root_world, surface_up, ring_atmosphere_camera_world_position);
	RingHaze vertex_haze = ring_vegetation_haze_sample(RING_VEGETATION_HAZE_TREES, INSTANCE_ID,
		ring_atmosphere_camera_world_position, world_position, ring_center, tree_day_factor);
	tree_haze_T = vertex_haze.T;
	tree_haze_S = ring_haze_inscatter(vertex_haze);
	tree_wet_state = vec2(0.0);
	if (ring_wet_params.w > 0.5) {
		RingWet wet_state = ring_wet_sample_vegetation(world_position);
		tree_wet_state = vec2(smoothstep(0.0, 0.5, wet_state.soak) * ring_wet_params.x, ring_wet_gloss(wet_state));
	}
}
void fragment() {
	apply_tree_dither(FRAGCOORD.xy);
	// Baked shading normal in the card's tangent frame, read for every card pixel (simplified shading included; no brightness term anywhere in the card path).
	// It is decoded by hand, not through NORMAL_MAP: NORMAL_MAP rebuilds z as +sqrt(1 - x^2 - y^2), and the bake stores a negative z on cards whose frame the
	// mesh shader negates. On a back face (cull_disabled) Godot negates only NORMAL, not TANGENT/BINORMAL (measured in tree_billboard_match_gpu_test: also
	// negating the in-plane part gave a worst noon luminance error of 0.72, this decode 0.31). The decode therefore already gives the baked normal mirrored
	// through the card plane, which keeps its up component, so a card seen from behind shows the same lit crown top.
	vec3 baked_normal = texture(normal_layers, vec3(UV, float(tree_variant))).rgb * 2.0 - 1.0;
	NORMAL = normalize(TANGENT * baked_normal.x + BINORMAL * baked_normal.y + NORMAL * baked_normal.z);
	vec4 albedo = texture(albedo_layers, vec3(UV, float(tree_variant))) * texelFetch(variant_parameters, ivec2(tree_variant, 0), 0);
	vec4 flags = texelFetch(variant_parameters, ivec2(tree_variant, 2), 0);
	if (flags.r > .5) {
		ALPHA = albedo.a;
		ALPHA_SCISSOR_THRESHOLD = far_quad_mode ? flags.a : tree_parameters.a;
	}
	if (ring_foliage_tint_on()) {
		// T6-8: per-instance and per-stand foliage colour, before the lighting and haze factors; foliage only (the FL-2c gate: a valid hull and at or above the crown base).
		vec4 tint_hull = texelFetch(variant_parameters, ivec2(tree_variant, 8), 0);
		vec4 tint_hull_extra = texelFetch(variant_parameters, ivec2(tree_variant, 9), 0);
		if (ring_crown_valid(tint_hull_extra) && tree_model_position.y >= tint_hull.x - tint_hull.z) albedo.rgb *= ring_foliage_tint(tree_foliage_tint);
	}
	ALBEDO = albedo.rgb * mix(night_tint, vec3(1.0), tree_day_factor) * (1.0 - tree_cloud_occlusion * tree_day_factor * .34);
	ALBEDO *= tree_haze_T;
	ALBEDO *= 1.0 - 0.55 * 0.5 * min(tree_wet_state.x, 1.0); // wet world: bark and foliage darken half as much as soil
	vec3 haze_emission = tree_haze_S;
	if (atmosphere_haze_model == 0) {
		// Legacy: scattering colour from the fragment position, opacity from the vertex.
		vec4 atmosphere = sample_ring_atmosphere(ring_atmosphere_camera_world_position,
			tree_world_position, ring_center, tree_day_factor);
		haze_emission = atmosphere.rgb * (1.0 - tree_haze_T.g) * atmosphere_scattering_energy;
	}
	EMISSION += haze_emission;
	if (simplified_tree_shading) {
		METALLIC = 0.0;
		ROUGHNESS = 0.9;
	} else {
		vec4 mr = texture(metallic_roughness_layers, vec3(UV, float(tree_variant)));
		METALLIC = mr.b * tree_parameters.g;
		ROUGHNESS = mr.g * tree_parameters.b;
	}
	float dry_roughness = ROUGHNESS;
	ROUGHNESS *= mix(1.0, 0.6, tree_wet_state.y); // wet world: glossier when wet
	tree_highlight_roughness = ROUGHNESS;
	ROUGHNESS = ring_wet_ssr_roughness(ROUGHNESS, dry_roughness); // distant canopy: no SSR on wet impostors
	AO = min(1.0 - tree_cloud_occlusion * tree_day_factor * .72, ring_storm_ambient(ring_storm_level(tree_world_position))); // storm light
	AO_LIGHT_AFFECT = 0.0;
	if (ring_forest_translucency_on() || ring_forest_crown_on()) {
		// Forest lighting: the statistical crown model shared with the mesh (docs/forest-lighting-plan.md sections 3 and 4): first leaf on the view ray, the variant's leaf inclination distribution (row 10).
		vec4 hull = texelFetch(variant_parameters, ivec2(tree_variant, 8), 0);
		vec4 hull_extra = texelFetch(variant_parameters, ivec2(tree_variant, 9), 0);
		tree_leaf_transmit = 0.0;
		// Foliage gate: no leaves below the crown base, so the trunk gets neither crown occlusion nor translucency.
		if (ring_crown_valid(hull_extra) && tree_model_position.y >= hull.x - hull.z) {
			float analytic_weight = ring_forest_analytic_weight(distance(tree_world_position, ring_atmosphere_camera_world_position));
			float crown_sky, crown_sun;
			ring_crown_first_leaf_terms(tree_model_camera, tree_model_position, tree_model_light, hull, hull_extra, ring_forest_translucency_on() && analytic_weight > 0.0, crown_sky, crown_sun);
			if (ring_forest_crown_on()) AO *= crown_sky;
			if (ring_forest_translucency_on()) {
				tree_leaf_transmit = hull_extra.z * (ring_leaf_lad_facing(tree_model_light, normalize(tree_model_camera - tree_model_position), vec3(0.0, 1.0, 0.0), texelFetch(variant_parameters, ivec2(tree_variant, 10), 0))) * mix(1.0, crown_sun, analytic_weight);
			}
		}
	}
	// Reflections R1-1a hold: the sky term stays black as before the sky source; crown interiors and trunks cannot see the sky and there is no crown-visibility term yet (docs/work-orders/reflections/R1-1-design.md). SSR on wet bark is unaffected.
	RADIANCE = vec4(0.0, 0.0, 0.0, 1.0);
	if (ring_shadow_debug_enabled()) {
		// Shadow debug view: only the selected shadow term reaches the screen (see ring_shadow_compose.gdshaderinc).
		ALBEDO = vec3(1.0);
		EMISSION = vec3(0.0);
		AO = 0.0;
		SPECULAR = 0.0;
		METALLIC = 0.0;
		ROUGHNESS = 1.0;
		tree_highlight_roughness = 1.0;
	}
}

void light() {
	float n_dot_l = max(dot(NORMAL, LIGHT), 0.0);
	vec3 sun_view = normalize((VIEW_MATRIX * vec4(sun_direction, 0.0)).xyz);
	float primary_sun = LIGHT_IS_DIRECTIONAL ? smoothstep(.90, .995, dot(normalize(LIGHT), sun_view)) : 0.0;
	float direct = mix(night_direct_light_factor, day_direct_light_factor, tree_day_factor);
	float cloud = LIGHT_IS_DIRECTIONAL ? 1.0 - tree_cloud_occlusion * tree_day_factor : 1.0;
	// Vegetation keeps its legacy product (geometry * cloud, no macro/terrain term): COMBINE_PRODUCT.
	RingShadowTerms terms = ring_shadow_terms(tree_world_position, SCREEN_UV, ATTENUATION, 0.0, 0.0, RING_SHADOW_COMBINE_PRODUCT);
	terms.storm = LIGHT_IS_DIRECTIONAL ? terms.storm : 0.0; // storm light fades the sun only, never a point or spot light
	terms.cloud = mix(1.0, cloud, primary_sun);
	terms.geometry = ring_height_shadow_vegetation_geometry(terms.geometry, tree_height_term, LIGHT_IS_DIRECTIONAL, primary_sun, tree_day_factor);
	terms.geometry = ring_height_shadow_vegetation_planet_geometry(terms.geometry, tree_height_term, LIGHT_IS_DIRECTIONAL, LIGHT, VIEW_MATRIX, primary_sun);
	float sun_visibility = ring_shadow_sun_visibility(terms, RING_SHADOW_COMBINE_PRODUCT);
	if (ring_shadow_debug_enabled()) {
		if (LIGHT_IS_DIRECTIONAL && primary_sun > 0.5) {
			DIFFUSE_LIGHT += vec3(ring_shadow_debug_value(terms, sun_visibility));
		}
	} else {
		float visibility = sun_visibility * mix(1.0, direct, primary_sun);
		DIFFUSE_LIGHT += (LIGHT_COLOR / PI) * n_dot_l * visibility;
		if (ring_forest_translucency_on() && tree_leaf_transmit > 0.0) {
			// Leaf translucency, primary sun only (docs/forest-lighting-plan.md section 3). Godot multiplies by ALBEDO afterwards.
			DIFFUSE_LIGHT += (LIGHT_COLOR / PI) * (tree_leaf_transmit * RING_LEAF_TRANSMISSION_SHAPE) * visibility * primary_sun;
		}
		if (!simplified_tree_shading) {
			vec3 half_vector = normalize(LIGHT + VIEW);
			float gloss = mix(8.0, 128.0, 1.0 - tree_highlight_roughness);
			float specular = pow(max(dot(NORMAL, half_vector), 0.0), gloss) * mix(mix(.04, .1, tree_wet_state.y), 1.0, METALLIC);
			SPECULAR_LIGHT += (LIGHT_COLOR / PI) * specular * n_dot_l * visibility * tree_haze_T;
		}
	}
}
