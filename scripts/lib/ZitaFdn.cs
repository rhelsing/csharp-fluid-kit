using System;

namespace GodotCsharpExperiments.Lib;

// ZitaFdn — a faithful C# port of the Zita reverb core from
// ../neptunely_standalone/js/audio/cmajor/patches/fx-zitareverb.cmajor.
//
// A FEEDBACK DELAY NETWORK: 8 parallel delay lines whose outputs are mixed by an orthogonal
// (Hadamard) butterfly and fed back. Structurally different from Dattorro's figure-of-eight,
// which is hardwired to TWO cross-coupled chains — an FDN's channel count is set by its mixing
// matrix, so it generalizes to N.
//
// That difference is the whole reason this is in the series. Dattorro gives four raw nodes;
// Zita gives EIGHT channels natively, and for water each channel is a place. Whether N-way
// orthogonal mixing buys anything spatially over two cross-coupled chains is the question
// 24_zita asks.
//
// TRUE TO SOURCE — copied verbatim:
//   * the two delay tables, in seconds (delayParams1 = allpass, delayParams2 = main line)
//   * DelayWithFeedback: an allpass with c = +/-0.6 alternating by channel index
//   * Filter: the per-channel RT60 / damping shelf, gmf/glo/wlo/whi exactly as written
//   * shuffle(): the 12-butterfly Hadamard mix, 3 stages of 4
//   * g = sqrt(0.125) — the orthonormal 1/sqrt(8) scaling that makes the mix lossless
//   * input polarity (+,+,-,- / +,+,-,-) and the y[1]+y[2] / y[1]-y[2] stereo fold
//
// THE ONE ADAPTATION — the clock, handled exactly as CxmTank does it. The source's delays are
// in SECONDS, which would make the tail ~0.25 s at any sample rate. To reach water timescales
// the lengths are converted to samples at a nominal 48 kHz and then clocked at Rate, so
// Rate = 48000 is the audio patch bit-for-bit and lowering it stretches every delay together
// with all ratios intact.
public sealed class ZitaFdn
{
    public const int Channels = 8;
    private const int DelaySize = 16384;
    private const float NominalRate = 48000.0f;

    // seconds — verbatim from the source
    private static readonly float[] DelayParams1 =
        { 20346e-6f, 24421e-6f, 31604e-6f, 27333e-6f, 22904e-6f, 29291e-6f, 13458e-6f, 19123e-6f };
    private static readonly float[] DelayParams2 =
        { 153129e-6f, 210389e-6f, 127837e-6f, 256891e-6f, 174713e-6f, 192303e-6f, 125000e-6f, 219991e-6f };

    // ---- DelayWithFeedback: allpass, verbatim ----
    private sealed class Ap
    {
        private readonly float[] _line = new float[DelaySize];
        private int _read, _write;
        private float _c;

        public void Init(int size, float c)
        {
            Array.Clear(_line);
            _write = size % DelaySize;
            _read = 0;
            _c = c;
        }

        public float Process(float x)
        {
            float z = _line[_read];
            x -= _c * z;
            _line[_write] = x;
            _write = (_write + 1) % DelaySize;
            _read = (_read + 1) % DelaySize;
            return z + _c * x;
        }
    }

    // ---- Filter: the RT60 / damping shelf, verbatim ----
    private sealed class Shelf
    {
        private float _gmf, _glo, _wlo, _whi, _slo, _shi;

        public void SetParams(float del, float tmf, float tlo, float wlo, float thi, float chi)
        {
            _gmf = MathF.Pow(0.001f, del / tmf);
            _glo = MathF.Pow(0.001f, del / tlo) / _gmf - 1.0f;
            _wlo = wlo;
            float g = MathF.Pow(0.001f, del / thi) / _gmf;
            float t = (1.0f - g * g) / (2.0f * g * g * chi);
            _whi = (MathF.Sqrt(1.0f + 4.0f * t) - 1.0f) / (2.0f * t);
        }

        public float Process(float x)
        {
            _slo += _wlo * (x - _slo) + 1.0e-10f;   // the 1e-10 is the source's denormal guard
            x += _glo * _slo;
            _shi += _whi * (x - _shi);
            return _gmf * _shi;
        }

        public void Reset() { _slo = 0.0f; _shi = 0.0f; }
    }

    // ---- plain Delay, verbatim ----
    private sealed class Line
    {
        private readonly float[] _line = new float[DelaySize];
        private int _read, _write;

        public void Init(int size)
        {
            Array.Clear(_line);
            _write = size % DelaySize;
            _read = 0;
        }

        public float Read() => _line[_read];

        public void Write(float f)
        {
            _line[_write] = f;
            _write = (_write + 1) % DelaySize;
            _read = (_read + 1) % DelaySize;
        }
    }

    private readonly Ap[] _ap = new Ap[Channels];
    private readonly Shelf[] _filter = new Shelf[Channels];
    private readonly Line[] _delay = new Line[Channels];
    private readonly float[] _x = new float[Channels];
    private readonly float[] _taps = new float[Channels];

    private float _rate;
    private float _crossover = 200.0f;
    private float _rtLow = 3.0f, _rtMid = 2.0f;
    private float _damping = 6000.0f;

    /// <summary>The network's own sample clock. 48000 == the audio patch verbatim.</summary>
    public float Rate
    {
        get => _rate;
        set { _rate = value; Retune(); }
    }

    public ZitaFdn(float rate)
    {
        for (int i = 0; i < Channels; ++i)
        {
            _ap[i] = new Ap();
            _filter[i] = new Shelf();
            _delay[i] = new Line();
        }
        _rate = rate;
        Init();
        Retune();
    }

    // Lengths are taken at the NOMINAL 48 kHz and then clocked at Rate, so the ratios are the
    // source's and only the overall timescale moves.
    private void Init()
    {
        for (int i = 0; i < Channels; ++i)
        {
            int k1 = (int)MathF.Floor(DelayParams1[i] * NominalRate + 0.5f);
            int k2 = (int)MathF.Floor(DelayParams2[i] * NominalRate + 0.5f);
            _ap[i].Init(k1, (i & 1) != 0 ? -0.6f : 0.6f);
            _delay[i].Init(k2 - k1);
        }
    }

    private void Retune()
    {
        float sr = MathF.Max(_rate, 1.0f);
        float wlo = 6.2832f * _crossover / sr;
        float chi = _damping > 0.49f * sr ? 2.0f : 1.0f - MathF.Cos(6.2832f * _damping / sr);
        for (int i = 0; i < Channels; ++i)
        {
            _filter[i].SetParams(DelayParams2[i], _rtMid, _rtLow, wlo, 0.5f * _rtMid, chi);
        }
    }

    public void SetCrossover(float hz) { _crossover = hz; Retune(); }
    public void SetRtLow(float secs) { _rtLow = MathF.Max(secs, 0.1f); Retune(); }
    public void SetRtMid(float secs) { _rtMid = MathF.Max(secs, 0.1f); Retune(); }
    public void SetDamping(float hz) { _damping = hz; Retune(); }

    public void Reset()
    {
        Init();
        foreach (var f in _filter) { f.Reset(); }
        Array.Clear(_x);
        Array.Clear(_taps);
    }

    // The Hadamard butterfly, verbatim: 3 stages of 4 in-place shuffles, which is what makes
    // the mix orthogonal (energy-preserving) and spreads every channel into every other.
    private static void Sh(float[] a, int i, int j)
    {
        float diff = a[i] - a[j];
        a[i] += a[j];
        a[j] = diff;
    }

    private static void Shuffle(float[] a)
    {
        Sh(a, 0, 1); Sh(a, 2, 3); Sh(a, 4, 5); Sh(a, 6, 7);
        Sh(a, 0, 2); Sh(a, 1, 3); Sh(a, 4, 6); Sh(a, 5, 7);
        Sh(a, 0, 4); Sh(a, 1, 5); Sh(a, 2, 6); Sh(a, 3, 7);
    }

    /// <summary>
    /// Advance one sample and return all EIGHT channel values. The source folds these to stereo
    /// as (y1+y2, y1-y2); handed out unmixed here because for water each channel is a place.
    /// </summary>
    public float[] Process(float input)
    {
        float g = MathF.Sqrt(0.125f);   // 1/sqrt(8): the orthonormal scaling
        float t0 = 0.3f * input;
        float t1 = 0.3f * input;        // mono in, driven into both halves as the source does

        _x[0] = _ap[0].Process(_delay[0].Read() + t0);
        _x[1] = _ap[1].Process(_delay[1].Read() + t0);
        _x[2] = _ap[2].Process(_delay[2].Read() - t0);
        _x[3] = _ap[3].Process(_delay[3].Read() - t0);
        _x[4] = _ap[4].Process(_delay[4].Read() + t1);
        _x[5] = _ap[5].Process(_delay[5].Read() + t1);
        _x[6] = _ap[6].Process(_delay[6].Read() - t1);
        _x[7] = _ap[7].Process(_delay[7].Read() - t1);

        Shuffle(_x);

        for (int i = 0; i < Channels; ++i)
        {
            _taps[i] = _x[i];
            _delay[i].Write(_filter[i].Process(g * _x[i]));
        }
        return _taps;
    }

    /// <summary>The source's stereo fold, for audio-identical reference.</summary>
    public (float L, float R) StereoFold() => (_taps[1] + _taps[2], _taps[1] - _taps[2]);
}
