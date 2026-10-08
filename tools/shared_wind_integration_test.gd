extends SceneTree
func _initialize():
 create_timer(30).timeout.connect(func():
  printerr("SHARED_WIND_INTEGRATION=FAIL timeout")
  quit(1))
 call_deferred("_run")
func _run():
 var world=load("res://scenes/VoxelRingIntegrationTest.tscn").instantiate()
 var preferences=load("res://scripts/Tools/MinimapWindContractTest.cs").new()
 var path="res://.godot/shared-wind-integration/%d" % Time.get_ticks_msec()
 DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(path))
 ProjectSettings.set_setting("ringworld/runtime/active_world_directory", "")
 preferences.InstallIsolatedPreferences(world,path)
 root.add_child(world)
 await process_frame
 var driver=load("res://tools/SharedWindCoreTests.cs").new()
 var result=driver.RunIntegration(world)
 var probe=SubViewport.new()
 probe.size=Vector2i(16,1)
 probe.disable_3d=true
 probe.world_3d=World3D.new()
 probe.use_hdr_2d=true
 probe.render_target_update_mode=SubViewport.UPDATE_ALWAYS
 root.add_child(probe)
 var shader=Shader.new()
 shader.code='shader_type canvas_item;\n#include "res://materials/ring_wind_field.gdshaderinc"\nuniform vec3 points[16];\nvoid fragment(){int index=int(clamp(floor(UV.x*16.0),0.0,15.0));COLOR=vec4(vec3(ring_wind_gust_at_world(points[index],0.0)/4.0),1.0);}'
 var material=ShaderMaterial.new()
 material.shader=shader
 var rect=ColorRect.new()
 rect.size=Vector2(16,1)
 rect.material=material
 probe.add_child(rect)
 var max_error=0.0
 for stage in range(5):
  var data=driver.ConfigureProductionProbe(world,stage)
  var positions=PackedVector3Array(data.points)
  while positions.size()<16:
   positions.append(positions[0])
  material.set_shader_parameter("points",positions)
  await process_frame
  await RenderingServer.frame_post_draw
  var image=probe.get_texture().get_image()
  for index in range(4):
   var actual=image.get_pixel(index,0).r*4.0
   var expected=float(data.gusts[index])
   var error=abs(actual-expected)
   max_error=max(max_error,error)
   if error>.025:
    printerr("SHARED_WIND_GPU_CPU=FAIL stage=%d sample=%d actual=%f expected=%f error=%f"%[stage,index,actual,expected,error])
    quit(1)
    return
 print("SHARED_WIND_GPU_CPU=PASS max_error=",max_error," stages=center/rebase/rotated-scale/second-anchor/seam")
 var before_values=[]
 var advective_error=0.0
 var opposite_error=0.0
 var fingerprint=""
 for phase in range(3):
  var data=driver.ConfigureProductionTravelProbe(world,phase>0,phase==2)
  fingerprint=data.atlas_sha256
  material.set_shader_parameter("points",PackedVector3Array(data.points))
  await process_frame
  await RenderingServer.frame_post_draw
  var image=probe.get_texture().get_image()
  for index in range(16):
   var actual=image.get_pixel(index,0).r*4.0
   var expected=float(data.gusts[index])
   if abs(actual-expected)>.025:
    printerr("SHARED_WIND_ADVECT=FAIL CPU/GPU phase=%d index=%d actual=%f expected=%f"%[phase,index,actual,expected])
    quit(1)
    return
   if phase==0:
    before_values.append(actual)
   elif phase==1:
    advective_error=max(advective_error,abs(actual-before_values[index]))
   else:
    opposite_error+=abs(actual-before_values[index])/16.0
 var spatial_spread=before_values.max()-before_values.min()
 if advective_error>.025 or opposite_error<.05 or spatial_spread<.25:
  printerr("SHARED_WIND_ADVECT=FAIL downwind=%f opposite=%f spatial=%f"%[advective_error,opposite_error,spatial_spread])
  quit(1)
  return
 print("SHARED_WIND_ADVECT=PASS downwind_error=",advective_error," opposite_error=",opposite_error," simultaneous_spread=",spatial_spread," atlas_sha256=",fingerprint)
 probe.queue_free()
 print("SHARED_WIND_INTEGRATION=",result)
 world.queue_free()
 await process_frame
 quit(0 if result.get("passed",false) else 1)
