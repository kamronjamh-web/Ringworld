#[compute]
#version 450
layout(local_size_x=1) in;
layout(binding=0) uniform sampler2D occlusion_map;
// values[0] = cover probe (centre cover, open fraction, mean cover depth, texel count), values[1] = marker,
// values[2 + i] = column sample i: x = depth below the map plane of the highest surface in a 3x3 texel window (0 = nothing drawn), y = 1 when the sample lies inside the map.
layout(std430,binding=1) buffer Result { vec4 values[27]; } result;
layout(std140,binding=2) uniform Params {
 vec4 listener;  // xy = listener texel (continuous), z = listener depth below the map plane (m), w = disc radius (texels)
 vec4 tuning;    // x = headroom (m), y = slope tolerance (m per m), z = texel size (m), w = column sample count
 vec4 columns[13]; // column sample texels (continuous), two per vec4 (xy then zw)
} params;
// One thread, <= 17x17 fetches at 10 Hz. A texel is covered when the first surface below the plane lies more than
// headroom + slope * lateral distance above the listener (so a hillside or kerb next to the listener is not "cover").
// The column samples feed the rain sound field's occlusion-map exposure provider (24 ring columns plus the overhead texel) in the same readback.
void main() {
 ivec2 size=textureSize(occlusion_map,0);
 vec2 centre=params.listener.xy; float radius=params.listener.w; int r=int(ceil(radius));
 float listenerDepth=params.listener.z; float headroom=params.tuning.x; float slope=params.tuning.y; float texel=params.tuning.z;
 float total=0., open=0., coveredSum=0., coveredCount=0., centreCover=0.;
 for(int dy=-r; dy<=r; dy++) for(int dx=-r; dx<=r; dx++){
  float dist=length(vec2(dx,dy)); if(dist>radius+.001) continue;
  ivec2 p=ivec2(floor(centre))+ivec2(dx,dy);
  if(any(lessThan(p,ivec2(0))) || any(greaterThanEqual(p,size))) continue;
  float d=texelFetch(occlusion_map,p,0).r; float cover=0.;
  if(d>0.) cover=listenerDepth-d;
  float threshold=headroom+slope*dist*texel;
  bool covered=cover>threshold;
  total+=1.; if(!covered) open+=1.; else { coveredSum+=cover; coveredCount+=1.; }
  if(dx==0 && dy==0) centreCover=covered?cover:0.;
 }
 result.values[0]=vec4(centreCover, total>0.?open/total:1., coveredCount>0.?coveredSum/coveredCount:0., total);
 result.values[1]=vec4(1.,0.,0.,0.);
 int count=int(params.tuning.w);
 for(int i=0;i<25;i++){
  vec4 sampleValue=vec4(0.);
  if(i<count){
   vec4 pair=params.columns[i/2]; vec2 point=(i%2==0)?pair.xy:pair.zw;
   ivec2 base=ivec2(floor(point));
   if(base.x>=1 && base.y>=1 && base.x<size.x-1 && base.y<size.y-1){
    float best=0.;
    for(int dy=-1;dy<=1;dy++) for(int dx=-1;dx<=1;dx++){
     float d=texelFetch(occlusion_map,base+ivec2(dx,dy),0).r;
     if(d>0. && (best<=0. || d<best)) best=d;
    }
    sampleValue=vec4(best,1.,0.,0.);
   }
  }
  result.values[2+i]=sampleValue;
 }
}
