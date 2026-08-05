using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 10 — Phase 4c: the 3D fluid's density as real VOLUMETRIC smoke, via Godot's
// built-in FogVolume (no custom ray marching). FluidSim3D runs the same 3D Stam sim as
// scene 09 (the stamper does the 3D pressure projection); its density volume is wrapped
// in a Texture3DRD and sampled live by a `shader_type fog` shader inside the engine's
// froxel volumetrics. No readback — the fog samples the RD texture directly.
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/10_fog3d.tscn 8 1280x900
public partial class StampFog3D : Node3D
{
    private static readonly Vector3I Grid = new(48, 48, 48);
    private const float WorldSize = 4.6f;
    private const string FogShaderPath = "res://shaders/fluid_fog.gdshader";

    private Camera3D _cam = null!;
    private ShaderMaterial _fogMat = null!;
    private Texture3Drd _densityTex = null!;
    private FluidSim3D? _fluid;
    private Label? _readout;
    private int _tick;

    private float _dt = 1.0f;
    private int _iters = 30;
    private float _buoy = 4.0f;
    private float _dyeAmt = 0.35f;
    private float _srcRadius = 6.0f;
    private float _gain = 6.0f;

    private static readonly Vector3 SrcCenter = new(24, 6, 24);

    public override void _Ready()
    {
        BuildEnvironment();
        BuildFog();
        RenderingServer.CallOnRenderThread(Callable.From(InitSim));
        BuildUi();
    }

    private void InitSim() => _fluid = new FluidSim3D(RenderingServer.GetRenderingDevice(), Grid);

    private void BuildEnvironment()
    {
        _cam = new Camera3D { Fov = 55.0f, Position = new Vector3(0, 0.4f, 6.6f), Far = 200.0f, Current = true };
        AddChild(_cam);
        _cam.LookAt(new Vector3(0, 0.2f, 0), Vector3.Up);

        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-40, -35, 0),
            LightColor = new Color(0.9f, 0.95f, 1.0f),
            LightEnergy = 1.4f,
        });

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.015f, 0.02f, 0.04f),
            TonemapMode = Godot.Environment.ToneMapper.Agx,
            VolumetricFogEnabled = true,
            VolumetricFogDensity = 0.0f,          // no base air fog; the FogVolume supplies it
            VolumetricFogAlbedo = new Color(0.9f, 0.92f, 1.0f),
            VolumetricFogLength = 24.0f,
            VolumetricFogDetailSpread = 2.0f,
        };
        AddChild(new WorldEnvironment { Environment = env });
    }

    private void BuildFog()
    {
        _densityTex = new Texture3Drd();
        _fogMat = new ShaderMaterial { Shader = GD.Load<Shader>(FogShaderPath) };
        _fogMat.SetShaderParameter("density_tex", _densityTex);
        _fogMat.SetShaderParameter("gain", _gain);

        AddChild(new FogVolume
        {
            Shape = RenderingServer.FogVolumeShape.Box,
            Size = new Vector3(WorldSize, WorldSize, WorldSize),
            Material = _fogMat,
        });
    }

    public override void _PhysicsProcess(double delta)
    {
        _densityTex.TextureRdRid = _fluid?.DensityRid ?? default;

        float t = _tick * 0.05f;
        float ivx = 0.5f * Mathf.Sin(t), ivz = 0.5f * Mathf.Cos(t * 1.3f);
        float[] add =
        {
            Grid.X, Grid.Y, Grid.Z, 0f, _dt, _buoy,
            SrcCenter.X, SrcCenter.Y, SrcCenter.Z, _srcRadius, ivx, 0.6f, ivz, _dyeAmt, 0f, 0f,
        };
        float[] advV = { Grid.X, Grid.Y, Grid.Z, 0f, _dt, 0.999f, 0f, 0f };
        float[] sim = { Grid.X, Grid.Y, Grid.Z, 0f, 0f, 0f, 0f, 0f };
        float[] advD = { Grid.X, Grid.Y, Grid.Z, 0f, _dt, 0.965f, 0f, 0f };
        byte[] addB = ToBytes(add), advVB = ToBytes(advV), simB = ToBytes(sim), advDB = ToBytes(advD);
        int iters = _iters;
        RenderingServer.CallOnRenderThread(Callable.From(() => _fluid?.Step(addB, advVB, simB, advDB, iters)));

        _tick++;
        if (_readout != null && _tick % 12 == 0)
        {
            _readout.Text = $"3D fog · FogVolume · {_iters} pressure iters · {Grid.X}³ · {Engine.GetFramesPerSecond():0}fps";
        }
    }

    private static byte[] ToBytes(float[] f)
    {
        var b = new byte[f.Length * sizeof(float)];
        Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return b;
    }

    public override void _ExitTree()
    {
        if (_densityTex != null) { _densityTex.TextureRdRid = default; }
        RenderingServer.CallOnRenderThread(Callable.From(() => _fluid?.Free()));
    }

    private void BuildUi()
    {
        var ui = new DemoUI(this, "10 · 3D fluid → volumetric FogVolume (C#)",
            "Phase 4 — the same FluidSim3D as scene 09, but its density volume is shown as REAL volumetric "
            + "smoke: a Texture3DRD wraps the RD density texture and a `shader_type fog` shader samples it "
            + "inside Godot's froxel volumetrics — no readback, no custom ray marching. The stamper still "
            + "does the 3D Poisson pressure projection each tick. Lean on the engine's volumetrics for the "
            + "look; the sim is all ours.");
        _readout = ui.AddReadout("3D fog —");
        ui.AddSlider("Fog gain", 1.0f, 20.0f, _gain, v => { _gain = v; _fogMat.SetShaderParameter("gain", v); });
        ui.AddSlider("Buoyancy", 0.0f, 8.0f, _buoy, v => _buoy = v);
        ui.AddSlider("Dye amount", 0.0f, 1.0f, _dyeAmt, v => _dyeAmt = v);
        ui.AddSlider("Source radius", 3.0f, 14.0f, _srcRadius, v => _srcRadius = v);
        ui.AddSlider("Pressure iters", 4, 60, _iters, v => _iters = (int)v);
        ui.AddSlider("Advect dt", 0.2f, 2.0f, _dt, v => _dt = v);
    }
}
