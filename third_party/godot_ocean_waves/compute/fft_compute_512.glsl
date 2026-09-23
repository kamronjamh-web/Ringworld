#[compute]
#version 460
#define MAX_MAP_SIZE (512U)
#define NUM_SPECTRA (4U)
layout(local_size_x = MAX_MAP_SIZE, local_size_y = 1, local_size_z = 1) in;
layout(std430,set=0,binding=0) restrict readonly buffer ButterflyFactorBuffer { vec4 butterfly[]; };
layout(std430,set=0,binding=1) restrict buffer FFTBuffer { vec2 data[]; };
layout(push_constant) restrict readonly uniform PushConstants { uint cascade_index; };
shared vec2 row_shared[2 * MAX_MAP_SIZE];
vec2 mul_complex(vec2 a, vec2 b) { return vec2(a.x*b.x-a.y*b.y,a.x*b.y+a.y*b.x); }
#define ROW(c,p) row_shared[(p)*MAX_MAP_SIZE+(c)]
void main(){ const uint n=gl_NumWorkGroups.y; const uint stages=findMSB(n); uvec3 id=uvec3(gl_GlobalInvocationID.xy,cascade_index); uint c=id.x,s=gl_GlobalInvocationID.z; uint base=id.z*n*n*NUM_SPECTRA*2; ROW(c,0)=data[base+s*n*n+id.y*n+c]; for(uint st=0;st<stages;++st){barrier();uvec2 ix=uvec2(floatBitsToUint(butterfly[st*n+c].xy));vec2 t=butterfly[st*n+c].zw;ROW(c,(st+1)%2)=ROW(ix.x,st%2)+mul_complex(ROW(ix.y,st%2),t);} data[base+NUM_SPECTRA*n*n+s*n*n+id.y*n+c]=ROW(c,stages%2); }
