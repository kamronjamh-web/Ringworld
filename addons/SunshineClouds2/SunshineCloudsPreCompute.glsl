#[compute]
// GenericData ABI: 276 floats; includes cloud mask controls.
#version 450

#include "./CloudsInc.comp"

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(binding = 0) uniform sampler2D depth_image;
layout(r32f, binding = 1) uniform image2D output_depth_image;

layout(binding = 2) uniform uniformBuffer {
	GenericData data;
} genericData;


void main() {
    ivec2 base_uv = ivec2(gl_GlobalInvocationID.xy);
	ivec2 lowres_size = imageSize(output_depth_image);
	// Dispatch dimensions are rounded up to the 8x8 workgroup. Arc drivers in
	// particular expose the undefined writes that occur without this guard.
	if (base_uv.x >= lowres_size.x || base_uv.y >= lowres_size.y) {
		return;
	}

	int resolutionScale = max(1, int(genericData.data.resolutionscale));
	ivec2 size = textureSize(depth_image, 0);

    int adjustedScale = resolutionScale * 2;
    int halfScale = int(floor(float(adjustedScale) * 0.5));
    ivec2 starting_uv = ivec2(floor(vec2(base_uv) * float(resolutionScale)));
    ivec2 current_uv = starting_uv;

    vec2 depthUV = vec2(0.0);

    float furthestDepth = 10000000000000000.0;
    for (int x = 0; x < adjustedScale; x++) {
        for(int y = 0; y < adjustedScale; y++) {
            current_uv = starting_uv + ivec2(x, y);
            if (current_uv.x >= size.x || current_uv.y >= size.y) {
                continue;
            }

            depthUV = vec2((float(current_uv.x) + 0.5) / float(size.x), (float(current_uv.y) + 0.5) / float(size.y));

            furthestDepth = min(texture(depth_image, depthUV).r, furthestDepth);
        }
    }

	imageStore(output_depth_image, base_uv, vec4(furthestDepth, 0.0, 0.0, 0.0));
}
