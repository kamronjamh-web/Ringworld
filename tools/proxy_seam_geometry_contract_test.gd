extends SceneTree

const ContractTest := preload("res://scripts/Tools/ProxySeamGeometryContractTest.cs")

# CPU-only regression launcher for proxy sector precision, parent-triangle
# interpolation, clipped-water morph selection, and skirt metadata contracts.
func _init() -> void:
	var passed: bool = ContractTest.new().RunContract()
	print("Proxy seam geometry contract: ", "PASS" if passed else "FAIL")
	quit(0 if passed else 1)
