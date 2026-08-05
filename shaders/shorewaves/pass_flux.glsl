#[compute]
#version 450

// KP07 central-upwind flux + bed-slope source.
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
}
