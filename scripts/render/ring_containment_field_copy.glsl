#[compute]
#version 450
// Copies the resolved scene colour into the containment field's own sampled texture (RingContainmentFieldComposite.gd): the post-Sunshine raster pass
// draws into the colour image and refracts from this copy. Only the patch's screen rectangle is copied (push constant), the rest is never read.
layout(local_size_x = 8, local_size_y = 8) in;
layout(rgba16f, binding = 0) uniform restrict readonly image2D scene_color;
layout(rgba16f, binding = 1) uniform restrict writeonly image2D scene_copy;
layout(push_constant, std430) uniform Push { ivec4 rect; } pc;   // xy origin, zw size (px)

void main() {
	ivec2 local = ivec2(gl_GlobalInvocationID.xy);
	if (any(greaterThanEqual(local, pc.rect.zw))) return;
	ivec2 p = pc.rect.xy + local;
	if (any(greaterThanEqual(p, imageSize(scene_color)))) return;
	imageStore(scene_copy, p, imageLoad(scene_color, p));
}
