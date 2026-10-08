extends SceneTree
func _initialize():
 create_timer(15).timeout.connect(func():
  printerr("SHARED_WIND_CORE=FAIL timeout")
  quit(1))
 var driver=load("res://tools/SharedWindCoreTests.cs").new()
 var result=driver.Run()
 print("SHARED_WIND_CORE=",result)
 quit(0 if result.get("passed",false) else 1)
