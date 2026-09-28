#[compute]
#version 450
layout(local_size_x=8, local_size_y=8) in;
layout(binding=0) uniform sampler2D depth_image;
layout(r32f,binding=1) uniform image2D output_image;
layout(std140,binding=2) uniform Camera { mat4 inverse_projection; float coordinate_scale; } camera;
void main() {
 ivec2 p=ivec2(gl_GlobalInvocationID.xy); ivec2 s=imageSize(output_image); if(any(greaterThanEqual(p,s))) return;
 float raw=texelFetch(depth_image,p,0).r;
 if(raw<=0.0 || raw>1.0){ imageStore(output_image,p,vec4(0)); return; }
 vec2 uv=(vec2(p)+.5)/vec2(s); vec4 v=camera.inverse_projection*vec4(uv*2.-1.,raw,1.); float d=length(v.xyz/max(abs(v.w),.000001))/max(camera.coordinate_scale,.0000001);
 imageStore(output_image,p,vec4(isnan(d)||isinf(d)?0.:d,0,0,0));
}
