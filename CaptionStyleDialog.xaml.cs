using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp.Models;

namespace HandPegApp;

/// <summary>
/// Font, colours, outline, words per line and animation of the auto-captions, with a sample drawn in the
/// style as it is edited. Works on a copy of the style, which the caller takes when the dialog is confirmed.
/// </summary>
public partial class CaptionStyleDialog : Window
{
    // The style is a plain object that does not announce its changes, so the sample is simply redrawn a
    // few times a second while the dialog is open.
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(150) };

    public CaptionStyleDialog(CaptionStyle style)
    {
        InitializeComponent();
        EditedStyle = style.Clone();

        // The fonts on this computer; a style made elsewhere may name one that is not, and keeps it all the same.
        var fonts = Fonts.SystemFontFamilies.Select(f => f.Source).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (!fonts.Contains(EditedStyle.FontName))
            fonts.Insert(0, EditedStyle.FontName);
        FontBox.ItemsSource = fonts;
        AnimationBox.ItemsSource = CaptionStyle.Animations;
        DataContext = EditedStyle;

        ShowColors();
        _refresh.Tick += (_, _) => Preview.Show(EditedStyle);
        Loaded += (_, _) =>
        {
            Preview.Show(EditedStyle);
            _refresh.Start();
        };
        Closed += (_, _) => _refresh.Stop();
    }

    /// <summary>The style as edited. Only meaningful once the dialog has been confirmed.</summary>
    public CaptionStyle EditedStyle { get; }

    /// <summary>Opens the Windows colour picker for the button that was pressed.</summary>
    private void PickColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string which })
            return;

        CaptionPreview.TryParse(GetColor(which), out var current);
        using var picker = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
            FullOpen = true,
        };

        // Owned by this window, so that it opens over it and blocks it like any other dialog.
        var owner = new System.Windows.Forms.NativeWindow();
        owner.AssignHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        try
        {
            if (picker.ShowDialog(owner) != System.Windows.Forms.DialogResult.OK)
                return;
        }
        finally
        {
            owner.ReleaseHandle();
        }

        SetColor(which, $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}");
        ShowColors();
    }

    private string GetColor(string which) => which switch
    {
        "Primary" => EditedStyle.PrimaryColor,
        "Highlight" => EditedStyle.HighlightColor,
        _ => EditedStyle.OutlineColor,
    };

    private void SetColor(string which, string value)
    {
        switch (which)
        {
            case "Primary": EditedStyle.PrimaryColor = value; break;
            case "Highlight": EditedStyle.HighlightColor = value; break;
            default: EditedStyle.OutlineColor = value; break;
        }
    }

    /// <summary>Fills each colour button with its colour and writes the value on it, light or dark so that it reads.</summary>
    private void ShowColors()
    {
        foreach (var (button, value) in new[]
                 {
                     (PrimaryButton, EditedStyle.PrimaryColor), (HighlightButton, EditedStyle.HighlightColor), (OutlineButton, EditedStyle.OutlineColor),
                 })
        {
            if (!CaptionPreview.TryParse(value, out var color))
                color = Colors.Gray;

            var brightness = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255;
            button.Background = new SolidColorBrush(color);
            button.Foreground = brightness > 0.55 ? Brushes.Black : Brushes.White;
            button.Content = value.ToUpperInvariant();
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (EditedStyle.FontSize is < 8 or > 400)
        {
            ErrorText.Text = "Font Size has to be between 8 and 400.";
            return;
        }

        if (string.IsNullOrWhiteSpace(EditedStyle.FontName))
            EditedStyle.FontName = "Arial";

        DialogResult = true;
    }
}
