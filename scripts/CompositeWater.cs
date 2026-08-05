using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 14 — Composite (multi-band) stylized water. C# port / extraction of the
// multi-band Gerstner field that lives inside water-kit scene 100, pulled into a
// clean water-on-a-seabed scene. Same ameye water look as scene 13 (depth fade,
// HSV shore colour, foam, blended normals + refraction, toon specular) but the
// fixed 4-wave same-wavelength sum is replaced by a MULTI-BAND composite field
// (swell + mid + chop) driven by the C# CompositeGerstner CPU class. The shader
// reads N array-driven bands; the CPU field feeds the SAME parallel arrays.
// https://ameye.dev/notes/stylized-water-shader/
//
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/14_composite_gerstner.tscn 5
public partial class CompositeWater : Node3D
{
    private const string WaterShader = "res://shaders/ameye_water_composite.gdshader";
    private const string FoamTex = "res://textures/ameye/Foam 5.png";
    private const string NormalTex = "res://textures/ameye/Normals 1.png";

    // swell + mid + chop, [wavelength, steepness, speed, direction]
    private static readonly float[][] Bands =
    {
        new[] { 34.0f, 0.13f, 1.10f, 0.12f },
        new[] { 26.0f, 0.10f, 1.00f, 0.28f },
        new[] { 14.0f, 0.10f, 1.05f, 0.52f },
        new[] { 9.0f, 0.09f, 1.15f, 0.66f },
        new[] { 5.0f, 0.07f, 1.35f, 0.82f },
        new[] { 3.0f, 0.05f, 1.60f, 0.40f },
    };

    private CompositeGerstner _field = new();
    private ShaderMaterial _mat = null!;
    private DirectionalLight3D _sun = null!;
    private float _waveAmp = 1.0f;
    private float _waveSpeedScale = 1.0f;

    public override void _Ready()
    {
        // Order matters: BuildEnvironment adds _sun to the tree first, so BuildWater
        // can read _sun.GlobalTransform (valid only once the node is in-tree).
        RebuildField();
        BuildEnvironment();
        BuildSeabed();
        BuildWater();
        BuildUi();
    }

    public override void _PhysicsProcess(double delta)
    {
        _field.Time += (float)delta;
        _mat.SetShaderParameter("wave_time", _field.Time);
    }

    private void BuildEnvironment()
    {
        var cam = new Camera3D
        {
            Position = new Vector3(0.0f, 5.0f, 14.0f),
            RotationDegrees = new Vector3(-18.0f, 0.0f, 0.0f),
            Far = 2000.0f,
            Current = true,
        };
        AddChild(cam);

        _sun = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-40.0f, -120.0f, 0.0f),
            LightEnergy = 1.4f,
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
    }

    private void BuildSeabed()
    {
        // A shallow sandy bed + half-submerged props so the depth fade has shorelines
        // to read against (shallow tint + see-through near objects).
        var bed = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(120, 120) },
            Position = new Vector3(0.0f, -2.2f, 0.0f),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.66f, 0.58f, 0.42f),
                Roughness = 1.0f,
            },
        };
        AddChild(bed);

        var rockMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.38f, 0.36f, 0.32f),
            Roughness = 0.9f,
        };
        Vector3[] rocks =
        {
            new(-4.0f, -0.6f, 2.0f), new(3.5f, -0.9f, -1.0f),
            new(6.5f, -1.2f, 3.0f), new(-6.0f, -1.0f, -3.0f), new(0.5f, -0.5f, 5.0f),
        };
        foreach (var p in rocks)
        {
            AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(2.2f, 3.0f, 2.2f) },
                Position = p,
                MaterialOverride = rockMat,
            });
        }
    }

    private void BuildWater()
    {
        var mi = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(80, 80), SubdivideWidth = 240, SubdivideDepth = 240 },
            ExtraCullMargin = 16.0f,
        };

        _mat = new ShaderMaterial { Shader = GD.Load<Shader>(WaterShader) };
        _mat.SetShaderParameter("foam_tex", GD.Load<Texture2D>(FoamTex));
        _mat.SetShaderParameter("normal_tex", GD.Load<Texture2D>(NormalTex));
        // direction from the water surface TO the sun (light's local +Z after rotation)
        _mat.SetShaderParameter("sun_direction", _sun.GlobalTransform.Basis.Z);

        // Tuned preset — the scene's starting values (shared with scene 13, plus the
        // composite-specific colour + depth-fade overrides).
        _mat.SetShaderParameter("water_roughness", 0.035f);
        _mat.SetShaderParameter("foam_distance", 1.575f);
        _mat.SetShaderParameter("foam_crest", 0.900f);
        _mat.SetShaderParameter("normal_strength", 0.770f);
        _mat.SetShaderParameter("refraction_strength", 1.720f);
        _mat.SetShaderParameter("specular_smoothness", 0.485f);
        _mat.SetShaderParameter("depth_fade_distance", 11.0f);
        _mat.SetShaderParameter("water_color", new Color(0.09f, 0.52f, 0.62f));
        _mat.SetShaderParameter("shallow_color", new Color(0.46f, 0.82f, 0.80f));

        // stage toggles — start with foam + toon lighting off (toggle them in the panel)
        _mat.SetShaderParameter("enable_depth_fade", true);
        _mat.SetShaderParameter("enable_shore_color", true);
        _mat.SetShaderParameter("enable_foam", false);
        _mat.SetShaderParameter("enable_normal_maps", true);
        _mat.SetShaderParameter("enable_refraction", true);
        _mat.SetShaderParameter("enable_lighting", false);

        SyncShaderWaves();

        mi.MaterialOverride = _mat;
        AddChild(mi);
    }

    // Rebuild the CPU field from the band table, scaled by the live amp/speed sliders,
    // then push the parallel arrays to the shader (once the material exists).
    private void RebuildField()
    {
        _field.ClearWaves();
        foreach (var b in Bands)
        {
            _field.AddWave(b[0], b[1] * _waveAmp, b[2] * _waveSpeedScale, b[3]);
        }
        if (_mat != null)
        {
            SyncShaderWaves();
        }
    }

    // Mirror the field's parallel arrays into the shader's array uniforms. A C# float[]
    // maps to the shader's float[MAX_WAVES] uniform; wave_count is an int.
    private void SyncShaderWaves()
    {
        _mat.SetShaderParameter("wave_count", _field.Wavelengths.Count);
        _mat.SetShaderParameter("wavelengths", _field.WavelengthsArr);
        _mat.SetShaderParameter("steepnesses", _field.SteepnessesArr);
        _mat.SetShaderParameter("speeds", _field.SpeedsArr);
        _mat.SetShaderParameter("dirs", _field.DirsArr);
        _mat.SetShaderParameter("wave_time", _field.Time);
    }

    private void BuildUi()
    {
        var ui = new DemoUI(this, "14 · Composite (multi-band) water",
            "Multi-band Gerstner: swell + mid + chop. Toggle stages / scale the sea:");

        // stage toggles (same six as scene 13, same initial states)
        ui.AddToggle("2 · Depth fade", true, p => _mat.SetShaderParameter("enable_depth_fade", p));
        ui.AddToggle("3 · HSV shore color", true, p => _mat.SetShaderParameter("enable_shore_color", p));
        ui.AddToggle("4 · Foam", false, p => _mat.SetShaderParameter("enable_foam", p));
        ui.AddToggle("5 · Normal maps", true, p => _mat.SetShaderParameter("enable_normal_maps", p));
        ui.AddToggle("5 · Refraction", true, p => _mat.SetShaderParameter("enable_refraction", p));
        ui.AddToggle("6 · Toon lighting", false, p => _mat.SetShaderParameter("enable_lighting", p));

        // tunables
        ui.AddSlider("Wave amp", 0.0f, 2.0f, 1.0f, v => { _waveAmp = v; RebuildField(); });
        ui.AddSlider("Wave speed", 0.0f, 2.0f, 1.0f, v => { _waveSpeedScale = v; RebuildField(); });
        ui.AddSlider("Depth fade dist", 0.1f, 20.0f, 11.0f, v => _mat.SetShaderParameter("depth_fade_distance", v));
        ui.AddSlider("Roughness", 0.0f, 1.0f, 0.035f, v => _mat.SetShaderParameter("water_roughness", v));
        ui.AddSlider("Foam distance", 0.0f, 5.0f, 1.575f, v => _mat.SetShaderParameter("foam_distance", v));
        ui.AddSlider("Foam crest", 0.0f, 2.0f, 0.900f, v => _mat.SetShaderParameter("foam_crest", v));
        ui.AddSlider("Normal strength", 0.0f, 2.0f, 0.770f, v => _mat.SetShaderParameter("normal_strength", v));
        ui.AddSlider("Refraction", 0.0f, 4.0f, 1.720f, v => _mat.SetShaderParameter("refraction_strength", v));
        ui.AddSlider("Specular smooth", 0.0f, 1.0f, 0.485f, v => _mat.SetShaderParameter("specular_smoothness", v));
    }
}
