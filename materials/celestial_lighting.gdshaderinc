// Shared eclipse visibility published by the celestial clock.  Keep the
// geometric day test separate so the ring's normal terminator still works.
global uniform float ringworld_solar_visibility;
// Eclipse hub sun ("noon everywhere", docs/eclipse-false-sun-spec.md C). Both are 0 outside the event, and every function below then
// returns exactly what it returned before the hub existed.
// ringworld_hub_day: the per-point day the hub contributes; the same value everywhere because every point sees the hub at its zenith.
// ringworld_hub_light_weight: the share of the primary DirectionalLight's direction owned by the hub (1 for the whole event).
// ringworld_night_light_scale is declared in ring_atmosphere.gdshaderinc, not here: a duplicate declaration fails to compile.
global uniform float ringworld_hub_day;
global uniform float ringworld_hub_light_weight;

float ringworld_effective_solar_day(float geometric_day) {
	return max(
		clamp(geometric_day, 0.0, 1.0) * clamp(ringworld_solar_visibility, 0.0, 1.0),
		clamp(ringworld_hub_day, 0.0, 1.0));
}

// For callers that pass the raw solar visibility into a day computation of their own.
float ringworld_effective_solar_visibility() {
	return max(ringworld_solar_visibility, clamp(ringworld_hub_day, 0.0, 1.0));
}

// Direction toward the primary light for a fragment, in view space. While the hub sun is up the light comes from each point's own zenith
// (the inward radial): the DirectionalLight can only follow the camera's zenith, so the opposite arch would be lit from behind.
// primary_weight is 1 for the star light and 0 for any other light (the planet light), so only the star light is moved.
vec3 ringworld_primary_light(vec3 light_view, vec3 surface_up_world, mat4 view, float primary_weight) {
	float hub_weight = clamp(ringworld_hub_light_weight, 0.0, 1.0) * clamp(primary_weight, 0.0, 1.0);
	if (hub_weight <= 0.0) return light_view;
	vec3 up_view = normalize((view * vec4(surface_up_world, 0.0)).xyz);
	vec3 blended = mix(light_view, up_view, hub_weight);
	float blended_length = length(blended);
	// Opposite directions cancel at a half weight; the light has no energy then, but never produce a NaN.
	return blended_length > 0.0001 ? blended / blended_length : up_view;
}
