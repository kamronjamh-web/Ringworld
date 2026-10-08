// Portable sampler contract. canonical=(around, canonical vertical, across) metres.
// Unknown samples are neutral for direct lighting, with confidence zero.
vec2 ring_cloud_sun_slice(vec3 canonical, sampler2D field, vec4 p[5], bool regional) {
    float n = regional ? p[2].x : p[1].x;
    float m = regional ? p[2].y : p[1].y;
    float slices = p[1].z;
    vec2 planar;
    if (regional) {
        float delta = mod(canonical.x - p[2].w + p[0].x * 0.5, p[0].x) - p[0].x * 0.5;
        planar = vec2(delta, canonical.z - p[3].x) / p[2].z + vec2(0.5);
        if (any(lessThan(planar, vec2(0.0))) || any(greaterThan(planar, vec2(1.0)))) return vec2(1.0, 0.0);
    } else {
        if (canonical.z < 0.0 || canonical.z > p[0].y) return vec2(1.0, 0.0);
        planar = vec2(fract(canonical.x / p[0].x), canonical.z / p[0].y);
    }
    if (canonical.y < p[0].z || canonical.y > p[0].w) return vec2(1.0, 0.0);
    float layer = clamp((canonical.y - p[0].z) / max(1.0, p[0].w-p[0].z), 0.0, 1.0) * (slices-1.0);
    float lo = floor(layer); float hi = min(lo+1.0, slices-1.0);
    vec2 pixel = planar * vec2(n,m) + vec2(1.0);
    if (regional) pixel.x = clamp(pixel.x, 1.5, n+0.5);
    pixel.y = clamp(pixel.y, 1.5, m+0.5);
    vec2 dims = vec2(n+2.0, (m+2.0)*slices);
    vec2 a = textureLod(field, (pixel+vec2(0.0,lo*(m+2.0)))/dims, 0.0).rg;
    vec2 b = textureLod(field, (pixel+vec2(0.0,hi*(m+2.0)))/dims, 0.0).rg;
    // Atlas R stores visibility (unknown=1), G completion. Unknown filters as neutral.
    vec2 value = mix(a,b,fract(layer));
    return vec2(clamp(value.x,0.0,1.0), clamp(value.y,0.0,1.0));
}
vec2 ring_terrain_cloud_sun_visibility(vec3 canonical, sampler2D global_field, sampler2D regional_field, vec4 p[5]) {
    if (p[0].x <= 0.0 || p[0].y <= 0.0 || p[1].z < 2.0) return vec2(1.0,0.0);
    vec2 global_value = p[3].y > 0.5 ? ring_cloud_sun_slice(canonical,global_field,p,false) : vec2(1.0,0.0);
    vec2 local_value = p[3].z > 0.5 ? ring_cloud_sun_slice(canonical,regional_field,p,true) : vec2(1.0,0.0);
    // Known regional lighting replaces coarse lighting; unknown regional data falls back.
    float confidence = local_value.y + (1.0-local_value.y)*global_value.y;
    float shadow = (1.0-local_value.x) + (1.0-local_value.y)*(1.0-global_value.x);
    return vec2(1.0-clamp(shadow,0.0,1.0)*clamp(p[1].w,0.0,1.0),confidence);
}
