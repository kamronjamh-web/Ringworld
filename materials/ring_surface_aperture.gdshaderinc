// Emitter bore apertures (M1-8): far/ocean terrain shaders discard fragments inside the bore cylinder so the
// bore liner is visible from above. The bore layer publishes up to two slots; w <= 0 means the slot is off.
// xyz = world centre at the pad datum, w = discard radius; axis xyz = inward up, axis w = floor depth.
// Opt-in per material (main-world materials only), so scaled far viewports and hulls never discard.
global uniform vec4 ring_surface_aperture_0;
global uniform vec4 ring_surface_aperture_axis_0;
global uniform vec4 ring_surface_aperture_1;
global uniform vec4 ring_surface_aperture_axis_1;
uniform bool surface_apertures_enabled = false;

bool ring_surface_aperture_slot(vec3 p, vec4 aperture, vec4 axis_depth) {
	if (aperture.w <= 0.0) return false;
	vec3 d = p - aperture.xyz;
	float reach = aperture.w + axis_depth.w + 100.0;
	if (dot(d, d) > reach * reach) return false;
	vec3 axis = axis_depth.xyz;
	float h = dot(d, axis);
	if (h <= -(axis_depth.w + 1.0) || h >= 100.0) return false;
	vec3 radial = d - axis * h;
	return dot(radial, radial) < aperture.w * aperture.w;
}

bool ring_surface_aperture_discard(vec3 p) {
	if (!surface_apertures_enabled) return false;
	return ring_surface_aperture_slot(p, ring_surface_aperture_0, ring_surface_aperture_axis_0)
		|| ring_surface_aperture_slot(p, ring_surface_aperture_1, ring_surface_aperture_axis_1);
}
