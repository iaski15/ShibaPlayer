using System.Windows;
using System.Windows.Media;

namespace ShibaPlayer;

/// <summary>Our own visualizer: reads what's coming out of the speakers from the Player and draws it, about 30 times a second.</summary>
public sealed class Visualizer : FrameworkElement
{
    public static readonly string[] Modes = ["Disable", "Spectrometer", "Scope", "Spectrum", "VU meter", "3D spectrum"];

    private const int Size = 1024, BandCount = 32, History = 22;
    private readonly float[] _samples = new float[Size * 2]; // interleaved stereo
    private readonly double[] _re = new double[Size], _im = new double[Size];
    private readonly double[] _bands = new double[BandCount], _peaks = new double[BandCount];
    private readonly Queue<double[]> _history = new();
    private double _vuL, _vuR, _peakL, _peakR;

    private static readonly Brush Fill = Frozen(0xE8, 0x92, 0x4A), Bright = Frozen(0xFF, 0xF2, 0xDF), Hot = Frozen(0xD2, 0x28, 0x37), Track = Frozen(0x55, 0x3A, 0x2A);
    private static readonly Pen Line = FrozenPen(Bright, 1.5), Edge = FrozenPen(Fill, 1);

    public int Mode { get; set; } = 1;

    /// <summary>Takes the next look at the sound. Inactive means silence, so everything falls to zero.</summary>
    public void Tick(Player player, bool active)
    {
        if (Mode == 0) return;
        var rate = player.VisualRate;
        if (!active || !player.ReadVisual(_samples)) Array.Clear(_samples);
        Analyze(_samples, rate, _bands, _re, _im);
        for (int i = 0; i < BandCount; i++) _peaks[i] = Math.Max(_bands[i], _peaks[i] - 0.012);

        double l = 0, r = 0;
        for (int i = 0; i < Size; i++)
        {
            l += _samples[2 * i] * _samples[2 * i];
            r += _samples[2 * i + 1] * _samples[2 * i + 1];
        }
        _vuL = Math.Max(Meter(l), _vuL - 0.03);
        _vuR = Math.Max(Meter(r), _vuR - 0.03);
        _peakL = Math.Max(_vuL, _peakL - 0.006);
        _peakR = Math.Max(_vuR, _peakR - 0.006);

        _history.Enqueue((double[])_bands.Clone());
        if (_history.Count > History) _history.Dequeue();
        InvalidateVisual();

        static double Meter(double sum) => Math.Clamp((10 * Math.Log10(sum / Size + 1e-9) + 48) / 48, 0, 1); // -48..0 dB
    }

    /// <summary>Falling-smoothed log-spaced bands, 0..1 for -70..-10 dB. Public for the self-test.</summary>
    public static void Analyze(float[] stereo, int rate, double[] bands, double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 0; i < n; i++)
        {
            re[i] = (stereo[2 * i] + stereo[2 * i + 1]) * 0.5 * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
            im[i] = 0;
        }
        Fft(re, im);
        double low = 40, high = Math.Min(16000, rate / 2.0);
        for (int b = 0; b < bands.Length; b++)
        {
            int from = (int)(low * Math.Pow(high / low, (double)b / bands.Length) * n / rate);
            int to = (int)(low * Math.Pow(high / low, (double)(b + 1) / bands.Length) * n / rate);
            to = Math.Clamp(Math.Max(to, from + 1), 1, n / 2);
            double max = 0;
            for (int k = Math.Min(from, to - 1); k < to; k++) max = Math.Max(max, re[k] * re[k] + im[k] * im[k]);
            var db = 10 * Math.Log10(max * 16.0 / (n * n) + 1e-12); // a full-scale sine lands near 0 dB
            var value = Math.Clamp((db + 70) / 60, 0, 1);
            bands[b] = Math.Max(value, bands[b] - 0.04);
        }
    }

    /// <summary>In-place radix-2 FFT; length must be a power of two.</summary>
    public static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2 * Math.PI / len, stepRe = Math.Cos(angle), stepIm = Math.Sin(angle);
            for (int i = 0; i < n; i += len)
            {
                double wRe = 1, wIm = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double tRe = re[b] * wRe - im[b] * wIm, tIm = re[b] * wIm + im[b] * wRe;
                    re[b] = re[a] - tRe;
                    im[b] = im[a] - tIm;
                    re[a] += tRe;
                    im[a] += tIm;
                    (wRe, wIm) = (wRe * stepRe - wIm * stepIm, wRe * stepIm + wIm * stepRe);
                }
            }
        }
    }

    protected override void OnRender(DrawingContext g)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        switch (Mode)
        {
            case 1: DrawBars(g, w, h); break;
            case 2: DrawScope(g, w, h); break;
            case 3: g.DrawGeometry(Fill, Line, Curve(_bands, 0, 0, w, h)); break;
            case 4: DrawMeters(g, w, h); break;
            case 5: Draw3D(g, w, h); break;
        }
    }

    private void DrawBars(DrawingContext g, double w, double h)
    {
        var slot = w / BandCount;
        for (int i = 0; i < BandCount; i++)
        {
            var bar = _bands[i] * h;
            g.DrawRectangle(Fill, null, new Rect(i * slot + 0.5, h - bar, Math.Max(slot - 1.5, 1), bar));
            g.DrawRectangle(Bright, null, new Rect(i * slot + 0.5, Math.Max(h - _peaks[i] * h - 2, 0), Math.Max(slot - 1.5, 1), 2));
        }
    }

    private void DrawScope(DrawingContext g, double w, double h)
    {
        var geometry = new StreamGeometry();
        using (var c = geometry.Open())
            for (int i = 0; i < Size; i += 2)
            {
                var value = Math.Clamp((_samples[2 * i] + _samples[2 * i + 1]) * 0.5, -1, 1);
                var point = new Point(i * w / (Size - 2), h / 2 - value * h / 2);
                if (i == 0) c.BeginFigure(point, false, false); else c.LineTo(point, true, false);
            }
        geometry.Freeze();
        g.DrawGeometry(null, Line, geometry);
    }

    private void DrawMeters(DrawingContext g, double w, double h)
    {
        Meter(0, _vuL, _peakL);
        Meter(1, _vuR, _peakR);

        void Meter(int row, double value, double peak)
        {
            double height = h * 0.34, y = h * (row == 0 ? 0.12 : 0.54);
            g.DrawRoundedRectangle(Track, null, new Rect(0, y, w, height), 4, 4);
            g.DrawRoundedRectangle(value > 0.9 ? Hot : Fill, null, new Rect(0, y, value * w, height), 4, 4);
            g.DrawRectangle(Bright, null, new Rect(Math.Min(peak * w, w - 2), y, 2, height));
        }
    }

    private void Draw3D(DrawingContext g, double w, double h)
    {
        var rows = _history.ToArray();
        double depthX = w * 0.3, depthY = h * 0.5;
        for (int i = 0; i < rows.Length; i++)
        {
            var back = 1 - (double)i / Math.Max(rows.Length - 1, 1); // oldest row is drawn first, furthest away
            g.PushOpacity(0.25 + 0.75 * (1 - back));
            g.DrawGeometry(Fill, Edge, Curve(rows[i], back * depthX, -back * depthY, w - depthX, h));
            g.Pop();
        }
    }

    private static StreamGeometry Curve(double[] values, double x, double y, double w, double h)
    {
        var geometry = new StreamGeometry();
        using (var c = geometry.Open())
        {
            c.BeginFigure(new Point(x, y + h), true, true);
            for (int i = 0; i < values.Length; i++)
                c.LineTo(new Point(x + i * w / (values.Length - 1), y + h - values[i] * h * 0.5), true, false);
            c.LineTo(new Point(x + w, y + h), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double width)
    {
        var pen = new Pen(brush, width) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }
}
