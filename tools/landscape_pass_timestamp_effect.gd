extends CompositorEffect

## Test-only CompositorEffect for the optional opaque-pass bracket of tools/landscape_baseline_common.gd (docs/landscape-audit-t0-spec.md WO T0-7). One instance per callback
## type (PRE_OPAQUE, POST_OPAQUE, POST_TRANSPARENT); each calls rd.capture_timestamp(<name>) on the render thread. LandscapeTimingBridge pairs the names
## lp_pre_opaque / lp_post_opaque / lp_post_transparent out of the engine timestamps and reports BRACKET:opaque (= opaque pass incl. the sky-less main scene geometry).
## Not used by the game.
const NAMES := {
	CompositorEffect.EFFECT_CALLBACK_TYPE_PRE_OPAQUE: "lp_pre_opaque",
	CompositorEffect.EFFECT_CALLBACK_TYPE_POST_OPAQUE: "lp_post_opaque",
	CompositorEffect.EFFECT_CALLBACK_TYPE_POST_TRANSPARENT: "lp_post_transparent",
}
var stamp_name := ""


static func make(callback_type: int) -> CompositorEffect:
	var effect: CompositorEffect = (load("res://tools/landscape_pass_timestamp_effect.gd") as GDScript).new()
	effect.set("stamp_name", NAMES[callback_type])
	effect.effect_callback_type = callback_type
	effect.enabled = true
	return effect


func _render_callback(_effect_callback_type: int, _render_data: RenderData) -> void:
	var rd := RenderingServer.get_rendering_device()
	if rd != null and stamp_name != "":
		rd.capture_timestamp(stamp_name)
