using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Maison.Controls;

/// <summary>
/// A 270° arc gauge with an animated needle-arc, centre value and caption.
/// The arc colour goes green → amber → red at <see cref="Warn"/> / <see cref="Danger"/> (if set).
/// </summary>
public sealed class ArcGauge : FrameworkElement
{
    private const double StartAngle = 135, Sweep = 270, Thickness = 9;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ArcGauge), new PropertyMetadata(0.0, OnValueChanged));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(ArcGauge), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DisplayProperty = DependencyProperty.Register(
        nameof(Display), typeof(string), typeof(ArcGauge), new FrameworkPropertyMetadata("—", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(ArcGauge), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty WarnProperty = DependencyProperty.Register(
        nameof(Warn), typeof(double), typeof(ArcGauge), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DangerProperty = DependencyProperty.Register(
        nameof(Danger), typeof(double), typeof(ArcGauge), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty AnimatedProperty = DependencyProperty.Register(
        "Animated", typeof(double), typeof(ArcGauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public string Display { get => (string)GetValue(DisplayProperty); set => SetValue(DisplayProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
    public double Warn { get => (double)GetValue(WarnProperty); set => SetValue(WarnProperty, value); }
    public double Danger { get => (double)GetValue(DangerProperty); set => SetValue(DangerProperty, value); }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var g = (ArcGauge)d;
        g.BeginAnimation(AnimatedProperty, new DoubleAnimation((double)e.NewValue, TimeSpan.FromMilliseconds(600))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    protected override Size MeasureOverride(Size available) =>
        new(double.IsInfinity(available.Width) ? 120 : available.Width, double.IsInfinity(available.Height) ? 120 : available.Height);

    protected override void OnRender(DrawingContext dc)
    {
        // Leave room under the arc for the caption.
        const double captionRoom = 20;
        double size = Math.Min(ActualWidth, ActualHeight - captionRoom);
        if (size < 20) return;
        var center = new Point(ActualWidth / 2, size / 2 + 2);
        double r = size / 2 - Thickness / 2 - 2;

        var track = new Pen(Brush("ControlStrokeColorDefaultBrush", Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        DrawArc(dc, center, r, StartAngle, Sweep, track);

        double v = (double)GetValue(AnimatedProperty);
        double frac = Maximum > 0 ? Math.Clamp(v / Maximum, 0, 1) : 0;
        if (frac > 0.002)
        {
            var pen = new Pen(new SolidColorBrush(ColorFor(Value)), Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            DrawArc(dc, center, r, StartAngle, Sweep * frac, pen);
        }

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var primary = Brush("TextFillColorPrimaryBrush", Colors.White);
        var secondary = Brush("TextFillColorSecondaryBrush", Color.FromRgb(0xC0, 0xC0, 0xC0));
        var valueText = new FormattedText(Display, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            Math.Max(12, size * 0.17), primary, dpi);
        dc.DrawText(valueText, new Point(center.X - valueText.Width / 2, center.Y - valueText.Height / 2 - 2));

        if (!string.IsNullOrEmpty(Caption))
        {
            var cap = new FormattedText(Caption, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), Math.Max(10, size * 0.095), secondary, dpi);
            // Just below the arc ends (which sit at 0.707·r below centre).
            dc.DrawText(cap, new Point(center.X - cap.Width / 2, center.Y + r * 0.707 + Thickness / 2 + 4));
        }
    }

    private Color ColorFor(double v)
    {
        if (!double.IsNaN(Danger) && v >= Danger) return Color.FromRgb(0xE5, 0x53, 0x4B);
        if (!double.IsNaN(Warn) && v >= Warn) return Color.FromRgb(0xE5, 0xB4, 0x54);
        return double.IsNaN(Warn)
            ? (TryFindResource("MaisonAccentBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x6C, 0xA0, 0xDC)
            : Color.FromRgb(0x4C, 0xC3, 0x8A);
    }

    private Brush Brush(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private static void DrawArc(DrawingContext dc, Point c, double r, double startDeg, double sweepDeg, Pen pen)
    {
        sweepDeg = Math.Min(sweepDeg, 359.9);
        Point At(double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(At(startDeg), false, false);
            ctx.ArcTo(At(startDeg + sweepDeg), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }
}
