// Shared eclipse visibility published by the celestial clock.  Keep the
// geometric day test separate so the ring's normal terminator still works.
global uniform float ringworld_solar_visibility;

float ringworld_effective_solar_day(float geometric_day) {
	return clamp(geometric_day, 0.0, 1.0) * clamp(ringworld_solar_visibility, 0.0, 1.0);
}
