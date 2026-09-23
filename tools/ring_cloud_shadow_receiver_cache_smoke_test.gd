extends SceneTree

# Real headless material/cache behavior plus a structural shader guard.
# Dedicated GPU smoke tests remain responsible for rendered image validation.
func _init() -> void:
	# Load explicitly so standalone tests do not depend on an editor-generated
	# global C# class cache after a clean rebuild.
	var behavior = load("res://tools/RingCloudShadowProjectorBehaviorTest.cs").new()
	var result: Variant = behavior.call("Run")
	if result != "PASS":
		fail("C# receiver behavior failed: " + str(result))
		return
	var shader := FileAccess.get_file_as_string("res://materials/voxel_ring_terrain.gdshader")
	var projector := FileAccess.get_file_as_string("res://scripts/RingCloudShadowProjector.cs")
	if shader.count("sample_local_cloud_shadow(world_position)") != 1:
		fail("terrain receiver must sample the projected field exactly once per fragment")
		return
	if not shader.contains("local_cloud_shadow_occlusion * ring_day_factor"):
		fail("directional light must reuse the fragment shadow sample")
		return
	for required in [
		"private struct Snapshot", "private sealed class Receiver",
		"!ReferenceEquals(previous.NearTexture, next.NearTexture)",
		"previous.NearOffset != next.NearOffset", "previous.Center != next.Center",
		"receiver.HasApplied = true"
	]:
		if not projector.contains(required):
			fail("missing receiver cache guard: " + required)
			return
	print("Ring cloud shadow receiver cache smoke: PASS")
	quit(0)

func fail(reason: String) -> void:
	push_error(reason)
	quit(1)
