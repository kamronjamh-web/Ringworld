extends SceneTree

## Shadow revamp S4c headless coverage: layer-role publish atomicity and crossfade gating, adaptive slicing rule, build cursor, the world -> anchor-tangent
## frame global vs RingLodGeometrySnapshot, bounds / previous-bounds shrink, recentre continuity vs a fresh fill, settings normalization.
## Headless-safe:  godot --headless --path . --script res://tools/height_shadow_schedule_test.gd

const ScheduleTest := preload("res://tools/HeightShadowScheduleTest.cs")

func _init() -> void:
	var passed: bool = ScheduleTest.new().Run()
	quit(0 if passed else 1)
