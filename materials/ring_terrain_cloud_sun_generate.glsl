#[compute]
#version 450
layout(local_size_x=8,local_size_y=8,local_size_z=1) in;
layout(rg16f,binding=0) uniform writeonly image2D output_field;
layout(std430,binding=1) readonly buffer GlobalHeights {float heights[];};
layout(std430,binding=2) readonly buffer RegionalHeights {float regional_heights[];};
layout(std140,binding=3) uniform Params {vec4 p[10];};
layout(std430,binding=4) readonly buffer Levels {vec4 levels[];};
// p0 C,width,R,verticaloffset; p1 hmin,hmax,N,M; p2 slices,firstTexel,batch,budget;
// p3 sunxyz,globalmax; p4 regionalcenterxy,extent,regionalheightresolution;
// p5 mode,levelcount,wallbottom,walltop; p6 globalminimum,regionalmax,regionalmin,reserved.
float altitude(vec3 point){return p[0].z-length(point.xz)-p[0].w;}
vec2 canonical(vec3 point){float a=dot(point.xz,point.xz)<1e-8?0.0:atan(point.z,point.x);if(a<0.0)a+=6.28318530718;return vec2(a*p[0].z,point.y);}
float wrapdelta(float a,float center){return mod(a-center+p[0].x*0.5,p[0].x)-p[0].x*0.5;}
float terrain_at(vec2 point){
    vec2 local=vec2(wrapdelta(point.x,p[4].x),point.y-p[4].y)/p[4].z+vec2(0.5);
    if(all(greaterThanEqual(local,vec2(0.0)))&&all(lessThan(local,vec2(1.0)))){
        int n=int(p[4].w);ivec2 cell=clamp(ivec2(floor(local*float(n))),ivec2(0),ivec2(n-1));
        return regional_heights[cell.y*n+cell.x];
    }
    vec4 level=levels[0];int x=clamp(int(floor(mod(point.x,p[0].x)/level.w)),0,int(level.x)-1);
    int y=clamp(int(floor(point.y/level.w)),0,int(level.y)-1);
    return heights[int(level.z)+y*int(level.x)+x];
}
float interval_bound(vec3 a,vec3 b){
    if(min(length(a.xz),length(b.xz))<max(1.0,p[0].z*1e-6))return p[3].w;
    vec2 ca=canonical(a),cb=canonical(b);float delta=wrapdelta(cb.x,ca.x);
    float ar0=min(ca.x,ca.x+delta),ar1=max(ca.x,ca.x+delta);
    float z0=min(ca.y,cb.y),z1=max(ca.y,cb.y);
    float footprint=max(ar1-ar0,z1-z0);int l=0;
    for(int k=0;k<int(p[5].y);k++){l=k;if(levels[k].w>=footprint)break;}
    vec4 level=levels[l];int width=int(level.x),height=int(level.y),offset=int(level.z);
    int x0=int(floor(mod(ar0,p[0].x)/level.w)),x1=int(floor(mod(ar1,p[0].x)/level.w));
    int y0=clamp(int(floor(z0/level.w)),0,height-1),y1=clamp(int(floor(z1/level.w)),0,height-1);
    float bound=-1e30;
    // Include the clipped last circumference cell when a box crosses the seam.
    for(int y=0;y<3;y++)for(int x=0;x<3;x++){
        int ix=x==0?x0:(x==1?(x0+1)%width:x1);
        int iy=y==0?y0:(y==1?min(y0+1,height-1):y1);
        bound=max(bound,heights[offset+clamp(iy,0,height-1)*width+clamp(ix,0,width-1)]);
    }
    float da=wrapdelta(ca.x,p[4].x),db=da+delta;
    if(min(da,db)<=p[4].z*0.5&&max(da,db)>=-p[4].z*0.5
       &&z0<=p[4].y+p[4].z*0.5&&z1>=p[4].y-p[4].z*0.5)bound=max(bound,p[6].y);
    return bound;
}
bool same_cell(vec3 a,vec3 b){
    if(min(length(a.xz),length(b.xz))<max(1.0,p[0].z*1e-6))return false;
    vec2 ca=canonical(a),cb=canonical(b);
    vec2 ra=vec2(wrapdelta(ca.x,p[4].x),ca.y-p[4].y)/p[4].z+vec2(0.5);
    vec2 rb=vec2(wrapdelta(cb.x,p[4].x),cb.y-p[4].y)/p[4].z+vec2(0.5);
    bool ina=all(greaterThanEqual(ra,vec2(0.0)))&&all(lessThan(ra,vec2(1.0)));
    bool inb=all(greaterThanEqual(rb,vec2(0.0)))&&all(lessThan(rb,vec2(1.0)));
    if(ina!=inb)return false;
    if(ina)return all(equal(ivec2(floor(ra*p[4].w)),ivec2(floor(rb*p[4].w))));
    return all(equal(ivec2(floor(ca/levels[0].w)),ivec2(floor(cb/levels[0].w))));
}
vec2 trace_visibility(vec3 origin,vec3 sun){
    // Walls are tested against the untruncated sun ray, not a local radius.
    if(abs(sun.y)>1e-8&&p[5].w>=p[5].z){
        for(int wall=0;wall<2;wall++){float t=((wall==0?0.0:p[0].y)-origin.y)/sun.y;
            if(t>0.01){float h=altitude(origin+sun*t);if(h>=p[5].z&&h<=p[5].w)return vec2(0.0,1.0);}}
    }
    float radius=p[0].z;
    float outer=max(1.0,radius-p[6].x-p[0].w+4.0);
    float aa=dot(sun.xz,sun.xz),bb=dot(origin.xz,sun.xz);
    float cc=dot(origin.xz,origin.xz)-outer*outer;
    float lo=0.01,hi=1e30;
    if(aa>1e-12){float disc=bb*bb-aa*cc;if(disc<0.0)return vec2(1.0,1.0);
        float root=sqrt(max(0.0,disc));lo=max(lo,(-bb-root)/aa);hi=min(hi,(-bb+root)/aa);}
    else if(cc>0.0)return vec2(1.0,1.0);
    if(abs(sun.y)>1e-8){float t0=-origin.y/sun.y,t1=(p[0].y-origin.y)/sun.y;lo=max(lo,min(t0,t1));hi=min(hi,max(t0,t1));}
    else if(origin.y<0.0||origin.y>p[0].y)return vec2(1.0,1.0);
    if(hi<=lo)return vec2(1.0,1.0);
    // Split at closest radial approach so angular spans never jump across the axis.
    vec2 stack[128];int count=0;bool unresolved=false;float apex=aa>1e-12?-bb/aa:lo;
    if(apex>lo&&apex<hi){stack[count++]=vec2(apex,hi);stack[count++]=vec2(lo,apex);}
    else stack[count++]=vec2(lo,hi);
    for(int visit=0;visit<int(p[2].w);visit++){
        if(count==0)return vec2(1.0,unresolved?0.0:1.0);
        vec2 interval=stack[--count];vec3 a=origin+sun*interval.x,b=origin+sun*interval.y;
        float minimum=min(altitude(a),altitude(b));
        if(minimum>p[3].w+2.0)continue;
        float bound=interval_bound(a,b);
        if(minimum>bound+2.0)continue;
        float mid=(interval.x+interval.y)*0.5;vec3 point=origin+sun*mid;
        if(altitude(point)<terrain_at(canonical(point))-2.0)return vec2(0.0,1.0);
        if(same_cell(a,b)&&minimum>terrain_at(canonical(point))+2.0)continue;
        if(interval.y-interval.x<1.0||count>125){unresolved=true;continue;}
        stack[count++]=vec2(mid,interval.y);stack[count++]=vec2(interval.x,mid);
    }
    return count==0&&!unresolved?vec2(1.0,1.0):vec2(1.0,0.0);
}
void main(){
    uint ordinal=gl_GlobalInvocationID.x+gl_GlobalInvocationID.y*uint(gl_NumWorkGroups.x*8u);
    if(ordinal>=uint(p[2].z))return;
    uint index=uint(p[2].y)+ordinal;int n=int(p[1].z),m=int(p[1].w),slices=int(p[2].x);
    int width=n+2,height=(m+2)*slices;if(index>=uint(width*height))return;
    int x=int(index)%width,y=int(index)/width,layer=y/(m+2),local_y=y%(m+2);
    float u=(float(x)-0.5)/float(n),v=clamp((float(local_y)-0.5)/float(m),0.0,1.0);
    float around=p[5].x<0.5?mod(u*p[0].x,p[0].x):p[4].x+(clamp(u,0.0,1.0)-0.5)*p[4].z;
    float across=p[5].x<0.5?v*p[0].y:p[4].y+(v-0.5)*p[4].z;
    if(across<0.0||across>p[0].y){imageStore(output_field,ivec2(x,y),vec4(1.0,0.0,0.0,0.0));return;}
    float altitude_value=mix(p[1].x,p[1].y,float(layer)/float(max(1,slices-1)));
    float angle=around/p[0].z,r=p[0].z-altitude_value-p[0].w;
    vec3 origin=vec3(cos(angle)*r,across,sin(angle)*r);
    vec2 visibility=trace_visibility(origin,normalize(p[3].xyz));
    imageStore(output_field,ivec2(x,y),vec4(visibility,0.0,0.0));
}
