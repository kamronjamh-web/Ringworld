shader_type canvas_item;
render_mode blend_premul_alpha;

// The cloud target itself is premultiplied, so preserve RGB and alpha exactly.
void fragment() {
    vec4 cloud = texture(TEXTURE, UV);
    COLOR = vec4(cloud.rgb, cloud.a);
}
