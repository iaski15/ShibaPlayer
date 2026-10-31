using System.Text.Json.Serialization;

namespace ShibaPlayer;

// ShibaPlayer's own sound effects: compressor, stereo widener, spatializer (reverb) and speed/pitch.
// All of them work on interleaved float samples on the playback thread; settings are read as they change.

/// <summary>Everything the Effects tab controls, apart from the equalizer's band gains. Saved as effects.json.</summary>
public sealed class EffectSettings
{
    public bool EqOn { get; set; } = true;
    public float Preamp { get; set; }

    public bool CompOn { get; set; }
    public float RmsPeak { get; set; } = 0.2f;
    public float Attack { get; set; } = 25;
    public float Release { get; set; } = 100;
    public float Threshold { get; set; } = -11;
    public float Ratio { get; set; } = 4;
    public float Knee { get; set; } = 5;
    public float Makeup { get; set; } = 4;

    public bool SpatialOn { get; set; }
    public float RoomSize { get; set; } = 8;
    public float RoomWidth { get; set; } = 10;
    public float Wet { get; set; } = 4;
    public float Dry { get; set; } = 5;
    public float Damp { get; set; } = 5;

    public bool WidenOn { get; set; }
    public float WidenDelay { get; set; } = 20;
    public float WidenFeedback { get; set; } = 0.3f;
    public float WidenCross { get; set; } = 0.3f;
    public float WidenDry { get; set; } = 0.8f;

    // Not saved: a song that's mysteriously slow next launch helps nobody.
    [JsonIgnore] public double Speed { get; set; } = 1;
    [JsonIgnore] public double Pitch { get; set; } // semitones

    /// <summary>Rms/peak, attack ms, release ms, threshold dB, ratio, knee dB, makeup dB.</summary>
    public static readonly (string Name, float[] Values)[] CompressorPresets =
    [
        ("Gentle", [0f, 30, 200, -18, 2, 6, 3]),
        ("Music", [0.2f, 25, 100, -11, 4, 5, 4]),
        ("Vocals", [0.3f, 10, 120, -20, 3, 6, 6]),
        ("Punchy drums", [1f, 30, 80, -16, 4, 2, 5]),
        ("Loud", [0.5f, 5, 80, -24, 6, 4, 10]),
        ("Night mode", [0.1f, 10, 250, -30, 8, 8, 14]),
        ("Limiter", [1f, 1.5f, 60, -3, 20, 1, 0]),
    ];
}

/// <summary>Compressor, then widener, then spatializer. One per track.</summary>
public sealed class EffectChain(EffectSettings fx, int channels, int rate)
{
    private readonly Compressor _comp = new(fx, channels, rate);
    private readonly Widener _widen = new(fx, rate);
    private readonly Reverb _reverb = new(fx, rate);

    public void Reset()
    {
        _comp.Reset();
        _widen.Reset();
        _reverb.Reset();
    }

    public void Process(Span<float> s)
    {
        if (fx.CompOn) _comp.Process(s);
        if (channels != 2) return; // both of these are about left vs right
        if (fx.WidenOn) _widen.Process(s);
        if (fx.SpatialOn) _reverb.Process(s);
    }
}

/// <summary>Feed-forward compressor with a soft knee; all channels share one gain so the stereo image holds still.</summary>
public sealed class Compressor(EffectSettings fx, int channels, int rate)
{
    private double _rms2, _env;

    public void Reset() => _rms2 = _env = 0;

    public void Process(Span<float> s)
    {
        double attack = Coefficient(fx.Attack), release = Coefficient(fx.Release), rmsCoef = Coefficient(20);
        double threshold = fx.Threshold, slope = 1 / Math.Max(1, fx.Ratio) - 1, knee = Math.Max(0.01, fx.Knee), mix = fx.RmsPeak;
        var makeup = fx.Makeup;
        for (int i = 0; i + channels <= s.Length; i += channels)
        {
            double peak = 0;
            for (int c = 0; c < channels; c++) peak = Math.Max(peak, Math.Abs(s[i + c]));
            _rms2 = rmsCoef * _rms2 + (1 - rmsCoef) * peak * peak;
            var level = mix * peak + (1 - mix) * Math.Sqrt(_rms2);
            var over = 20 * Math.Log10(Math.Max(level, 1e-6)) - threshold;

            double target = over * 2 < -knee ? 0
                : over * 2 > knee ? slope * over
                : slope * (over + knee / 2) * (over + knee / 2) / (2 * knee);
            var coef = target < _env ? attack : release; // gain going down = attack
            _env = target + coef * (_env - target);

            var gain = (float)Math.Pow(10, (_env + makeup) / 20);
            for (int c = 0; c < channels; c++) s[i + c] *= gain;
        }

        double Coefficient(double ms) => Math.Exp(-1 / (Math.Max(ms, 0.1) * 0.001 * rate));
    }
}

/// <summary>Stereo widener: each side gets the other's delayed signal, inverted, which pushes the sound outward.</summary>
public sealed class Widener(EffectSettings fx, int rate)
{
    private readonly float[] _left = new float[rate / 5], _right = new float[rate / 5]; // 200 ms of room
    private int _write;

    public void Reset()
    {
        Array.Clear(_left);
        Array.Clear(_right);
    }

    public void Process(Span<float> s)
    {
        int delay = Math.Clamp((int)(fx.WidenDelay * 0.001 * rate), 1, _left.Length - 1);
        float feedback = fx.WidenFeedback, cross = fx.WidenCross, dry = fx.WidenDry;
        for (int i = 0; i + 2 <= s.Length; i += 2)
        {
            int read = (_write - delay + _left.Length) % _left.Length;
            float dl = _left[read], dr = _right[read], l = s[i], r = s[i + 1];
            _left[_write] = l + feedback * dl;
            _right[_write] = r + feedback * dr;
            s[i] = dry * l - cross * dr;
            s[i + 1] = dry * r - cross * dl;
            _write = (_write + 1) % _left.Length;
        }
    }
}

/// <summary>Spatializer: a room reverb built the classic Schroeder way, 8 damped combs into 4 allpasses per side.</summary>
public sealed class Reverb
{
    private static readonly int[] CombTunings = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];
    private static readonly int[] AllpassTunings = [556, 441, 341, 225];
    private const int Spread = 23; // right side is slightly detuned, which is what makes it sound wide

    private readonly EffectSettings _fx;
    private readonly float[][] _combs, _allpasses;
    private readonly float[] _combState;
    private readonly int[] _combAt, _allpassAt;

    public Reverb(EffectSettings fx, int rate)
    {
        _fx = fx;
        var scale = rate / 44100.0;
        _combs = [.. CombTunings.Select(t => new float[(int)(t * scale)]), .. CombTunings.Select(t => new float[(int)((t + Spread) * scale)])];
        _allpasses = [.. AllpassTunings.Select(t => new float[(int)(t * scale)]), .. AllpassTunings.Select(t => new float[(int)((t + Spread) * scale)])];
        _combState = new float[_combs.Length];
        _combAt = new int[_combs.Length];
        _allpassAt = new int[_allpasses.Length];
    }

    public void Reset()
    {
        foreach (var b in _combs) Array.Clear(b);
        foreach (var b in _allpasses) Array.Clear(b);
        Array.Clear(_combState);
    }

    public void Process(Span<float> s)
    {
        float feedback = _fx.RoomSize / 10 * 0.28f + 0.7f, damp = _fx.Damp / 10 * 0.4f, width = _fx.RoomWidth / 10;
        float wet = _fx.Wet / 10 * 3, wet1 = wet * (width / 2 + 0.5f), wet2 = wet * (1 - width) / 2, dry = _fx.Dry / 10 * 2;
        int combs = CombTunings.Length, allpasses = AllpassTunings.Length;
        for (int i = 0; i + 2 <= s.Length; i += 2)
        {
            float l = s[i], r = s[i + 1], input = (l + r) * 0.015f, outL = 0, outR = 0;
            for (int c = 0; c < combs; c++)
            {
                outL += Comb(c, input, feedback, damp);
                outR += Comb(c + combs, input, feedback, damp);
            }
            for (int a = 0; a < allpasses; a++)
            {
                outL = Allpass(a, outL);
                outR = Allpass(a + allpasses, outR);
            }
            s[i] = outL * wet1 + outR * wet2 + l * dry;
            s[i + 1] = outR * wet1 + outL * wet2 + r * dry;
        }
    }

    private float Comb(int n, float input, float feedback, float damp)
    {
        var buffer = _combs[n];
        var output = buffer[_combAt[n]];
        _combState[n] = output * (1 - damp) + _combState[n] * damp;
        buffer[_combAt[n]] = input + _combState[n] * feedback;
        _combAt[n] = (_combAt[n] + 1) % buffer.Length;
        return output;
    }

    private float Allpass(int n, float input)
    {
        var buffer = _allpasses[n];
        var delayed = buffer[_allpassAt[n]];
        buffer[_allpassAt[n]] = input + delayed * 0.5f;
        _allpassAt[n] = (_allpassAt[n] + 1) % buffer.Length;
        return delayed - input;
    }
}

/// <summary>
/// Changes speed without changing pitch, and pitch without changing speed. WSOLA: overlapping 40 ms windows are taken from
/// the input at a different pace than they're laid down, each nudged up to 10 ms to line up with the waveform so far.
/// Pitch is the same trick plus a resample at the end.
/// ponytail: linear resampling and up to 40 ms lost at the very end of a song; fine for listening, not for mastering.
/// </summary>
public sealed class Stretcher
{
    private readonly int _channels, _window, _hop, _search;
    private readonly double[] _hann;
    private readonly float[] _overlap;
    private float[] _input = new float[1 << 16];
    private int _inputFrames, _previous;
    private double _position;
    private float[] _lastFrame;
    private double _resampleAt;

    public Stretcher(int channels, int rate)
    {
        _channels = channels;
        _window = rate * 40 / 1000 & ~1;
        _hop = _window / 2;
        _search = rate * 10 / 1000;
        _hann = [.. Enumerable.Range(0, _window).Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / _window))];
        _overlap = new float[_window * channels];
        _lastFrame = new float[channels];
        Reset();
    }

    public void Reset()
    {
        _inputFrames = 0;
        _previous = -1;
        _position = _search;
        _resampleAt = 0;
        Array.Clear(_overlap);
        Array.Clear(_lastFrame);
    }

    /// <summary>Takes more decoded samples, returns however many are ready (often none).</summary>
    public float[] Process(float[] input, double speed, double pitchRatio)
    {
        Append(input);
        var tempo = speed / pitchRatio;
        var output = new List<float>();
        while (true)
        {
            int nominal = (int)_position;
            if (nominal + _search + _window > _inputFrames) break;
            if (_previous >= 0 && _previous + _hop + _hop > _inputFrames) break;

            int start = nominal + BestOffset(nominal);
            for (int i = 0; i < _window; i++)
                for (int c = 0; c < _channels; c++)
                    _overlap[i * _channels + c] += (float)(_input[(start + i) * _channels + c] * _hann[i]);
            for (int i = 0; i < _hop * _channels; i++) output.Add(_overlap[i]);
            Array.Copy(_overlap, _hop * _channels, _overlap, 0, _overlap.Length - _hop * _channels);
            Array.Clear(_overlap, _overlap.Length - _hop * _channels, _hop * _channels);

            _previous = start;
            _position += _hop * tempo;
        }
        Compact();
        return pitchRatio == 1 ? [.. output] : Resample(output, pitchRatio);
    }

    /// <summary>Where near the nominal spot the waveform best continues what the last window would have played next.</summary>
    private int BestOffset(int nominal)
    {
        if (_previous < 0) return 0;
        int target = _previous + _hop, bestOffset = 0;
        var best = double.MinValue;
        for (int d = -_search; d <= _search; d += 2)
        {
            double sum = 0, energy = 1e-9;
            for (int i = 0; i < _hop; i += 2)
            {
                float a = _input[(nominal + d + i) * _channels], b = _input[(target + i) * _channels];
                sum += a * b;
                energy += a * a;
            }
            var score = sum / Math.Sqrt(energy);
            if (score > best) { best = score; bestOffset = d; }
        }
        return bestOffset;
    }

    private float[] Resample(List<float> stretched, double ratio)
    {
        int frames = stretched.Count / _channels;
        var output = new List<float>((int)(frames / ratio) * _channels + _channels);
        while (_resampleAt < frames)
        {
            int at = (int)_resampleAt;
            var t = (float)(_resampleAt - at);
            for (int c = 0; c < _channels; c++)
            {
                // position 0 means the last frame of the previous batch, so batches join without a gap
                float a = at == 0 ? _lastFrame[c] : stretched[(at - 1) * _channels + c];
                float b = stretched[at * _channels + c];
                output.Add(a + (b - a) * t);
            }
            _resampleAt += ratio;
        }
        if (frames > 0)
        {
            _resampleAt -= frames;
            for (int c = 0; c < _channels; c++) _lastFrame[c] = stretched[(frames - 1) * _channels + c];
        }
        return [.. output];
    }

    private void Append(float[] input)
    {
        int need = _inputFrames * _channels + input.Length;
        if (need > _input.Length) Array.Resize(ref _input, Math.Max(need, _input.Length * 2));
        input.CopyTo(_input, _inputFrames * _channels);
        _inputFrames += input.Length / _channels;
    }

    private void Compact()
    {
        int keepFrom = Math.Min((int)_position - _search, _previous >= 0 ? _previous : int.MaxValue);
        keepFrom = Math.Min(keepFrom, _inputFrames);
        if (keepFrom <= 0) return;
        Array.Copy(_input, keepFrom * _channels, _input, 0, (_inputFrames - keepFrom) * _channels);
        _inputFrames -= keepFrom;
        _position -= keepFrom;
        if (_previous >= 0) _previous -= keepFrom;
    }
}
