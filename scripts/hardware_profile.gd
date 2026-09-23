extends Node

const RUNTIME_TIER_SETTING := "ringworld/runtime/hardware_tier"
const FORCED_TIER_SETTING := "ringworld/hardware/forced_tier"
const LAPTOP_BASELINE := "laptop_140v_baseline"
const DESKTOP_MID_HIGH := "desktop_mid_high"


func _ready() -> void:
	var adapter := RenderingServer.get_video_adapter_name()
	var forced := str(ProjectSettings.get_setting(FORCED_TIER_SETTING, "auto")).to_lower()
	var arguments := OS.get_cmdline_user_args()
	if "--desktop-profile" in arguments:
		forced = DESKTOP_MID_HIGH
	elif "--laptop-profile" in arguments:
		forced = LAPTOP_BASELINE
	var desktop := forced == DESKTOP_MID_HIGH or (
		forced == "auto" and _is_desktop_mid_high_adapter(adapter)
	)
	var tier := DESKTOP_MID_HIGH if desktop else LAPTOP_BASELINE
	ProjectSettings.set_setting(RUNTIME_TIER_SETTING, tier)

	# These renderer-wide settings are selected before the gameplay scene is
	# instantiated. The Arc 140V path retains the former certified defaults.
	ProjectSettings.set_setting(
		"rendering/environment/screen_space_reflection/half_size",
		not desktop)
	ProjectSettings.set_setting(
		"rendering/environment/screen_space_reflection/roughness_quality",
		2 if desktop else 1)
	ProjectSettings.set_setting(
		"rendering/lights_and_shadows/directional_shadow/soft_shadow_filter_quality",
		2 if desktop else 0)

	print("Hardware profile: %s | GPU=%s | logical CPUs=%d" % [
		tier,
		adapter,
		OS.get_processor_count()
	])


func is_desktop_mid_high() -> bool:
	return str(ProjectSettings.get_setting(RUNTIME_TIER_SETTING, LAPTOP_BASELINE)) == DESKTOP_MID_HIGH


func _is_desktop_mid_high_adapter(adapter: String) -> bool:
	var gpu := adapter.to_lower()
	return (
		"rtx 4070" in gpu
		or "rtx 4080" in gpu
		or "rtx 4090" in gpu
		or "rtx 5070" in gpu
		or "rtx 5080" in gpu
		or "rtx 5090" in gpu
	)
