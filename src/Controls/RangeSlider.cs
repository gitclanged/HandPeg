using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace HandPegApp;

/// <summary>
/// Two-handle range slider selecting a span between Minimum and Maximum (e.g. the kept part of a recording). Handles cannot
/// cross.
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
        // Shade regions outside the handles so the selected span stands out over the underlying content.
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
/// Converts a gain in dB to a vertical waveform scale factor (+6 dB is about 2x, clamped 0.05..4): an immediate visual of the
/// change, while the actual gain is applied by the audio engine.
/// </summary>
public sealed class DecibelToScaleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double decibels ? Math.Clamp(Math.Pow(10, decibels / 20), 0.05, 4) : 1.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
