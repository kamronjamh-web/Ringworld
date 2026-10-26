#[compute]
#version 450
layout(local_size_x=8,local_size_y=8) in;
layout(rgba16f,binding=0) uniform image2D scene_color;
layout(binding=1) uniform sampler2D scene_depth;
layout(binding=2) uniform sampler2D gust_texture;
layout(std140,binding=3) uniform Frame {
 mat4 inverse_projection; mat4 inverse_view;
 vec4 rain; vec4 tint; vec4 options; vec4 anchor; vec4 around; vec4 across; vec4 gust;
 vec4 map_origin; vec4 map_around; vec4 map_across; vec4 map_state;
} frame;
layout(binding=4) uniform sampler2D occ_map;
// Precipitation occlusion map exposure (materials/ring_precip_occlusion.gdshaderinc, same maths): 1 = open sky, 0 = under a roof.
float map_exposure(vec3 world) {
 if(frame.map_state.x<.5) return 1.;
 vec3 rel=world-frame.map_origin.xyz;
 vec3 image_x=frame.map_across.xyz; vec3 image_y=frame.map_around.xyz;
 vec3 up=cross(image_x,image_y);
 float half_extent=frame.map_around.w;
 vec2 uv=vec2(dot(rel,image_x)/(2.*half_extent)+.5,.5-dot(rel,image_y)/(2.*half_extent));
 if(uv.x<0.||uv.y<0.||uv.x>=1.||uv.y>=1.) return 1.;
 float depth_below_plane=-dot(rel,up);
 if(depth_below_plane>frame.map_origin.w) return 1.;
 float surface=texelFetch(occ_map,ivec2(uv*frame.map_state.y),0).r;
 float cover=surface>0.?max(0.,depth_below_plane-surface):0.;
 return 1.-smoothstep(.2,.5,cover);
}
// Metres of the ray inside rain-exposed air: the first 64 m after the near-clear distance are weighted by the map (6 samples), the rest counts fully.
float exposed_path(vec3 direction,float distance,float near_clear) {
 float s0=near_clear; float s1=min(distance,64.);
 float path=0.;
 if(s1>s0) {
  float step_len=(s1-s0)/6.;
  for(int i=0;i<6;i++) {
   float t=s0+(float(i)+.5)*step_len;
   vec3 w=frame.inverse_view[3].xyz+(frame.inverse_view*vec4(direction*t,0.)).xyz;
   path+=map_exposure(w)*step_len;
  }
 }
 return path+max(0.,distance-max(near_clear,64.));
}
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
 float optical_depth=frame.rain.y*frame.rain.x*exposed_path(direction,distance,frame.rain.z)*modulation;
 float opacity=clamp(min(1.-exp(-optical_depth),frame.rain.w*frame.rain.x),0.,.9);
 vec3 tint=frame.tint.rgb*mix(.035,1.,frame.tint.a);
 vec4 color=imageLoad(scene_color,p);
 imageStore(scene_color,p,vec4(mix(color.rgb,tint,opacity),color.a));
}
