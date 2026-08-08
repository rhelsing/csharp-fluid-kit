shader_type spatial;
render_mode cull_disabled;

// Debug water material: displace by the free surface, tuck dry cells under
// the bed, color by depth + foam.

uniform sampler2D tx_state : filter_linear, repeat_disable;
uniform sampler2D tx_bottom : filter_linear, repeat_disable;
uniform float GRID_RES = 608.0;   // sim cells per side
uniform float tuck_h : hint_range(0.001, 0.2) = 0.004;   // shoreline blend depth (scale with dx)

varying float v_h;
varying float v_foam;

void vertex() {
	// vertex k of GRID_RES sits at fraction k/(GRID_RES-1); texel center k at (k+0.5)/GRID_RES
	vec2 suv = (UV * (GRID_RES - 1.0) + 0.5) / GRID_RES;
	vec4 q = texture(tx_state, suv);
	float B = texture(tx_bottom, suv).r;
	float h = q.r - B;
	VERTEX.y = mix(B - 0.04, q.r, smoothstep(0.0, tuck_h, h));
	v_h = max(h, 0.0);
	v_foam = clamp(q.a / max(h, 0.02), 0.0, 1.0);
}

void fragment() {
	vec3 shallow = vec3(0.20, 0.55, 0.50);
	vec3 deep = vec3(0.03, 0.12, 0.18);
	vec3 col = mix(shallow, deep, clamp(v_h * 0.9, 0.0, 1.0));
	col = mix(col, vec3(0.92), v_foam * 0.9);
	ALBEDO = col;
	ROUGHNESS = mix(0.08, 0.6, v_foam);
	SPECULAR = 0.5;
}
