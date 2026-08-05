using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 13 — Stylized Water (ameye.dev), C# port of water-kit scene 08
// (scene_08_ameye_water.gd). Gerstner wave surface built stage by stage: depth
// fade · HSV shore color · foam · refraction · blended normals · toon lighting,
// each toggleable in the panel. The shader is portable verbatim from water-kit.
// https://ameye.dev/notes/stylized-water-shader/
//
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/13_ameye_water.tscn 5
public partial class AmeyeWater : Node3D
{
    private const string WaterShader = "res://shaders/ameye_water.gdshader";
    private const string FoamTex = "res://textures/ameye/Foam 5.png";
    private const string NormalTex = "res://textures/ameye/Normals 1.png";

    private ShaderMaterial _mat = null!;
    private DirectionalLight3D _sun = null!;
    private Vector4 _dirs = new(0.175f, 0.220f, 0.470f, 0.800f);

    public override void _Ready()
    {
        // Order matters: BuildEnvironment adds _sun to the tree first, so BuildWater
        // can read _sun.GlobalTransform (valid only once the node is in-tree).
        BuildEnvironment();
        BuildSeabed();
        BuildWater();
        BuildUi();
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
        _mat.SetShaderParameter("directions", _dirs);

        // Tuned preset — the scene's starting values.
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

        // start with foam + toon lighting off (toggle them back on in the panel)
        _mat.SetShaderParameter("enable_foam", false);
        _mat.SetShaderParameter("enable_lighting", false);

        mi.MaterialOverride = _mat;
        AddChild(mi);
    }

    private void BuildUi()
    {
        var ui = new DemoUI(this, "13 · Stylized water (ameye) — full",
            "Ported stage by stage. Toggle each stage on/off:");

        // stage toggles
        ui.AddToggle("2 · Depth fade", true, p => _mat.SetShaderParameter("enable_depth_fade", p));
        ui.AddToggle("3 · HSV shore color", true, p => _mat.SetShaderParameter("enable_shore_color", p));
        ui.AddToggle("4 · Foam", false, p => _mat.SetShaderParameter("enable_foam", p));
        ui.AddToggle("5 · Normal maps", true, p => _mat.SetShaderParameter("enable_normal_maps", p));
        ui.AddToggle("5 · Refraction", true, p => _mat.SetShaderParameter("enable_refraction", p));
        ui.AddToggle("6 · Toon lighting", false, p => _mat.SetShaderParameter("enable_lighting", p));

        // tunables
        ui.AddSlider("Steepness", 0.0f, 1.0f, 0.030f, v => _mat.SetShaderParameter("steepness", v));
        ui.AddSlider("Wavelength", 0.5f, 40.0f, 17.090f, v => _mat.SetShaderParameter("wavelength", v));
        ui.AddSlider("Wave speed", 0.0f, 4.0f, 3.840f, v => _mat.SetShaderParameter("wave_speed", v));
        ui.AddSlider("Roughness", 0.0f, 1.0f, 0.035f, v => _mat.SetShaderParameter("water_roughness", v));
        ui.AddSlider("Depth fade dist", 0.1f, 20.0f, 6.567f, v => _mat.SetShaderParameter("depth_fade_distance", v));
        ui.AddSlider("Foam distance", 0.0f, 5.0f, 1.575f, v => _mat.SetShaderParameter("foam_distance", v));
        ui.AddSlider("Foam crest", 0.0f, 2.0f, 0.900f, v => _mat.SetShaderParameter("foam_crest", v));
        ui.AddSlider("Normal strength", 0.0f, 2.0f, 0.770f, v => _mat.SetShaderParameter("normal_strength", v));
        ui.AddSlider("Refraction", 0.0f, 4.0f, 1.720f, v => _mat.SetShaderParameter("refraction_strength", v));
        ui.AddSlider("Specular smooth", 0.0f, 1.0f, 0.485f, v => _mat.SetShaderParameter("specular_smoothness", v));

        ui.AddSlider("Dir 1", 0.0f, 1.0f, _dirs.X, v => { _dirs.X = v; _mat.SetShaderParameter("directions", _dirs); });
        ui.AddSlider("Dir 2", 0.0f, 1.0f, _dirs.Y, v => { _dirs.Y = v; _mat.SetShaderParameter("directions", _dirs); });
        ui.AddSlider("Dir 3", 0.0f, 1.0f, _dirs.Z, v => { _dirs.Z = v; _mat.SetShaderParameter("directions", _dirs); });
        ui.AddSlider("Dir 4", 0.0f, 1.0f, _dirs.W, v => { _dirs.W = v; _mat.SetShaderParameter("directions", _dirs); });
    }
}
