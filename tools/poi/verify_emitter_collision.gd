extends SceneTree
# Verifies BeamEmitterPOI collision covers every sampled walking route from the Blender export.
#   godot --headless --path . --script res://tools/poi/verify_emitter_collision.gd
# Per sample (Blender xyz -> Godot (x, z, -y)): a downward ray must hit collision within TOLERANCE of
# the sampled floor height. Also checks that nothing blocks the radius-75 m beam bore.

const SAMPLES := "res://prototypes/Ringworld_Surface_Spire_and_Facility/facility_route_samples.json"
const TOLERANCE := 0.12
const EYE := 1.7

func _initialize() -> void:
	var root := Node3D.new()
	root.add_child((load("res://assets/poi/beam_emitter/BeamEmitterPOI.tscn") as PackedScene).instantiate())
	get_root().add_child(root)
	await physics_frame
	await physics_frame
	var space := root.get_world_3d().direct_space_state
	# prototypes/ is hidden from the editor (.gdignore) but plain file reads work.
	var text := FileAccess.get_file_as_string(ProjectSettings.globalize_path(SAMPLES))
	var routes: Array = JSON.parse_string(text)
	var total := 0
	var failed := 0
	var strict_miss := 0
	var worst := 0.0
	for r in routes:
		var rfail := 0
		for p in r.points:
			var g := Vector3(p[0], p[2], -p[1])
			total += 1
			var hit := _down(space, g)
			if hit.is_empty() or absf(hit.position.y - g.y) > TOLERANCE:
				strict_miss += 1
				# A ray landing exactly on a shared triangle edge can slip through; retry on a 5 cm cross.
				hit = {}
				for o in [Vector3(0.05, 0, 0), Vector3(-0.05, 0, 0), Vector3(0, 0, 0.05), Vector3(0, 0, -0.05)]:
					var h2 := _down(space, g + o)
					if not h2.is_empty() and absf(h2.position.y - g.y) <= TOLERANCE:
						hit = h2
						break
			if hit.is_empty():
				rfail += 1
				if rfail <= 3:
					print("  MISS ", r.name, " at ", g, " hit=", hit.get("position", "none"))
			else:
				worst = maxf(worst, absf(hit.position.y - g.y))
		failed += rfail
		print("route ", r.name, " samples=", r.points.size(), " misses=", rfail)
	# Beam bore: horizontal rays from the axis outwards (several heights) must not hit anything inside r<75.
	var bore_hits := 0
	for h in [0.5, 5.0, 10.0, 17.7]:
		for k in 72:
			var a := TAU * k / 72.0
			var q := PhysicsRayQueryParameters3D.create(Vector3(0, h, 0), Vector3(cos(a), 0, sin(a)) * 74.5 + Vector3(0, h, 0))
			if not space.intersect_ray(q).is_empty():
				bore_hits += 1
	print("TOTAL samples=", total, " misses=", failed, " (exact-ray misses recovered by 5 cm retry: ", strict_miss - failed, ") max |dy|=", snappedf(worst, 0.001), " bore(r<74.5) hits=", bore_hits)
	quit(1 if (failed > 0 or bore_hits > 0) else 0)

func _down(space: PhysicsDirectSpaceState3D, g: Vector3) -> Dictionary:
	return space.intersect_ray(PhysicsRayQueryParameters3D.create(g + Vector3(0, 1.5, 0), g - Vector3(0, 1.5, 0)))
