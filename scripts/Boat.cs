using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 15 — Drivable boat. C# port of water-kit scene 07. Arrow keys throttle + steer
// a little boat that RIDES the shared Gerstner wave field: the water shader and the CPU
// GerstnerField share ONE instance (+ wave_time), so the hull sits on the exact crests
// you see. Throttle/steer is the gameidea.org tutorial; the wave-riding (height + tilt-
// to-slope) is the modular "feel" solver in lib/BoatRider.cs. Water look is the ameye
// single-band shader; a caustic seabed carries the hull's shadow; low-poly islands bump
// the boat; a smoothed chase camera ignores the wave bob.
//   ameye water:   https://ameye.dev/notes/stylized-water-shader/
//   boat throttle: https://gameidea.org/2023/07/02/make-a-boat-in-godot/
//
// See it:
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/15_boat.tscn 5 1600x1200
public partial class Boat : Node3D
{
    private const string WaterShader = "res://shaders/ameye_water_boat.gdshader";
    private const string FloorShader = "res://shaders/caustics_floor.gdshader";
    private const string FoamTex = "res://textures/ameye/Foam 5.png";
    private const string NormalTex = "res://textures/ameye/Normals 1.png";

    // Tropical-lagoon seabed a few metres down (visual only — no collision with the boat).
    private const float BedDepth = -3.2f;

    private GerstnerField _field = new();
    private ShaderMaterial _mat = null!;
    private ShaderMaterial _floorMat = null!;
    private BoatRider _boat = null!;
    private Camera3D _cam = null!;
    private DirectionalLight3D _sun = null!;

    // Chase-camera smoothing: a focus point that follows the boat responsively in XZ but
    // heavily damps Y, so the camera stops popping up/down with every wave bob.
    private Vector3 _camFocus;
    private bool _camFocusInit;
    private float _camFollowXz = 8f;   // horizontal follow rate (higher = snappier)
    private float _camSmoothY = 1.5f;  // vertical follow rate (LOW = ignores wave bob)
    private float _camPosRate = 6f;    // how fast the camera body eases to its target spot

    public override void _Ready()
    {
        // CPU field matches the water shader's STAMPED preset EXACTLY so the boat rides
        // the visible crests in phase (same steepness/wavelength/speed/dirs).
        _field.Steepness = 0.030f;
        _field.Wavelength = 17.090f;
        _field.Speed = 3.840f;
        _field.Directions = new[] { 0.175f, 0.220f, 0.470f, 0.800f };
        BuildEnvironment();
        BuildSeabed();
        BuildWater();
        BuildBoat();
        BuildIslands();
        BuildUi();
    }

    public override void _PhysicsProcess(double delta)
    {
        _field.Time += (float)delta;
        _mat.SetShaderParameter("wave_time", _field.Time);
        UpdateFloorShadow();
    }

    public override void _Process(double delta)
    {
        UpdateCamera((float)delta);
    }

    // Feed the boat transform + sun + wave field into the caustics_floor shader so it can
    // project the hull's shadow onto the seabed. boat_yaw is the rider's authoritative
    // heading; the floor's wave_time folds in wave_speed so the refracted ripple's phase
    // matches the water surface (whose shader multiplies wave_speed * wave_time itself).
    private void UpdateFloorShadow()
    {
        if (_floorMat == null || _boat == null)
        {
            return;
        }
        _floorMat.SetShaderParameter("boat_pos", _boat.GlobalPosition);
        _floorMat.SetShaderParameter("boat_yaw", _boat.Yaw);
        _floorMat.SetShaderParameter("wave_time", _field.Time * _field.Speed);
        _floorMat.SetShaderParameter("steepness", _field.Steepness);
        _floorMat.SetShaderParameter("wl", _field.Wavelength);
        _floorMat.SetShaderParameter("wave_dirs", new Vector4(
            _field.Directions[0], _field.Directions[1], _field.Directions[2], _field.Directions[3]));
    }

    // ---------------------------------------------------------------------------

    private void BuildEnvironment()
    {
        _sun = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-42.0f, -120.0f, 0.0f),
            LightEnergy = 1.35f,
        };
        AddChild(_sun);

        var skyMat = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.30f, 0.48f, 0.80f),
            SkyHorizonColor = new Color(0.74f, 0.82f, 0.88f),
            GroundBottomColor = new Color(0.30f, 0.34f, 0.36f),
        };
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = skyMat },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            TonemapMode = Godot.Environment.ToneMapper.Agx,
        };
        AddChild(new WorldEnvironment { Environment = env });

        _cam = new Camera3D
        {
            Far = 3000.0f,
            Current = true,
            Position = new Vector3(0.0f, 3.5f, 8.0f),
        };
        AddChild(_cam);
    }

    private void BuildSeabed()
    {
        // Big sandy bed a few metres under the surface, carrying the caustics_floor shader
        // (OpenSimplex2 caustics as EMISSION). Visual only — the boat rides the surface.
        var mi = new MeshInstance3D
        {
            Mesh = new PlaneMesh
            {
                Size = new Vector2(800, 800),
                SubdivideWidth = 32,
                SubdivideDepth = 32,
            },
            Position = new Vector3(0.0f, BedDepth, 0.0f),
            ExtraCullMargin = 80.0f,
        };

        _floorMat = new ShaderMaterial { Shader = GD.Load<Shader>(FloorShader) };
        _floorMat.SetShaderParameter("albedo", new Color(0.76f, 0.68f, 0.50f));
        _floorMat.SetShaderParameter("caustic_scale", 1.0f);
        _floorMat.SetShaderParameter("caustic_strength", 1.3f);
        _floorMat.SetShaderParameter("caustic_speed", 0.6f);

        // Boat shadow on the seabed. Static bits set once: hull footprint (beam/length half-
        // extents incl. the prow) and the sun's TRAVEL direction (light forward = -basis.z).
        _floorMat.SetShaderParameter("hull_half", new Vector2(1.0f, 2.6f));
        _floorMat.SetShaderParameter("sun_dir", -_sun.GlobalTransform.Basis.Z);
        _floorMat.SetShaderParameter("shadow_darkness", 0.75f);
        _floorMat.SetShaderParameter("shadow_softness", 0.4f);
        _floorMat.SetShaderParameter("shadow_size", 1.5f);
        _floorMat.SetShaderParameter("shadow_refracted", true);
        _floorMat.SetShaderParameter("shadow_refract_amount", 1.0f);
        mi.MaterialOverride = _floorMat;
        AddChild(mi);
    }

    private void BuildWater()
    {
        var mi = new MeshInstance3D
        {
            Mesh = new PlaneMesh
            {
                Size = new Vector2(400, 400),
                SubdivideWidth = 220,
                SubdivideDepth = 220,
            },
            ExtraCullMargin = 40.0f,
        };
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>(WaterShader) };

        // Ameye look — the user's STAMPED scene-08 preset.
        _mat.SetShaderParameter("steepness", 0.030f);
        _mat.SetShaderParameter("wavelength", 17.090f);
        _mat.SetShaderParameter("wave_speed", 3.840f);
        _mat.SetShaderParameter("water_roughness", 0.035f);
        _mat.SetShaderParameter("depth_fade_distance", 6.567f);
        _mat.SetShaderParameter("foam_distance", 1.575f);
        _mat.SetShaderParameter("foam_crest", 0.900f);
        _mat.SetShaderParameter("normal_strength", 0.770f);
        _mat.SetShaderParameter("refraction_strength", 1.720f);
        _mat.SetShaderParameter("specular_smoothness", 0.485f);

        // Tropical-lagoon clarity so the caustic-lit bed reads through the surface: push
        // the depth fade out past the stamped 6.567 and lighten/turquoise the tints.
        _mat.SetShaderParameter("depth_fade_distance", 11.0f);
        _mat.SetShaderParameter("water_color", new Color(0.09f, 0.52f, 0.62f));
        _mat.SetShaderParameter("shallow_color", new Color(0.46f, 0.82f, 0.80f));
        SyncShaderWaves();  // steepness/wavelength/wave_speed/directions from the shared field
        _mat.SetShaderParameter("foam_tex", GD.Load<Texture2D>(FoamTex));
        _mat.SetShaderParameter("normal_tex", GD.Load<Texture2D>(NormalTex));
        // direction from the water surface TO the sun (light's local +Z after rotation)
        _mat.SetShaderParameter("sun_direction", _sun.GlobalTransform.Basis.Z);

        // Stamped toggle state.
        _mat.SetShaderParameter("enable_depth_fade", true);
        _mat.SetShaderParameter("enable_shore_color", true);
        _mat.SetShaderParameter("enable_foam", false);
        _mat.SetShaderParameter("enable_normal_maps", true);
        _mat.SetShaderParameter("enable_refraction", true);
        _mat.SetShaderParameter("enable_lighting", false);

        mi.MaterialOverride = _mat;
        AddChild(mi);
    }

    private void SyncShaderWaves()
    {
        _mat.SetShaderParameter("steepness", _field.Steepness);
        _mat.SetShaderParameter("wavelength", _field.Wavelength);
        _mat.SetShaderParameter("wave_speed", _field.Speed);
        _mat.SetShaderParameter("directions", new Vector4(
            _field.Directions[0], _field.Directions[1], _field.Directions[2], _field.Directions[3]));
    }

    private void BuildBoat()
    {
        _boat = new BoatRider { Field = _field };

        // Hull (elongated box, length runs down -Z = forward), wooden red.
        var hullMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.55f, 0.16f, 0.14f),
            Roughness = 0.65f,
        };
        var hull = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(2.0f, 0.7f, 5.0f) },
            MaterialOverride = hullMat,
        };
        _boat.AddChild(hull);

        // A wedge prow so the front reads as a bow (rotated box, tapered by scale).
        var prow = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(2.0f, 0.7f, 1.4f) },
            MaterialOverride = hullMat,
            Position = new Vector3(0.0f, 0.0f, -2.9f),
            Scale = new Vector3(0.25f, 1.0f, 1.0f),  // pinch the nose to a point
        };
        _boat.AddChild(prow);

        // Deck (cream), sits just on top of the hull.
        var deckMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.88f, 0.83f, 0.70f),
            Roughness = 0.8f,
        };
        var deck = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(1.7f, 0.12f, 4.4f) },
            MaterialOverride = deckMat,
            Position = new Vector3(0.0f, 0.40f, 0.2f),
        };
        _boat.AddChild(deck);

        // Cabin (white box toward the stern).
        var cabinMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.92f, 0.94f, 0.96f),
            Roughness = 0.7f,
        };
        var cabin = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(1.3f, 0.9f, 1.6f) },
            MaterialOverride = cabinMat,
            Position = new Vector3(0.0f, 0.85f, 1.2f),
        };
        _boat.AddChild(cabin);

        // Collision (box roughly matching the hull) so islands push the boat around.
        var col = new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(2.0f, 0.7f, 5.0f) },
        };
        _boat.AddChild(col);

        AddChild(_boat);
        _boat.GlobalPosition = Vector3.Zero;
    }

    private void BuildIslands()
    {
        // Low-poly islands: a big oblate SphereMesh sunk so only a sandy cap breaks the
        // surface. Collision is a vertical CylinderShape at the water-level footprint
        // (uniform scale — keeps Jolt happy) so the boat bumps the shore.
        var sand = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.80f, 0.72f, 0.52f),
            Roughness = 0.95f,
        };

        // px, pz, xz_radius, mesh_height, cap_above_water
        float[][] specs =
        {
            new[] { 70.0f, -95.0f, 40.0f, 24.0f, 3.5f },
            new[] { -90.0f, -120.0f, 34.0f, 20.0f, 2.5f },
            new[] { 60.0f, 140.0f, 50.0f, 26.0f, 4.0f },
            new[] { -140.0f, 50.0f, 30.0f, 18.0f, 2.0f },
            new[] { 150.0f, 90.0f, 38.0f, 22.0f, 3.0f },
            new[] { -55.0f, 120.0f, 28.0f, 16.0f, 2.0f },
        };
        foreach (var s in specs)
        {
            float px = s[0];
            float pz = s[1];
            float r = s[2];
            float h = s[3];
            float cap = s[4];
            float centerY = cap - h * 0.5f;

            var mi = new MeshInstance3D
            {
                Mesh = new SphereMesh
                {
                    Radius = r,
                    Height = h,
                    RadialSegments = 24,
                    Rings = 12,
                },
                MaterialOverride = sand,
                Position = new Vector3(px, centerY, pz),
            };
            AddChild(mi);

            // Footprint radius where the oblate spheroid crosses y = 0.
            float b = h * 0.5f;
            float ratio = Mathf.Clamp(Mathf.Abs(centerY) / b, 0.0f, 0.999f);
            float foot = r * Mathf.Sqrt(1.0f - ratio * ratio);

            var body = new StaticBody3D { Position = new Vector3(px, 0.0f, pz) };
            var ccol = new CollisionShape3D
            {
                Shape = new CylinderShape3D { Radius = foot, Height = 40.0f },
            };
            body.AddChild(ccol);
            AddChild(body);
        }
    }

    private void UpdateCamera(float delta)
    {
        if (_boat == null)
        {
            return;
        }
        // Chase from behind + above, but stay upright (ignore roll/pitch) so the horizon
        // doesn't tumble. "Behind" = the boat's flattened backward axis.
        Vector3 bp = _boat.GlobalPosition;
        if (!_camFocusInit)
        {
            _camFocus = bp;
            _camFocusInit = true;
        }
        // Smooth a focus point: snappy in XZ, heavily damped in Y so the wave bob is
        // averaged out instead of tracked 1:1 (this is what was popping the camera).
        float kxz = 1.0f - Mathf.Exp(-_camFollowXz * delta);
        float ky = 1.0f - Mathf.Exp(-_camSmoothY * delta);
        _camFocus.X = Mathf.Lerp(_camFocus.X, bp.X, kxz);
        _camFocus.Z = Mathf.Lerp(_camFocus.Z, bp.Z, kxz);
        _camFocus.Y = Mathf.Lerp(_camFocus.Y, bp.Y, ky);

        Vector3 back = _boat.GlobalTransform.Basis.Z;
        back.Y = 0.0f;
        back = back.Normalized();
        Vector3 targetPos = _camFocus + back * 8.0f + Vector3.Up * 3.5f;
        float k = 1.0f - Mathf.Exp(-_camPosRate * delta);
        _cam.GlobalPosition = _cam.GlobalPosition.Lerp(targetPos, k);
        // look at the SMOOTHED focus (not the raw bobbing boat) so framing stays steady
        _cam.LookAt(_camFocus + Vector3.Up * 0.6f, Vector3.Up);
    }

    private void BuildUi()
    {
        var ui = new DemoUI(this, "15 · Boat",
            "Arrow keys: throttle + steer. The boat rides the shared Gerstner field via a MODULAR feel solver — buoyancy spring + damp + slam + launch (vertical) and a predicted angular spring (tilt). Each term is a slider; a coefficient of 0 mutes it. Crank Slam / drop Linear damp / raise Gravity for hard slams and bounce.");
        ui.AddSlider("Cam vertical smooth (low = steadier)", 0.3f, 12.0f, _camSmoothY, v => _camSmoothY = v);
        ui.AddSlider("Cam follow XZ", 1.0f, 20.0f, _camFollowXz, v => _camFollowXz = v);
        ui.AddSlider("Max speed", 4.0f, 30.0f, _boat.MaxSpeed, v => _boat.MaxSpeed = v);
        ui.AddSlider("Max turn speed", 0.5f, 4.0f, _boat.MaxTurnSpeed, v => _boat.MaxTurnSpeed = v);
        ui.AddSlider("Hull offset (sit depth)", -0.4f, 1.0f, _boat.HullOffset, v => _boat.HullOffset = v);

        // --- Vertical feel solver ---
        ui.AddSlider("Buoyancy k", 0.0f, 60.0f, _boat.KBuoy, v => _boat.KBuoy = v);
        ui.AddSlider("Linear damp", 0.0f, 30.0f, _boat.CLin, v => _boat.CLin = v);
        ui.AddSlider("Slam (quadratic)", 0.0f, 5.0f, _boat.CSlam, v => _boat.CSlam = v);
        ui.AddSlider("Launch kick", 0.0f, 5.0f, _boat.KWave, v => _boat.KWave = v);
        ui.AddSlider("Gravity", 0.0f, 20.0f, _boat.Gravity, v => _boat.Gravity = v);
        ui.AddToggle("Gravity always (else airborne only)", _boat.GravityAlways, on => _boat.GravityAlways = on);

        // --- Angular feel solver ---
        ui.AddSlider("Conform (upright bias)", 0.0f, 1.0f, _boat.Conform, v => _boat.Conform = v);
        ui.AddSlider("Lookahead", 0.0f, 8.0f, _boat.LookaheadBase, v => _boat.LookaheadBase = v);
        ui.AddSlider("Ang stiffness", 0.0f, 100.0f, _boat.AngStiffness, v => _boat.AngStiffness = v);
        ui.AddSlider("Ang damp", 0.0f, 30.0f, _boat.AngDamp, v => _boat.AngDamp = v);
        ui.AddSlider("Ang slam", 0.0f, 5.0f, _boat.AngSlam, v => _boat.AngSlam = v);

        ui.AddSlider("Steepness", 0.0f, 0.4f, _field.Steepness, v => { _field.Steepness = v; SyncShaderWaves(); });
        ui.AddSlider("Wavelength", 6.0f, 30.0f, _field.Wavelength, v => { _field.Wavelength = v; SyncShaderWaves(); });
        ui.AddSlider("Wave speed", 0.0f, 4.0f, _field.Speed, v => { _field.Speed = v; SyncShaderWaves(); });
        ui.AddSlider("Caustic strength", 0.0f, 2.0f, 1.3f, v => _floorMat.SetShaderParameter("caustic_strength", v));
        ui.AddSlider("Caustic scale", 0.1f, 2.0f, 1.0f, v => _floorMat.SetShaderParameter("caustic_scale", v));
        ui.AddSlider("Water clarity (depth fade)", 3.0f, 20.0f, 11.0f, v => _mat.SetShaderParameter("depth_fade_distance", v));

        // --- Boat shadow on the seabed ---
        ui.AddSlider("Shadow darkness", 0.0f, 1.0f, 0.75f, v => _floorMat.SetShaderParameter("shadow_darkness", v));
        ui.AddSlider("Shadow softness", 0.0f, 1.0f, 0.4f, v => _floorMat.SetShaderParameter("shadow_softness", v));
        ui.AddSlider("Shadow size", 0.5f, 4.0f, 1.5f, v => _floorMat.SetShaderParameter("shadow_size", v));
        ui.AddToggle("Shadow refracted (ripple)", true, on => _floorMat.SetShaderParameter("shadow_refracted", on));
        ui.AddSlider("Shadow refract amount", 0.0f, 2.0f, 1.0f, v => _floorMat.SetShaderParameter("shadow_refract_amount", v));
    }
}
