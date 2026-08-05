#[compute]
#version 450

// FUSED KP07 step: flux + bed source + Euler integrate + friction + foam
// in one dispatch. R is consumed only by the computing cell, so fusing
// removes a full-grid read, a barrier, and a dispatch per substep with
// bit-identical results. R is still written for pass_ground (plunge term).
// State Q = (w, hu, hv, hc); w = B + h is the free surface.
// Includes the KP07 positivity-preserving reconstruction correction at
// wet/dry cells: if a reconstructed face value dips below the face bed, the
// slope is shifted so that face is exactly dry and the opposite face
// compensates (cell average preserved). The bed source uses the corrected
// face depths, which is what makes lake-at-rest exact next to the shoreline.
// Every face quantity is computed identically by both adjacent cells, so the
// scheme is conservative bit-exact.

layout(local_size_x = 16, local_size_y = 16, local_size_z = 1) in;

layout(rgba32f, set = 0, binding = 0) uniform restrict readonly image2D img_state;
layout(rgba32f, set = 0, binding = 1) uniform restrict readonly image2D img_bottom;
layout(rgba32f, set = 0, binding = 2) uniform restrict writeonly image2D img_r;
layout(rgba32f, set = 0, binding = 3) uniform restrict writeonly image2D img_state_out;

layout(push_constant) uniform Push {
	float dt;
	float dx;
	float g;
	float theta;
	float kappa4;   // desingularization: kappa^4
	float manning;
	float time;
	float k_foam;
} pc;


const float H_EPS = 1e-6;

float minmod3(float a, float b, float c) {
	float mn = min(a, min(b, c));
	float mx = max(a, max(b, c));
	return mn > 0.0 ? mn : (mx < 0.0 ? mx : 0.0);
}

vec4 slope4(vec4 qm, vec4 q0, vec4 qp) {
	vec4 s;
	s.x = minmod3(pc.theta * (q0.x - qm.x), 0.5 * (qp.x - qm.x), pc.theta * (qp.x - q0.x));
	s.y = minmod3(pc.theta * (q0.y - qm.y), 0.5 * (qp.y - qm.y), pc.theta * (qp.y - q0.y));
	s.z = minmod3(pc.theta * (q0.z - qm.z), 0.5 * (qp.z - qm.z), pc.theta * (qp.z - q0.z));
	s.w = minmod3(pc.theta * (q0.w - qm.w), 0.5 * (qp.w - qm.w), pc.theta * (qp.w - q0.w));
	return s;
}

float desing(float h, float hq, float k4) {
	float h4 = h * h * h * h;
	return 1.41421356237 * h * hq / sqrt(h4 + max(h4, k4));
}

// KP07 positivity correction: corrected w at the two faces of one cell.
// Returns vec2(w_minus_face, w_plus_face), e.g. (W, E) for the x direction.
vec2 correctedW(float w, float sw, float B_minus, float B_plus) {
	float wp = w + 0.5 * sw;
	float wm = w - 0.5 * sw;
	if (wp < B_plus) {
		wp = B_plus;
		wm = 2.0 * w - B_plus;
	}
	if (wm < B_minus) {
		wm = B_minus;
		wp = 2.0 * w - B_minus;
	}
	return vec2(wm, wp);
}

// central-upwind flux across a face; axis 0 = x (normal = u), 1 = y (normal = v)
// wL/wR are the CORRECTED face surface values from the two adjacent cells.
vec4 faceFlux(float wL, vec4 qL, vec4 sL, float wR, vec4 qR, vec4 sR, float Bf, int axis, float k4) {
	float hL = max(wL - Bf, 0.0);
	float hR = max(wR - Bf, 0.0);
	if (hL < H_EPS && hR < H_EPS) {
		return vec4(0.0);
	}
	vec4 UL = qL + 0.5 * sL;
	vec4 UR = qR - 0.5 * sR;
	float uL = desing(hL, UL.y, k4);
	float vL = desing(hL, UL.z, k4);
	float cL = desing(hL, UL.w, k4);
	float uR = desing(hR, UR.y, k4);
	float vR = desing(hR, UR.z, k4);
	float cR = desing(hR, UR.w, k4);
	float nL = (axis == 0) ? uL : vL;
	float nR = (axis == 0) ? uR : vR;
	float aL = sqrt(pc.g * hL);
	float aR = sqrt(pc.g * hR);
	float ap = max(max(nL + aL, nR + aR), 0.0);
	float am = min(min(nL - aL, nR - aR), 0.0);
	float ad = ap - am;
	if (ad < 1e-6) {
		return vec4(0.0);
	}
	float qnL = hL * nL;
	float qnR = hR * nR;
	vec4 FL, FR;
	if (axis == 0) {
		FL = vec4(qnL, qnL * uL + 0.5 * pc.g * hL * hL, qnL * vL, qnL * cL);
		FR = vec4(qnR, qnR * uR + 0.5 * pc.g * hR * hR, qnR * vR, qnR * cR);
	} else {
		FL = vec4(qnL, qnL * uL, qnL * vL + 0.5 * pc.g * hL * hL, qnL * cL);
		FR = vec4(qnR, qnR * uR, qnR * vR + 0.5 * pc.g * hR * hR, qnR * cR);
	}
	vec4 jump = vec4(hR - hL, hR * uR - hL * uL, hR * vR - hL * vL, hR * cR - hL * cL);
	return (ap * FL - am * FR + ap * am * jump) / ad;
}

void main() {
	int N = imageSize(img_state).x;
	ivec2 id = ivec2(gl_GlobalInvocationID.xy);
	if (id.x >= N || id.y >= N) {
		return;
	}
	if (id.x < 2 || id.x > N - 3 || id.y < 2 || id.y > N - 3) {
		imageStore(img_r, id, vec4(0.0));
		imageStore(img_state_out, id, imageLoad(img_state, id));
		return;
	}

	// state stencil
	vec4 q_xmm = imageLoad(img_state, id + ivec2(-2, 0));
	vec4 q_xm  = imageLoad(img_state, id + ivec2(-1, 0));
	vec4 q_0   = imageLoad(img_state, id);
	vec4 q_xp  = imageLoad(img_state, id + ivec2(1, 0));
	vec4 q_xpp = imageLoad(img_state, id + ivec2(2, 0));
	vec4 q_ymm = imageLoad(img_state, id + ivec2(0, -2));
	vec4 q_ym  = imageLoad(img_state, id + ivec2(0, -1));
	vec4 q_yp  = imageLoad(img_state, id + ivec2(0, 1));
	vec4 q_ypp = imageLoad(img_state, id + ivec2(0, 2));

	// bottom stencil (face values: g = east face, b = north face)
	vec4 bot     = imageLoad(img_bottom, id);
	vec4 bot_xm2 = imageLoad(img_bottom, id + ivec2(-2, 0));
	vec4 bot_xm1 = imageLoad(img_bottom, id + ivec2(-1, 0));
	vec4 bot_xp1 = imageLoad(img_bottom, id + ivec2(1, 0));
	vec4 bot_ym2 = imageLoad(img_bottom, id + ivec2(0, -2));
	vec4 bot_ym1 = imageLoad(img_bottom, id + ivec2(0, -1));
	vec4 bot_yp1 = imageLoad(img_bottom, id + ivec2(0, 1));
	float BE_xm2 = bot_xm2.g;
	float BE_xm1 = bot_xm1.g;
	float BE_0   = bot.g;
	float BE_xp1 = bot_xp1.g;
	float BN_ym2 = bot_ym2.b;
	float BN_ym1 = bot_ym1.b;
	float BN_0   = bot.b;
	float BN_yp1 = bot_yp1.b;

	// steep-bed desingularization: where the bed jumps a sizable fraction of
	// the local depth within one cell, sub-cell velocities are meaningless;
	// raise kappa with the face bed-step so thin films there stay quiet.
	float k4_E = pow(max(0.01, 1.5 * abs(bot_xp1.r - bot.r)), 4.0);
	float k4_W = pow(max(0.01, 1.5 * abs(bot.r - bot_xm1.r)), 4.0);
	float k4_N = pow(max(0.01, 1.5 * abs(bot_yp1.r - bot.r)), 4.0);
	float k4_S = pow(max(0.01, 1.5 * abs(bot.r - bot_ym1.r)), 4.0);

	// slopes
	vec4 sxm = slope4(q_xmm, q_xm, q_0);
	vec4 sx0 = slope4(q_xm, q_0, q_xp);
	vec4 sxp = slope4(q_0, q_xp, q_xpp);
	vec4 sym = slope4(q_ymm, q_ym, q_0);
	vec4 sy0 = slope4(q_ym, q_0, q_yp);
	vec4 syp = slope4(q_0, q_yp, q_ypp);

	// corrected face surface values (x: vec2 = (W face, E face); y: (S, N))
	vec2 cw_xm = correctedW(q_xm.x, sxm.x, BE_xm2, BE_xm1);
	vec2 cw_x0 = correctedW(q_0.x, sx0.x, BE_xm1, BE_0);
	vec2 cw_xp = correctedW(q_xp.x, sxp.x, BE_0, BE_xp1);
	vec2 cw_ym = correctedW(q_ym.x, sym.x, BN_ym2, BN_ym1);
	vec2 cw_y0 = correctedW(q_0.x, sy0.x, BN_ym1, BN_0);
	vec2 cw_yp = correctedW(q_yp.x, syp.x, BN_0, BN_yp1);

	vec4 FE = faceFlux(cw_x0.y, q_0, sx0, cw_xp.x, q_xp, sxp, BE_0, 0, k4_E);
	vec4 FW = faceFlux(cw_xm.y, q_xm, sxm, cw_x0.x, q_0, sx0, BE_xm1, 0, k4_W);
	vec4 GN = faceFlux(cw_y0.y, q_0, sy0, cw_yp.x, q_yp, syp, BN_0, 1, k4_N);
	vec4 GS = faceFlux(cw_ym.y, q_ym, sym, cw_y0.x, q_0, sy0, BN_ym1, 1, k4_S);

	// bed-slope source with the corrected face depths of THIS cell:
	// balances the pressure flux exactly for lake-at-rest, including at the
	// shoreline (this pairing is the KP07 well-balancing).
	float hE = max(cw_x0.y - BE_0, 0.0);
	float hW = max(cw_x0.x - BE_xm1, 0.0);
	float hN = max(cw_y0.y - BN_0, 0.0);
	float hS = max(cw_y0.x - BN_ym1, 0.0);
	vec4 S = vec4(
		0.0,
		-pc.g * 0.5 * (hE + hW) * (BE_0 - BE_xm1) / pc.dx,
		-pc.g * 0.5 * (hN + hS) * (BN_0 - BN_ym1) / pc.dx,
		0.0
	);

	vec4 R = -(FE - FW) / pc.dx - (GN - GS) / pc.dx + S;
	imageStore(img_r, id, R);

	// ---- fused integrate stage (identical math to pass_integrate.glsl) ----
	const float H_DRY = 1e-5;
	const float H_FRICTION = 3e-4;
	const float V_CLAMP = 12.0;
	float B = bot.r;
	float k4c = pow(max(0.01, 1.5 * (bot.a - bot.r)), 4.0);
	float h0 = max(q_0.x - B, 0.0);
	float u0 = desing(h0, q_0.y, k4c);
	float v0 = desing(h0, q_0.z, k4c);
	float speed0 = length(vec2(u0, v0));
	float fric = (h0 > H_FRICTION)
		? pc.g * pc.manning * pc.manning * speed0 / pow(max(h0, H_FRICTION), 4.0 / 3.0)
		: 0.0;
	vec4 qn;
	qn.x = q_0.x + pc.dt * R.x;
	qn.y = (q_0.y + pc.dt * R.y) / (1.0 + pc.dt * fric);
	qn.z = (q_0.z + pc.dt * R.z) / (1.0 + pc.dt * fric);
	qn.w = q_0.w + pc.dt * R.w;
	float hn = qn.x - B;
	if (hn < H_DRY) {
		imageStore(img_state_out, id, vec4(B, 0.0, 0.0, 0.0));
		return;
	}
	float un = desing(hn, qn.y, k4c);
	float vn = desing(hn, qn.z, k4c);
	float sp = length(vec2(un, vn));
	if (sp > V_CLAMP) {
		float sc = V_CLAMP / sp;
		un *= sc;
		vn *= sc;
		qn.y = hn * un;
		qn.z = hn * vn;
		sp = V_CLAMP;
	}
	float detadt = R.x;
	float cb = sqrt(pc.g * max(hn, 1e-4));
	float brk = smoothstep(0.35 * cb, 0.65 * cb, detadt);
	float Fr = sp / cb;
	float bore = smoothstep(1.05, 1.35, Fr);
	float swash = step(hn, 0.12) * smoothstep(0.5, 0.9, sp);
	float west_mask = smoothstep(100.0 * pc.dx, 128.0 * pc.dx, (float(id.x) + 0.5) * pc.dx);
	float inject = (2.0 * brk + 0.8 * bore + 1.5 * swash) * hn * west_mask;
	qn.w = clamp(qn.w * exp(-pc.dt / 4.0) + pc.k_foam * inject * pc.dt, 0.0, hn);
	imageStore(img_state_out, id, qn);
}
