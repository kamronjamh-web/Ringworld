#[compute]
#version 460

// Downsample one array-layer slice at a time. RG are the mean gradient, B is
// E(|gradient|^2), and A is foam coverage. Retaining B makes the shader's
// E(g^2)-|E(g)|^2 roughness term survive minification.
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) restrict readonly uniform image2D source_map;
layout(rgba16f, set = 0, binding = 1) restrict writeonly uniform image2D destination_map;

layout(push_constant) restrict readonly uniform PushConstants {
	uint source_size;
};

void main() {
	ivec2 destination = ivec2(gl_GlobalInvocationID.xy);
	ivec2 destination_size = imageSize(destination_map);
	if (any(greaterThanEqual(destination, destination_size))) return;
	ivec2 source = destination * 2;
	ivec2 dimensions = ivec2(int(source_size));
	vec4 a = imageLoad(source_map, source % dimensions);
	vec4 b = imageLoad(source_map, (source + ivec2(1, 0)) % dimensions);
	vec4 c = imageLoad(source_map, (source + ivec2(0, 1)) % dimensions);
	vec4 d = imageLoad(source_map, (source + ivec2(1, 1)) % dimensions);
	imageStore(destination_map, destination, (a + b + c + d) * 0.25);
}
