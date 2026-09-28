class_name CanonicalNoiseDomain
extends RefCounted

## The certified periodic mapping uses a circle around the ring and across as Z.
## FastNoiseLite receives 32-bit coordinates, so its precision at the roughly
## five-million-metre circle radius must be evaluated before production use.
var circumference_m: float
var radius_m: float

func _init(source_circumference_m: float = 31415936.0) -> void:
	circumference_m = maxf(1.0, source_circumference_m)
	radius_m = circumference_m / TAU

func wrap_around(around_m: float) -> float:
	return fposmod(around_m, circumference_m)

func floor_lattice_index(meters: float, spacing_m: float) -> int:
	assert(spacing_m > 0.0)
	return int(floor(meters / spacing_m))

func region_index(sample_index: int, side: int) -> int:
	assert(side > 0)
	return int(floor(float(sample_index) / float(side)))

func sample_noise(noise: FastNoiseLite, around_m: float, across_m: float) -> float:
	var wrapped := wrap_around(around_m)
	var theta := wrapped / circumference_m * TAU
	return noise.get_noise_3d(cos(theta) * radius_m, sin(theta) * radius_m, across_m)
