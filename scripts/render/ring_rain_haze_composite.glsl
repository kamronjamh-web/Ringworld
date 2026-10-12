#[compute]
#version 450
layout(local_size_x=8,local_size_y=8) in;
layout(rgba16f,binding=0) uniform image2D scene_color;
layout(binding=1) uniform sampler2D scene_depth;
layout(binding=2) uniform sampler2D gust_texture;
layout(std140,binding=3) uniform Frame {
 mat4 inverse_projection; mat4 inverse_view;
 vec4 rain; vec4 tint; vec4 options; vec4 anchor; vec4 around; vec4 across; vec4 gust;
} frame;
void main() {
 ivec2 p=ivec2(gl_GlobalInvocationID.xy); ivec2 size=imageSize(scene_color);
 if(any(greaterThanEqual(p,size))) return;
 vec2 uv=(vec2(p)+.5)/vec2(size);
 float depth=texelFetch(scene_depth,p,0).r;
 vec4 scene=frame.inverse_projection*vec4(uv*2.-1.,depth,1.);
 vec4 ray=frame.inverse_projection*vec4(uv*2.-1.,1.,1.);
 vec3 direction=normalize(ray.xyz/max(ray.w,.000001));
 float distance=frame.options.y;
 if(depth>0. && scene.w>0. && !any(isnan(scene)) && !any(isinf(scene))) distance=min(length(scene.xyz/scene.w),distance);
 float gust=1.;
 if(frame.options.z>.5) {
  vec3 sample_world=frame.inverse_view[3].xyz+(frame.inverse_view*vec4(direction*min(distance*.5,250.),0.)).xyz;
  vec3 delta=sample_world-frame.anchor.xyz;
  vec2 field_uv=frame.gust.xy+vec2(dot(delta,frame.around.xyz)*frame.around.w,dot(delta,frame.across.xyz)*frame.across.w);
  gust=clamp(frame.gust.z+frame.gust.w*textureLod(gust_texture,field_uv,0.).r*1.6,0.,4.);
 }
 float modulation=mix(1.,clamp(.7+gust*.3,.7,1.3),frame.options.x);
 float optical_depth=frame.rain.y*frame.rain.x*max(0.,distance-frame.rain.z)*modulation;
 float opacity=clamp(min(1.-exp(-optical_depth),frame.rain.w*frame.rain.x),0.,.9);
 vec3 tint=frame.tint.rgb*mix(.035,1.,frame.tint.a);
 vec4 color=imageLoad(scene_color,p);
 imageStore(scene_color,p,vec4(mix(color.rgb,tint,opacity),color.a));
}
