extends SceneTree

var world: Node
var driver: RefCounted
var finished := false

func _initialize():
 create_timer(180.0).timeout.connect(func(): _fail("180 second watchdog expired"))
 call_deferred("_run")

func _run():
 if DisplayServer.get_name().to_lower().contains("headless") or RenderingServer.get_rendering_device() == null:
  _fail("Rendered gameplay capture requires a display and RenderingDevice (Forward+).")
  return
 driver = load("res://tools/FarRingGameplayCapture.cs").new()
 if not driver.ConfigureArguments():
  _fail(driver.ErrorText)
  return
 var directory = "res://.godot/far-ring-gameplay/%s-%d-%d-%d" % [driver.ArtifactLabel, OS.get_process_id(), Time.get_unix_time_from_system(), Time.get_ticks_usec()]
 world = load("res://scenes/VoxelRingIntegrationTest.tscn").instantiate()
 # The bare integration scene is the small test ring. Use the real Certified boot.
 var launcher = load("res://scenes/WorldSizeLauncher.tscn").instantiate()
 root.add_child(launcher)
 launcher.call("_on_certified_preset_pressed")
 launcher.call("_configure_game", world, 5000000.0)
 launcher.free()
 if not driver.Prepare(world, directory):
  _fail(driver.ErrorText)
  return
 root.add_child(world)
 await process_frame
 if not driver.SetPose(world):
  _fail(driver.ErrorText)
  return
 Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
 print("FAR_RING_GAMEPLAY_CAPTURE=WARMUP directory=", directory)
 await create_timer(maxf(0.001, driver.WarmupSeconds)).timeout
 if not driver.BeginCollection(world):
  _fail(driver.ErrorText)
  return
 print("FAR_RING_GAMEPLAY_CAPTURE=COLLECTING seconds=", driver.CollectionSeconds, " stationary=true")
 await create_timer(driver.CollectionSeconds).timeout
 driver.BeginCapture(world)
 while driver.Status == "CAPTURING":
  await process_frame
 if driver.Status != "DONE":
  _fail(driver.ErrorText)
  return
 await RenderingServer.frame_post_draw
 var image = root.get_texture().get_image()
 var error = image.save_png(driver.BundleDirectory.path_join("main.png"))
 if error != OK:
  _fail("Main screenshot failed: %s" % error)
  return
 print("FAR_RING_GAMEPLAY_CAPTURE=PASS bundle=", driver.BundleDirectory)
 finished = true
 world.queue_free()
 await process_frame
 await process_frame
 quit(0)

func _fail(reason):
 if finished:
  return
 finished = true
 printerr("FAR_RING_GAMEPLAY_CAPTURE=FAIL ", reason)
 if is_instance_valid(world):
  if world.is_inside_tree():
   world.queue_free()
  else:
   world.free()
 await process_frame
 quit(1)
