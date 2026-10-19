#[compute]
#version 450
layout(local_size_x=8,local_size_y=8) in;
layout(rgba16f,binding=0) uniform image2D scene_color;
layout(binding=1) uniform sampler2D scene_depth;
layout(binding=2) uniform sampler2D far_color;
layout(binding=3) uniform sampler2D regional_color;
layout(push_constant) uniform Params { vec4 options; } params; // x = regional layer present
void main() {
 ivec2 p=ivec2(gl_GlobalInvocationID.xy); ivec2 size=imageSize(scene_color);
 if(any(greaterThanEqual(p,size))) return;
 // Reverse-Z: 0 = nothing was rendered by the main pass (the old canvas-background region).
 if(texelFetch(scene_depth,p,0).r>0.) return;
 vec2 uv=(vec2(p)+.5)/vec2(size);
 vec3 rgb=textureLod(far_color,uv,0.).rgb;
 if(params.options.x>.5) { vec4 r=textureLod(regional_color,uv,0.); rgb=mix(rgb,r.rgb,clamp(r.a,0.,1.)); }
 vec4 color=imageLoad(scene_color,p);
 imageStore(scene_color,p,vec4(rgb,color.a));
}
