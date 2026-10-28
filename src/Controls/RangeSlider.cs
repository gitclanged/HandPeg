using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace HandPegApp;

/// <summary>
/// A slider with two handles, for choosing a stretch between a minimum and a maximum: here, which part of
/// a recording to keep. The handles cannot pass each other.
/// </summary>
public sealed class RangeSlider : Grid
{
    private const double HandleWidth = 10;

    public static readonly DependencyProperty MinimumProperty = Register(nameof(Minimum), 0.0, twoWay: false);
    public static readonly DependencyProperty MaximumProperty = Register(nameof(Maximum), 1.0, twoWay: false);
    public static readonly DependencyProperty LowerValueProperty = Register(nameof(LowerValue), 0.0, twoWay: true);
    public static readonly DependencyProperty UpperValueProperty = Register(nameof(UpperValue), 1.0, twoWay: true);

    private readonly Canvas _canvas = new();
    private readonly Border _excludedLeft = Shade();
    private readonly Border _excludedRight = Shade();
    private readonly Thumb _lower = Handle("Trim Start");
    private readonly Thumb _upper = Handle("Trim End");

    public RangeSlider()
    {
        // What lies outside the handles is dimmed, so the part that is kept stands out over whatever is behind.
        _canvas.Children.Add(_excludedLeft);
        _canvas.Children.Add(_excludedRight);
        _canvas.Children.Add(_lower);
        _canvas.Children.Add(_upper);
        Children.Add(_canvas);

        _lower.DragDelta += (_, e) => LowerValue = Math.Clamp(LowerValue + ToValue(e.HorizontalChange), Minimum, UpperValue);
        _upper.DragDelta += (_, e) => UpperValue = Math.Clamp(UpperValue + ToValue(e.HorizontalChange), LowerValue, Maximum);
        SizeChanged += (_, _) => Place();
    }

    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double LowerValue { get => (double)GetValue(LowerValueProperty); set => SetValue(LowerValueProperty, value); }
    public double UpperValue { get => (double)GetValue(UpperValueProperty); set => SetValue(UpperValueProperty, value); }

    private static DependencyProperty Register(string name, double defaultValue, bool twoWay) => DependencyProperty.Register(
        name, typeof(double), typeof(RangeSlider),
        new FrameworkPropertyMetadata(defaultValue,
            twoWay ? FrameworkPropertyMetadataOptions.BindsTwoWayByDefault : FrameworkPropertyMetadataOptions.None,
            (d, _) => ((RangeSlider)d).Place()));

    private static readonly Brush ShadeBrush = MakeShadeBrush();

    private static Brush MakeShadeBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x10, 0x10));
        brush.Freeze();
        return brush;
    }

    private static Border Shade() => new() { Background = ShadeBrush, IsHitTestVisible = false };

    private static Thumb Handle(string name)
    {
        var handle = new Thumb { Width = HandleWidth, Cursor = Cursors.SizeWE, Template = HandleTemplate() };
        System.Windows.Automation.AutomationProperties.SetName(handle, name);
        return handle;
    }

    private static ControlTemplate HandleTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.White);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
        return new ControlTemplate(typeof(Thumb)) { VisualTree = border };
    }

    private double TrackWidth => Math.Max(ActualWidth - HandleWidth, 1);

    private double ToValue(double pixels) => pixels / TrackWidth * Math.Max(Maximum - Minimum, 0);

    private double ToOffset(double value) =>
        Maximum > Minimum ? Math.Clamp((value - Minimum) / (Maximum - Minimum), 0, 1) * TrackWidth : 0;

    private void Place()
    {
        var (lower, upper) = (ToOffset(LowerValue), ToOffset(UpperValue));
        foreach (var element in new FrameworkElement[] { _excludedLeft, _excludedRight, _lower, _upper })
            element.Height = ActualHeight;

        Canvas.SetLeft(_lower, lower);
        Canvas.SetLeft(_upper, upper);

        _excludedLeft.Width = lower + HandleWidth / 2;
        Canvas.SetLeft(_excludedRight, upper + HandleWidth / 2);
        _excludedRight.Width = Math.Max(ActualWidth - upper - HandleWidth / 2, 0);
    }
}

/// <summary>
/// A gain in decibels as the factor a waveform picture is stretched by vertically: +6 dB draws it about
/// twice as tall. A picture of the change, shown at once, while the real gain is applied by FFmpeg.
/// </summary>
public sealed class DecibelToScaleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double decibels ? Math.Clamp(Math.Pow(10, decibels / 20), 0.05, 4) : 1.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
