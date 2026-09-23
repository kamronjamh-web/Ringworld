shader_type sky;

uniform vec3 sun_direction = vec3(0.4, 0.5, 0.7);
uniform vec3 giant_direction = vec3(-0.35, 0.08, -0.93);
uniform float giant_angular_radius : hint_range(0.01, 1.5, 0.001) = 0.55;
uniform float sky_time = 0.0;
uniform float seed = 1.0;

float hash12(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

float noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash12(i), hash12(i + vec2(1.0, 0.0)), f.x),
		mix(hash12(i + vec2(0.0, 1.0)), hash12(i + vec2(1.0)), f.x), f.y);
}

float turbulence(vec2 p) {
	float n = 0.0;
	float a = 0.5;
	for (int i = 0; i < 4; i++) {
		n += a * noise(p);
		p = p * 2.03 + vec2(17.3, -9.1);
		a *= 0.5;
	}
	return n;
}

void sky() {
	vec3 ray = normalize(EYEDIR);
	vec3 sun = normalize(sun_direction);
	vec3 giant = normalize(giant_direction);
	float night = pow(max(0.0, 1.0 - ray.y), 1.7);
	vec3 color = vec3(0.0006, 0.0010, 0.0021) * (0.35 + night);

	// Stable, sparse point stars: no time term means no distracting twinkle.
	vec2 starUv = vec2(atan(ray.x, ray.z) / 6.2831853 + 0.5, asin(ray.y) / 3.14159265 + 0.5);
	vec2 starCell = floor(starUv * vec2(900.0, 460.0));
	starCell.x = mod(starCell.x, 900.0);
	vec2 local = fract(starUv * vec2(900.0, 460.0)) - 0.5;
	float seedSmall = mod(abs(seed), 4096.0);
	float starHash = hash12(starCell + seedSmall * 0.017);
	if (starHash > 0.992) {
		vec2 offset = vec2(hash12(starCell + 2.3), hash12(starCell + 7.1)) - 0.5;
		float starSize = mix(0.08, 0.20, hash12(starCell + 5.7));
		float point = smoothstep(starSize, starSize * 0.35, length(local - offset * 0.6));
		color += point * mix(vec3(0.45, 0.57, 0.9), vec3(1.0, 0.82, 0.62), hash12(starCell + 12.0)) * (0.45 + starHash);
	}

	float cosRadius = cos(giant_angular_radius);
	float rayDotGiant = dot(ray, giant);
	float disk = smoothstep(cosRadius - fwidth(rayDotGiant) * 1.5, cosRadius + fwidth(rayDotGiant) * 1.5, rayDotGiant);
	if (disk > 0.0) {
		vec3 up = abs(giant.y) > 0.93 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
		vec3 tangent = normalize(cross(up, giant));
		vec3 bitangent = cross(giant, tangent);
		float hitT = rayDotGiant - sqrt(max(0.0, rayDotGiant * rayDotGiant - cosRadius * cosRadius));
		vec3 normal = normalize(hitT * ray - giant);
		float nx = dot(normal, tangent);
		float ny = dot(normal, bitangent);
		float nz = dot(normal, -giant);
		float longitude = atan(nx, nz);
		float latitude = asin(clamp(ny, -1.0, 1.0));
		float flow = sky_time * 0.006 + latitude * 1.6;
		float warp = turbulence(vec2(longitude * 3.0 + flow, latitude * 8.0 - flow * 0.35));
		float bands = sin(latitude * 27.0 + warp * 4.8 + sky_time * 0.003);
		float fine = sin(latitude * 67.0 + warp * 8.0);
		vec3 cream = vec3(0.92, 0.77, 0.56);
		vec3 tan = vec3(0.54, 0.30, 0.17);
		vec3 rust = vec3(0.48, 0.13, 0.07);
		vec3 body = mix(tan, cream, smoothstep(-0.7, 0.75, bands));
		body = mix(body, rust, smoothstep(0.58, 0.92, -bands + fine * 0.18));
		float storm = exp(-((nx + 0.20) * (nx + 0.20) * 42.0 + (ny + 0.18) * (ny + 0.18) * 190.0));
		body = mix(body, vec3(0.76, 0.29, 0.12), storm);
		float light = max(0.0, dot(normal, sun));
		float rim = pow(1.0 - max(0.0, nz), 2.8);
		body *= 0.06 + 0.94 * pow(light, 0.72);
		body += vec3(0.72, 0.48, 0.25) * rim * (0.18 + 0.35 * light);
		color = mix(color, body, disk);
	}
	COLOR = color;
}
