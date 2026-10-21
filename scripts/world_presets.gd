extends RefCounted
class_name WorldPresets

# New World presets shared by the start menu and the launcher. Radius/width come from the launcher config resources so the numbers
# are never duplicated. The launcher applies the preset (see WorldSizeLauncher._apply_new_world_request).

const PRESETS := {
	"standard": {
		"label": "STANDARD",
		"config_path": "res://resources/launcher/certified_ring_config.tres",
		"backend": "runtime_generation",
		"blurb": "The certified full-scale ring with runtime-generated terrain.",
	},
	"compact": {
		"label": "COMPACT",
		"config_path": "res://resources/launcher/world_size_default_config.tres",
		"backend": "smooth_voxel",
		"blurb": "A small development ring with smooth voxel terrain and caves.",
	},
}

const ORDER := ["standard", "compact"]


static func describe(id: String) -> Dictionary:
	var preset: Dictionary = PRESETS.get(id, PRESETS["standard"])
	var config: Resource = load(str(preset["config_path"]))
	return {
		"id": id if PRESETS.has(id) else "standard",
		"label": preset["label"],
		"radius_km": float(config.get("default_radius_km")) if config != null else 0.0,
		"width_km": float(config.get("ring_width_km")) if config != null else 0.0,
		"backend": preset["backend"],
		"blurb": preset["blurb"],
	}
