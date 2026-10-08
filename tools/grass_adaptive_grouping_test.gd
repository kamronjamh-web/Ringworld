extends SceneTree

var fixture: RefCounted
var frames := 0

func _initialize():
 fixture = load("res://tools/GrassAdaptiveGroupingTest.cs").new()
 fixture.call_deferred("Start")

func _process(_delta: float) -> bool:
 frames += 1
 var result: String = fixture.Result
 if result == "RUNNING":
  if frames > 15000:
   push_error("GRASS_ADAPTIVE_GROUPING=FAIL timeout")
   quit(1)
  return false
 if result.begins_with("PASS"):
  print("GRASS_ADAPTIVE_GROUPING=" + result)
  quit(0)
 else:
  push_error("GRASS_ADAPTIVE_GROUPING=" + result)
  quit(1)
 return true
