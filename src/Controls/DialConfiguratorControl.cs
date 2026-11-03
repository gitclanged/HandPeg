using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// The Lightning Dial's settings (Settings > Keybinds &amp; Lightning Dial, 4.4): its eight slices (5.1-Polish), each an action
/// run with one flick or a category (a name and the seven actions of its slices, clockwise from the slice the pointer comes
/// in by), chosen by the slice's first box. Edits a copy of the dial; the settings keep it when they are saved
/// (<see cref="KeybindManager.ForSettings"/>). Above them, the dial's top level drawn as it opens (4.4-BugHunt): the one
/// ring of its slices, following the names and actions as they are chosen.
/// </summary>
public sealed class DialConfiguratorControl : StackPanel
{
    /// <summary>An action a slice can hold, as the pickers list it ("" is an empty slice).</summary>
    public sealed record ActionChoice(string Id, string Label);

    private readonly List<DialCategoryState> _dial;
    private DialPicture? _picture;

    // Every action but the held navigation keys (held keys, not things to run), by category.
    private static readonly IReadOnlyList<ActionChoice> Choices =
    [
        new("", "(Empty)"),
        .. KeybindManager.Actions
            .Where(a => !a.Has(KeyActionFlags.Continuous))
            .Select(a => new ActionChoice(a.Id, $"{a.Category}: {a.Label}")),
    ];

    public DialConfiguratorControl(List<DialCategoryState> dial)
    {
        _dial = dial;
        AutomationProperties.SetName(this, "Lightning Dial categories");
        Build();
    }

    /// <summary>The dial as it is now edited (eight slices: actions, or categories of seven slots).</summary>
    public IReadOnlyList<DialCategoryState> Dial => _dial;

    // What a slice's first box offers: its own action, or a category (the choices' first, "Category...").
    private const string CategoryChoice = "\u0001category";

    private static readonly IReadOnlyList<ActionChoice> SliceChoices = [new(CategoryChoice, "Category (a ring of 7 actions)..."), .. Choices];

    private void Build()
    {
        Children.Clear();
        _picture = new DialPicture(_dial) { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 8) };
        Children.Add(_picture);

        for (var index = 0; index < _dial.Count; index++)
        {
            var sliceAt = index;
            var category = _dial[index];
            var header = new DockPanel { Margin = new Thickness(0, index == 0 ? 2 : 12, 0, 4), LastChildFill = false };
            var glyph = new TextBlock { Text = category.Glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16, Width = 26, VerticalAlignment = VerticalAlignment.Center };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            var number = new TextBlock { Text = $"{index + 1}.", Width = 20, VerticalAlignment = VerticalAlignment.Center };
            number.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

            // The slice's own action (run with one flick), or a category of its own (5.1-Polish, the user's directive).
            var kind = new ComboBox
            {
                ItemsSource = SliceChoices,
                DisplayMemberPath = nameof(ActionChoice.Label),
                SelectedValuePath = nameof(ActionChoice.Id),
                SelectedValue = category.IsAction ? category.Action : category.Actions.Exists(a => a.Length > 0) ? CategoryChoice : "",
                Width = 230,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(kind, $"Slice {index + 1}");
            kind.SelectionChanged += (_, _) =>
            {
                var chosen = kind.SelectedValue as string ?? "";
                if (chosen == CategoryChoice)
                {
                    if (!category.IsAction && category.Actions.Exists(a => a.Length > 0))
                        return;
                    _dial[sliceAt] = KeybindManager.CategoryFor(sliceAt);
                }
                else
                {
                    _dial[sliceAt] = chosen.Length > 0 ? KeybindManager.Leaf(chosen) : new DialCategoryState { Actions = [.. Enumerable.Repeat("", KeybindManager.DialSlots)] };
                }

                // The rest of the slice's row changes with it: made again after this choice is done.
                Dispatcher.BeginInvoke(Build);
            };

            DockPanel.SetDock(number, Dock.Left);
            DockPanel.SetDock(glyph, Dock.Left);
            DockPanel.SetDock(kind, Dock.Left);
            header.Children.Add(number);
            header.Children.Add(glyph);
            header.Children.Add(kind);
            if (category.IsAction || !category.Actions.Exists(a => a.Length > 0))
            {
                Children.Add(header);
                continue;
            }

            var name = new TextBox { Text = category.Name, Width = 170, MaxLength = 24, VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(name, $"Category {index + 1} name");
            name.TextChanged += (_, _) =>
            {
                if (name.Text.Trim() is { Length: > 0 } text)
                {
                    category.Name = text;
                    _picture?.Relabel();
                }
            };
            DockPanel.SetDock(name, Dock.Left);
            header.Children.Add(name);
            Children.Add(header);

            var slots = new WrapPanel { Margin = new Thickness(46, 0, 0, 0) };
            for (var slot = 0; slot < category.Actions.Count; slot++)
            {
                var at = slot;
                var picker = new ComboBox
                {
                    ItemsSource = Choices,
                    DisplayMemberPath = nameof(ActionChoice.Label),
                    SelectedValuePath = nameof(ActionChoice.Id),
                    SelectedValue = category.Actions[slot],
                    Width = 200,
                    Margin = new Thickness(0, 2, 6, 2),
                };
                AutomationProperties.SetName(picker, $"Category {index + 1}, slice {slot + 1}");
                picker.SelectionChanged += (_, _) =>
                {
                    category.Actions[at] = picker.SelectedValue as string ?? "";
                    _picture?.Relabel();
                };
                slots.Children.Add(picker);
            }

            Children.Add(slots);
        }

        var reset = new Button { Content = "Reset Dial", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
        reset.Click += (_, _) =>
        {
            _dial.Clear();
            _dial.AddRange(KeybindManager.DefaultDial());
            Build();
        };
        Children.Add(reset);
    }

    /// <summary>
    /// The dial's top level as it opens, drawn by the dial's own ring (<see cref="LightningDialOverlay.DrawRing"/>): each
    /// slice's glyph and name (its action's, or its category's), clockwise from the top. Nothing to click: it shows what the
    /// boxes below make.
    /// </summary>
    private sealed class DialPicture : FrameworkElement
    {
        private const int Slices = 8;

        private readonly List<DialCategoryState> _dial;
        private readonly Brush[] _fills = new Brush[Slices];
        private Pen? _edge, _spoke;
        private Brush? _edgeBrush;

        public DialPicture(List<DialCategoryState> dial)
        {
            _dial = dial;
            Width = Height = LightningDialOverlay.DialSize;
            Focusable = false;
            IsHitTestVisible = false;
            AutomationProperties.SetName(this, "Lightning Dial picture");
        }

        /// <summary>A category's name changed: drawn again (its texts are made as it is drawn).</summary>
        public void Relabel() => InvalidateVisual();

        private Brush Themed(string key, Color fallback)
        {
            if (TryFindResource(key) is Brush brush)
                return brush;

            var made = new SolidColorBrush(fallback);
            made.Freeze();
            return made;
        }

        protected override void OnRender(DrawingContext drawing)
        {
            var text = Themed("TextBrush", Colors.White);
            var face = Themed("CardBrush", Color.FromRgb(0x2D, 0x2D, 0x2D));
            var well = Themed("InsetBrush", Color.FromRgb(0x18, 0x18, 0x18));
            var outline = Themed("ControlBorderBrush", Color.FromRgb(0x3F, 0x3F, 0x3F));
            if (_edge is null || _spoke is null || !ReferenceEquals(outline, _edgeBrush))
                (_edge, _spoke, _edgeBrush) = (LightningDialOverlay.RingPen(outline, 1), LightningDialOverlay.RingPen(outline, LightningDialOverlay.SpokeWidth), outline);

            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var shown = new (string Glyph, string Name)[Slices];
            for (var i = 0; i < Slices; i++)
            {
                var slice = i < _dial.Count ? _dial[i] : null;
                shown[i] = slice is null ? ("", "")
                    : slice.IsAction ? KeybindManager.Action(slice.Action) is { } action ? (action.Glyph, action.Label.TrimEnd('.')) : ("", "")
                    : slice.Actions.Exists(a => a.Length > 0) ? (slice.Glyph, slice.Name)
                    : ("", "");
                _fills[i] = shown[i].Name.Length > 0 ? face : well;
            }

            LightningDialOverlay.DrawRing(drawing, _fills, face, _edge, _spoke);

            for (var i = 0; i < Slices; i++)
            {
                var (glyph, name) = shown[i];
                if (name.Length == 0)
                    continue;

                LightningDialOverlay.DrawSliceText(drawing, i, LightningDialOverlay.GlyphText(glyph, text, dpi), LightningDialOverlay.LabelText(name, text, dpi));
            }

            var centre = LightningDialOverlay.CentreText("Lightning Dial", text, dpi);
            drawing.DrawText(centre, new Point(LightningDialOverlay.Centre.X - centre.MaxTextWidth / 2, LightningDialOverlay.Centre.Y - centre.Height / 2));
        }
    }
}
