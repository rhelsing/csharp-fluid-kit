using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 16 — Boat on a COMPOSITE (multi-band) Gerstner sea. C# port of water-kit's
// scene 100 (scripts/scene_100_boat_composite.gd). It is scene 15's boat + ameye water
// look + caustic seabed + hull shadow + islands + smoothed chase camera and feel-solver,
// with ONE change: the sea is a real composite — long swell + mid waves + fine chop —
// instead of a single wavelength across 4 directions.
//
// CompositeGerstner (CPU, extends GerstnerField -> drops into the boat) and
// shaders/ameye_water_composite.gdshader (the ameye water shader with an N-band wave loop)
// sum the SAME bands + a shared wave_time, so the boat rides the exact crests you see.
//
// Water look ported verbatim from ameye.dev's stylised water tutorial. See it:
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/16_boat_composite.tscn 5 1600x1200
public partial class BoatComposite : Node3D
{
    private const string WaterShader = "res://shaders/ameye_water_composite.gdshader";
    private const string FloorShader = "res://shaders/caustics_floor.gdshader";
    private const string FoamTex = "res://textures/ameye/Foam 5.png";
    private const string NormalTex = "res://textures/ameye/Normals 1.png";
    private const float BedDepth = -3.2f;

    // Composite bands: [wavelength, steepness, speed, direction(0..1)] — swell -> chop.
    private static readonly float[][] Bands =
    {
        new[] { 34f, 0.13f, 1.10f, 0.12f },
        new[] { 26f, 0.10f, 1.00f, 0.28f },
        new[] { 14f, 0.10f, 1.05f, 0.52f },
        new[] { 9f, 0.09f, 1.15f, 0.66f },
        new[] { 5f, 0.07f, 1.35f, 0.82f },
        new[] { 3f, 0.05f, 1.60f, 0.40f },
    };

    private CompositeGerstner _field = new();
    private ShaderMaterial _mat = null!;
    private ShaderMaterial _floorMat = null!;
    private BoatRider _boat = null!;
    private Camera3D _cam = null!;
    private DirectionalLight3D _sun = null!;

    private float _waveAmp = 1f;
    private float _waveSpeedScale = 1f;

    // Chase-camera smoothing (from scene 15): snappy XZ, damped Y so it doesn't pop.
    private Vector3 _camFocus;
    private bool _camFocusInit;
    private float _camFollowXz = 8f;
    private float _camSmoothY = 1.5f;
    private float _camPosRate = 6f;

    public override void _Ready()
    {
        RebuildField();
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

    // Fill the composite bands (× live amp/speed) and mirror them to the shader. Also set the
    // base GerstnerField members to the dominant swell so the caustic-floor hull shadow (which
    // reads steepness/wavelength/directions) still ripples sensibly.
    private void RebuildField()
    {
        _field.ClearWaves();
        foreach (var b in Bands)
        {
            _field.AddWave(b[0], b[1] * _waveAmp, b[2] * _waveSpeedScale, b[3]);
        }
        _field.Wavelength = Bands[0][0];
        _field.Steepness = Bands[0][1] * _waveAmp;
        _field.Speed = Bands[0][2] * _waveSpeedScale;
        _field.Directions = new[] { Bands[0][3], Bands[1][3], Bands[2][3], Bands[3][3] };
        if (_mat != null)
        {
            SyncShaderWaves();
        }
    }

    private void SyncShaderWaves()
    {
        _mat.SetShaderParameter("wave_count", _field.Wavelengths.Count);
        _mat.SetShaderParameter("wavelengths", _field.WavelengthsArr);
        _mat.SetShaderParameter("steepnesses", _field.SteepnessesArr);
        _mat.SetShaderParameter("speeds", _field.SpeedsArr);
        _mat.SetShaderParameter("dirs", _field.DirsArr);
        _mat.SetShaderParameter("wave_time", _field.Time);
    }

    private void BuildEnvironment()
    {
        _sun = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-42f, -120f, 0f),
            LightEnergy = 1.35f,
        };
        AddChild(_sun);

        var skyMat = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.30f, 0.48f, 0.80f),
            SkyHorizonColor = new Color(0.74f, 0.82f, 0.88f),
            GroundBottomColor = new Color(0.30f, 0.34f, 0.36f),
        };
        var sky = new Sky { SkyMaterial = skyMat };
        var env = new Environment
        {
            BackgroundMode = Environment.BGMode.Sky,
            Sky = sky,
            AmbientLightSource = Environment.AmbientSource.Sky,
            TonemapMode = Environment.ToneMapper.Agx,
        };
        var we = new WorldEnvironment { Environment = env };
        AddChild(we);

        _cam = new Camera3D
        {
            Far = 3000f,
            Current = true,
            Position = new Vector3(0f, 3.5f, 8f),
        };
        AddChild(_cam);
    }

    private void BuildSeabed()
    {
        var mi = new MeshInstance3D();
        var plane = new PlaneMesh
        {
            Size = new Vector2(800, 800),
            SubdivideWidth = 32,
            SubdivideDepth = 32,
        };
        mi.Mesh = plane;
        mi.Position = new Vector3(0f, BedDepth, 0f);
        mi.ExtraCullMargin = 80f;

        _floorMat = new ShaderMaterial { Shader = GD.Load<Shader>(FloorShader) };
        _floorMat.SetShaderParameter("albedo", new Color(0.76f, 0.68f, 0.50f));
        _floorMat.SetShaderParameter("caustic_scale", 1.0f);
        _floorMat.SetShaderParameter("caustic_strength", 1.3f);
        _floorMat.SetShaderParameter("caustic_speed", 0.6f);
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
        var mi = new MeshInstance3D();
        var plane = new PlaneMesh
        {
            Size = new Vector2(400, 400),
            SubdivideWidth = 220,
            SubdivideDepth = 220,
        };
        mi.Mesh = plane;
        mi.ExtraCullMargin = 40f;
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>(WaterShader) };

        // Scene 15's STAMPED scene-08 (ameye) preset — the water LOOK (unchanged). The
        // composite shader carries no single steepness/wavelength/wave_speed (the band
        // arrays replace them), so only the look params are set here.
        _mat.SetShaderParameter("water_roughness", 0.035f);
        _mat.SetShaderParameter("foam_distance", 1.575f);
        _mat.SetShaderParameter("foam_crest", 0.900f);
        _mat.SetShaderParameter("normal_strength", 0.770f);
        _mat.SetShaderParameter("refraction_strength", 1.720f);
        _mat.SetShaderParameter("specular_smoothness", 0.485f);

        _mat.SetShaderParameter("depth_fade_distance", 11.0f);
        _mat.SetShaderParameter("water_color", new Color(0.09f, 0.52f, 0.62f));
        _mat.SetShaderParameter("shallow_color", new Color(0.46f, 0.82f, 0.80f));
        SyncShaderWaves();
        _mat.SetShaderParameter("foam_tex", GD.Load<Texture2D>(FoamTex));
        _mat.SetShaderParameter("normal_tex", GD.Load<Texture2D>(NormalTex));
        _mat.SetShaderParameter("sun_direction", _sun.GlobalTransform.Basis.Z);

        _mat.SetShaderParameter("enable_depth_fade", true);
        _mat.SetShaderParameter("enable_shore_color", true);
        _mat.SetShaderParameter("enable_foam", false);
        _mat.SetShaderParameter("enable_normal_maps", true);
        _mat.SetShaderParameter("enable_refraction", true);
        _mat.SetShaderParameter("enable_lighting", false);

        mi.MaterialOverride = _mat;
        AddChild(mi);
    }

    private void BuildBoat()
    {
        _boat = new BoatRider { Field = _field };

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

        var prow = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(2.0f, 0.7f, 1.4f) },
            MaterialOverride = hullMat,
            Position = new Vector3(0f, 0f, -2.9f),
            Scale = new Vector3(0.25f, 1.0f, 1.0f),
        };
        _boat.AddChild(prow);

        var deck = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(1.7f, 0.12f, 4.4f) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.88f, 0.83f, 0.70f),
                Roughness = 0.8f,
            },
            Position = new Vector3(0f, 0.40f, 0.2f),
        };
        _boat.AddChild(deck);

        var cabin = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(1.3f, 0.9f, 1.6f) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.92f, 0.94f, 0.96f),
                Roughness = 0.7f,
            },
            Position = new Vector3(0f, 0.85f, 1.2f),
        };
        _boat.AddChild(cabin);

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
        var sand = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.80f, 0.72f, 0.52f),
            Roughness = 0.95f,
        };
        // [px, pz, radius, height, cap] per island.
        float[][] specs =
        {
            new[] { 70f, -95f, 40f, 24f, 3.5f },
            new[] { -90f, -120f, 34f, 20f, 2.5f },
            new[] { 60f, 140f, 50f, 26f, 4.0f },
            new[] { -140f, 50f, 30f, 18f, 2.0f },
            new[] { 150f, 90f, 38f, 22f, 3.0f },
            new[] { -55f, 120f, 28f, 16f, 2.0f },
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

            float b = h * 0.5f;
            float ratio = Mathf.Clamp(Mathf.Abs(centerY) / b, 0f, 0.999f);
            float foot = r * Mathf.Sqrt(1.0f - ratio * ratio);
            var body = new StaticBody3D { Position = new Vector3(px, 0f, pz) };
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
        Vector3 bp = _boat.GlobalPosition;
        if (!_camFocusInit)
        {
            _camFocus = bp;
            _camFocusInit = true;
        }
        float kxz = 1.0f - Mathf.Exp(-_camFollowXz * delta);
        float ky = 1.0f - Mathf.Exp(-_camSmoothY * delta);
        _camFocus.X = Mathf.Lerp(_camFocus.X, bp.X, kxz);
        _camFocus.Z = Mathf.Lerp(_camFocus.Z, bp.Z, kxz);
        _camFocus.Y = Mathf.Lerp(_camFocus.Y, bp.Y, ky);

        Vector3 back = _boat.GlobalTransform.Basis.Z;
        back.Y = 0f;
        back = back.Normalized();
        Vector3 targetPos = _camFocus + back * 8.0f + Vector3.Up * 3.5f;
        float k = 1.0f - Mathf.Exp(-_camPosRate * delta);
        _cam.GlobalPosition = _cam.GlobalPosition.Lerp(targetPos, k);
        _cam.LookAt(_camFocus + Vector3.Up * 0.6f, Vector3.Up);
    }

    private void BuildUi()
    {
        var ui = new DemoUI(this, "16 · Boat — composite waves",
            "Arrow keys: throttle + steer. Exact scene-15 look/seabed/shadow, but the sea is a COMPOSITE Gerstner field (swell + mid + chop). Wave amp / speed scale all bands; the feel-solver + smoothed camera are scene 15's.");

        ui.AddSlider("Wave amplitude x", 0f, 2f, _waveAmp, v =>
        {
            _waveAmp = v;
            RebuildField();
        });
        ui.AddSlider("Wave speed x", 0f, 2.5f, _waveSpeedScale, v =>
        {
            _waveSpeedScale = v;
            RebuildField();
        });
        ui.AddSlider("Cam vertical smooth (low = steadier)", 0.3f, 12f, _camSmoothY, v => _camSmoothY = v);
        ui.AddSlider("Cam follow XZ", 1f, 20f, _camFollowXz, v => _camFollowXz = v);
        ui.AddSlider("Max speed", 4f, 30f, _boat.MaxSpeed, v => _boat.MaxSpeed = v);
        ui.AddSlider("Max turn speed", 0.5f, 4f, _boat.MaxTurnSpeed, v => _boat.MaxTurnSpeed = v);
        ui.AddSlider("Hull offset (sit depth)", -0.4f, 1f, _boat.HullOffset, v => _boat.HullOffset = v);

        ui.AddSlider("Buoyancy k", 0f, 60f, _boat.KBuoy, v => _boat.KBuoy = v);
        ui.AddSlider("Linear damp", 0f, 30f, _boat.CLin, v => _boat.CLin = v);
        ui.AddSlider("Slam (quadratic)", 0f, 5f, _boat.CSlam, v => _boat.CSlam = v);
        ui.AddSlider("Launch kick", 0f, 5f, _boat.KWave, v => _boat.KWave = v);
        ui.AddSlider("Gravity", 0f, 20f, _boat.Gravity, v => _boat.Gravity = v);
        ui.AddToggle("Gravity always (else airborne only)", _boat.GravityAlways, on => _boat.GravityAlways = on);

        ui.AddSlider("Conform (upright bias)", 0f, 1f, _boat.Conform, v => _boat.Conform = v);
        ui.AddSlider("Lookahead", 0f, 8f, _boat.LookaheadBase, v => _boat.LookaheadBase = v);
        ui.AddSlider("Ang stiffness", 0f, 100f, _boat.AngStiffness, v => _boat.AngStiffness = v);
        ui.AddSlider("Ang damp", 0f, 30f, _boat.AngDamp, v => _boat.AngDamp = v);
        ui.AddSlider("Ang slam", 0f, 5f, _boat.AngSlam, v => _boat.AngSlam = v);

        ui.AddSlider("Caustic strength", 0f, 2f, 1.3f, v => _floorMat.SetShaderParameter("caustic_strength", v));
        ui.AddSlider("Water clarity (depth fade)", 3f, 20f, 11.0f, v => _mat.SetShaderParameter("depth_fade_distance", v));
        ui.AddSlider("Shadow darkness", 0f, 1f, 0.75f, v => _floorMat.SetShaderParameter("shadow_darkness", v));
        ui.AddSlider("Shadow size", 0.5f, 4f, 1.5f, v => _floorMat.SetShaderParameter("shadow_size", v));
        ui.AddToggle("Shadow refracted (ripple)", true, on => _floorMat.SetShaderParameter("shadow_refracted", on));
    }
}
