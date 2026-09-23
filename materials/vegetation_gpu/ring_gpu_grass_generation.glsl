#[compute]
#version 450

// Hybrid diagnostic only: CPU owns candidate jitter, hashes, biome chance,
// water, exclusions, priority selection, packing, and installation. This
// kernel resolves the cached mesh-authoritative surface hit for each candidate.
layout(local_size_x = 64) in;
layout(set = 0, binding = 0, std430) readonly buffer Queries { float query[]; };
layout(set = 0, binding = 1, std430) readonly buffer Cells { float cell[]; };
layout(set = 0, binding = 2, std430) writeonly buffer Results { float result[]; };
layout(push_constant, std430) uniform Push { uint query_count; uint unused0; uint unused1; uint unused2; } pc;

const int cell_stride = 64;

float density_at(int base, int x, int y, int z) {
    return cell[base + 2 + x + y * 2 + z * 4];
}

float difference_at(int base, int ax, int ay, int az, int bx, int by, int bz) {
    return density_at(base, ax, ay, az) - density_at(base, bx, by, bz);
}

vec3 gradient_at(int base, vec3 p) {
    float tx = p.x, ty = p.y, tz = p.z;
    vec3 g = vec3(
        difference_at(base,1,0,0,0,0,0)*(1.-ty)*(1.-tz) + difference_at(base,1,1,0,0,1,0)*ty*(1.-tz) + difference_at(base,1,0,1,0,0,1)*(1.-ty)*tz + difference_at(base,1,1,1,0,1,1)*ty*tz,
        difference_at(base,0,1,0,0,0,0)*(1.-tx)*(1.-tz) + difference_at(base,1,1,0,1,0,0)*tx*(1.-tz) + difference_at(base,0,1,1,0,0,1)*(1.-tx)*tz + difference_at(base,1,1,1,1,0,1)*tx*tz,
        difference_at(base,0,0,1,0,0,0)*(1.-tx)*(1.-ty) + difference_at(base,1,0,1,1,0,0)*tx*(1.-ty) + difference_at(base,0,1,1,0,1,0)*(1.-tx)*ty + difference_at(base,1,1,1,1,1,0)*tx*ty);
    float l = length(g);
    return l > 0. ? g / l : vec3(0.);
}

bool height_on_triangle(vec2 point, vec3 a, vec3 b, vec3 c, out float height) {
    float denominator = (b.z-c.z)*(a.x-c.x)+(c.x-b.x)*(a.z-c.z);
    if (abs(denominator) <= 1e-7) { height=0.; return false; }
    float u=((b.z-c.z)*(point.x-c.x)+(c.x-b.x)*(point.y-c.z))/denominator;
    float v=((c.z-a.z)*(point.x-c.x)+(a.x-c.x)*(point.y-c.z))/denominator;
    float w=1.-u-v;
    if (u < -1e-5 || v < -1e-5 || w < -1e-5) { height=0.; return false; }
    height=a.y*u+b.y*v+c.y*w;
    return true;
}

void main() {
    uint id=gl_GlobalInvocationID.x;
    if(id>=pc.query_count)return;
    int q=int(id)*4;
    vec2 point=vec2(query[q],query[q+1]);
    int first=int(query[q+2]);
    int count=int(query[q+3]);
    int result_offset=int(id)*8;
    for(int i=0;i<8;i++)result[result_offset+i]=0.;
    for(int c=0;c<count;c++) {
        int base=(first+c)*cell_stride;
        int triangles=int(cell[base+1]);
        bool found=false;
        float best=-3.402823e38;
        vec3 ba=vec3(0.),bb=vec3(0.),bc=vec3(0.);
        float material=0.;
        for(int t=0;t<triangles;t++) {
            int offset=base+12+t*10;
            vec3 a=vec3(cell[offset],cell[offset+1],cell[offset+2]);
            vec3 b=vec3(cell[offset+3],cell[offset+4],cell[offset+5]);
            vec3 d=vec3(cell[offset+6],cell[offset+7],cell[offset+8]);
            float height;
            if(!height_on_triangle(point,a,b,d,height) || (found && height<=best)) continue;
            found=true;best=height;ba=a;bb=b;bc=d;material=cell[offset+9];
        }
        // The first mixed cell that geometrically covers the query is the
        // resolver's authority. A blocked normal is a miss, not permission to
        // walk down to a cave floor.
        if(!found) continue;
        vec3 normal=cross(bb-ba,bc-ba);
        float normal_length=length(normal);
        if(dot(normal, normal)<=1e-12) return;
        normal/=normal_length;
        vec3 gradient=gradient_at(base,vec3(point.x,best,point.y));
        // CPU Gradient normalizes every nonzero vector before its subsequent
        // length check, so only an exactly zero gradient is blocked here.
        if(dot(gradient, gradient)<=0.) return;
        if(dot(normal,gradient)<0.)normal=-normal;
        if(normal.y<=1e-5)return;
        result[result_offset]=1.; result[result_offset+1]=cell[base]; result[result_offset+2]=best;
        result[result_offset+3]=normal.x;result[result_offset+4]=normal.y;result[result_offset+5]=normal.z;
        result[result_offset+6]=material;result[result_offset+7]=0.;
        return;
    }
}
