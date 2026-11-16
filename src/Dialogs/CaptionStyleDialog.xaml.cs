using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HandPegApp.Models;

namespace HandPegApp;

/// <summary>
/// Auto-caption style editor (font, colours, outline, words per line, animation) with a live sample. Edits a copy the caller takes on OK.
/// </summary>
public partial class CaptionStyleDialog : Window
{
    // The style is a plain (non-observable) object, so the sample is redrawn on a timer a few times per second.
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(150) };

    public CaptionStyleDialog(CaptionStyle style)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Services.ThemeManager.ApplyTitleBar(this);

        // Dialog-local Ctrl+Z/Ctrl+Y, one step per press.
        _ = new BoundUndo(this);
        EditedStyle = style.Clone();

        // Installed fonts; a style's missing font is preserved as-is.
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

    /// <summary>Edited style; valid only after OK.</summary>
    public CaptionStyle EditedStyle { get; }

    /// <summary>Opens the Windows colour picker for the pressed button.</summary>
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

        // Owned by this window so it opens on top and is modal to it.
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

    /// <summary>Fills each colour button with its colour and value label, using contrasting text.</summary>
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
            var fill = new SolidColorBrush(color);
            fill.Freeze();
            button.Background = fill;
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
