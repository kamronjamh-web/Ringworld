extends "res://tools/grass_cost_gameplay_capture.gd"

# All phases retain native grass resources and mask drawing. Only native scenario
# membership changes. Node.world_3d remains the owner world, not a backend getter.
var saved_scenarios: Array = []
var scenario_detached := false

func _run():
 driver = load("res://tools/FarRingGameplayCapture.cs").new()
 helper = load("res://tools/GrassCostGameplayCapture.cs").new()
 if not driver.ConfigureArguments():
  _fail(driver.ErrorText)
  return
 if DisplayServer.get_name().to_lower().contains("headless") or RenderingServer.get_rendering_device() == null:
  _fail("Forward+ display/RenderingDevice required")
  return
 for path in ["res://tools/grass_scenario_cost_capture.gd", "res://tools/grass_cost_gameplay_capture.gd", "res://tools/GrassCostGameplayCapture.cs", "res://tools/FarRingGameplayCapture.cs", "res://scripts/Voxel/LOD/RingStandingGrassLayer.cs", "res://scripts/Voxel/LOD/RingStandingGrassLayer.OutputResidency.cs", "res://scripts/Voxel/LOD/RingStandingGrassLayer.PresentationCache.cs", "res://scripts/Voxel/LOD/RingGpuVegetationCompute.cs"]:
  source_hashes[path] = FileAccess.get_sha256(path)
 create_timer(driver.WarmupSeconds + regenerate_seconds + 3 * (driver.CollectionSeconds + driver.SettleSeconds + 20) + 40, true, false, true).timeout.connect(func(): _fail("Grass scenario cost watchdog expired"))
 suite_directory = "res://.godot/far-ring-gameplay/%s-grass-scenario-cost-%d-%d" % [driver.ArtifactLabel, OS.get_process_id(), Time.get_ticks_usec()]
 world = load("res://scenes/VoxelRingIntegrationTest.tscn").instantiate()
 var launcher = load("res://scenes/WorldSizeLauncher.tscn").instantiate()
 root.add_child(launcher)
 launcher.call("_on_certified_preset_pressed")
 launcher.call("_configure_game", world, 5000000.0)
 launcher.free()
 if not driver.Prepare(world, suite_directory):
  _fail(driver.ErrorText)
  return
 root.add_child(world)
 await process_frame
 if not driver.SetPose(world) or not driver.SetPoseAltitudeOffset(world, -340):
  _fail(driver.ErrorText)
  return
 Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
 grass = world.get_node("StandingGrass")
 var weather = world.get_node("RingVolumetricClouds")
 var celestial = world.get_node("RingDayNightCycle")
 initial = {"enabled":grass.Enabled, "processing":grass.is_processing(), "visible":grass.visible,
  "cloudPaused":weather.CloudSimulationPaused, "celestialPaused":celestial.Paused,
  "settings":grass.CaptureGrassStreamingDiagnostics().get("settings", {})}
 restore = func():
  _restore_scenarios()
  _restore_layers()
  if is_instance_valid(grass):
   grass.Enabled = initial.enabled
   grass.visible = initial.visible
   grass.set_process(initial.processing)
  if is_instance_valid(weather): weather.CloudSimulationPaused = initial.cloudPaused
  if is_instance_valid(celestial): celestial.Paused = initial.celestialPaused
 grass.Enabled = true
 grass.set_process(true)
 if not _manifest("RUNNING", ""):
  _fail("Cannot write scenario suite manifest")
  return
 await create_timer(maxf(.001, driver.WarmupSeconds), true, false, true).timeout
 if finished or not await _regenerate(): return
 weather.CloudSimulationPaused = true
 celestial.Paused = true
 frozen_cloud = weather.CloudSimulationTimeSeconds
 frozen_celestial = celestial.GetSimulationSeconds()
 frozen_view = helper.CaptureView(world)
 grass.set_process(false)
 for node in _geometry_nodes():
  saved_layers.append({"node":node, "layers":node.layers})
  saved_scenarios.append({"node":node, "instance":node.get_instance(), "scenario":node.get_world_3d().scenario})
  node.layers = 0
 if saved_scenarios.is_empty():
  _fail("Ground baseline has no native grass instances")
  return
 # Let any already submitted compute work drain before establishing the reference.
 await create_timer(driver.SettleSeconds, true, false, true).timeout
 if finished: return
 retained = _snapshot()
 if not await _phase("A-scenario-attached", true, true): return
 scenario_detached = true
 for item in saved_scenarios:
  RenderingServer.instance_set_scenario(item.instance, RID())
 if not await _phase("B-scenario-detached", true, true): return
 _restore_scenarios()
 if not await _phase("A2-scenario-restored", true, true): return
 if restore.is_valid(): restore.call()
 if not _manifest("PASS", ""):
  _fail("Cannot write final scenario suite manifest")
  return
 finished = true
 print("GRASS_SCENARIO_COST=PASS directory=", suite_directory)
 world.queue_free()
 await process_frame
 await process_frame
 quit(0)

func _snapshot() -> Dictionary:
 var state: Dictionary = super._snapshot()
 var native: Array = []
 for item in saved_scenarios:
  if not is_instance_valid(item.node):
   native.append({"nodeValid":false})
  else:
   native.append({"nodeId":str(item.node.get_instance_id()), "instanceRid":str(item.node.get_instance()),
    "savedScenario":str(item.scenario), "ownerWorldScenario":str(item.node.get_world_3d().scenario)})
 state["nativeInstances"] = native
 state["requestedScenarioDetached"] = scenario_detached
 state["scenarioVerification"] = "RenderingServer has no public instance scenario getter; requested mutation recorded, not backend readback. Owner world scenario is expected to remain unchanged."
 return state

func _check(state: Dictionary, _preserve: bool, _masked: bool) -> String:
 if state.view != frozen_view: return "Camera/canonical pose/altitude changed"
 if not state.cloudPaused or not state.celestialPaused or state.cloudClock != frozen_cloud or state.celestialClock != frozen_celestial:
  return "Authoritative clocks changed"
 if not state.enabled or state.processing or not state.visible or state.diagnostics.streaming_suspended:
  return "Grass effective enabled/paused/visible/suspension state changed"
 if state.requestedScenarioDetached != (active_phase == "B-scenario-detached"):
  return "Requested scenario mutation does not match phase"
 if state.nativeInstances != retained.nativeInstances: return "Native instance/world scenario identities changed"
 if state.geometry != retained.geometry: return "Native geometry/resources/masks/visibility changed"
 for item in state.geometry:
  if item.layers != 0: return "Grass drawing is not masked"
 for key in ["totalBuilds", "totalUploads", "residentPlacementHash"]:
  if state[key] != retained[key]: return "Retained grass build/upload/placement state changed: " + key
 for key in ["resident_roots", "resident_tiles", "gpu_registered_batches", "gpu_buffer_bytes", "gpu_shared_source_bytes", "gpu_output_bytes", "gpu_completed_updates"]:
  if state.diagnostics[key] != retained.diagnostics[key]: return "Retained grass/compute resource state changed: " + key
 if state.skippedUnchanged != retained.skippedUnchanged: return "Paused grass unexpectedly requested compute"
 if state.diagnostics.settings != retained.diagnostics.settings: return "Grass settings changed"
 if state.diagnostics.pending_requests != 0 or state.diagnostics.active_jobs != 0 or state.diagnostics.pending_uploads != 0:
  return "Grass build/upload queues are not empty"
 return ""

func _restore_scenarios():
 for item in saved_scenarios:
  if is_instance_valid(item.node): RenderingServer.instance_set_scenario(item.instance, item.scenario)
 scenario_detached = false

func _manifest(status: String, reason: String) -> bool:
 if suite_directory.is_empty(): return false
 DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(suite_directory))
 var file = FileAccess.open(suite_directory.path_join("grass-scenario-cost-suite.json"), FileAccess.WRITE)
 if file == null: return false
 file.store_string(JSON.stringify({"schemaVersion":1, "status":status, "reason":reason, "activePhase":active_phase,
  "results":results, "sourceHashes":source_hashes, "initial":initial, "baselineAltitudeOffsetMeters":-340, "expectedPhaseCount":3,
  "policy":"All phases keep grass enabled, manager paused, all grass geometry layers zero, roots/native nodes/MultiMesh resources/GPU buffers and camera unchanged. Authoritative cloud/celestial clocks frozen after normal warmup. No density/geometry/shader/settings changes.",
  "comparison":"A/A2 brackets B to measure native scenario registration with drawing excluded in every phase and compute/build/upload work unchanged. This does not measure shader draw cost or released-memory effects.",
  "scenarioVerification":"Native instance RIDs and requested scenario mutations recorded; no backend scenario getter/readback available. Root must check engine logs for RenderingServer errors.",
  "limitations":"Scene drift remains possible. Base phase output names use grass-cost-phase.json and GRASS_COST collection marker; this suite uses GRASS_SCENARIO_COST final marker and dedicated manifest. Far viewports have separate worlds."}, "  "))
 file.close()
 return true

func _fail(reason):
 if finished: return
 finished = true
 printerr("GRASS_SCENARIO_COST=FAILED ", reason)
 _manifest("FAILED", str(reason))
 if restore.is_valid(): restore.call()
 if is_instance_valid(world):
  if world.is_inside_tree(): world.queue_free()
  else: world.free()
 await process_frame
 quit(1)
