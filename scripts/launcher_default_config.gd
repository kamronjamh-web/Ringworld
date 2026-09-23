extends Resource
class_name LauncherDefaultConfig

@export var preset_name: String = "Development Ring"
@export var certification_target: bool = false
@export_range(80.0, 8000.0, 1.0) var default_radius_km: float = 80.0
@export_range(5.0, 318.0, 0.5) var ring_width_km: float = 20.0
@export_range(-8192.0, 0.0, 32.0) var terrain_floor_y: float = -8192.0
@export_range(512.0, 8000.0, 32.0) var build_height_y: float = 4096.0
@export_range(1.0, 50.0, 0.1) var structural_depth_km: float = 22.3
@export var vertical_domain_version: int = 1
@export_range(32.0, 8192.0, 32.0) var protected_roof_thickness_m: float = 512.0
@export_range(32.0, 8192.0, 32.0) var lower_hull_thickness_m: float = 800.0

@export_range(0.0, 2147483647.0, 1.0) var terrain_seed: float = 1337.0
@export_range(1.0, 1024.0, 1.0) var cave_generator_version: float = 1.0
@export var designed_cave_density_composition: bool = false
@export_range(1.0, 512.0, 1.0) var base_surface_height: float = 16.0
@export_range(0.0, 2048.0, 1.0) var height_amplitude: float = 12.0
@export_range(0.0, 512.0, 1.0) var detail_amplitude: float = 4.0
@export_range(1.0, 3.0, 0.01) var continental_contrast: float = 1.35
@export_range(0.0, 1.0, 0.01) var regional_detail_strength: float = 0.65
@export_range(512.0, 8000.0, 1.0) var rare_peak_height: float = 1848.0
@export var auto_raise_peak_to_build_limit: bool = false
