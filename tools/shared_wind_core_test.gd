extends SceneTree
func _initialize():
 create_timer(15).timeout.connect(func():
  printerr("SHARED_WIND_CORE=FAIL timeout")
  quit(1))
 var driver=load("res://tools/SharedWindCoreTests.cs").new()
 var result=driver.Run()
 var ok = bool(result.get("passed",false))
 print("SHARED_WIND_CORE=%s %s" % ["PASS" if ok else "FAIL", JSON.stringify(result)])
 quit(0 if ok else 1)
