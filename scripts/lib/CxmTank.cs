using System;

namespace GodotCsharpExperiments.Lib;

// CxmTank — a FAITHFUL C# port of the CXM-1978 reverb
// (../neptunely_standalone/js/audio/cmajor/fx/cxm-1978/cxm-1978.cmajor).
// Lexicon 224-inspired; Dattorro, "Effect Design Part 1", JAES 1997.
//
// TRUE TO SOURCE — everything structural is copied verbatim from the .cmajor:
//   * pre-delay ring
//   * 4-stage input diffuser, delays 229/173/612/447, coeffs 0.75/0.75/0.625/0.625
//   * split-band crossover (2x one-pole lowpass), independent bass/mids decay
//   * figure-of-eight tank: two cross-coupled chains, inputA = in + lastB*fb,
//     inputB = in + lastA*fb
//   * per chain: decay diffuser g=0.7 -> MODULATED delay (cubic Hermite, sin LFO,
//     chains 90 deg apart) -> one-pole damping -> decay diffuser g=0.5 -> plain delay
//   * decay-diffuser delays 1085/2906/1466/4288, tank delays 7188/6005/6807/5106
//
// THE ONE ADAPTATION — the clock. The source runs at 48 kHz, where the longest tank
// delay (7188 samples) is 150 ms. Water wants modal periods of SECONDS, and a tank
// tuned to 150 ms reads as ringing, not swell. So the delay lengths and every ratio
// between them are kept EXACTLY as written and only the tank's sample clock changes:
// Rate = 48000 reproduces the audio patch bit-for-bit; Rate = 480 stretches every time
// constant 100x (7188 "samples" -> 15 s) with the incommensurate ratios — the thing that
// stops the modes phase-locking — untouched. This is not an invention: the real 224 had
// exactly this control (the `clock` param, HiFi/Standard/LoFi).
//
// Scalar in, scalar taps out. No Godot types, no GPU — a few dozen state variables and
// a handful of ops per tick, exactly as advertised.
public sealed class CxmTank
{
    // ---- source delay lengths, verbatim (samples @ the tank's own clock) ----
    private const int DDiff1 = 229, DDiff2 = 173, DDiff3 = 612, DDiff4 = 447;
    private const int DA1 = 1085, DA2 = 2906, DB1 = 1466, DB2 = 4288;
    private const int DDelA1 = 7188, DDelA2 = 6005, DDelB1 = 6807, DDelB2 = 5106;

    // Buffer sizes from the source (next power-of-two-ish headroom above each delay).
    private readonly float[] _diff1 = new float[256], _diff2 = new float[192];
    private readonly float[] _diff3 = new float[640], _diff4 = new float[480];
    private int _wDiff1, _wDiff2, _wDiff3, _wDiff4;

    private readonly float[] _apA1 = new float[1280], _apA2 = new float[3072];
    private readonly float[] _apB1 = new float[1536], _apB2 = new float[4480];
    private int _wA1, _wA2, _wB1, _wB2;

    private readonly float[] _delA1 = new float[7424], _delA2 = new float[6144];
    private readonly float[] _delB1 = new float[7040], _delB2 = new float[5248];
    private int _wDelA1, _wDelA2, _wDelB1, _wDelB2;

    private readonly float[] _preDelayBuf = new float[96000];
    private int _preDelayWrite;
    private int _preDelaySamples = 2016;   // 42 ms @ 48k (HiFi), as in the source

    // ---- coefficients, verbatim ----
    private float _gDiff1 = 0.75f, _gDiff2 = 0.75f, _gDiff3 = 0.625f, _gDiff4 = 0.625f;
    private float _gDecay = 0.7f, _gDecay2 = 0.5f;
    private float _fb = 0.7f;

    // damping one-pole per chain
    private float _z1A, _z1B;
    private float _dampB0 = 0.211f, _dampA1 = 0.577f;   // ~4 kHz @ 48k, the source's init

    // split-band crossover
    private float _lp1Z1, _lp2Z1;
    private float _crossB0 = 0.0232f, _crossA1 = 0.9536f;   // ~362 Hz @ 48k
    private float _bassDecay = 0.7f, _midsDecay = 0.7f;

    // tank modulation — chains 90 degrees apart (phaseB starts at pi/2), as in the source
    private float _phaseA, _phaseB = 1.5708f;
    private float _modDepth = 8.0f, _modRate = 0.8f;

    // cross-feedback state — the figure-of-eight
    private float _lastA, _lastB;

    private float _decayScale = 1.0f;

    /// <summary>The tank's own sample clock in Hz. 48000 == the audio patch verbatim.</summary>
    public float Rate { get; set; } = 48000.0f;

    public CxmTank(float rate)
    {
        Rate = rate;
        // Re-derive the filter coefficients that the source hard-codes for 48 kHz so the
        // FREQUENCIES stay put when the clock moves. Both are expressed as a fraction of
        // the clock, which is what keeps this a faithful rescale rather than a retune.
        SetDamping(4000.0f * rate / 48000.0f);
        SetCrossover(362.0f * rate / 48000.0f);
        SetPreDelayMs(42.0f * 48000.0f / rate);
    }

    // ---- parameter events, mirroring the source's event handlers ----

    public void SetDecay(float mids01)
    {
        _midsDecay = Math.Clamp(mids01, 0.0f, 1.0f);
        _fb = 0.3f + _midsDecay * 0.65f * _decayScale;   // source: event mids()
    }

    public void SetBass(float bass01) => _bassDecay = Math.Clamp(bass01, 0.0f, 1.0f);

    public void SetDamping(float fcHz)
    {
        float w = MathF.Tan(MathF.PI * Math.Clamp(fcHz, Rate * 0.0005f, Rate * 0.49f) / Rate);
        _dampB0 = w / (1.0f + w);
        _dampA1 = (1.0f - w) / (1.0f + w);
    }

    public void SetCrossover(float fcHz)
    {
        float w = MathF.Tan(MathF.PI * Math.Clamp(fcHz, Rate * 0.0004f, Rate * 0.45f) / Rate);
        _crossB0 = w / (1.0f + w);
        _crossA1 = (1.0f - w) / (1.0f + w);
    }

    public void SetPreDelayMs(float ms)
    {
        _preDelaySamples = Math.Clamp((int)(ms * Rate / 1000.0f), 1, _preDelayBuf.Length - 1);
    }

    /// <summary>0 = Room, 1 = Plate, 2 = Hall — the source's `type` event, verbatim.</summary>
    public void SetType(int t)
    {
        if (t == 0) { _decayScale = 0.7f; _gDecay = 0.6f; _gDecay2 = 0.45f; }
        else if (t == 1) { _decayScale = 1.0f; _gDecay = 0.7f; _gDecay2 = 0.5f; }
        else { _decayScale = 1.3f; _gDecay = 0.75f; _gDecay2 = 0.55f; }
        _fb = 0.3f + _midsDecay * 0.65f * _decayScale;
    }

    /// <summary>0 = Low, 1 = Med, 2 = High — the source's `diffusion` event, verbatim.</summary>
    public void SetDiffusion(int d)
    {
        float scale = d == 0 ? 0.4f : (d == 1 ? 0.7f : 0.9f);
        _gDiff1 = 0.75f * scale;
        _gDiff2 = 0.75f * scale;
        _gDiff3 = 0.625f * scale;
        _gDiff4 = 0.625f * scale;
    }

    /// <summary>0 = Low, 1 = Med, 2 = High — the source's `tankMod` event, verbatim.</summary>
    public void SetTankMod(int m)
    {
        if (m == 0) { _modDepth = 4.0f; _modRate = 0.5f; }
        else if (m == 1) { _modDepth = 12.0f; _modRate = 1.0f; }
        else { _modDepth = 24.0f; _modRate = 2.5f; }
    }

    public void SetModDepth(float samples) => _modDepth = Math.Clamp(samples, 0.0f, 24.0f);
    public void SetModRate(float hz) => _modRate = Math.Clamp(hz, 0.0f, 5.0f);

    public void Reset()
    {
        Array.Clear(_diff1); Array.Clear(_diff2); Array.Clear(_diff3); Array.Clear(_diff4);
        Array.Clear(_apA1); Array.Clear(_apA2); Array.Clear(_apB1); Array.Clear(_apB2);
        Array.Clear(_delA1); Array.Clear(_delA2); Array.Clear(_delB1); Array.Clear(_delB2);
        Array.Clear(_preDelayBuf);
        _wDiff1 = _wDiff2 = _wDiff3 = _wDiff4 = 0;
        _wA1 = _wA2 = _wB1 = _wB2 = 0;
        _wDelA1 = _wDelA2 = _wDelB1 = _wDelB2 = 0;
        _preDelayWrite = 0;
        _z1A = _z1B = _lp1Z1 = _lp2Z1 = 0.0f;
        _lastA = _lastB = 0.0f;
        _phaseA = 0.0f;
        _phaseB = 1.5708f;
    }

    // The four raw tank nodes this tick, in the order the source combines them into wetL/wetR:
    // [0] delA1Out  [1] apA2Out  [2] delB1Out  [3] apB2Out.
    // Kept RAW (not pre-mixed into L/R) because for water each node is a place to READ the
    // basin, not a stereo channel — see Taps().
    private readonly float[] _nodes = new float[4];

    /// <summary>
    /// Advance the tank one sample and return the four raw tank nodes.
    /// The source folds these into 2 stereo taps; we hand them out unmixed.
    /// </summary>
    public float[] Process(float input)
    {
        float twoPi = MathF.PI * 2.0f;
        float phaseInc = _modRate * twoPi / Rate;

        // ---- pre-delay ----
        _preDelayBuf[_preDelayWrite] = input;
        float preDelayed = _preDelayBuf[Wrap(_preDelayWrite - _preDelaySamples, _preDelayBuf.Length)];
        _preDelayWrite = Wrap(_preDelayWrite + 1, _preDelayBuf.Length);

        // ---- 4-stage input diffuser ----
        float apD1Out = Allpass(_diff1, ref _wDiff1, DDiff1, _gDiff1, preDelayed);
        float apD2Out = Allpass(_diff2, ref _wDiff2, DDiff2, _gDiff2, apD1Out);
        float apD3Out = Allpass(_diff3, ref _wDiff3, DDiff3, _gDiff3, apD2Out);
        float diffused = Allpass(_diff4, ref _wDiff4, DDiff4, _gDiff4, apD3Out);

        // ---- split-band crossover (two cascaded one-poles = 2nd order) ----
        float lp1 = _crossB0 * diffused + _crossB0 * _lp1Z1 + _crossA1 * _lp1Z1;
        _lp1Z1 = lp1;
        float lp2 = _crossB0 * lp1 + _crossB0 * _lp2Z1 + _crossA1 * _lp2Z1;
        _lp2Z1 = lp2;
        float tankInput = lp2 * _bassDecay + (diffused - lp2) * _midsDecay;

        // ---- figure-of-eight: each chain is fed by the OTHER chain's tail ----
        float inputA = tankInput + _lastB * _fb;
        float inputB = tankInput + _lastA * _fb;

        // Chain A
        float apA1Out = Allpass(_apA1, ref _wA1, DA1, _gDecay, inputA);
        float modA = MathF.Sin(_phaseA) * _modDepth;
        _delA1[_wDelA1] = apA1Out;
        float delA1Out = ReadCubic(_delA1, _wDelA1, MathF.Max((float)DDelA1 + modA, 1.0f));
        _wDelA1 = Wrap(_wDelA1 + 1, _delA1.Length);
        float dampA = _dampB0 * delA1Out + _dampB0 * _z1A + _dampA1 * _z1A;
        _z1A = dampA;
        float apA2Out = Allpass(_apA2, ref _wA2, DA2, _gDecay2, dampA);
        _delA2[_wDelA2] = apA2Out;
        float delA2Out = _delA2[Wrap(_wDelA2 - DDelA2, _delA2.Length)];
        _wDelA2 = Wrap(_wDelA2 + 1, _delA2.Length);
        _lastA = delA2Out;

        // Chain B
        float apB1Out = Allpass(_apB1, ref _wB1, DB1, _gDecay, inputB);
        float modB = MathF.Sin(_phaseB) * _modDepth;
        _delB1[_wDelB1] = apB1Out;
        float delB1Out = ReadCubic(_delB1, _wDelB1, MathF.Max((float)DDelB1 + modB, 1.0f));
        _wDelB1 = Wrap(_wDelB1 + 1, _delB1.Length);
        float dampB = _dampB0 * delB1Out + _dampB0 * _z1B + _dampA1 * _z1B;
        _z1B = dampB;
        float apB2Out = Allpass(_apB2, ref _wB2, DB2, _gDecay2, dampB);
        _delB2[_wDelB2] = apB2Out;
        float delB2Out = _delB2[Wrap(_wDelB2 - DDelB2, _delB2.Length)];
        _wDelB2 = Wrap(_wDelB2 + 1, _delB2.Length);
        _lastB = delB2Out;

        _phaseA += phaseInc;
        _phaseB += phaseInc;
        if (_phaseA >= twoPi) { _phaseA -= twoPi; }
        if (_phaseB >= twoPi) { _phaseB -= twoPi; }

        _nodes[0] = delA1Out;
        _nodes[1] = apA2Out;
        _nodes[2] = delB1Out;
        _nodes[3] = apB2Out;
        return _nodes;
    }

    /// <summary>
    /// The source's stereo fold, for reference / audio-identical checks:
    ///   wetL = (delA1 + apA2 - delB1 + apB2) * 0.25
    ///   wetR = (delB1 + apB2 - delA1 + apA2) * 0.25
    /// </summary>
    public (float L, float R) StereoFold()
    {
        float l = (_nodes[0] + _nodes[1] - _nodes[2] + _nodes[3]) * 0.25f;
        float r = (_nodes[2] + _nodes[3] - _nodes[0] + _nodes[1]) * 0.25f;
        return (l, r);
    }

    // ---- Schroeder allpass, verbatim from the source's inlined form ----
    private static float Allpass(float[] buf, ref int w, int delay, float g, float x)
    {
        float delayed = buf[Wrap(w - delay, buf.Length)];
        float outv = -g * x + delayed;
        buf[w] = x + g * outv;
        w = Wrap(w + 1, buf.Length);
        return outv;
    }

    // ---- cubic Hermite fractional read, verbatim ----
    private static float ReadCubic(float[] buf, int w, float d)
    {
        float frac = d - MathF.Floor(d);
        int idx = (int)MathF.Floor(d);
        int n = buf.Length;
        float y0 = buf[Wrap(w - idx - 1, n)];
        float y1 = buf[Wrap(w - idx, n)];
        float y2 = buf[Wrap(w - idx + 1, n)];
        float y3 = buf[Wrap(w - idx + 2, n)];
        return (((-0.5f * y0 + 1.5f * y1 - 1.5f * y2 + 0.5f * y3) * frac +
                 (y0 - 2.5f * y1 + 2.0f * y2 - 0.5f * y3)) * frac +
                (-0.5f * y0 + 0.5f * y2)) * frac + y1;
    }

    private static int Wrap(int i, int n)
    {
        i %= n;
        return i < 0 ? i + n : i;
    }
}
