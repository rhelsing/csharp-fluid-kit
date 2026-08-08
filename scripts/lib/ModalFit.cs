using System;
using Godot;

namespace GodotCsharpExperiments.Lib;

// ModalFit — Path E's missing half: turn a recorded impulse response into a set of damped
// oscillators, so it can be replayed by resonators instead of by a stored kernel.
//
// path-e §2 is explicit that this belongs in MODE SPACE, not sample space. 24_ir does sample
// space: whole-field slices, an FIR, 268 MB at 1024 slices. Here the same response becomes N
// scalar time series
//
//     a_n[k] = SUM_cells  h(x, z, k) * PHI_n(x, z)
//
// each of which should look like  A * exp(-sigma*k) * cos(omega*k + phi)  if the basin really
// does separate into independent damped modes. Fit sigma and omega per mode, and replay is a
// bank of 2-pole resonators with NO stored kernel at all.
//
// The projection runs host-side during capture. It is a one-time offline cost, and doing it in
// the shader would mean writing partial sums into the ping-pong state and corrupting the sim.
public sealed class ModalFit
{
    public const int MaxModes = 8;

    // Must match modeIndex() in exp_sim.glsl — the synthesis basis and the analysis basis have
    // to be the same object or the fit describes modes the replay cannot produce.
    private static readonly Vector2[] Modes =
    {
        new(1, 2), new(2, 1), new(2, 3), new(3, 2),
        new(1, 4), new(4, 1), new(3, 5), new(5, 3),
    };

    private readonly int _n;
    private readonly int _modes;
    private readonly float[][] _series;      // [mode][tick] — the measured coefficient history
    private readonly float[] _basisScratch;
    private int _ticks;

    public float[] Omega { get; }            // rad/tick
    public float[] Sigma { get; }            // decay per tick
    public float[] Amp { get; }
    public bool Fitted { get; private set; }
    public int Ticks => _ticks;
    public int ModeCount => _modes;

    public ModalFit(int gridN, int modes, int capacity)
    {
        _n = gridN;
        _modes = Math.Clamp(modes, 1, MaxModes);
        _series = new float[_modes][];
        for (int m = 0; m < _modes; ++m) { _series[m] = new float[capacity]; }
        _basisScratch = new float[gridN * gridN];
        Omega = new float[_modes];
        Sigma = new float[_modes];
        Amp = new float[_modes];
    }

    public void Reset()
    {
        _ticks = 0;
        Fitted = false;
        Array.Clear(Omega); Array.Clear(Sigma); Array.Clear(Amp);
    }

    /// <summary>
    /// Project one captured tick onto every mode. `field` is the RGBA32F readback; only .r
    /// (height) is used, so the stride is 4 floats per cell.
    /// </summary>
    public void Accumulate(float[] field)
    {
        if (_ticks >= _series[0].Length) { return; }
        for (int m = 0; m < _modes; ++m)
        {
            float mx = Modes[m].X * MathF.PI, mz = Modes[m].Y * MathF.PI;
            double acc = 0.0;
            for (int y = 0; y < _n; ++y)
            {
                float vz = MathF.Cos(mz * (y + 0.5f) / _n);
                int row = y * _n;
                for (int x = 0; x < _n; ++x)
                {
                    acc += field[(row + x) * 4] * MathF.Cos(mx * (x + 0.5f) / _n) * vz;
                }
            }
            _series[m][_ticks] = (float)(acc / (_n * _n));
        }
        ++_ticks;
    }

    /// <summary>
    /// Fit each mode's time series to A*exp(-sigma*k)*cos(omega*k + phi).
    ///   omega — from the dominant DFT bin, which is robust to the noise a short record has.
    ///   sigma — least-squares slope of log|envelope|, sampled at local peaks so the cosine's
    ///           own zero crossings do not drag the regression down.
    /// A mode whose series is essentially zero is reported with Amp 0 and skipped at replay.
    /// </summary>
    public void Fit()
    {
        int n = _ticks;
        if (n < 16) { Fitted = false; return; }

        for (int m = 0; m < _modes; ++m)
        {
            float[] a = _series[m];
            float peak = 0.0f;
            for (int k = 0; k < n; ++k) { peak = MathF.Max(peak, MathF.Abs(a[k])); }
            Amp[m] = peak;
            if (peak < 1.0e-9f) { Omega[m] = 0.0f; Sigma[m] = 1.0f; continue; }

            // --- omega: dominant DFT bin (skip bin 0, which is the DC the leak term removes)
            int bestBin = 1;
            double bestMag = -1.0;
            int bins = Math.Min(n / 2, 256);
            for (int b = 1; b < bins; ++b)
            {
                double re = 0.0, im = 0.0;
                double w = 2.0 * Math.PI * b / n;
                for (int k = 0; k < n; ++k)
                {
                    re += a[k] * Math.Cos(w * k);
                    im -= a[k] * Math.Sin(w * k);
                }
                double mag = re * re + im * im;
                if (mag > bestMag) { bestMag = mag; bestBin = b; }
            }
            Omega[m] = (float)(2.0 * Math.PI * bestBin / n);

            // --- sigma: regress log|peak envelope| against k, STARTING AT THE PEAK.
            // A point impulse does not excite a mode instantly — energy spreads into the mode
            // shape first, so the envelope RISES before it decays. Regressing across the whole
            // record averages that rise against the decay and reports sigma ~ 0, which then
            // makes every resonator marginally stable (r = 1) and ring forever. Measuring decay
            // from the peak onward is also just what RT60 estimation does.
            int peakAt = 0;
            for (int k = 0; k < n; ++k) { if (MathF.Abs(a[k]) >= peak * 0.999f) { peakAt = k; break; } }
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            int cnt = 0;
            for (int k = Math.Max(peakAt, 1); k < n - 1; ++k)
            {
                float v = MathF.Abs(a[k]);
                if (v < MathF.Abs(a[k - 1]) || v < MathF.Abs(a[k + 1])) { continue; }   // peaks only
                if (v < peak * 1.0e-4f) { continue; }
                double ly = Math.Log(v);
                sx += k; sy += ly; sxx += (double)k * k; sxy += k * ly; ++cnt;
            }
            if (cnt >= 3)
            {
                double denom = cnt * sxx - sx * sx;
                double slope = Math.Abs(denom) < 1e-12 ? 0.0 : (cnt * sxy - sx * sy) / denom;
                // Floor it: a fitted decay of exactly zero puts the resonator pole ON the unit
                // circle, which rings forever and drifts unstable in float. Never trust a
                // measurement to keep a feedback loop inside the circle.
                Sigma[m] = (float)Math.Clamp(-slope, 1.0e-4, 1.0);
            }
            else { Sigma[m] = 0.01f; }   // too few peaks to fit — assume a mild decay
        }
        Fitted = true;
    }

    public string Describe(int m) =>
        Amp[m] < 1.0e-9f ? $"m{m}: silent"
        : $"m{m}: f={Omega[m] / (2.0f * MathF.PI):0.0000}/tick  decay={Sigma[m]:0.0000}  A={Amp[m]:0.00000}";
}

// A bank of two-pole resonators — the replay side. One per fitted mode:
//     y[k] = 2*r*cos(theta)*y[k-1] - r^2*y[k-2] + x[k]
// r = exp(-sigma), theta = omega. This is the whole runtime state: two floats per mode. No
// kernel, no slices, no CFL, no neighbours.
public sealed class ResonatorBank
{
    private readonly int _n;
    private readonly float[] _y1, _y2, _a1, _a2, _gain;
    private readonly float[] _out;

    public ResonatorBank(int modes)
    {
        _n = modes;
        _y1 = new float[modes]; _y2 = new float[modes];
        _a1 = new float[modes]; _a2 = new float[modes];
        _gain = new float[modes]; _out = new float[modes];
    }

    public void Configure(ModalFit fit)
    {
        for (int m = 0; m < _n && m < fit.ModeCount; ++m)
        {
            float r = MathF.Exp(-MathF.Max(fit.Sigma[m], 1.0e-5f));
            r = MathF.Min(r, 0.99999f);            // strictly inside the unit circle
            _a1[m] = 2.0f * r * MathF.Cos(fit.Omega[m]);
            _a2[m] = -r * r;
            _gain[m] = fit.Amp[m];
            _y1[m] = _y2[m] = 0.0f;
        }
    }

    public void Reset() { Array.Clear(_y1); Array.Clear(_y2); }

    /// <summary>Drive every resonator with the same input and return their outputs.</summary>
    public float[] Process(float x)
    {
        for (int m = 0; m < _n; ++m)
        {
            float y = _a1[m] * _y1[m] + _a2[m] * _y2[m] + x * _gain[m];
            _y2[m] = _y1[m];
            _y1[m] = y;
            _out[m] = y;
        }
        return _out;
    }
}
