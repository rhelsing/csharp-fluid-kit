using Godot;

namespace GodotCsharpExperiments.Lib;

// Drivable boat that rides a shared GerstnerField. The throttle/steer half is the
// gameidea.org boat tutorial (arrow keys -> CurrentSpeed + rotate for heading,
// MoveAndSlide for XZ + island collision). The boat stays KINEMATIC — no rigidbody.
//
// The wave response is a MODULAR "feel" solver: instead of hard-snapping Y to the
// surface and slerping fully to the wave normal, the vertical + angular motion are
// each a SUM of named, individually-tunable force terms (buoyancy spring, viscous
// damp, quadratic slam drag, wave launch kick, gravity — and their angular mirrors).
// Soft vs. hard landings/bounces and self-righting all emerge from one system; every
// term has its own slider. Boat + visible crests stay in phase because they share one
// GerstnerField (+ wave time).
//
// C# port of water-kit's scripts/lib/boat_rider.gd (class_name BoatRider extends
// CharacterBody3D). Public API mirrors the .gd 1:1 so shared scenes call the same names.
public partial class BoatRider : CharacterBody3D
{
    // --- Throttle / steer tunables (driven by the scene / DemoUI) ---
    public float MaxSpeed = 15f;
    public float Acceleration = 2f;
    public float Deceleration = 1.5f;
    public float MaxTurnSpeed = 2f;
    public float MinTurnSpeed = 0.3f;
    public float TurnBank = 0f;        // roll into turns (rad at full speed+turn); − = lean out. 0 = off
    public float Grip = 30f;           // course chases heading at this rate (1/s); low = nose leads, drifty. 30 ≈ instant
    public float TrimAngle = 0f;       // bow-up pitch at full speed (rad); hinged at TrimPivot. 0 = off
    public float SpeedLift = 0f;       // ride-height gain at full speed (m) — the hull planes up. 0 = off
    public float TrimPivot = 0f;       // trim hinge station along the hull (m; + bow, − stern): which end pays for the tilt
    public float TurnPivot = 0f;       // yaw pivot station (m; + bow, − stern); 0 = rotate about the centre (original)

    // --- Speed-feel curves: paired properties blend REST → base (top-speed) value by
    // pow(speedRatio, SpeedCurveExp). Rest values default NaN = "same as base" →
    // lerp(x, x) = x, constant behaviour; scenes opt in by setting/dialing them.
    // SpeedCurves=false short-circuits to the base values (the original code path).
    public bool SpeedCurves = true;
    public float SpeedCurveExp = 1f;   // 1 = linear; high = the transition bites late (planing snap)
    public float GripRest = float.NaN;
    public float ConformRest = float.NaN;
    public float KBuoyRest = float.NaN;
    public float CLinRest = float.NaN;
    public float AngStiffnessRest = float.NaN;
    public float AngDampRest = float.NaN;
    public float TurnPivotRest = float.NaN;
    public float HullOffset = 0.05f;   // lift so the waterline sits mid-hull (surface reference)
    public float NormalStep = 1.6f;    // gradient sample distance for the wave normal

    // --- Vertical "feel" solver coefficients (each has a slider) ---
    public float KBuoy = 20f;          // buoyancy spring: accel up per metre of submerged depth
    public float CLin = 8f;            // linear (viscous) damp — near-critical for a calm baseline
    public float CSlam = 0.6f;         // quadratic slam drag — makes FAST entries feel like hard hits
    public float KWave = 0.5f;         // launch kick — a rising wave flings the hull up
    public float Gravity = 8f;         // gravity accel (variable/tunable)
    public bool GravityAlways = false; // true: gravity every frame; false: only when airborne

    // --- Angular "feel" solver coefficients (each has a slider) ---
    public float Conform = 0.6f;        // upright bias: 0 = stay level, 1 = fully match wave normal
    public float LookaheadBase = 2f;    // predict the wave normal this far ahead along travel
    public float LookaheadSpeed = 0.15f;// + this * CurrentSpeed (look further when moving faster)
    public float AngStiffness = 40f;    // angular spring toward targetUp
    public float AngDamp = 11f;         // angular linear damp (near-critical)
    public float AngSlam = 0.5f;        // angular quadratic damp — kills violent roll snap-back

    // Read-only for future use (e.g. camera shake): entry speed of the last water slam.
    public float ImpactIntensity = 0f;

    // Shared with the scene's water shader (one field == in phase). Base type so a
    // CompositeGerstner also fits.
    public GerstnerField Field = null!;

    public float CurrentSpeed = 0f;
    public float Yaw = 0f;             // authoritative heading (radians); tilt never touches it
    private float _courseYaw;          // velocity direction; lags Yaw by the slip angle (Grip)

    // Vertical solver state
    private float _vy;                 // vertical velocity we integrate
    private float _prevSurface;        // last frame's surface height (for wave rise-rate)
    private bool _wasSubmerged = true; // for detecting water-entry (impact) crossings

    // Angular solver state
    private Vector3 _up = Vector3.Up;      // current hull up-axis
    private Vector3 _upVel = Vector3.Zero; // its angular velocity

    public override void _Ready()
    {
        Yaw = Rotation.Y;
        _courseYaw = Yaw;
        MotionMode = MotionModeEnum.Floating; // no gravity; we own the Y
        if (Field != null)
        {
            _prevSurface = Field.Height(GlobalPosition.X, GlobalPosition.Z) + HullOffset;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        float moveInput = Input.GetAxis("ui_down", "ui_up");
        float turnInput = Input.GetAxis("ui_left", "ui_right");

        // --- throttle (tutorial) ---
        if (moveInput != 0f)
        {
            CurrentSpeed = Mathf.MoveToward(CurrentSpeed, moveInput * MaxSpeed, Acceleration * dt);
        }
        else
        {
            CurrentSpeed = Mathf.MoveToward(CurrentSpeed, 0f, Deceleration * dt);
        }

        // --- steer: faster boat turns harder (tutorial) ---
        float speedRatio = Mathf.Abs(CurrentSpeed) / MaxSpeed;

        // speed-feel curves: resolve each paired property for THIS tick's speed
        float sCurve = SpeedCurves ? Mathf.Pow(Mathf.Clamp(speedRatio, 0f, 1f), Mathf.Max(SpeedCurveExp, 0.01f)) : 1f;
        float AtSpeed(float rest, float top) => (!SpeedCurves || float.IsNaN(rest)) ? top : Mathf.Lerp(rest, top, sCurve);
        float gripNow = AtSpeed(GripRest, Grip);
        float conformNow = AtSpeed(ConformRest, Conform);
        float kBuoyNow = AtSpeed(KBuoyRest, KBuoy);
        float cLinNow = AtSpeed(CLinRest, CLin);
        float angStiffNow = AtSpeed(AngStiffnessRest, AngStiffness);
        float angDampNow = AtSpeed(AngDampRest, AngDamp);
        float turnPivotNow = AtSpeed(TurnPivotRest, TurnPivot);

        float turnSpeed = Mathf.Lerp(MinTurnSpeed, MaxTurnSpeed, speedRatio);
        float dyaw = -turnInput * turnSpeed * dt; // right key turns the boat right
        if (turnPivotNow != 0f && dyaw != 0f)
        {
            // rotate about the pivot station instead of the centre: hold that point
            // world-fixed and swing the rest of the hull around it (stern pivot →
            // the nose sweeps onto the new heading while the tail holds its track)
            Vector3 fwdOld = new(-Mathf.Sin(Yaw), 0f, -Mathf.Cos(Yaw));
            Vector3 fwdNew = new(-Mathf.Sin(Yaw + dyaw), 0f, -Mathf.Cos(Yaw + dyaw));
            GlobalPosition += (fwdOld - fwdNew) * turnPivotNow;
        }
        Yaw += dyaw;

        // Co-rotate the angular spring state with the heading. The spring chases its
        // target in world space; while yawing, the target rotates with the hull and the
        // spring LAGS it by ~ω·τ — and a sideways bank-lean dragged behind a yawing
        // frame acquires a fore-aft component: bank leaks into pitch, sign flipping
        // with turn direction (the "nose dives banking one way" bug). Rotating the
        // state by dyaw makes hull-frame leans (trim, bank) lag-free in steady turns
        // while transients still ease through the spring exactly as before.
        if (dyaw != 0f)
        {
            _up = _up.Rotated(Vector3.Up, dyaw);
            _upVel = _upVel.Rotated(Vector3.Up, dyaw);
        }

        // --- slip: the HEADING (Yaw) turns now; the COURSE (velocity direction) chases it
        // at Grip/s. Low grip = the nose leads the turn while momentum carries straight,
        // then the hull carves onto the new line. High grip = on rails (course ≈ heading).
        float slip = Mathf.Wrap(Yaw - _courseYaw, -Mathf.Pi, Mathf.Pi);
        _courseYaw += slip * (1f - Mathf.Exp(-gripNow * dt));

        // --- drive across the water (XZ only; Y is the solver's job) ---
        Vector3 fwd = new(-Mathf.Sin(Yaw), 0f, -Mathf.Cos(Yaw)); // matches -basis.z of a pure yaw
        Vector3 courseFwd = new(-Mathf.Sin(_courseYaw), 0f, -Mathf.Cos(_courseYaw));
        Velocity = courseFwd * CurrentSpeed;
        MoveAndSlide();

        if (Field == null)
        {
            return;
        }

        float dtClamped = Mathf.Max(dt, 1e-5f);
        Vector3 pos = GlobalPosition;

        // ============================================================
        // VERTICAL SOLVER — sum of named force terms (replaces pos.y snap)
        // ============================================================
        // speed trim/lift: throttle raises the ride height (planing) and the trim hinge
        // decides which end pays for the tilt — a stern hinge (TrimPivot < 0) lifts the
        // centre as the bow kicks up; a bow hinge squats the stern under a level nose.
        float trimNow = TrimAngle * speedRatio;
        float surface = Field.Height(pos.X, pos.Z) + HullOffset
            + SpeedLift * speedRatio
            - TrimPivot * Mathf.Sin(trimNow);
        float depth = surface - pos.Y;                    // depth > 0 => hull submerged
        float waveVy = (surface - _prevSurface) / dtClamped; // how fast the surface is rising
        _prevSurface = surface;

        float accel = 0f;
        if (depth > 0f)
        {
            accel += kBuoyNow * depth;                    // buoyancy spring (push up)
            accel += -cLinNow * _vy;                      // linear viscous damp
            accel += -CSlam * _vy * Mathf.Abs(_vy);       // quadratic slam drag (hard entries)
            accel += KWave * Mathf.Max(waveVy, 0f);       // launch kick from a rising wave
        }
        if (GravityAlways || depth <= 0f)
        {
            accel -= Gravity;                             // gravity: always, or only airborne
        }

        _vy = Mathf.Clamp(_vy + accel * dt, -60f, 60f);
        pos.Y += _vy * dt;
        GlobalPosition = pos;

        // Water entry: depth crosses <=0 -> >0 while falling => record slam intensity.
        bool submerged = depth > 0f;
        if (submerged && !_wasSubmerged && _vy < 0f)
        {
            ImpactIntensity = -_vy;
        }
        _wasSubmerged = submerged;

        // ============================================================
        // ANGULAR SOLVER — predicted spring-damper to an upright-biased target
        // ============================================================
        Vector3 probe = pos + courseFwd * (LookaheadBase + LookaheadSpeed * Mathf.Abs(CurrentSpeed));
        Vector3 predN = _WaveNormal(probe.X, probe.Z);
        Vector3 targetUp = depth > 0f ? Vector3.Up.Slerp(predN, conformNow) : Vector3.Up;

        // speed trim: bow-up pitch with throttle, riding the same spring as conform —
        // gun it and the nose eases up, back off and it settles. Height share of the
        // hinge is applied in the vertical solver above.
        if (trimNow != 0f)
        {
            Vector3 right = fwd.Cross(Vector3.Up).Normalized();
            targetUp = targetUp.Rotated(right, trimNow);
        }

        // bank: tilt the target up around the HULL's long axis (heading fwd) with turn
        // input — the spring/damp then makes the lean ease in, settle, and release.
        // +TurnBank leans INTO the turn (right key → mast tips right), − leans out.
        // Around fwd (not courseFwd): the travel axis differs from heading by the slip
        // angle, and rotating the trim-pitched up about it leaks bow-up into pitch
        // (pitch ≈ trim − sinσ·sinbank — nose dives banking one way, rears the other).
        // fwd is invariant under rotation about itself, so trim survives bank exactly.
        float bank = TurnBank * turnInput * speedRatio;
        if (bank != 0f)
        {
            targetUp = targetUp.Rotated(fwd, bank);
        }

        Vector3 torque = angStiffNow * (targetUp - _up)
            - angDampNow * _upVel
            - AngSlam * _upVel * _upVel.Length();
        _upVel += torque * dt;
        if (_upVel.Length() > 30f) // guard: keep the angular state sane
        {
            _upVel = _upVel.Normalized() * 30f;
        }
        _up = (_up + _upVel * dt).Normalized();

        GlobalBasis = BasisFrom(Yaw, _up);
    }

    // Surface normal from the height gradient (central differences), so the boat pitches
    // to crests it's climbing and rolls with beam-on swell.
    private Vector3 _WaveNormal(float x, float z)
    {
        float e = NormalStep;
        float dhdx = (Field.Height(x + e, z) - Field.Height(x - e, z)) / (2f * e);
        float dhdz = (Field.Height(x, z + e) - Field.Height(x, z - e)) / (2f * e);
        return new Vector3(-dhdx, 1f, -dhdz).Normalized();
    }

    // Rebuild an orthonormal basis: instant heading (yaw) + up aimed at the solver's up.
    private Basis BasisFrom(float yaw, Vector3 up)
    {
        Vector3 fwd = new(-Mathf.Sin(yaw), 0f, -Mathf.Cos(yaw));
        fwd = (fwd - up * fwd.Dot(up)).Normalized();  // forward flattened onto the up-plane
        Vector3 zAxis = -fwd;                          // Godot basis.z points backward
        Vector3 xAxis = up.Cross(zAxis).Normalized();
        return new Basis(xAxis, up, zAxis).Orthonormalized();
    }
}
