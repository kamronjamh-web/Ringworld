extends SceneTree
func _initialize():
 var driver = load("res://tools/PerformanceAttributionTests.cs").new()
 if driver.RunContract():
  print("PERFORMANCE_ATTRIBUTION=PASS source-frame uniqueness/missing/duplicate/parent/shadow isolation")
  quit(0)
 else:
  printerr("PERFORMANCE_ATTRIBUTION=FAIL")
  quit(1)
