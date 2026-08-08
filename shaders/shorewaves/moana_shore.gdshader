shader_type canvas_item;

// moana_shore — hypothesis C: the scene-210 Moana raymarch, DRIVEN BY KP07.
//
// Forked from ../water-kit/shaders/moana_water_barrel.gdshader (itself a fork of the 204
// Wallis oracle). The shading is untouched — Beer-Lambert teal, IOR fresnel, secondary
// reflection, voronoi white water, fake caustics, wet parallax sand, FBM clouds. ONE thing
// changes: the ocean and the ground stop being analytic and start being the real solver.
//
//   204/210:  water = sin/cos swell + fbm + sdPlane      ground = sdPlane
//   here:     water = pos.y - tx_state.r                 ground = pos.y - tx_bottom.r
//
// WHY THIS EXISTS. Hypothesis E put an SDF carve on top of a MESH ocean, and every problem it
// had came from that seam — a second water material, the layer repainting the whole sea,
// discard logic to contain it, foam that did not match. If the ocean IS the raymarch there is
// no seam: one material, one representation, and a barrel becomes one more SDF term rather
// than a second pass fighting the first.
//
// Still a heightfield (pos.y - surf is single-valued), so this alone does not curl. What it
// buys is that adding the curl later is a term, not an architecture.

uniform sampler2D tx_state : filter_linear, repeat_disable;    // r = w (free surface, m)
uniform sampler2D tx_bottom : filter_linear, repeat_disable;   // r = B (bed, m)
uniform float DOMAIN_SIZE = 92.16;

// CHEAP SHAPE MODE. Skips 204's volume march, refraction, foam and clouds entirely and shades
// the hit with a matte N.L. The point is to get the SHAPE and the scale mapping right before
// paying for the look — every tuned constant in the oracle is in 35 m-bay units and has to be
// remapped to a 92 m shore, and doing that through the full shading is guesswork.
uniform int shade_mode : hint_range(0, 1) = 0;   // 0 = cheap matte, 1 = full 204
uniform int cheap_steps : hint_range(16, 256) = 96;
uniform float cheap_relax : hint_range(0.1, 1.0) = 0.55;
uniform float sea_level : hint_range(-5.0, 5.0) = 0.0;
uniform bool show_ground = true;

// Scene 210 — BARREL CARVE fork of the scene-204 Moana reference.
// Base: "Tropical" volumetric ocean by Chris Wallis (@chriskwallis), ShaderToy,
// vendored verbatim in shaders/moana_water_wallis.gdshader (the style oracle —
// which stays untouched; this file is the experimental fork).
//
// Fork adds ONE idea: a second sdSmoothSubtraction — a tilted, ridged capsule
// ("the tube") carved out of the swell the same way the bay sphere is, so the
// water wraps around a barrel-shaped void. Ridge noise is sampled in the
// capsule's angular frame and spun with TIME (br_spin) — the void is static,
// the surface pours. br_debug ghost-renders the carve solid for placement.
// All br_* uniforms. Since the shadow remap (br_shadow_*) replaced the
// vendor's cyan Beer-Lambert shadow transmission, this fork no longer has a
// pixel-identical-to-204 setting — 204 itself is the untouched reference.
//
// Forced transforms only (Godot shading language; no logic changes):
//  - no structs: Sphere/Material/CameraDescription -> out-params + consts.
//  - ShaderToy keyboard/iChannel0 state hack (LoadConstants/ProcessInput/
//    Encode/DecodeWaterColor/KeyDown/IsInputThread) -> real uniforms with the
//    shader's exact defaults (that machinery only existed because ShaderToy
//    has no uniforms).
//  - iTime -> TIME; iMouse.x camera pivot -> camera_rotation uniform;
//    iFrame loop-unroll guards (min(0,iFrame)) -> 0; fragCoord y flipped.
//  - full-quality path: PERFORMANCE_MODE 0 (ULTRA adds a second volume entry).

// ---- tunables (source LoadConstants defaults; W/S E/D Q/A Y..K keys) ----
uniform float WaterIor = 1.33;             // actual IOR of water
uniform float WaterTurbulence = 2.5;
uniform float WaterAbsorption = 0.028;
uniform vec3 WaterColor = vec3(0.1, 0.82, 1.0);

// Fork: FLY CAMERA — replaces the vendor's const camera + iMouse pivot.
// Defaults reproduce the original framing exactly (pos (0,10,-20) looking +Z).
// Yaw around Y (0 = +Z), pitch up positive, radians; scene drives via WASD.
uniform vec3 cam_pos = vec3(0.0, 10.0, -20.0);
uniform float cam_yaw = 0.0;
uniform float cam_pitch = 0.0;

// ---- TUNING INSTRUMENTATION (user request): hardcoded source constants
// promoted to uniforms. EVERY default equals the original constant — at
// defaults the shader is pixel-identical to the vendored original. Grouped:
// shore shape · waves · white water · sand/wet · caustics.
uniform float or_shore_radius = 35.0;      // carve sphere radius (bay size)
uniform float or_shore_smooth = 12.0;      // sdSmoothSubtraction k (edge feather!)
uniform float or_shore_noise = 5.0;        // fbm on the carve sphere (ragged edge)
uniform float or_ground_smooth = 13.0;     // sdSmoothUnion k (water melts onto sand)
uniform float or_wave_amp_x = 7.0;         // sin swell amplitude
uniform float or_wave_amp_z = 6.0;         // cos swell amplitude
uniform float or_wave_scale = 15.0;        // swell wavelength divisor
uniform float or_ww_height = 5.0;          // whiteWaterMaxHeight (surf zone top)
uniform float or_ww_scale = 1.5;           // froth voronoi position divisor
uniform float or_ww_churn = 2.0;           // downward churn speed (-time * this)
uniform float or_ww_softness = 0.2;        // pow() exponent on froth (lower = denser)
uniform float or_sand_grain = 0.1;         // grain noise amplitude
uniform float or_dune_height = 0.7;        // dune sine amplitude
uniform vec3 or_sand_color = vec3(0.9, 0.9, 0.72);  // 0.9*(1,1,0.8)
uniform float or_wet_distance = 0.7;       // wetSandDistance
uniform float or_wet_fade = 0.2;           // wet band fadeEdge
uniform float or_caustic_intensity = 1.0;  // multiplies the 0.027 base
uniform float or_caustic_scale = 4.0;      // caustic uv divisor
uniform float or_caustic_sharpness = 5.0;  // pow() exponent

// ---- BARREL CARVE (fork additions; br_enabled=0 -> pixel-identical to 204) --
// br_* defaults are an early tuned snapshot only — the CANONICAL preset lives
// in scene_210_barrel_carve.gd (PRESET) and is pushed on load, so it can
// iterate without touching (= recompiling) this file.
uniform float br_enabled = 0.0;            // gate the whole carve
uniform float br_debug = 0.0;              // ghost-render the carve solid
uniform vec3 br_center = vec3(-2.8, 0.0, 50.0);
uniform float br_yaw_deg = 21.6;           // rotate axis around Y (peel angle)
uniform float br_pitch_deg = 0.0;          // tip axis up/down ("comes down at")
uniform float br_radius = 20.0;            // tube bore radius
uniform float br_half_len = 40.0;          // capsule half-length along its axis
uniform float br_smooth = 20.0;            // sdSmoothSubtraction k (lip feather)
uniform float br_ridge_amp = 3.46;         // ridged-fbm depth on the bore wall
uniform float br_ridge_scale = 0.3425;     // ridge noise frequency along axis
uniform float br_ridge_stretch = 2.935;    // elongation around the circumference
uniform float br_spin = -2.4;              // rad/s ridge rotation (the ONE anim)
uniform float br_sand_bounce = 1.0;        // gain on UNDERWATER sand's reflected
                                           // light (the bounce the water tints
                                           // blue); dry beach unaffected. 1=vendor
uniform vec3 br_shadow_color = vec3(0.22, 0.28, 0.38);  // shade water-cast
                                           // shadows push toward (vendor had
                                           // bright-cyan transmission instead)
uniform float br_shadow_intensity = 0.85;  // 0 = shadowless .. 1+ = full shade

// ---- TOP FOAM: two artist layers on the wave surface (the vendor's white
// water is surf-zone-only — strongest at the BEACH, zero at the crests).
// Curl layer masks by slope/overhang (foams lips); churn layer masks by the
// same fbm field that drives turbulence displacement (foam rides the churn).
// Per layer: amount = volume, spread = mask reach, scale = froth size.
uniform float br_curl_amount = 0.7;        // 0 = layer off
uniform float br_curl_spread = 0.45;       // 0 = only overhangs .. 1 = mild slopes
uniform float br_curl_scale = 1.2;         // froth voronoi size
uniform float br_curl_bright = 1.0;        // lit-color gain (vs volume = coverage)
uniform float br_churn_amount = 0.5;       // 0 = layer off
uniform float br_churn_spread = 0.4;       // 0 = only wildest churn .. 1 = most
uniform float br_churn_scale = 1.6;        // froth voronoi size
uniform float br_churn_bright = 1.0;       // lit-color gain
uniform float br_ww_bright = 1.0;          // vendor surf-zone foam lit-color gain

// ---- defines (non-performance path) ----
const int MAX_VOLUME_ENTRIES = 1;
const bool SECONDARY_REFLECTION = true;
const bool ADD_WHITE_WATER = true;
const int MAX_SDF_SPHERE_STEPS = 20;
const float SDF_START_STEP_SIZE = 1.5;
const float SDF_END_STEP_SIZE = 8.0;
const int MAX_VOLUME_MARCH_STEPS = 20;
const int BINARY_SEARCH_STEPS = 6;
const int MAX_OPAQUE_SHADOW_MARCH_STEPS = 10;
const float SHADOW_FACTOR_STEP_SIZE = 7.0;

const float GROUND_LEVEL = 0.0;
const float WATER_LEVEL = 22.0;
const float PI_C = 3.14;
const float LARGE_NUMBER = 1e20;
const float EPSILON = 0.0001;

const int NUM_SPHERES = 5;
const int SAND_FLOOR_OBJECT_ID = 0;
const int CORAL_ROCK_BASE_OBJECT_ID = 1;
const int INVALID_OBJECT_ID = -1;

const float AIR_IOR = 1.0;
const float SKY_AMBIENT_MULTIPLIER = 0.1;
const float MAX_WATER_DISPLACEMENT = 15.0;
const float MIN_REFLECTION_COEFFECIENT = 0.0;

const int SCENE_TYPE_OCEAN = 1;
const int SCENE_TYPE_SIMPLIFIED_OCEAN = 2;

// CameraDescription (struct -> consts)
const vec3 CAMERA_POSITION = vec3(0.0, 10.0, -20.0);
const vec3 CAMERA_LOOKAT = vec3(0.0, 10.0, 0.0);
const float CAMERA_LENS_HEIGHT = 2.0;
const float CAMERA_FOCAL_DISTANCE = 1.6;

// --------------------------------------------
//               Noise Functions
// (Inigo Quilez, Rainforest — shadertoy 4ttSWf)
// --------------------------------------------
float hash1(float n) {
	return fract(n * 17.0 * fract(n * 0.3183099));
}

float noise_iq(in vec3 x) {
	vec3 p = floor(x);
	vec3 w = fract(x);
	vec3 u = w * w * w * (w * (w * 6.0 - 15.0) + 10.0);
	float n = p.x + 317.0 * p.y + 157.0 * p.z;
	float a = hash1(n + 0.0);
	float b = hash1(n + 1.0);
	float c = hash1(n + 317.0);
	float d = hash1(n + 318.0);
	float e = hash1(n + 157.0);
	float f = hash1(n + 158.0);
	float g = hash1(n + 474.0);
	float h = hash1(n + 475.0);
	float k0 = a;
	float k1 = b - a;
	float k2 = c - a;
	float k3 = e - a;
	float k4 = a - b - c + d;
	float k5 = a - c - e + g;
	float k6 = a - b - e + f;
	float k7 = -a + b + c - d + e - f - g + h;
	return -1.0 + 2.0 * (k0 + k1 * u.x + k2 * u.y + k3 * u.z
		+ k4 * u.x * u.y + k5 * u.y * u.z + k6 * u.z * u.x + k7 * u.x * u.y * u.z);
}

const mat3 m3 = mat3(
	vec3(0.00, 0.80, 0.60),
	vec3(-0.80, 0.36, -0.48),
	vec3(-0.60, -0.48, 0.64));

float fbm(in vec3 x, int iterations) {
	float f = 2.0;
	float s = 0.5;
	float a = 0.0;
	float b = 0.5;
	for (int i = 0; i < iterations; i++) {
		float n = noise_iq(x);
		a += b * n;
		b *= s;
		x = f * m3 * x;
	}
	return a;
}

float fbm_4(in vec3 x) {
	return fbm(x, 4);
}

// (iquilezles.org/articles/distfunctions)
float sdPlane(vec3 p) {
	return p.y;
}

float sdSmoothUnion(float d1, float d2, float k) {
	float h = clamp(0.5 + 0.5 * (d2 - d1) / k, 0.0, 1.0);
	return mix(d2, d1, h) - k * h * (1.0 - h);
}

float sdSmoothSubtraction(float d1, float d2, float k) {
	float h = clamp(0.5 - 0.5 * (d2 + d1) / k, 0.0, 1.0);
	return mix(d2, -d1, h) + k * h * (1.0 - h);
}

vec3 sdTranslate(vec3 pos, vec3 translate) {
	return pos - translate;
}

float sdSphere(vec3 p, vec3 origin, float s) {
	p = sdTranslate(p, origin);
	return length(p) - s;
}

// Sphere struct -> out params
void GetSphere(int index, out vec3 origin, out float radius) {
	if (index == 0) { origin = vec3(38.0, GROUND_LEVEL, 32.0); radius = 12.0; }
	else if (index == 1) { origin = vec3(33.0, GROUND_LEVEL - 2.0, 20.0); radius = 8.5; }
	else if (index == 2) { origin = vec3(-25.0, GROUND_LEVEL - 32.0, 55.0); radius = 40.0; }
	else if (index == 3) { origin = vec3(-40.0, GROUND_LEVEL, 25.0); radius = 12.0; }
	else { origin = vec3(45.0, GROUND_LEVEL, 10.0); radius = 12.0; }
}

float GetWaterWavesDisplacement(vec3 position, float time) {
	return or_wave_amp_x * sin(position.x / or_wave_scale + time * 1.3)
		+ or_wave_amp_z * cos(position.z / or_wave_scale + time / 1.1);
}

float GetWaterNoise(vec3 position, float time) {
	return WaterTurbulence * fbm_4(position / 15.0 + time / 3.0);
}

// KP07's real free surface. A heightfield distance, so it OVERESTIMATES on steep faces and
// the march has to be under-relaxed (cheap_relax) — the analytic swell it replaces had the
// same property, which is why the oracle already marches conservatively.
float SampleSurface(vec2 xz) {
	vec2 uv = clamp(xz / DOMAIN_SIZE, vec2(0.0), vec2(1.0));
	float w = texture(tx_state, uv).r;
	float b = texture(tx_bottom, uv).r;
	return max(w, b) + sea_level;
}

float SampleBed(vec2 xz) {
	vec2 uv = clamp(xz / DOMAIN_SIZE, vec2(0.0), vec2(1.0));
	return texture(tx_bottom, uv).r + sea_level;
}

float QueryOceanDistanceField(in vec3 pos, float time) {
	return pos.y - SampleSurface(pos.xz);
}

// Fork: the tube. Capsule along its local X axis, rotated by yaw (around Y)
// then pitch (around Z), with ridged |fbm| sampled in the bore's angular frame
// — cos/sin of the spun angle keep it seamless, br_ridge_stretch elongates
// features around the circumference so they read as water drawn over the lip.
// The void is rotationally static; br_spin only rotates the noise frame.
float BarrelCarveField(vec3 pos, float time) {
	float yaw = radians(br_yaw_deg);
	float pitch = radians(br_pitch_deg);
	vec3 q = pos - br_center;
	q = vec3(cos(yaw) * q.x - sin(yaw) * q.z, q.y, sin(yaw) * q.x + cos(yaw) * q.z);
	q = vec3(cos(pitch) * q.x + sin(pitch) * q.y, -sin(pitch) * q.x + cos(pitch) * q.y, q.z);
	float capsule = length(vec3(max(abs(q.x) - br_half_len, 0.0), q.y, q.z)) - br_radius;
	float ang = atan(q.z, q.y) - time * br_spin;
	vec3 nd = vec3(q.x * br_ridge_scale,
		cos(ang) * br_radius * br_ridge_scale / br_ridge_stretch,
		sin(ang) * br_radius * br_ridge_scale / br_ridge_stretch);
	return capsule + br_ridge_amp * abs(fbm_4(nd));
}

float QueryVolumetricDistanceField(in vec3 pos, float time) {
	float minDist = QueryOceanDistanceField(pos, time);
	// 204 carved a sphere out of its swell to fake a bay. ShoreScenario has a REAL curved
	// shoreline with a sandbar and a Dean profile, so the fake is not just unnecessary, it
	// would fight the bathymetry. Dropped.
	if (br_enabled > 0.5) {
		minDist = sdSmoothSubtraction(BarrelCarveField(pos, time), minDist, br_smooth);
	}
	if (show_ground) {
		minDist = sdSmoothUnion(minDist, pos.y - SampleBed(pos.xz), or_ground_smooth);
	}
	return minDist;
}

float IntersectVolumetric(in vec3 rayOrigin, in vec3 rayDirection, float maxT,
		float time, int sceneType, out bool intersectFound) {
	float t = 0.0;
	float sdfValue = 0.0;
	float stepSize = SDF_START_STEP_SIZE;
	float stepIncrement = (SDF_END_STEP_SIZE - SDF_START_STEP_SIZE) / float(MAX_SDF_SPHERE_STEPS);
	for (int i = 0; i < MAX_SDF_SPHERE_STEPS; i++) {
		sdfValue = sceneType == SCENE_TYPE_OCEAN
			? QueryVolumetricDistanceField(rayOrigin + rayDirection * t, time)
			: QueryOceanDistanceField(rayOrigin + rayDirection * t, time);
		stepSize += stepIncrement;
		if (sdfValue < 0.0 || t > maxT) {
			break;
		}
		t += max(sdfValue, stepSize);
	}

	if (sdfValue < 0.0) {
		float start = 0.0;
		float end = stepSize;
		t -= stepSize;
		for (int j = 0; j < BINARY_SEARCH_STEPS; j++) {
			float midPoint = (start + end) * 0.5;
			vec3 nextMarchPosition = rayOrigin + (t + midPoint) * rayDirection;
			float sdfValue2 = (sceneType == SCENE_TYPE_OCEAN)
				? QueryVolumetricDistanceField(nextMarchPosition, time)
				: QueryOceanDistanceField(nextMarchPosition, time);
			// Use the SDF to nudge the mid point closer to the actual edge
			midPoint = clamp(midPoint + sdfValue2, start, end);
			if (sdfValue2 < 0.0) {
				end = midPoint;
			} else {
				start = midPoint;
			}
		}
		t += end;
	}

	intersectFound = t < maxT && sdfValue < 0.0;
	return t;
}

// (iquilezles.org/articles/normalsSDF)
vec3 GetVolumeNormal(in vec3 pos, float time, int sceneType) {
	vec3 n = vec3(0.0);
	for (int i = 0; i < 4; i++) {
		vec3 e = 0.5773 * (2.0 * vec3(float(((i + 3) >> 1) & 1), float((i >> 1) & 1), float(i & 1)) - 1.0);
		n += e * ((sceneType == SCENE_TYPE_OCEAN)
			? QueryVolumetricDistanceField(pos + 0.5 * e, time)
			: QueryOceanDistanceField(pos + 0.5 * e, time));
	}
	return normalize(n);
}

// Material struct -> out params
void GetMaterial(int objectID, out vec3 albedo, out float shininess) {
	if (objectID == SAND_FLOOR_OBJECT_ID) {
		albedo = or_sand_color;
		shininess = 50.0;
	} else {   // it's coral
		albedo = vec3(0.3, 0.4, 0.2);
		shininess = 3.0;
	}
}

// (scratchapixel ray-plane)
float PlaneIntersection(vec3 rayOrigin, vec3 rayDirection, vec3 planeOrigin, vec3 planeNormal) {
	float t = -1.0;
	float denom = dot(-planeNormal, rayDirection);
	if (denom > EPSILON) {
		vec3 rayToPlane = planeOrigin - rayOrigin;
		return dot(rayToPlane, -planeNormal) / denom;
	}
	return t;
}

float SphereIntersection(in vec3 rayOrigin, in vec3 rayDirection,
		in vec3 sphereCenter, in float sphereRadius) {
	vec3 eMinusC = rayOrigin - sphereCenter;
	float dDotD = dot(rayDirection, rayDirection);
	float discriminant = dot(rayDirection, eMinusC) * dot(rayDirection, eMinusC)
		- dDotD * (dot(eMinusC, eMinusC) - sphereRadius * sphereRadius);
	if (discriminant < 0.0) {
		return -1.0;
	}
	float firstIntersect = (dot(-rayDirection, eMinusC) - sqrt(discriminant)) / dDotD;
	return firstIntersect;
}

// NOTE: source declares objectID `out`; strict GLSL leaves unassigned `out`
// params undefined and Godot/Metal really does trash them (sand became sky).
// `inout` preserves the ShaderToy-observed behavior — a portability fix, not a
// logic change.
void UpdateIfIntersected(inout float t, in float intersectionT,
		in int intersectionObjectID, inout int objectID) {
	if (intersectionT > EPSILON && intersectionT < t) {
		objectID = intersectionObjectID;
		t = intersectionT;
	}
}

float SandHeightMap(vec3 position) {
	float sandGrainNoise = or_sand_grain * fbm(position * 10.0, 2);
	float sandDuneDisplacement = or_dune_height * sin(10.0 * fbm_4(10.0 + position / 40.0));
	return sandGrainNoise + sandDuneDisplacement;
}

float QueryOpaqueDistanceField(vec3 position, int objectID) {
	if (objectID == SAND_FLOOR_OBJECT_ID) {
		return sdPlane(position) + SandHeightMap(position);
	} else {
		vec3 origin;
		float radius;
		GetSphere(objectID - CORAL_ROCK_BASE_OBJECT_ID, origin, radius);
		return sdSphere(position, origin, radius) + fbm_4(position);
	}
}

vec3 GetOpaqueNormal(in vec3 pos, int objectID) {
	vec3 n = vec3(0.0);
	for (int i = 0; i < 4; i++) {
		vec3 e = 0.5773 * (2.0 * vec3(float(((i + 3) >> 1) & 1), float((i >> 1) & 1), float(i & 1)) - 1.0);
		n += e * QueryOpaqueDistanceField(pos + 0.5 * e, objectID);
	}
	return normalize(n);
}

float IntersectOpaqueScene(in vec3 rayOrigin, in vec3 rayDirection, out int objectID) {
	float t = LARGE_NUMBER;
	objectID = INVALID_OBJECT_ID;

	for (int i = 0; i < NUM_SPHERES; i++) {
		vec3 origin;
		float radius;
		GetSphere(i, origin, radius);
		UpdateIfIntersected(t,
			SphereIntersection(rayOrigin, rayDirection, origin, radius),
			CORAL_ROCK_BASE_OBJECT_ID + i, objectID);
	}

	UpdateIfIntersected(t,
		PlaneIntersection(rayOrigin, rayDirection, vec3(0.0, GROUND_LEVEL, 0.0), vec3(0.0, 1.0, 0.0)),
		SAND_FLOOR_OBJECT_ID, objectID);

	// Optimization for early-out on volume intersections
	UpdateIfIntersected(t,
		PlaneIntersection(rayOrigin, rayDirection, vec3(0.0, WATER_LEVEL + MAX_WATER_DISPLACEMENT, 0.0), vec3(0.0, 1.0, 0.0)),
		INVALID_OBJECT_ID, objectID);

	return t;
}

float Specular(in vec3 reflection, in vec3 lightDirection, float shininess) {
	return 0.05 * pow(max(0.0, dot(reflection, lightDirection)), shininess);
}

vec3 BeerLambert(vec3 absorptionv, float dist) {
	return exp(-absorptionv * dist);
}

vec3 GetShadowFactor(in vec3 rayOrigin, in vec3 rayDirection, in int maxSteps,
		in float minMarchSize, float time) {
	float t = 0.0;
	vec3 shadowFactor = vec3(1.0);
	float signedDistance = 0.0;
	bool enteredVolume = false;
	for (int i = 0; i < maxSteps; i++) {
		float marchSize = max(minMarchSize, abs(signedDistance));
		t += marchSize;
		vec3 position = rayOrigin + t * rayDirection;
		signedDistance = QueryVolumetricDistanceField(position, time);
		if (signedDistance < 0.0) {
			// Soften the shadows towards the edges to simulate an area light
			float softEdgeMultiplier = min(abs(signedDistance / 5.0), 1.0);
			shadowFactor *= BeerLambert(WaterAbsorption * softEdgeMultiplier / WaterColor, marchSize);
			enteredVolume = true;
		} else if (enteredVolume) {
			// The water isn't concave: entered once and exiting -> done
			break;
		}
	}
	// Fork: artist shadow. The physical transmission above goes bright cyan
	// when sun filters through the swell (red dies ~10x faster) — that's what
	// washed the bay floor blue. Keep it only as an occlusion probe (red
	// channel = fastest-absorbed = best thickness proxy, same one the vendor
	// caustics use) and push the shadow toward a chosen shade instead.
	float occlusion = clamp((1.0 - shadowFactor.r) * br_shadow_intensity, 0.0, 1.0);
	return mix(vec3(1.0), br_shadow_color, occlusion);
}

float GetApproximateIntersect(vec3 position, vec3 rayDirection) {
	float distanceToPlane;
	// Special case for rays parallel to the ground plane
	if (abs(rayDirection.y) < 0.01) {
		distanceToPlane = LARGE_NUMBER;
	} else if (position.y < GROUND_LEVEL || position.y > WATER_LEVEL) {
		distanceToPlane = 0.0;
	} else if (rayDirection.y > 0.0) {
		distanceToPlane = (WATER_LEVEL - position.y) / rayDirection.y;
		distanceToPlane = max(0.0, distanceToPlane);
	} else {
		distanceToPlane = (position.y - GROUND_LEVEL) / abs(rayDirection.y);
	}
	return distanceToPlane;
}

vec3 GetApproximateShadowFactor(vec3 position, vec3 rayDirection) {
	float distanceToPlane = GetApproximateIntersect(position, rayDirection);
	return BeerLambert(WaterAbsorption / WaterColor, distanceToPlane);
}

float smoothVoronoi(in vec2 x) {
	ivec2 p = ivec2(floor(x));
	vec2 f = fract(x);
	float res = 0.0;
	for (int j = -1; j <= 1; j++) {
		for (int i = -1; i <= 1; i++) {
			ivec2 b = ivec2(i, j);
			vec2 r = vec2(b) - f + noise_iq(vec3(vec2(p + b), 0.0));
			float d = length(r);
			res += exp(-32.0 * d);
		}
	}
	return -(1.0 / 32.0) * log(res);
}

vec3 GetSunLightDirection() {
	return normalize(vec3(0.3, 1.0, 1.65));
}

vec3 GetSunLightColor() {
	return 0.9 * vec3(0.9, 0.75, 0.7);
}

vec3 GetBaseSkyColor(vec3 rayDirection) {
	return mix(vec3(0.2, 0.5, 0.8), vec3(0.7, 0.75, 0.9), max(rayDirection.y, 0.0));
}

vec3 GetAmbientSkyColor() {
	return SKY_AMBIENT_MULTIPLIER * GetBaseSkyColor(vec3(0.0, 1.0, 0.0));
}

vec3 GetAmbientShadowColor() {
	return vec3(0.0, 0.0, 0.2);
}

float GetCloudDenity(vec3 position, float time) {
	float t = time * 0.25;
	vec3 noisePosition = position + vec3(0.0, 0.0, t);
	float n = fbm_4(noisePosition);
	float noiseCutoff = -0.3;
	return max(0.0, 3.0 * (n - noiseCutoff));
}

vec4 GetCloudColor(vec3 position, float time) {
	float cloudDensity = GetCloudDenity(position, time);
	vec3 cloudAlbedo = vec3(1.0, 1.0, 1.0);
	float cloudAbsorption = 0.6;
	float marchSize = 0.25;

	vec3 lightFactor = vec3(1.0, 1.0, 1.0);
	{
		vec3 marchPosition = position;
		int selfShadowSteps = 4;
		for (int i = 0; i < selfShadowSteps; i++) {
			marchPosition += GetSunLightDirection() * marchSize;
			float density = cloudAbsorption * GetCloudDenity(marchPosition, time);
			lightFactor *= BeerLambert(vec3(density, density, density), marchSize);
		}
	}

	return vec4(
		cloudAlbedo * (mix(GetAmbientShadowColor(), 1.3 * GetSunLightColor(), lightFactor)
			+ GetAmbientSkyColor()),
		min(cloudDensity, 1.0));
}

vec3 GetSkyColor(in vec3 rayDirection, float time) {
	vec3 skyColor = GetBaseSkyColor(rayDirection);
	vec4 cloudColor = GetCloudColor(rayDirection * 4.0, time);
	skyColor = mix(skyColor, cloudColor.rgb, cloudColor.a);
	return skyColor;
}

float FresnelFactor(float CurrentIOR, float NewIOR, vec3 Normal, vec3 RayDirection) {
	float ReflectionCoefficient =
		((CurrentIOR - NewIOR) / (CurrentIOR + NewIOR))
		* ((CurrentIOR - NewIOR) / (CurrentIOR + NewIOR));
	return clamp(
		ReflectionCoefficient + (1.0 - ReflectionCoefficient) * pow(1.0 - dot(Normal, -RayDirection), 5.0),
		MIN_REFLECTION_COEFFECIENT, 1.0);
}

vec3 SandParallaxOcclusionMapping(vec3 position, vec3 view) {
	int pomCount = 6;
	float marchSize = 0.3;
	for (int i = 0; i < pomCount; i++) {
		if (position.y < GROUND_LEVEL - SandHeightMap(position)) {
			break;
		}
		position += view * marchSize;
	}
	return position;
}

void CalculateLighting(vec3 position, vec3 view, int objectID, inout vec3 color,
		bool useFastLighting, float time) {
	vec3 albedo;
	float shininess;
	GetMaterial(objectID, albedo, shininess);
	float sdfValue = QueryVolumetricDistanceField(position, time);
	bool bUnderWater = sdfValue < 0.0;

	float wetnessFactor = 0.0;
	if (objectID == SAND_FLOOR_OBJECT_ID && !useFastLighting) {
		float wetSandDistance = or_wet_distance;
		if (sdfValue <= wetSandDistance) {
			// Darken the sand albedo to make it look wet
			float fadeEdge = or_wet_fade;
			wetnessFactor = 1.0 - max(0.0, (sdfValue - (wetSandDistance - fadeEdge)) / fadeEdge);
			albedo *= albedo * mix(1.0, 0.5, wetnessFactor);
		}
		position = SandParallaxOcclusionMapping(position, view);
	}

	vec3 normal = GetOpaqueNormal(position, objectID);
	vec3 reflectionDirection = reflect(view, normal);

	int shadowObjectID = INVALID_OBJECT_ID;
	if (!useFastLighting) {
		IntersectOpaqueScene(position, GetSunLightDirection(), shadowObjectID);
	}

	vec3 shadowFactor = vec3(0.0, 0.0, 0.0);
	if (shadowObjectID == INVALID_OBJECT_ID) {
		shadowFactor = useFastLighting
			? GetApproximateShadowFactor(position, GetSunLightDirection())
			: GetShadowFactor(position, GetSunLightDirection(), MAX_OPAQUE_SHADOW_MARCH_STEPS, SHADOW_FACTOR_STEP_SIZE, time);

		// Small back splash of the sky ambient color to fake a bit of gi
		color += shadowFactor * albedo
			* mix(0.4 * GetAmbientShadowColor(), GetSunLightColor(), max(0.0, dot(normal, GetSunLightDirection())));

		color += shadowFactor * GetSunLightColor() * Specular(reflectionDirection, GetSunLightDirection(), shininess);

		if (!useFastLighting) {
			// Fake caustics
			float waterNoise = fract(GetWaterNoise(position, time));
			float causticMultiplier = bUnderWater ? 7.0 : (1.0 - shadowFactor.r);
			color += albedo * causticMultiplier * 0.027 * or_caustic_intensity * pow(
				smoothVoronoi(position.xz / or_caustic_scale
					+ vec2(time, time + 3.0)
					+ 3.0 * vec2(cos(waterNoise), sin(waterNoise))), or_caustic_sharpness);
		}
	}

	// A bit of reflection on wet sand — leftover water film
	if (!useFastLighting && wetnessFactor > 0.0) {
		// Water fills the sand holes -> planar surface, normal straight up
		vec3 wetNormal = vec3(0.0, 1.0, 0.0);
		vec3 wetReflection = reflect(view, wetNormal);
		float fresnel = FresnelFactor(AIR_IOR, WaterIor, wetNormal, view);
		color += shadowFactor * wetnessFactor * fresnel * GetSkyColor(wetReflection, time);
	}

	color += GetAmbientSkyColor() * albedo;

	// Fork: gain on what submerged sand bounces back toward the eye — the
	// light the water column then tints teal. Dry beach (sdf >= 0) untouched.
	if (objectID == SAND_FLOOR_OBJECT_ID && bUnderWater) {
		color *= br_sand_bounce;
	}
}

vec3 Render(in vec3 rayOrigin, in vec3 rayDirection, float time) {
	vec3 accumulatedColor = vec3(0.0);
	vec3 accumulatedColorMultiplier = vec3(1.0);

	// Local copy: the white-water pass blends the IOR near the shore
	float waterIor = WaterIor;

	int materialID = INVALID_OBJECT_ID;
	float t = IntersectOpaqueScene(rayOrigin, rayDirection, materialID);
	vec3 opaquePosition = rayOrigin + t * rayDirection;

	bool outsideVolume = true;
	for (int entry = 0; entry < MAX_VOLUME_ENTRIES; entry++) {
		if (!outsideVolume) {
			break;
		}
		bool firstEntry = (entry == 0);
		bool intersectFound = false;
		float volumeStart = IntersectVolumetric(rayOrigin, rayDirection, t, time,
			(firstEntry ? SCENE_TYPE_OCEAN : SCENE_TYPE_SIMPLIFIED_OCEAN), intersectFound);

		if (!intersectFound) {
			break;
		} else {
			outsideVolume = false;
			rayOrigin = rayOrigin + rayDirection * volumeStart;
			vec3 volumeNormal = GetVolumeNormal(rayOrigin, time, SCENE_TYPE_OCEAN);
			vec3 reflection = reflect(rayDirection, volumeNormal);
			float fresnelFactor = FresnelFactor(AIR_IOR, waterIor, volumeNormal, rayDirection);
			float waterShininess = 100.0;

			float whiteWaterFactor = 0.0;
			float whiteWaterMaxHeight = or_ww_height;
			float groundBlendFactor = min(1.0, (rayOrigin.y - GROUND_LEVEL) * 0.75);
			if (ADD_WHITE_WATER && firstEntry && rayOrigin.y <= whiteWaterMaxHeight) {
				waterIor = mix(1.0, waterIor, groundBlendFactor);

				vec3 voronoisePosition = rayOrigin / or_ww_scale + vec3(0.0, -time * or_ww_churn, sin(time));
				float noiseValue = abs(fbm(voronoisePosition, 2));
				voronoisePosition += 1.0 * vec3(cos(noiseValue), 0.0, sin(noiseValue));

				float heightLerp = (whiteWaterMaxHeight - rayOrigin.y) / whiteWaterMaxHeight;
				whiteWaterFactor = abs(smoothVoronoi(voronoisePosition.xz)) * heightLerp;
				whiteWaterFactor = clamp(whiteWaterFactor, 0.0, 1.0);
				whiteWaterFactor = pow(whiteWaterFactor, or_ww_softness) * heightLerp;
				whiteWaterFactor *= mix(abs(fbm(rayOrigin + vec3(0.0, -time * 5.0, 0.0), 2)), 1.0, heightLerp);
				whiteWaterFactor *= groundBlendFactor;

				vec3 wwShadowFactor = GetShadowFactor(rayOrigin, GetSunLightDirection(), MAX_OPAQUE_SHADOW_MARCH_STEPS, SHADOW_FACTOR_STEP_SIZE, time);
				vec3 diffuse = 0.5 * wwShadowFactor * GetSunLightColor()
					+ 0.7 * wwShadowFactor * mix(GetAmbientShadowColor(), GetSunLightColor(), max(0.0, dot(volumeNormal, GetSunLightDirection())));
				accumulatedColor += vec3(whiteWaterFactor) * br_ww_bright * (
					diffuse
					+ wwShadowFactor * Specular(reflection, GetSunLightDirection(), 30.0) * GetSunLightColor()
					+ GetAmbientSkyColor());
			}
			accumulatedColorMultiplier *= (1.0 - whiteWaterFactor);

			// Fork: TOP FOAM — the two artist layers, no height gate (the
			// vendor pass above only foams near the beach). Voronoi is
			// sampled with height sheared into xz so vertical curl faces
			// don't streak; froth pow(0.35) densifies the fine web.
			if (firstEntry && (br_curl_amount > 0.001 || br_churn_amount > 0.001)) {
				float steep = 1.0 - volumeNormal.y;   // 0 flat .. 2 inverted lip
				float curlEdge = mix(1.35, 0.3, br_curl_spread);
				vec3 cvp = rayOrigin / br_curl_scale + vec3(0.0, -time * or_ww_churn * 0.5, 0.0);
				float curlFroth = pow(clamp(abs(smoothVoronoi(
					vec2(cvp.x + 0.9 * cvp.y, cvp.z - 0.6 * cvp.y))), 0.0, 1.0), 0.35);
				float curlFoam = br_curl_amount * curlFroth
					* smoothstep(curlEdge - 0.3, curlEdge + 0.3, steep);

				// same field GetWaterNoise displaces with, amplitude-free
				float churnField = abs(fbm_4(rayOrigin / 15.0 + time / 3.0));
				float churnEdge = mix(0.8, 0.12, br_churn_spread);
				vec3 bvp = rayOrigin / br_churn_scale + vec3(0.0, -time * or_ww_churn, 0.0);
				float churnFroth = pow(clamp(abs(smoothVoronoi(
					vec2(bvp.x + 0.9 * bvp.y, bvp.z - 0.6 * bvp.y))), 0.0, 1.0), 0.35);
				float churnFoam = br_churn_amount * churnFroth
					* smoothstep(churnEdge - 0.15, churnEdge + 0.15, churnField);

				float foamSum = curlFoam + churnFoam;
				float topFoam = clamp(foamSum, 0.0, 0.95);
				if (topFoam > 0.004) {
					// per-layer brightness, weighted by each layer's share
					float foamBright = (curlFoam * br_curl_bright
						+ churnFoam * br_churn_bright) / foamSum;
					vec3 tfShadow = GetShadowFactor(rayOrigin, GetSunLightDirection(),
						MAX_OPAQUE_SHADOW_MARCH_STEPS, SHADOW_FACTOR_STEP_SIZE, time);
					vec3 tfDiffuse = 0.5 * tfShadow * GetSunLightColor()
						+ 0.7 * tfShadow * mix(GetAmbientShadowColor(), GetSunLightColor(),
							max(0.0, dot(volumeNormal, GetSunLightDirection())));
					accumulatedColor += accumulatedColorMultiplier * topFoam * foamBright
						* (tfDiffuse + GetAmbientSkyColor());
					accumulatedColorMultiplier *= (1.0 - topFoam);
				}
			}

			rayDirection = refract(rayDirection, volumeNormal, AIR_IOR / waterIor);

			accumulatedColor += accumulatedColorMultiplier * Specular(reflection, GetSunLightDirection(), waterShininess) * GetSunLightColor();
			accumulatedColor += accumulatedColorMultiplier * fresnelFactor * GetSkyColor(reflection, time);
			accumulatedColorMultiplier *= (1.0 - fresnelFactor);

			// recalculate opaque depth now that the ray has been refracted
			t = IntersectOpaqueScene(rayOrigin, rayDirection, materialID);
			if (materialID != INVALID_OBJECT_ID) {
				opaquePosition = rayOrigin + t * rayDirection;
			}

			float volumeDepth = 0.0;
			float signedDistance = 0.0;
			vec3 marchPosition = vec3(0.0);
			float minStepSize = SDF_START_STEP_SIZE;
			float minStepIncrement = (SDF_END_STEP_SIZE - SDF_START_STEP_SIZE) / float(MAX_VOLUME_MARCH_STEPS);
			for (int i = 0; i < MAX_VOLUME_MARCH_STEPS; i++) {
				float marchSize = max(minStepSize, signedDistance);
				minStepSize += minStepIncrement;

				vec3 nextMarchPosition = rayOrigin + (volumeDepth + marchSize) * rayDirection;
				signedDistance = QueryOceanDistanceField(nextMarchPosition, time);
				if (signedDistance > 0.0) {
					float start = 0.0;
					float end = marchSize;
					for (int j = 0; j < BINARY_SEARCH_STEPS; j++) {
						float midPoint = (start + end) * 0.5;
						vec3 bsPosition = rayOrigin + (volumeDepth + midPoint) * rayDirection;
						float sdfValue = QueryVolumetricDistanceField(bsPosition, time);
						// Use the SDF to nudge the mid point closer to the actual edge
						midPoint = clamp(midPoint - sdfValue, start, end);
						if (sdfValue > 0.0) {
							end = midPoint;
						} else {
							start = midPoint;
						}
					}
					marchSize = end;
				}

				volumeDepth += marchSize;
				marchPosition = rayOrigin + volumeDepth * rayDirection;

				if (volumeDepth > t) {
					intersectFound = true;
					volumeDepth = min(volumeDepth, t);
					break;
				}

				vec3 previousLightFactor = accumulatedColorMultiplier;
				accumulatedColorMultiplier *= BeerLambert(vec3(WaterAbsorption) / WaterColor, marchSize);
				vec3 absorptionFromMarch = previousLightFactor - accumulatedColorMultiplier;

				accumulatedColor += accumulatedColorMultiplier * WaterColor * absorptionFromMarch
					* GetSunLightColor() * GetApproximateShadowFactor(marchPosition, GetSunLightDirection());
				accumulatedColor += accumulatedColorMultiplier * absorptionFromMarch * GetAmbientSkyColor();

				if (signedDistance > 0.0) {
					intersectFound = true;
					outsideVolume = true;
					break;
				}
			}

			if (intersectFound && outsideVolume) {
				// Flip the normal since we're coming from inside the volume
				vec3 exitNormal = -GetVolumeNormal(marchPosition, time, SCENE_TYPE_SIMPLIFIED_OCEAN);

				if (SECONDARY_REFLECTION) {
					float exitFresnel = max(0.2, FresnelFactor(waterIor, AIR_IOR, exitNormal, rayDirection));
					vec3 exitReflection = reflect(rayDirection, exitNormal);
					int reflectedMaterialID;
					float reflectionT = IntersectOpaqueScene(marchPosition, exitReflection, reflectedMaterialID);
					if (reflectedMaterialID != INVALID_OBJECT_ID) {
						vec3 pos = marchPosition + exitReflection * reflectionT;
						vec3 color = vec3(0.0);
						CalculateLighting(pos, exitReflection, reflectedMaterialID, color, true, time);
						accumulatedColor += accumulatedColorMultiplier * exitFresnel * color;
					} else {
						accumulatedColor += exitFresnel * accumulatedColorMultiplier * GetSkyColor(rayDirection, time);
					}
					accumulatedColorMultiplier *= (1.0 - exitFresnel);
				}

				rayDirection = refract(rayDirection, exitNormal, waterIor / AIR_IOR);
				rayOrigin = marchPosition;
				t = IntersectOpaqueScene(marchPosition, rayDirection, materialID);
				if (materialID != INVALID_OBJECT_ID) {
					opaquePosition = rayOrigin + t * rayDirection;
				}
				outsideVolume = true;
			}

			if (!intersectFound) {
				float approxT = GetApproximateIntersect(marchPosition, rayDirection);
				float halfT = approxT / 2.0;
				vec3 halfwayPosition = marchPosition + rayDirection * halfT;
				vec3 shadowFactor = GetApproximateShadowFactor(halfwayPosition, GetSunLightDirection());

				vec3 previousLightFactor = accumulatedColorMultiplier;
				accumulatedColorMultiplier *= BeerLambert(WaterAbsorption / WaterColor, approxT);
				vec3 absorptionFromMarch = previousLightFactor - accumulatedColorMultiplier;
				accumulatedColor += accumulatedColorMultiplier * WaterColor * shadowFactor * absorptionFromMarch * GetSunLightColor();
				accumulatedColor += accumulatedColorMultiplier * WaterColor * GetAmbientSkyColor() * absorptionFromMarch;

				volumeDepth += approxT;
				rayOrigin = rayOrigin + volumeDepth * rayDirection;
			}
		}
	}

	vec3 opaqueColor = vec3(0.0);
	if (materialID != INVALID_OBJECT_ID) {
		CalculateLighting(opaquePosition, rayDirection, materialID, opaqueColor, false, time);
	} else {
		opaqueColor = GetSkyColor(rayDirection, time);
	}

	return accumulatedColor + accumulatedColorMultiplier * opaqueColor;
}

vec3 GammaCorrect(vec3 color) {
	return pow(color, vec3(1.0 / 2.2));
}

void fragment() {
	// mainImage: fragCoord/iResolution with ShaderToy's bottom-left origin.
	vec2 resolution = 1.0 / SCREEN_PIXEL_SIZE;
	vec2 uv = vec2(SCREEN_UV.x, 1.0 - SCREEN_UV.y);

	float aspectRatio = resolution.x / resolution.y;
	float lensWidth = CAMERA_LENS_HEIGHT * aspectRatio;

	// Fork: fly camera — same lens model, basis built from yaw/pitch uniforms
	// (same cross order as the vendor so the default framing is identical).
	vec3 CameraView = vec3(
		sin(cam_yaw) * cos(cam_pitch),
		sin(cam_pitch),
		cos(cam_yaw) * cos(cam_pitch));
	vec3 lensPoint = cam_pos;

	vec3 CameraRight = normalize(cross(CameraView, vec3(0.0, 1.0, 0.0)));
	vec3 CameraUp = normalize(cross(CameraRight, CameraView));

	vec3 focalPoint = lensPoint - CAMERA_FOCAL_DISTANCE * CameraView;
	lensPoint += CameraRight * (uv.x * 2.0 - 1.0) * lensWidth / 2.0;
	lensPoint += CameraUp * (uv.y * 2.0 - 1.0) * CAMERA_LENS_HEIGHT / 2.0;

	vec3 rayOrigin = focalPoint;
	vec3 rayDirection = normalize(lensPoint - focalPoint);

	vec3 color;
	if (shade_mode == 0) {
		// CHEAP: sphere-trace the same field, shade N.L. Nothing else runs.
		float tt = 0.0;
		float hit = -1.0;
		for (int i = 0; i < cheap_steps; i++) {
			vec3 pp = rayOrigin + rayDirection * tt;
			float dd = QueryVolumetricDistanceField(pp, TIME);
			if (dd < 0.01) { hit = tt; break; }
			tt += max(dd * cheap_relax, 0.02);
			if (tt > 400.0) { break; }
		}
		if (hit < 0.0) {
			color = mix(vec3(0.55, 0.70, 0.86), vec3(0.86, 0.90, 0.95),
				clamp(rayDirection.y * 2.0 + 0.5, 0.0, 1.0));
		} else {
			vec3 pp = rayOrigin + rayDirection * hit;
			vec2 e = vec2(0.04, 0.0);
			vec3 nn = normalize(vec3(
				QueryVolumetricDistanceField(pp + e.xyy, TIME) - QueryVolumetricDistanceField(pp - e.xyy, TIME),
				QueryVolumetricDistanceField(pp + e.yxy, TIME) - QueryVolumetricDistanceField(pp - e.yxy, TIME),
				QueryVolumetricDistanceField(pp + e.yyx, TIME) - QueryVolumetricDistanceField(pp - e.yyx, TIME)));
			float lam = clamp(dot(nn, normalize(vec3(-0.45, 0.78, 0.42))), 0.0, 1.0);
			// water vs sand by whether the surface sits above the bed here
			float depth = SampleSurface(pp.xz) - SampleBed(pp.xz);
			vec3 base = depth > 0.02 ? vec3(0.30, 0.62, 0.66) : vec3(0.80, 0.72, 0.55);
			color = base * (0.30 + 0.75 * lam);
		}
	} else {
		color = Render(rayOrigin, rayDirection, TIME);
	}

	// Fork: debug ghost — sphere-trace the carve solid itself and blend it on
	// top as a magenta X-ray (occlusion ignored on purpose: it's a placement
	// aid). 0.7 step safety because the ridge noise breaks exact-SDF bounds.
	if (br_enabled > 0.5 && br_debug > 0.5) {
		float gt = 0.0;
		for (int i = 0; i < 64; i++) {
			vec3 p = rayOrigin + rayDirection * gt;
			float d = BarrelCarveField(p, TIME);
			if (d < 0.05) {
				vec2 e = vec2(0.25, 0.0);
				vec3 gn = normalize(vec3(
					BarrelCarveField(p + e.xyy, TIME) - BarrelCarveField(p - e.xyy, TIME),
					BarrelCarveField(p + e.yxy, TIME) - BarrelCarveField(p - e.yxy, TIME),
					BarrelCarveField(p + e.yyx, TIME) - BarrelCarveField(p - e.yyx, TIME)));
				float lam = max(0.0, dot(gn, GetSunLightDirection()));
				float rim = pow(1.0 - max(0.0, dot(gn, -rayDirection)), 2.0);
				color = mix(color,
					vec3(1.0, 0.15, 0.75) * (0.35 + 0.65 * lam) + vec3(0.9, 0.9, 1.0) * 0.6 * rim,
					0.55);
				break;
			}
			gt += max(d * 0.7, 0.05);
			if (gt > 250.0) { break; }
		}
	}
	COLOR = vec4(GammaCorrect(color), 1.0);
}
