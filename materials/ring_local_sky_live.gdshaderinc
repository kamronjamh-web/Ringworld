// Per-frame sky inputs of ring_local_sky.gdshader as global shader parameters, packed into vec4s by day_side_atmosphere.gd
// (_pack_sky_live_inputs). Non-texture global writes do not dirty the sky radiance map; material writes do. Included AFTER the other
// includes so the object-like defines below cannot rewrite identifiers inside them.
// ringworld_sky_live8 (ring_atmosphere_planet_direction, eclipse_solar_transmittance) is declared and defined in the sky shader head,
// because ring_atmosphere.gdshaderinc uses it before this file is reached.
global uniform vec4 ringworld_sky_live0;
global uniform vec4 ringworld_sky_live1;
global uniform vec4 ringworld_sky_live2;
global uniform vec4 ringworld_sky_live3;
global uniform vec4 ringworld_sky_live4;
global uniform vec4 ringworld_sky_live5;
global uniform vec4 ringworld_sky_live6;
global uniform vec4 ringworld_sky_live7;
global uniform vec4 ringworld_sky_live9;
global uniform vec4 ringworld_sky_live10;
global uniform vec4 ringworld_sky_live11;
global uniform vec4 ringworld_sky_live12;

#define local_surface_up (ringworld_sky_live0.xyz)
#define camera_radial_ratio (ringworld_sky_live0.w)
#define ring_axis_direction (ringworld_sky_live1.xyz)
#define camera_across_ratio (ringworld_sky_live1.w)
#define sun_direction (ringworld_sky_live2.xyz)
#define atmosphere_factor (ringworld_sky_live2.w)
#define sun_color (ringworld_sky_live3.xyz)
#define sky_camera_height_meters (ringworld_sky_live3.w)
#define sky_top_color (ringworld_sky_live4.xyz)
#define ring_interior_factor (ringworld_sky_live4.w)
#define sky_horizon_color (ringworld_sky_live5.xyz)
#define star_visibility (ringworld_sky_live5.w)
#define hub_direction (ringworld_sky_live6.xyz)
#define hub_level (ringworld_sky_live6.w)
#define hub_color (ringworld_sky_live7.xyz)
#define hub_halo_intensity (ringworld_sky_live7.w)
#define giant_direction (ringworld_sky_live9.xyz)
#define planet_angular_radius_degrees (ringworld_sky_live9.w)
#define hub_in_scatter (ringworld_sky_live10.x)
#define eclipse_coverage (ringworld_sky_live10.y)
#define starfield_rotation_radians (ringworld_sky_live10.z)
#define gas_giant_spin_radians (ringworld_sky_live10.w)
#define planet_haze_enabled (ringworld_sky_live11.x > 0.5)
#define planet_enabled (ringworld_sky_live11.y > 0.5)
#define day_start_dot (ringworld_sky_live11.z)
#define day_full_dot (ringworld_sky_live11.w)
#define ground_bounce_radiance (ringworld_sky_live12.xyz)
