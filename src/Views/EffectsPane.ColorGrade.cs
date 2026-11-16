using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using HandPegApp.Models.Effects;

namespace HandPegApp.Views;

// ----- Pass 5.6A (lane A): the Color Grade card's footer: what the Color Studio holds (wheels, curves) and Open Color Studio -----
// The card's wheels and curves are edited in the Color tab only (EffectParam.StudioOnly): the footer says whether they are in use (two
// chips, lit while they change the picture) and opens the tab. The chips follow the card while the footer is in a window (the card's
// ValueChanged, let go when the footer leaves: a rebuilt pane never keeps an old footer alive).
public partial class EffectsPane
{
    /// <summary>Raised by the Color Grade card's Open Color Studio button: the window brings the Color tab forward.</summary>
    public event Action? OpenColorStudioRequested;

    private partial FrameworkElement BuildColorGradeFooter(EffectInstance card, string title);

    private partial FrameworkElement BuildColorGradeFooter(EffectInstance card, string title)
    {
        var footer = new DockPanel { Margin = new Thickness(0, 6, 0, 2), LastChildFill = false };
        var wheels = Chip("Wheels", "Lift, gamma or gain in use");
        var curves = Chip("Curves", "A curve in use");
        footer.Children.Add(wheels);
        footer.Children.Add(curves);

        var open = new Button
        {
            Padding = new Thickness(8, 2, 8, 2), ToolTip = "Open the Color tab",
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = Glyphs.Color, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) },
                    new TextBlock { Text = "Open Color Studio", VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        AutomationProperties.SetName(open, $"Open Color Studio ({title})");
        open.Click += (_, _) => OpenColorStudioRequested?.Invoke();
        DockPanel.SetDock(open, Dock.Right);
        footer.Children.Add(open);

        void Update()
        {
            var values = ColorGradeValues.From(card);
            var keyed = false;
            foreach (var key in new[] { ColorGradeKeys.LiftR, ColorGradeKeys.LiftG, ColorGradeKeys.LiftB, ColorGradeKeys.GammaR, ColorGradeKeys.GammaG,
                         ColorGradeKeys.GammaB, ColorGradeKeys.GainR, ColorGradeKeys.GainG, ColorGradeKeys.GainB })
                keyed |= card.HasKeys(key);
            Light(wheels, keyed || values.LiftActive || values.GammaActive || values.GainActive);
            Light(curves, ColorGradeMath.CurvesActive(card));
        }

        void Changed(EffectInstance _, string key) => Update();
        footer.Loaded += (_, _) =>
        {
            card.ValueChanged -= Changed;
            card.ValueChanged += Changed;
            Update();
        };
        footer.Unloaded += (_, _) => card.ValueChanged -= Changed;
        Update();
        return footer;
    }

    /// <summary>A small chip naming a part of the grade, lit (the accent) while that part is in use.</summary>
    private static Border Chip(string text, string tip)
    {
        var words = new TextBlock { Text = text, FontSize = 11 };
        var chip = new Border
        {
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(7, 1, 7, 1), Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center, Child = words, ToolTip = tip,
        };
        AutomationProperties.SetName(chip, text);
        Light(chip, false);
        return chip;
    }

    private static void Light(Border chip, bool on)
    {
        chip.Tag = on;
        AutomationProperties.SetItemStatus(chip, on ? "In use" : "Not used");
        if (on)
        {
            chip.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            chip.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            (chip.Child as FrameworkElement)?.SetResourceReference(TextBlock.ForegroundProperty, "OnAccentBrush");
        }
        else
        {
            chip.Background = Brushes.Transparent;
            chip.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
            (chip.Child as FrameworkElement)?.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        }
    }
}
