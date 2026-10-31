using System.Windows;
using System.Windows.Media;

namespace ShibaPlayer;

public enum ShibaMood { Neutral, Happy, Sad, Sleepy }

/// <summary>
/// The Shibaberg mascot, ported from Autoberg's GDI+ Shiba.Draw (src\Ui.cs) so both apps share one dog.
/// Drawn as vectors in a 100x100 design space and scaled to whatever size the element gets.
/// </summary>
public sealed class Shiba : FrameworkElement
{
    public static readonly DependencyProperty MoodProperty = DependencyProperty.Register(
        nameof(Mood), typeof(ShibaMood), typeof(Shiba),
        new FrameworkPropertyMetadata(ShibaMood.Neutral, FrameworkPropertyMetadataOptions.AffectsRender));

    public ShibaMood Mood
    {
        get => (ShibaMood)GetValue(MoodProperty);
        set => SetValue(MoodProperty, value);
    }

    private static readonly Brush Fur = Frozen(Color.FromRgb(0xE8, 0x92, 0x4A));
    private static readonly Brush Cream = Frozen(Color.FromRgb(0xFF, 0xF2, 0xDF));
    private static readonly Brush Ink = Frozen(Color.FromRgb(0x2A, 0x1A, 0x12));
    private static readonly Brush Blush = Frozen(Color.FromArgb(150, 0xFF, 0x8F, 0xA3));
    private static readonly Brush Tongue = Frozen(Color.FromRgb(0xFF, 0x7C, 0x8C));
    private static readonly Brush Zzz = Frozen(Color.FromRgb(0xC4, 0x65, 0x2A));
    private static readonly Pen Line = FrozenPen();

    protected override void OnRender(DrawingContext g)
    {
        var k = Math.Min(ActualWidth, ActualHeight) / 100;
        if (k <= 0) return;
        g.PushTransform(new TranslateTransform((ActualWidth - 100 * k) / 2, (ActualHeight - 100 * k) / 2));
        g.PushTransform(new ScaleTransform(k, k));

        // ears (behind the head)
        foreach (var left in new[] { true, false })
        {
            g.DrawGeometry(Fur, Line, ClosedCurve(Mirror(left, new(13, 44), new(19, 9), new(45, 27)), 0.25));
            g.DrawGeometry(Cream, null, ClosedCurve(Mirror(left, new(20, 37), new(23, 18), new(36, 29)), 0.3));
        }

        // head: wide chibi oval, cream muzzle clipped to it, plus the two shiba "eyebrow" spots
        var head = new EllipseGeometry(new Rect(7, 22, 86, 70));
        g.DrawGeometry(Fur, null, head);
        g.PushClip(head);
        g.DrawGeometry(Cream, null, Oval(17, 54, 66, 37));
        g.Pop();
        g.DrawGeometry(Cream, null, Oval(28, 41, 10, 6));
        g.DrawGeometry(Cream, null, Oval(62, 41, 10, 6));
        g.DrawGeometry(null, Line, head);

        g.DrawGeometry(Blush, null, Oval(16, 63, 13, 7));
        g.DrawGeometry(Blush, null, Oval(71, 63, 13, 7));

        switch (Mood)
        {
            case ShibaMood.Happy:
                g.DrawGeometry(null, Line, Arc(27, 51, 12, 10, 200, 140));
                g.DrawGeometry(null, Line, Arc(61, 51, 12, 10, 200, 140));
                break;
            case ShibaMood.Sleepy:
                g.DrawGeometry(null, Line, Arc(27, 50, 12, 9, 20, 140));
                g.DrawGeometry(null, Line, Arc(61, 50, 12, 9, 20, 140));
                break;
            default:
                g.DrawGeometry(Ink, null, Oval(29, 50, 9, 11));
                g.DrawGeometry(Ink, null, Oval(62, 50, 9, 11));
                g.DrawGeometry(Brushes.White, null, Oval(31.5, 52, 3.5, 3.5));
                g.DrawGeometry(Brushes.White, null, Oval(64.5, 52, 3.5, 3.5));
                if (Mood == ShibaMood.Sad)
                {
                    g.DrawLine(Line, new Point(26, 45), new Point(37, 42)); // worried brows
                    g.DrawLine(Line, new Point(74, 45), new Point(63, 42));
                }
                break;
        }

        g.DrawGeometry(Ink, null, ClosedCurve([new(45, 63), new(55, 63), new(50, 68.5)], 0.45)); // nose

        switch (Mood)
        {
            case ShibaMood.Happy:
                var mouth = Arc(42, 64, 16, 16, 0, 180);
                ((PathGeometry)mouth).Figures[0].IsClosed = true;
                g.DrawGeometry(Tongue, Line, mouth);
                break;
            case ShibaMood.Sad:
                g.DrawGeometry(null, Line, Arc(43, 72, 14, 9, 200, 140));
                break;
            default:
                g.DrawGeometry(null, Line, Arc(41, 64, 9, 8, 0, 180)); // the little "w"
                g.DrawGeometry(null, Line, Arc(50, 64, 9, 8, 0, 180));
                break;
        }

        if (Mood == ShibaMood.Sleepy)
        {
            var z = new FormattedText("z", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                15, Zzz, 1.0);
            g.DrawText(z, new Point(82, 10));
        }

        g.Pop();
        g.Pop();
    }

    private static Geometry Oval(double x, double y, double w, double h) => new EllipseGeometry(new Rect(x, y, w, h));

    /// <summary>GDI+ DrawArc semantics: angles in degrees, clockwise from +x, on the ellipse inscribed in the rect.</summary>
    private static Geometry Arc(double x, double y, double w, double h, double start, double sweep)
    {
        double rx = w / 2, ry = h / 2, cx = x + rx, cy = y + ry;
        Point At(double deg) => new(cx + rx * Math.Cos(deg * Math.PI / 180), cy + ry * Math.Sin(deg * Math.PI / 180));
        var fig = new PathFigure { StartPoint = At(start) };
        fig.Segments.Add(new ArcSegment(At(start + sweep), new Size(rx, ry), 0, sweep > 180, SweepDirection.Clockwise, true));
        return new PathGeometry([fig]);
    }

    /// <summary>GDI+ AddClosedCurve: a closed cardinal spline, converted to Beziers (control offset = tension/3).</summary>
    private static Geometry ClosedCurve(Point[] p, double tension)
    {
        int n = p.Length;
        var fig = new PathFigure { StartPoint = p[0], IsClosed = true };
        for (int i = 0; i < n; i++)
        {
            Point p0 = p[(i - 1 + n) % n], p1 = p[i], p2 = p[(i + 1) % n], p3 = p[(i + 2) % n];
            var t = tension / 3;
            fig.Segments.Add(new BezierSegment(
                new Point(p1.X + t * (p2.X - p0.X), p1.Y + t * (p2.Y - p0.Y)),
                new Point(p2.X - t * (p3.X - p1.X), p2.Y - t * (p3.Y - p1.Y)),
                p2, true));
        }
        return new PathGeometry([fig]);
    }

    private static Point[] Mirror(bool left, params Point[] pts) =>
        left ? pts : pts.Select(pt => new Point(100 - pt.X, pt.Y)).ToArray();

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Pen FrozenPen()
    {
        var p = new Pen(Frozen(Color.FromRgb(0x3A, 0x24, 0x18)), 3.2)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        p.Freeze();
        return p;
    }
}
