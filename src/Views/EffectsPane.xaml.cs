using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using HandPegApp.Controls;
using HandPegApp.Models;
using HandPegApp.Models.Effects;
using HandPegApp.Services;
using HandPegApp.ViewModels;
using static HandPegApp.UiBrushes;

namespace HandPegApp.Views;

/// <summary>
/// Effects pane: one card per effect in the target's stack (selected clip, blur fill row, or Sequence Output) in apply order with zone
/// headings. Cards are built in code (not templated), one row type per parameter kind, reading values from the effect or, for clip-bound
/// effects, the clip. Advanced parameters (EffectParam.IsAdvanced) fold under a chevron. Edits are clip-local (Layer.Echo).
/// </summary>
public partial class EffectsPane : UserControl
{
    private MainViewModel? _vm;

    // Target clip (null for Sequence Output) and its effect stack.
    private Layer? _layer;
    private ObservableCollection<EffectInstance>? _stack;

    // Target is the hidden "No Video" stand-in (no video clip on the timeline): show and add nothing, since effects on it would never
    // render.
    private bool _noClip;

    // Selected audio clip (MainWindow.SelectClip -> SelectSound) and the key layer at selection time; the audio clip is the target while
    // that key layer is unchanged (KeyLayer is video-only, so Keyframes, text tools and crop keep their target).
    private Layer? _sound, _soundOver;

    private readonly List<ParamModel> _params = [];
    private readonly HashSet<Guid> _collapsed = [];

    // Effect types with an expanded Advanced section (collapsed by default; process-lifetime).
    private static readonly HashSet<string> AdvancedOpen = new(StringComparer.Ordinal);

    // Advanced chevron: right-pointing stroke, rotated down when expanded.
    private static readonly Geometry Chevron = FrozenGeometry("M 0,0 L 4,4 L 0,8");

    // Edit writes (rows, audio filters, Pitch Shift) paced at Pacing.GestureWritesPerSecond, flushed as one look batch and grouped into
    // undo steps by GestureWriter (one per drag, run rule for isolated writes). Slider/number-box drags are gestures while they hold mouse
    // capture (CaptureThrottle).
    private readonly TimerScope _timers = new();
    private readonly GestureWriter<object> _writes;
    private readonly CaptureThrottle _drag;
    private static readonly object AudioWriteKey = new(), PitchWriteKey = new();

    // Copy Effects clipboard for Paste Effects / Paste Some Effects..., shared across targets.
    private static List<EffectState>? _clipboard;

    // Stack menu "Apply Paste to All Clips on Track" toggle (process-lifetime).
    private static bool _pasteToTrack;

    // Window hooked for click-away close while the Add Effect flyout is open.
    private Window? _addOwner;

    // Add Effect flyout close delay after the pointer leaves it and its button (260 ms, matching Settings).
    private readonly DispatcherTimer _addClose;

    // Coalesced card rebuild and value refresh (at most one of each queued).
    private readonly RunGate _rebuild, _refresh;

    // View-model handlers attached (Attach, then while loaded); a rebuild deferred while the loaded pane was hidden.
    private bool _hooked, _rebuildWhenShown;

    /// <summary>Raised by the Mask card's Edit Mask... button; the window opens the layer's mask tools.</summary>
    public event Action<Layer>? EditMaskRequested;

    private static readonly SolidColorBrush GreenCost = Frozen(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly SolidColorBrush AmberCost = Frozen(Color.FromRgb(0xF0, 0xA8, 0x30));
    private static readonly SolidColorBrush RedCost = Frozen(Color.FromRgb(0xE0, 0x4B, 0x4B));

    public EffectsPane()
    {
        InitializeComponent();
        _writes = new GestureWriter<object>(() => _vm, _timers);
        _drag = new CaptureThrottle(_writes);
        _addClose = _timers.Make(TimeSpan.FromMilliseconds(260), DispatcherPriority.Background, AddClose_Tick);
        _rebuild = new RunGate(Dispatcher, Rebuild, DispatcherPriority.Background);
        _refresh = new RunGate(Dispatcher, RefreshParams, DispatcherPriority.Background);
        AddPopup.Opened += AddPopup_Opened;
        AddPopup.Closed += AddPopup_Closed;
        ReorderBehavior.Reordered += OnReordered;

        // Unloaded (closed or undocked) panes hold no view-model handlers; reloading re-hooks and retargets.
        Loaded += (_, _) => Hook();
        Unloaded += (_, _) => Unhook();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && _rebuildWhenShown)
            {
                _rebuildWhenShown = false;
                Rebuild();
            }
        };
    }

    private static Geometry FrozenGeometry(string path)
    {
        var geometry = Geometry.Parse(path);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Binds to <paramref name="viewModel"/>: hooks handlers now (and on every load) and follows its target.</summary>
    public void Attach(MainViewModel viewModel)
    {
        if (ReferenceEquals(_vm, viewModel))
            return;
        Detach();
        _vm = viewModel;
        DataContext = viewModel;
        Hook();
    }

    /// <summary>Unbinds the view model: removes all handlers from <see cref="Attach"/> and the target, flushing pending writes.</summary>
    public void Detach()
    {
        Unhook();
        _vm = null;
        DataContext = null;
    }

    // Hooks view-model handlers and retargets (rebuilding if the target changed while unhooked).
    private void Hook()
    {
        if (_hooked || _vm is not { } viewModel)
            return;
        _hooked = true;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        viewModel.Layers.CollectionChanged += Layers_CollectionChanged;
        viewModel.EditTargetChanged += FollowSequenceOutput;
        Retarget();
    }

    // Exact inverse of Hook and Retarget's handlers; clears the target so the next Hook rebuilds for the current one.
    private void Unhook()
    {
        if (!_hooked || _vm is not { } viewModel)
            return;
        _hooked = false;
        _writes.Flush();
        _timers.StopAll();
        viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        viewModel.Layers.CollectionChanged -= Layers_CollectionChanged;
        viewModel.EditTargetChanged -= FollowSequenceOutput;
        if (_stack is not null)
            _stack.CollectionChanged -= Stack_CollectionChanged;
        if (_layer is not null)
            _layer.PropertyChanged -= Layer_PropertyChanged;
        (_layer, _stack) = (null, null);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.ActiveKeyLayer) or nameof(MainViewModel.BackgroundSelected):
                Retarget();
                break;
            case nameof(MainViewModel.PositionMs):
                OnPlayheadMoved();
                break;
            case nameof(MainViewModel.SequenceEffectsBypassed):
                UpdateHeader();
                break;

            // Blur fill values changed externally (Blur Settings..., timeline inspector, Undo).
            case nameof(MainViewModel.BlurRadius) or nameof(MainViewModel.BlurPasses) or nameof(MainViewModel.BackgroundDim) or nameof(MainViewModel.BackgroundZoom)
                when _layer is { IsBackground: true }:
                QueueRefresh();
                break;
        }
    }

    private void Layers_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Retarget();

    // Loaded but invisible (inactive tab, collapsed column): skip building; rebuild once visible.
    private bool HiddenNow => IsLoaded && !IsVisible;

    // Syncs this pane's Sequence Output toggle when Color Studio changes MainViewModel.EditSequenceOutput.
    private void FollowSequenceOutput()
    {
        if (_vm is null || SequenceToggle.IsChecked == _vm.EditSequenceOutput)
            return;
        SequenceToggle.IsChecked = _vm.EditSequenceOutput;
        Retarget();
    }

    private bool IsSequence => SequenceToggle.IsChecked == true;

    /// <summary>Playhead time in target-local seconds (clip-relative, or sequence time).</summary>
    private double ClipTime => (_vm?.PositionMs ?? 0) / 1000 - (_layer?.StartTime ?? 0);

    // ----- Target selection -----

    private void Retarget()
    {
        if (_vm is null)
            return;

        // The blur fill row (template for every video clip's blur fill) is not a timeline clip but still has an effect stack.
        // A selected audio clip can also be the target (Speed & Pitch and audio cards; ActiveKeyLayer is video-only), as reported by
        // the timeline selection (SelectSound, SoundTarget).
        var layer = IsSequence ? null
            : _vm.BackgroundSelected ? _vm.BackgroundLayer
            : SoundTarget() ?? _vm.ActiveKeyLayer;
        var noClip = layer is { IsBackground: false } && !_vm.Layers.Contains(layer);
        if (noClip)
            layer = null;
        var stack = noClip ? null : layer?.Effects ?? _vm.SequenceEffects;
        if (ReferenceEquals(layer, _layer) && ReferenceEquals(stack, _stack) && noClip == _noClip)
        {
            _vm.SetEditTarget(EditTarget);
            return;
        }

        _writes.Flush();
        if (_stack is not null)
            _stack.CollectionChanged -= Stack_CollectionChanged;
        if (_layer is not null)
            _layer.PropertyChanged -= Layer_PropertyChanged;
        (_layer, _stack, _noClip) = (layer, stack, noClip);
        if (_stack is not null)
            _stack.CollectionChanged += Stack_CollectionChanged;
        if (_layer is not null)
            _layer.PropertyChanged += Layer_PropertyChanged;
        Rebuild();
        _vm.SetEditTarget(EditTarget);
    }

    private void SequenceToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
            _vm.EditSequenceOutput = SequenceToggle.IsChecked == true;
        Retarget();
    }

    /// <summary>
    /// Timeline selection hook (MainWindow.SelectClip, every selection): an audio clip becomes the target; null reverts to the key layer.
    /// Routed here rather than via KeyLayer, which stays the video target for Keyframes, keyframe buttons, text tools and crop.
    /// </summary>
    public void SelectSound(Layer? clip)
    {
        var sound = clip is { IsAudio: true, IsDeleted: false, IsEmptyTrack: false } && _vm?.Layers.Contains(clip) == true ? clip : null;
        if (ReferenceEquals(sound, _sound) && (sound is null || ReferenceEquals(_soundOver, _vm?.KeyLayer)))
            return;

        (_sound, _soundOver) = (sound, sound is null ? null : _vm?.KeyLayer);
        Retarget();
    }

    /// <summary>
    /// Selected audio clip while still the target: null once the key layer changed (clip added, tab switch); re-resolved by id after
    /// undo/redo rebuilds views (as KeyLayer via FollowSelection); null while absent from this timeline so it returns with the clip.
    /// </summary>
    private Layer? SoundTarget()
    {
        if (_vm is null || _sound is null)
            return null;
        if (!SameClip(_vm.KeyLayer, _soundOver))
        {
            (_sound, _soundOver) = (null, null);
            return null;
        }

        _soundOver = _vm.KeyLayer;
        if (!_vm.Layers.Contains(_sound) && _sound.ClipId != Guid.Empty && _vm.Document.FindClip(_sound.ClipId) is { } clip && _vm.ViewOf(clip) is { } view)
            _sound = view;
        return _sound is { IsAudio: true, IsDeleted: false, IsEmptyTrack: false } && _vm.Layers.Contains(_sound) ? _sound : null;

        static bool SameClip(Layer? a, Layer? b) => ReferenceEquals(a, b) || a is { ClipId: var id } && id != Guid.Empty && id == b?.ClipId;
    }

    // ----- Target publication (MainViewModel.EditTarget) -----

    /// <summary>Current edit target: clip, Sequence Output, or none.</summary>
    private ColorGradeTarget EditTarget =>
        _noClip || _stack is null ? ColorGradeTarget.None : _layer is null ? ColorGradeTarget.ForSequence : ColorGradeTarget.For(_layer);

    private void Stack_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => _rebuild.Request();

    private void Layer_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        SpeedLayerChanged(e.PropertyName);
        switch (e.PropertyName)
        {
            case nameof(Layer.Name) or nameof(Layer.EffectsBypassed):
                UpdateHeader();
                break;
            case nameof(Layer.AudioFilters) when _layer is { IsAudio: true }:
                AudioFiltersChangedElsewhere();
                break;
            case Layer.EffectsChanged or Layer.KeysChanged or nameof(Layer.Opacity) or nameof(Layer.ChromaColor) or nameof(Layer.ChromaSimilarity) or nameof(Layer.ChromaBlend)
                or nameof(Layer.MaskGrow) or nameof(Layer.MaskInvert) or nameof(Layer.MaskOffsetX) or nameof(Layer.MaskOffsetY) or nameof(Layer.CornerRadius) or nameof(Layer.Feather) or nameof(Layer.FeatherRadius)
                or nameof(Layer.ShadowOpacity) or nameof(Layer.ShadowDistance) or nameof(Layer.ShadowAngle) or nameof(Layer.ShadowSoftness) or nameof(Layer.ShadowColor):
                QueueRefresh();
                break;
        }
    }

    private void QueueRefresh() => _refresh.Request();

    private void RefreshParams()
    {
        if (HiddenNow)
        {
            _rebuildWhenShown = true;
            return;
        }

        foreach (var model in _params)
            model.Refresh();
    }

    private void OnPlayheadMoved()
    {
        if (HiddenNow)
        {
            _rebuildWhenShown = true;
            return;
        }

        foreach (var model in _params)
        {
            if (model.Animated)
                model.Refresh();
        }
    }

    private void UpdateHeader()
    {
        var bypassed = _layer?.EffectsBypassed ?? _vm?.SequenceEffectsBypassed ?? false;
        TargetLabel.Text = _noClip ? "No clip" : _layer is null ? "Sequence Output" : _layer.IsBackground ? _layer.Name : $"Clip: {_layer.Name}";
        BypassButton.IsChecked = bypassed;
        // No audio path reads an audio clip's EffectsBypassed (its cards are Audio Filters), so bypass is disabled for audio.
        BypassButton.IsEnabled = !_noClip && _layer is not { IsAudio: true };
        BypassButton.Content = bypassed ? Glyphs.Hide : Glyphs.RedEye;
        AddButton.IsEnabled = EffectRegistry.All.Any(d => Fits(d, ignorePresent: true));
    }

    // ----- Cards -----

    private void Rebuild()
    {
        if (HiddenNow)
        {
            _rebuildWhenShown = true;
            return;
        }

        CardsPanel.Children.Clear();
        PinnedPanel.Children.Clear();
        SpeedPanel.Children.Clear();
        _params.Clear();
        UpdateHeader();
        if (_stack is null)
        {
            EmptyHint.Text = _noClip ? "No clip to give effects to: add a video, or select a clip on the timeline (or Sequence Output)." : "";
            EmptyHint.Visibility = EmptyHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        EmptyHint.Text = _stack.Count > 0 ? ""
            : _layer is null ? "No effects on the Sequence Output. Add Effect puts one on the finished picture, over the whole sequence."
            : _layer.IsBackground ? "Add Effect puts colour, a LUT, a vignette or any other look on the blur fill alone."
            : _layer.IsAdjustment ? "This adjustment clip has no effects yet: Add Effect, and they work on everything under it while it lasts."
            : _layer.HasFilters ? "No effects on this clip yet. Add Effect, or select another clip on the timeline (or Sequence Output)."
            : _layer.IsAudio ? ""
            : "This clip takes no effects: select a video, picture or text clip, or Sequence Output.";
        EmptyHint.Visibility = EmptyHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        EffectZone? zone = null;
        foreach (var card in _stack)
        {
            if (card.Definition is { } definition && definition.Zone != zone)
            {
                zone = definition.Zone;
                CardsPanel.Children.Add(ZoneHeading(definition.Zone));
            }

            CardsPanel.Children.Add(BuildCard(card));
        }

        // Speed & Time card (Speed & Pitch for audio) above the stack (EffectsPane.Speed.cs).
        AddSpeedCard();

        // Pinned Opacity card for visual clips (animated by the clip's Opacity keyframes), gated by ShowsOpacityCard.
        if (_layer is { HasStyle: true, IsAdjustment: false } && ShowsOpacityCard(_layer) && EffectRegistry.Find(EffectRegistry.Opacity) is { } opacity)
            PinnedPanel.Children.Add(BuildCard(null, opacity));

        // Blur fill's own blur/zoom card (sequence-level values) at the top of its stack.
        if (_layer is { IsBackground: true } && EffectRegistry.Find(EffectRegistry.BackgroundBlur) is { } blur)
            CardsPanel.Children.Insert(0, BuildCard(null, blur));

        // Audio filter cards: Studio Voice, De-Esser, Equalizer and dynamics (EffectsPane.Audio.cs).
        if (_layer is { IsAudio: true } sound)
            AddAudioCards(sound);
        else
            (_audioLayer, _audio) = (null, null);
    }

    private TextBlock ZoneHeading(EffectZone zone)
    {
        var heading = new TextBlock
        {
            Text = zone switch { EffectZone.Look => "LOOK", EffectZone.KeyAndMatte => "KEY & MATTE", EffectZone.Composite => "COMPOSITE", _ => zone.ToString().ToUpperInvariant() },
            FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 4, 0, 3),
            ToolTip = zone switch
            {
                EffectZone.Look => "Colour and detail, applied first, in this order. Drag an effect's grip to change the order.",
                EffectZone.KeyAndMatte => "Keys and everything that works on the transparency, after the colour, in this order.",
                _ => "How the clip is laid on the picture under it: a fixed order.",
            },
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        return heading;
    }

    /// <param name="pinned">Definition for a pinned card with no EffectInstance (Opacity, blur fill).</param>
    private Border BuildCard(EffectInstance? card, EffectDefinition? pinned = null)
    {
        var definition = card?.Definition ?? pinned;
        var title = card?.Title ?? pinned!.Name;
        var border = new Border { Tag = card, Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(4, 3, 4, 5), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "InsetBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        var root = new StackPanel();
        border.Child = root;

        var header = new DockPanel();
        root.Children.Add(header);
        var reorderable = card is not null && definition?.Reorderable == true;
        var grip = new TextBlock
        {
            Text = "⋮⋮", Width = 16, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, Opacity = reorderable ? 1 : 0.25,
            Cursor = reorderable ? Cursors.SizeNS : Cursors.Arrow,
            ToolTip = reorderable ? "Drag to move this effect up or down within its zone." : "This effect's place is fixed.",
        };
        ReorderBehavior.SetIsGrip(grip, reorderable);
        header.Children.Add(grip);

        if (card is not null)
        {
            var eye = new ToggleButton
            {
                Content = card.Enabled ? Glyphs.RedEye : Glyphs.Hide, FontFamily = Glyphs.Font, FontSize = 11, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(0, 0, 6, 0),
                IsChecked = card.Enabled, IsEnabled = !card.IsMissing, ToolTip = "On / off: an effect that is off does nothing, and keeps its values.",
            };
            AutomationProperties.SetName(eye, $"{title} On");
            eye.Click += (_, _) =>
            {
                _vm?.Checkpoint($"switch {title} {(eye.IsChecked == true ? "on" : "off")}");
                using (OwnLooks())
                    card.Enabled = eye.IsChecked == true;
                eye.Content = card.Enabled ? Glyphs.RedEye : Glyphs.Hide;
            };
            header.Children.Add(eye);

            var remove = new Button { Content = Glyphs.Cancel, FontFamily = Glyphs.Font, FontSize = 10, Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Remove this effect." };
            AutomationProperties.SetName(remove, $"Remove {title}");
            remove.Click += (_, _) => Remove(card);
            DockPanel.SetDock(remove, Dock.Right);
            header.Children.Add(remove);
        }

        var collapsed = card is not null && _collapsed.Contains(card.Id);
        var collapse = new ToggleButton { Content = collapsed ? Glyphs.ChevronDown : Glyphs.ChevronUp, FontFamily = Glyphs.Font, FontSize = 10, Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(4, 0, 0, 0), IsChecked = !collapsed, ToolTip = "Show or hide this effect's values." };
        AutomationProperties.SetName(collapse, $"Expand {title}");
        DockPanel.SetDock(collapse, Dock.Right);
        header.Children.Add(collapse);

        if (definition is not null)
        {
            var (brush, tip) = definition.Cost switch
            {
                EffectCost.Green => (GreenCost, "Green: the GPU preview barely feels it."),
                EffectCost.Amber => (AmberCost, "Amber: felt on 4K frames, and in exports."),
                _ => (RedCost, "Red: the heaviest on the GPU (and on exports): many of these on large frames can slow playback."),
            };
            var dot = new Ellipse { Width = 8, Height = 8, Fill = brush, Margin = new Thickness(6, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
            DockPanel.SetDock(dot, Dock.Right);
            header.Children.Add(dot);
        }

        header.Children.Add(new TextBlock
        {
            Text = title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = definition?.Description is { Length: > 0 } description ? description : title,
        });

        var body = new StackPanel { Margin = new Thickness(16, 4, 0, 0), Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible };
        root.Children.Add(body);
        collapse.Click += (_, _) =>
        {
            var open = collapse.IsChecked == true;
            body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            collapse.Content = open ? Glyphs.ChevronUp : Glyphs.ChevronDown;
            if (card is not null && !open)
                _collapsed.Add(card.Id);
            else if (card is not null)
                _collapsed.Remove(card.Id);
        };

        if (definition is null)
        {
            var missing = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "This project uses an effect this HandPeg does not have (from a plugin, or a later version). It does nothing here, and is saved back exactly as it was.",
            };
            missing.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            body.Children.Add(missing);
            return border;
        }

        foreach (var param in definition.Params)
        {
            if (!param.IsAdvanced && !param.StudioOnly)
                body.Children.Add(BuildRow(card, param, title));
        }

        if (definition.HasAdvanced)
            body.Children.Add(BuildAdvanced(card, definition, title));
        // Color Grade card footer: indicators and Open Color Studio (EffectsPane.ColorGrade.cs).
        if (card is not null && definition.Type == EffectRegistry.ColorStudio)
            body.Children.Add(BuildColorGradeFooter(card, title));
        if (card is not null)
            body.Children.Add(BuildRangeRow(card, title));
        return border;
    }

    /// <summary>
    /// Advanced parameters under a chevron toggle; collapsed by default, expansion shared per effect type for the process lifetime.
    /// </summary>
    private FrameworkElement BuildAdvanced(EffectInstance? card, EffectDefinition definition, string title)
    {
        var section = new StackPanel { Margin = new Thickness(0, 3, 0, 0) };
        var open = AdvancedOpen.Contains(definition.Type);
        var turn = new RotateTransform(open ? 90 : 0, 2, 4);
        var chevron = new System.Windows.Shapes.Path
        {
            Data = Chevron, StrokeThickness = 1.4, Width = 6, Height = 9, Margin = new Thickness(2, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, RenderTransform = turn,
        };
        chevron.SetResourceReference(Shape.StrokeProperty, "MutedTextBrush");
        var label = new TextBlock { Text = "Advanced", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        var header = new ToggleButton
        {
            IsChecked = open, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(2, 1, 6, 1), Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { chevron, label } },
            ToolTip = "Shows or hides this effect's deeper controls (kept open or folded for this effect on every clip while HandPeg runs).",
        };
        AutomationProperties.SetName(header, $"{title} Advanced");
        section.Children.Add(header);

        var rows = new StackPanel { Margin = new Thickness(10, 2, 0, 0), Visibility = open ? Visibility.Visible : Visibility.Collapsed };
        foreach (var param in definition.Params)
        {
            if (param.IsAdvanced && !param.StudioOnly)
                rows.Children.Add(BuildRow(card, param, title));
        }

        section.Children.Add(rows);
        header.Click += (_, _) =>
        {
            var shown = header.IsChecked == true;
            rows.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            turn.Angle = shown ? 90 : 0;
            if (shown)
                AdvancedOpen.Add(definition.Type);
            else
                AdvancedOpen.Remove(definition.Type);
        };
        return section;
    }

    /// <summary>
    /// Range row: when on, the effect applies only between Start and End in target-local seconds (sequence time for Sequence Output);
    /// buttons trim either end to the playhead. When off, it applies throughout.
    /// </summary>
    private FrameworkElement BuildRangeRow(EffectInstance card, string title)
    {
        var model = new RangeModel(this, card);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        var on = new CheckBox
        {
            Content = "Range", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0),
            ToolTip = "Only between two moments of the clip (its own seconds from where it begins); off, all of it. The buttons trim either end to the playhead.",
        };
        on.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(RangeModel.IsOn)) { Source = model, Mode = BindingMode.TwoWay });
        AutomationProperties.SetName(on, $"{title} Range");
        row.Children.Add(on);

        void Add(string property, string label, string glyph, string tip)
        {
            var box = new TextBox { Width = 52, Margin = new Thickness(4, 0, 0, 0), Style = TryFindResource("ValueBox") as Style, ToolTip = tip };
            box.SetBinding(TextBox.TextProperty, new Binding(property) { Source = model, Mode = BindingMode.TwoWay, StringFormat = "{0:0.##}" });
            box.SetBinding(IsEnabledProperty, new Binding(nameof(RangeModel.IsOn)) { Source = model, Mode = BindingMode.OneWay });
            NumericInput.SetMinimum(box, 0);
            NumericInput.SetMaximum(box, 86400);
            NumericInput.SetDragStep(box, 0.05);
            NumericInput.SetDecimals(box, 2);
            AutomationProperties.SetName(box, $"{title} Range {label}");
            row.Children.Add(box);

            var trim = new Button { Content = glyph, FontFamily = Glyphs.Font, FontSize = 10, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(2, 0, 0, 0), ToolTip = $"Trim to Playhead: the range {label.ToLowerInvariant()}s at the playhead." };
            trim.Click += (_, _) => model.TrimToPlayhead(property == nameof(RangeModel.End));
            AutomationProperties.SetName(trim, $"{title} Range {label} at Playhead");
            row.Children.Add(trim);
        }

        Add(nameof(RangeModel.Start), "Start", "\uE892", "Where the effect starts working, in seconds from the clip's start.");
        Add(nameof(RangeModel.End), "End", "\uE893", "Where it stops, in seconds from the clip's start.");
        return row;
    }

    /// <summary>Writes a card's range as one undo step (value-box drags coalesce per burst).</summary>
    private void WriteRange(EffectInstance card, (double Start, double End)? range, string what)
    {
        if (card.Range == range)
            return;

        Step(card, EffectInstance.RangeKey, what);
        using (OwnLooks())
            card.SetRange(range);
    }

    private FrameworkElement BuildRow(EffectInstance? card, EffectParam param, string title)
    {
        var model = new ParamModel(this, card, param, _layer, card is null && _vm is not null && _layer is { IsBackground: true } ? BackgroundValue(_vm, param.Key) : null);
        _params.Add(model);
        var name = $"{title} {param.Label}";

        if (param.Kind == ParamKind.Toggle)
        {
            var check = new CheckBox { Content = param.Label, Margin = new Thickness(0, 2, 0, 2), ToolTip = param.Tip.Length > 0 ? param.Tip : null };
            check.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(ParamModel.IsOn)) { Source = model, Mode = BindingMode.TwoWay });
            AutomationProperties.SetName(check, name);
            return check;
        }

        if (param.Kind == ParamKind.Action)
        {
            var action = new Button { Content = param.Label, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 2, 0, 4), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = param.Tip };
            action.Click += (_, _) =>
            {
                if (_layer is not null)
                    EditMaskRequested?.Invoke(_layer);
            };
            AutomationProperties.SetName(action, name);
            return action;
        }

        var row = new Grid { Margin = new Thickness(0, 2, 0, 2), ToolTip = param.Tip.Length > 0 ? param.Tip : null };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = param.Unit.Length > 0 ? $"{param.Label} ({param.Unit})" : param.Label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });

        FrameworkElement editor;
        switch (param.Kind)
        {
            case ParamKind.Number:
            {
                var defaultText = param.Default.ToString(CultureInfo.InvariantCulture);
                var slider = new Slider
                {
                    Minimum = param.Min, Maximum = param.Max, SmallChange = param.Step, LargeChange = param.Step * 10, VerticalAlignment = VerticalAlignment.Center,
                    IsSnapToTickEnabled = param.Step >= 1, TickFrequency = param.Step, Margin = new Thickness(4, 0, 0, 0),
                };
                slider.SetBinding(RangeBase.ValueProperty, new Binding(nameof(ParamModel.Value)) { Source = model, Mode = BindingMode.TwoWay });
                NumericInput.SetDefault(slider, defaultText);
                AutomationProperties.SetName(slider, name);
                Grid.SetColumn(slider, 1);
                row.Children.Add(slider);

                var box = new TextBox { Width = 56, Margin = new Thickness(6, 0, 0, 0), Style = TryFindResource("ValueBox") as Style };
                box.SetBinding(TextBox.TextProperty, new Binding(nameof(ParamModel.Value))
                {
                    Source = model, Mode = BindingMode.TwoWay, StringFormat = "{0:0." + new string('#', Math.Max(param.Decimals, 1)) + "}",
                });
                NumericInput.SetDefault(box, defaultText);
                NumericInput.SetMinimum(box, param.Min);
                NumericInput.SetMaximum(box, param.Max);
                NumericInput.SetDragStep(box, param.Step);
                NumericInput.SetDecimals(box, param.Decimals);
                AutomationProperties.SetName(box, name + " Value");
                Grid.SetColumn(box, 2);
                row.Children.Add(box);

                // Edge-shaping values (key, matte, mask, corners/feather): while held, the player shows the edge loupe.
                if (EdgeKindOf(card?.Type) is not EdgeTuningKind.None and var edge)
                {
                    NumericInput.WatchHold(slider, held => HoldEdge(held, edge));
                    NumericInput.WatchHold(box, held => HoldEdge(held, edge));
                }

                if (param.Animation != ParamAnimation.None)
                    row.Children.Add(BuildKeyButtons(model, name));
                return row;
            }

            case ParamKind.Choice:
            {
                var combo = new ComboBox
                {
                    Margin = new Thickness(4, 0, 0, 0), DisplayMemberPath = "Value", SelectedValuePath = "Key",
                    ItemsSource = param.Choices.Select(c => new KeyValuePair<string, string>(c.Value, c.Label)).ToList(),
                };
                combo.SetBinding(Selector.SelectedValueProperty, new Binding(nameof(ParamModel.Text)) { Source = model, Mode = BindingMode.TwoWay });
                editor = combo;
                break;
            }

            default:
            {
                var text = new TextBox { Margin = new Thickness(4, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
                text.SetBinding(TextBox.TextProperty, new Binding(nameof(ParamModel.Text)) { Source = model, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
                editor = text;
                if (param.Kind == ParamKind.File)
                {
                    var browse = new Button { Content = "Browse...", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
                    browse.Click += (_, _) =>
                    {
                        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "3D LUT (*.cube)|*.cube|All files|*.*" };
                        if (dialog.ShowDialog(Window.GetWindow(this) ?? Application.Current.MainWindow) == true)
                            model.Text = dialog.FileName;
                    };
                    AutomationProperties.SetName(browse, $"Browse {name}");
                    Grid.SetColumn(browse, 2);
                    row.Children.Add(browse);
                }

                break;
            }
        }

        AutomationProperties.SetName(editor, name);
        Grid.SetColumn(editor, 1);
        row.Children.Add(editor);
        return row;
    }

    /// <summary>Stopwatch (toggle keyframe animation) and diamond (toggle a keyframe at the playhead) buttons.</summary>
    private StackPanel BuildKeyButtons(ParamModel model, string name)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0) };
        Grid.SetColumn(buttons, 3);
        var watch = new ToggleButton
        {
            Content = Glyphs.Stopwatch, FontFamily = Glyphs.Font, FontSize = 11, Padding = new Thickness(4, 2, 4, 2),
            ToolTip = "Animate: on, this value gets keyframes (one at the playhead now; changing the value writes another). Off: the keyframes go and the value stays as it is here.",
        };
        watch.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(ParamModel.Animated)) { Source = model, Mode = BindingMode.OneWay });
        watch.Click += (_, _) => ToggleAnimated(model);
        AutomationProperties.SetName(watch, $"Animate {name}");

        var diamond = new Button { FontSize = 12, Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(2, 0, 0, 0), Foreground = Brushes.Gold, ToolTip = "A keyframe at the playhead: adds one holding the value, or removes the one there." };
        diamond.SetBinding(ContentProperty, new Binding(nameof(ParamModel.Diamond)) { Source = model, Mode = BindingMode.OneWay });
        diamond.SetBinding(VisibilityProperty, new Binding(nameof(ParamModel.DiamondVisibility)) { Source = model, Mode = BindingMode.OneWay });
        diamond.Click += (_, _) => ToggleKeyAtPlayhead(model);
        AutomationProperties.SetName(diamond, $"Keyframe {name}");
        buttons.Children.Add(watch);
        buttons.Children.Add(diamond);
        return buttons;
    }

    // ----- Edge tuning loupe -----

    /// <summary>Loupe edge kind for an effect type; None when its values do not shape an edge.</summary>
    private static EdgeTuningKind EdgeKindOf(string? type) => type switch
    {
        EffectRegistry.ChromaKey => EdgeTuningKind.ChromaKey,
        EffectRegistry.Choke or EffectRegistry.Smooth => EdgeTuningKind.Matte,
        EffectRegistry.Mask => EdgeTuningKind.Mask,
        EffectRegistry.Edges => EdgeTuningKind.Feather,
        _ => EdgeTuningKind.None,
    };

    // Slider press or box drag start/end on an edge value: toggles the player's edge loupe.
    private void HoldEdge(bool held, EdgeTuningKind kind)
    {
        if (held && _layer is not null)
            _vm?.BeginEdgeTuning(_layer, kind);
        else if (!held)
            _vm?.EndEdgeTuning();
    }

    /// <summary>
    /// Pinned Blur Fill accessors: sequence-level radius, zoom, passes and dim on the view model (which records their undo steps).
    /// </summary>
    private static (Func<double> Get, Action<double> Set)? BackgroundValue(MainViewModel vm, string key) => key switch
    {
        "radius" => (() => vm.BlurRadius, value => vm.BlurRadius = value),
        "zoom" => (() => vm.BackgroundZoom * 100, value => vm.BackgroundZoom = value / 100),
        "passes" => (() => vm.BlurPasses, value => vm.BlurPasses = (int)Math.Round(value)),
        "dim" => (() => -vm.BackgroundDim * 100, value => vm.BackgroundDim = -value / 100),
        _ => null,
    };

    // ----- Value writes -----

    /// <summary>Opens the undo step for an imminent write (<see cref="MainViewModel.StepForLook"/>): one per drag, run rule otherwise.</summary>
    private void Step(object? target, string field, string what, bool startRun = false)
    {
        if (_vm is { } vm && (target ?? _layer ?? (object)vm) is { } owner)
            vm.StepForLook(owner, field, what, _writes.Gesture, startRun);
    }

    /// <summary>Flushes all pending paced writes (row drags, audio filters, Pitch Shift).</summary>
    internal void FlushWrites() => _writes.Flush();

    /// <summary>Owned-look scope for unpaced writes, so the view model records no extra undo step.</summary>
    private MainViewModel.OwnedLooks? OwnLooks() => _vm?.OwnLookScope();

    /// <summary>
    /// Paced row write (<see cref="GestureWriter{TKey}"/>): the latest value is shown as Pending until flushed; a drag's final value is
    /// written on release or retarget.
    /// </summary>
    private void WriteValue(ParamModel model, double value)
    {
        _drag.Follow();
        model.Pending = value;
        _writes.Queue(model, () =>
        {
            model.Pending = null;
            Write(model, value);
        });
    }

    private void Write(ParamModel model, double value)
    {
        var param = model.Param;
        value = param.Quantize(value);
        if (Math.Abs(value - model.Value) < 1e-9)
            return;

        // View-model-bound value (blur fill): record its undo step here before writing.
        if (model.Bound is { } own)
        {
            Step(_vm, param.Key, $"change {param.Label}");
            own.Set(value);
            model.Refresh();
            return;
        }

        Step(model.Card, param.Key, $"change {param.Label}");
        using (OwnLooks())
        {
            // Layer-bound values with card keyframes (e.g. chroma key) upsert a keyframe on the card instead.
            if (model.Card is { } keyed && param.Bind != nameof(Layer.Opacity) && keyed.HasKeys(param.Key))
                keyed.SetKeys(param.Key, KeyEdit.Upsert(keyed.GetKeys(param.Key), ClipTime, value));
            else if (param.Bind is { } bind && _layer is not null && typeof(Layer).GetProperty(bind) is { } property)
                property.SetValue(_layer, property.PropertyType == typeof(int) ? (int)Math.Round(value) : property.PropertyType == typeof(bool) ? value >= 0.5 : value);
            else if (model.Card is { } card && card.HasKeys(param.Key))
                card.SetKeys(param.Key, KeyEdit.Upsert(card.GetKeys(param.Key), ClipTime, value));
            else
                model.Card?.Set(param.Key, value);
        }

        model.Refresh();
    }

    private void WriteText(ParamModel model, string value)
    {
        if (value == model.Text)
            return;

        _vm?.Checkpoint($"change {model.Param.Label}");
        using (OwnLooks())
        {
            if (model.Param.Bind is { } bind && _layer is not null && typeof(Layer).GetProperty(bind) is { PropertyType: var type } property && type == typeof(string))
                property.SetValue(_layer, value);
            else
                model.Card?.SetText(model.Param.Key, value);
        }

        model.Refresh();
    }

    private void ToggleAnimated(ParamModel model)
    {
        var (param, time, value) = (model.Param, Math.Max(ClipTime, 0), model.Value);
        var on = !model.Animated;
        _vm?.Checkpoint($"{(on ? "animate" : "stop animating")} {param.Label}");
        using (OwnLooks())
        {
            if (param.Bind == nameof(Layer.Opacity) && _layer is not null)
            {
                _layer.SetKeys(KeyProperty.Opacity, on ? [new Keyframe(time, _layer.Opacity)] : null);
            }
            else if (model.Card is { } card)
            {
                card.Set(param.Key, value);
                card.SetKeys(param.Key, on ? [new Keyframe(time, value)] : null);

                // Layer-bound value keeps the value at this time.
                if (!on && param.Bind is { } bind && _layer is not null && typeof(Layer).GetProperty(bind) is { PropertyType: var type } property && type == typeof(double))
                    property.SetValue(_layer, value);
            }
        }

        model.Refresh();
    }

    private void ToggleKeyAtPlayhead(ParamModel model)
    {
        var (param, time, value) = (model.Param, Math.Max(ClipTime, 0), model.Value);
        var keys = KeyEdit.Toggle(model.Keys, time, value, out var removed);

        _vm?.Checkpoint($"{(removed ? "remove a keyframe of" : "add a keyframe to")} {param.Label}");
        using (OwnLooks())
        {
            if (param.Bind == nameof(Layer.Opacity) && _layer is not null)
            {
                _layer.SetKeys(KeyProperty.Opacity, keys);
            }
            else if (model.Card is { } card)
            {
                // Last keyframe removed: keep the value from this time as the static value.
                if (keys.Count == 0)
                    card.Set(param.Key, value);
                card.SetKeys(param.Key, keys);
            }
        }

        model.Refresh();
    }

    /// <summary>Evaluates keyframes at a time using the graph's easing.</summary>
    private static double Evaluate(IReadOnlyList<Keyframe> keys, double time) => EasingCurves.ValueAt(keys, time);

    // ----- Add, remove, reorder -----

    /// <summary>True if the effect fits the target: Look effects for Sequence Output; per clip kind for clips; bound effects at most once.</summary>
    private bool Fits(EffectDefinition definition, bool ignorePresent = false)
    {
        if (_stack is null)
            return false;
        if (definition.Pinned)
            return PinnedFits(definition);
        return _layer is null ? definition.ForWholePicture : _layer.CanTake(definition, ignorePresent);
    }

    private void Add(EffectDefinition definition)
    {
        if (_vm is null || _stack is null)
            return;

        CloseAdd();
        if (definition.Pinned)
        {
            AddPinned(definition);
            return;
        }

        if (_layer is null)
        {
            _vm.Checkpoint($"add {definition.Name}");
            using (OwnLooks())
            {
                var card = definition.NewEffect();
                _stack.Insert(Layer.InsertionIndex(_stack, card), card);
            }

            _vm.StatusText = $"{definition.Name} added to the Sequence Output. Ctrl+Z takes it off.";
            return;
        }

        // Clip or blur fill: add with visible defaults, same path as the track menu's Add Effect.
        var layer = _layer;
        if (_vm.AddEffect(layer, definition) is not null && definition.Type == EffectRegistry.Mask && !layer.CustomMask)
            EditMaskRequested?.Invoke(layer);
    }

    private void Remove(EffectInstance card)
    {
        if (_vm is null || _stack is null)
            return;

        _vm.Checkpoint($"remove {card.Title}");
        using (OwnLooks())
        {
            if (_layer is not null)
                _layer.RemoveEffect(card);
            else
                _stack.Remove(card);
        }
    }

    private void OnReordered(Panel panel)
    {
        if (!ReferenceEquals(panel, CardsPanel) || _stack is null || _vm is null)
            return;

        var order = CardsPanel.Children.OfType<FrameworkElement>().Select(c => c.Tag).OfType<EffectInstance>().ToList();
        var zones = order.Select(c => c.Definition?.Zone).OfType<EffectZone>().ToList();
        var valid = order.Count == _stack.Count && zones.Zip(zones.Skip(1)).All(p => p.First <= p.Second);
        if (!valid || order.SequenceEqual(_stack))
        {
            if (!valid)
                _vm.StatusText = "Effects move within their own zone: keys and masks stay below the colour effects, and the composite below both.";
            Rebuild();
            return;
        }

        _vm.Checkpoint("reorder the effects");
        using (OwnLooks())
        {
            for (var i = 0; i < order.Count; i++)
            {
                var from = _stack.IndexOf(order[i]);
                if (from != i)
                    _stack.Move(from, i);
            }
        }
    }

    // ----- Title row -----

    private void BypassButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null)
            return;

        var on = BypassButton.IsChecked == true;
        _vm.Checkpoint(on ? "bypass the effects" : "bring the effects back");
        using (OwnLooks())
        {
            if (_layer is not null)
                _layer.EffectsBypassed = on;
            else
                _vm.SequenceEffectsBypassed = on;
        }

        UpdateHeader();
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = PlacementMode.Bottom };
        void Item(string header, string tip, bool enabled, Action action)
        {
            var item = new MenuItem { Header = header, ToolTip = tip, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        var has = _stack is { Count: > 0 };
        // Audio clips cannot take video effect cards.
        var canPaste = _clipboard is { Count: > 0 } && _stack is not null && _layer is not { IsAudio: true };
        Item("Copy Effects", "Copies every effect of this stack, with its values, to paste onto another clip or the Sequence Output.", has, CopyStack);
        Item("Paste Effects", "Replaces this clip's stack with the effects copied. Hold Shift while clicking (or tick Apply Paste to All Clips on Track) to give every clip on this track the copied stack.",
            canPaste, () => PasteEffects(_pasteToTrack || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)));
        Item("Paste Some Effects...", "Lists the copied effects, each with a box, to choose which are added to this clip (or every clip on its track).", canPaste, PasteSelected);
        var toTrack = new MenuItem
        {
            Header = "Apply Paste to All Clips on Track", IsCheckable = true, IsChecked = _pasteToTrack, StaysOpenOnClick = true, IsEnabled = CanPasteToTrack,
            ToolTip = "While ticked, Paste Effects gives every clip on the selected clip's track the copied stack, not only the clip.",
        };
        toTrack.Click += (_, _) => _pasteToTrack = toTrack.IsChecked;
        menu.Items.Add(toTrack);
        menu.Items.Add(new Separator());
        Item("Save Effect Preset...", $"Saves this stack as a .hpfx file in {AppPaths.EffectPresets}; it is then listed under Presets in Add Effect.", has, SavePreset);
        Item("Remove All Effects", "Removes every effect of this stack (Ctrl+Z brings them back).", has, ResetStack);
        menu.IsOpen = true;
    }

    /// <summary>True when the effects clipboard is non-empty (queried by the clip context menu).</summary>
    public bool HasCopiedEffects => _clipboard is { Count: > 0 };

    /// <summary>Copies the target's stack (clip context menu selects the clip first).</summary>
    public void CopyEffects() => CopyStack();

    /// <summary>Pastes the clipboard onto the target or its multi-selection (clip context menu).</summary>
    public void PasteCopiedEffects() => PasteEffects(toTrack: false);

    private List<EffectState> CaptureStack() => _stack is null ? [] : [.. _stack.Select(c => _layer?.CaptureEffect(c) ?? c.ToState())];

    private void CopyStack()
    {
        _clipboard = CaptureStack();
        if (_vm is not null)
            _vm.StatusText = $"Copied {_clipboard.Count} effect{(_clipboard.Count == 1 ? "" : "s")}: Paste Effects puts them on another clip.";
    }

    /// <summary>True when the target is a timeline clip (not the blur fill row or Sequence Output).</summary>
    private bool CanPasteToTrack => _layer is not null && _vm?.Layers.Contains(_layer) == true;

    /// <summary>Paste Effects: the copied stack replaces the target's, or every clip's on its track.</summary>
    private void PasteEffects(bool toTrack)
    {
        if (_clipboard is null || _vm is null || _layer is { IsAudio: true })
            return;

        // Multi-selection (Ctrl/Shift on the timeline): paste to all in one undo step.
        if (!toTrack && _layer is { } target && _vm.IsInClipGroup(target))
        {
            _vm.PasteEffectsToClips(_vm.SelectedClips, _clipboard);
            return;
        }

        if (toTrack && CanPasteToTrack)
        {
            _vm.PasteEffectsToTrack(_layer!, _clipboard);
            return;
        }

        _vm.Checkpoint("paste effects");
        ReplaceWith(_clipboard, append: false);
    }

    /// <summary>Paste Some Effects...: appends the effects checked in a dialog to the target, or to every clip on its track.</summary>
    private void PasteSelected()
    {
        if (_clipboard is null || _vm is null || _stack is null || _layer is { IsAudio: true })
            return;

        var owner = Window.GetWindow(this) ?? Application.Current.MainWindow;
        var target = _layer is null ? "the Sequence Output" : _layer.Name;
        var dialog = new PasteEffectsDialog(_clipboard, target, CanPasteToTrack, _pasteToTrack) { Owner = owner };
        if (dialog.ShowDialog() != true || dialog.Chosen is not { Count: > 0 } chosen)
            return;

        if (dialog.ToTrack && CanPasteToTrack)
        {
            _vm.PasteEffectsToTrack(_layer!, chosen, append: true);
            return;
        }

        // Multi-selection: append the chosen effects to all in one undo step.
        if (_layer is { } grouped && _vm.IsInClipGroup(grouped))
        {
            _vm.PasteEffectsToClips(_vm.SelectedClips, chosen, append: true);
            return;
        }

        _vm.Checkpoint($"paste {chosen.Count} effect{(chosen.Count == 1 ? "" : "s")}");
        ReplaceWith(chosen, append: true);
    }

    /// <summary>Applies effect states to the target (adopted): skips unfitting ones (unknown types kept); single-instance effects never duplicate.</summary>
    private void ReplaceWith(IReadOnlyList<EffectState> states, bool append)
    {
        if (_stack is null)
            return;

        using (OwnLooks())
        {
            var keep = append ? _stack.ToList() : [];
            var cards = new List<EffectInstance>(keep);
            foreach (var state in states)
            {
                var definition = EffectRegistry.Find(state.Type);
                if (definition is not null && !Fits(definition, ignorePresent: true))
                    continue;

                // Single-instance effect already present: the pasted one replaces the existing one.
                if (definition is { Feature: not null } && cards.FindIndex(c => c.Type == state.Type) is var present and >= 0)
                {
                    if (!append || present >= keep.Count)
                        continue;
                    cards.RemoveAt(present);
                    keep.RemoveAt(present);
                }

                cards.Add(_layer?.AdoptEffect(state) ?? EffectInstance.FromState(state));
            }

            // Stable sort by zone, preserving order within each zone.
            cards = [.. cards.OrderBy(c => (int)(c.Definition?.Zone ?? EffectZone.Look))];
            if (_layer is not null)
            {
                _layer.SetEffects(cards);
            }
            else
            {
                _stack.Clear();
                foreach (var card in cards)
                    _stack.Add(card);
            }
        }
    }

    private void SavePreset()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.EffectPresets);
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                InitialDirectory = AppPaths.EffectPresets, Filter = "HandPeg effect preset (*.hpfx)|*.hpfx", DefaultExt = ".hpfx",
                FileName = _layer is null ? "Sequence Output" : _layer.Name,
            };
            if (dialog.ShowDialog(Window.GetWindow(this) ?? Application.Current.MainWindow) != true)
                return;

            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(CaptureStack(), new JsonSerializerOptions { WriteIndented = true }));
            if (_vm is not null)
                _vm.StatusText = $"Saved the effect preset {System.IO.Path.GetFileNameWithoutExtension(dialog.FileName)}: Add Effect lists it under Presets.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_vm is not null)
                _vm.Fail($"The effect preset could not be saved: {ex.Message}", "Save an effect preset", ex);
        }
    }

    private void ApplyPreset(string path)
    {
        CloseAdd();
        try
        {
            var states = JsonSerializer.Deserialize<List<EffectState>>(File.ReadAllText(path)) ?? [];
            _vm?.Checkpoint($"apply the effect preset {System.IO.Path.GetFileNameWithoutExtension(path)}");
            ReplaceWith(states, append: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            if (_vm is not null)
                _vm.Fail($"The effect preset could not be read: {ex.Message}", "Read an effect preset", ex);
        }
    }

    private void ResetStack()
    {
        if (_stack is null || _vm is null)
            return;

        _vm.Checkpoint("reset the effects");
        using (OwnLooks())
        {
            foreach (var card in _stack.ToList())
            {
                if (_layer is not null)
                    _layer.RemoveEffect(card);
                else
                    _stack.Remove(card);
            }

            if (_layer is not null)
                _layer.EffectsBypassed = false;
            else
                _vm.SequenceEffectsBypassed = false;
        }
    }

    // ----- Add Effect flyout -----

    private void OpenAdd(bool focusSearch)
    {
        _addClose.Stop();
        SearchBox.Text = "";
        BuildAddList("");
        AddPopup.IsOpen = true;
        if (focusSearch)
            Dispatcher.BeginInvoke(() => Keyboard.Focus(SearchBox), DispatcherPriority.Loaded);
    }

    private void BuildAddList(string search)
    {
        AddList.Children.Clear();
        bool Matches(string text) => search.Length == 0 || text.Contains(search, StringComparison.OrdinalIgnoreCase);
        var available = EffectRegistry.All.Where(d => Fits(d) && (Matches(d.Name) || Matches(d.Category)))
            .GroupBy(d => d.Category).OrderBy(g => EffectRegistry.CategoryRank(g.Key));
        foreach (var group in available)
        {
            AddList.Children.Add(MenuHeading(group.Key));
            foreach (var definition in group)
                AddList.Children.Add(MenuButton(definition.Name, $"{definition.Description} ({definition.Cost} cost in the preview.)", () => Add(definition)));
        }

        // Effect presets (.hpfx) saved from this pane.
        var presets = Directory.Exists(AppPaths.EffectPresets)
            ? Directory.EnumerateFiles(AppPaths.EffectPresets, "*.hpfx").Where(f => Matches(System.IO.Path.GetFileNameWithoutExtension(f))).Order().ToList()
            : [];
        if (presets.Count > 0 && _stack is not null)
        {
            AddList.Children.Add(MenuHeading("Presets"));
            foreach (var preset in presets)
                AddList.Children.Add(MenuButton(System.IO.Path.GetFileNameWithoutExtension(preset), "Adds this saved stack's effects to the target.", () => ApplyPreset(preset)));
        }

        if (AddList.Children.Count == 0)
            AddList.Children.Add(MenuHeading(_stack is null ? "Nothing selected." : "No effect matches."));
    }

    private TextBlock MenuHeading(string text)
    {
        var heading = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 6, 0, 2) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        return heading;
    }

    private static Button MenuButton(string text, string tip, Action click)
    {
        var button = new Button
        {
            Content = text, ToolTip = tip, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 1, 0, 1),
            Background = Brushes.Transparent,
        };
        button.SetResourceReference(ForegroundProperty, "TextBrush");
        button.Click += (_, _) => click();
        return button;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => BuildAddList(SearchBox.Text.Trim());

    private void AddButton_Click(object sender, RoutedEventArgs e) => OpenAdd(focusSearch: true);

    private void AddHost_MouseEnter(object sender, MouseEventArgs e)
    {
        _addClose.Stop();
        if (!AddPopup.IsOpen && AddButton.IsEnabled)
            OpenAdd(focusSearch: false);
    }

    private void AddFlyout_MouseLeave(object sender, MouseEventArgs e)
    {
        _addClose.Stop();
        _addClose.Start();
    }

    private void AddClose_Tick(object? sender, EventArgs e)
    {
        _addClose.Stop();
        if (!AddHost.IsMouseOver && !AddFlyoutPanel.IsMouseOver && !AddFlyoutPanel.IsKeyboardFocusWithin)
            CloseAdd();
    }

    /// <summary>
    /// Closes the Add Effect flyout and moves focus out of its search box (which otherwise keeps it open via <see cref="AddClose_Tick"/>
    /// and would swallow typing once hidden).
    /// </summary>
    private void CloseAdd()
    {
        _addClose.Stop();
        var hadFocus = AddFlyoutPanel.IsKeyboardFocusWithin;
        AddPopup.IsOpen = false;
        if (hadFocus)
            Keyboard.Focus(AddButton);
    }

    // While open, a press in the window outside the flyout and its button, or window deactivation, closes it (a focused search box
    // would otherwise hold it open). Presses inside the flyout also route to the window and are filtered by IsInFlyout.
    private void AddPopup_Opened(object? sender, EventArgs e)
    {
        _addOwner = Window.GetWindow(this);
        if (_addOwner is null)
            return;

        _addOwner.PreviewMouseDown += AddOwner_PreviewMouseDown;
        _addOwner.Deactivated += AddOwner_Deactivated;
    }

    private void AddPopup_Closed(object? sender, EventArgs e)
    {
        if (_addOwner is not null)
        {
            _addOwner.PreviewMouseDown -= AddOwner_PreviewMouseDown;
            _addOwner.Deactivated -= AddOwner_Deactivated;
            _addOwner = null;
        }
    }

    private void AddOwner_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!AddHost.IsMouseOver && !AddFlyoutPanel.IsMouseOver && !IsInFlyout(e.OriginalSource as DependencyObject))
            CloseAdd();
    }

    // Popup input routes through the popup's logical parent, so the window's PreviewMouseDown sees flyout presses first; closing on them
    // would remove the button before its Click.
    private bool IsInFlyout(DependencyObject? source)
    {
        for (var at = source; at is not null; at = at is Visual ? VisualTreeHelper.GetParent(at) ?? LogicalTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
        {
            if (ReferenceEquals(at, AddFlyoutPanel))
                return true;
        }

        return false;
    }

    private void AddOwner_Deactivated(object? sender, EventArgs e)
    {
        // Focus moving into the popup's own HWND is not a real deactivation; any other window is.
        if (!AddFlyoutPanel.IsKeyboardFocusWithin)
            CloseAdd();
    }

    private void AddButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key == Key.System ? e.SystemKey : e.Key) != Key.Down)
            return;

        e.Handled = true;
        OpenAdd(focusSearch: true);
    }

    private void AddFlyout_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        CloseAdd();
        Keyboard.Focus(AddButton);
    }

    // ----- Parameter row binding model -----

    private sealed class ParamModel(EffectsPane pane, EffectInstance? card, EffectParam param, Layer? layer, (Func<double> Get, Action<double> Set)? bound = null)
        : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public EffectParam Param => param;

        public EffectInstance? Card => card;

        /// <summary>View-model accessor (blur fill) used instead of an effect or clip value.</summary>
        public (Func<double> Get, Action<double> Set)? Bound => bound;

        /// <summary>Unflushed drag value (<see cref="WriteValue"/>), displayed until written.</summary>
        public double? Pending { get; set; }

        private System.Reflection.PropertyInfo? BoundProperty => param.Bind is { } name && layer is not null ? typeof(Layer).GetProperty(name) : null;

        public IReadOnlyList<Keyframe> Keys => param.Bind == nameof(Layer.Opacity) && layer is not null ? layer.GetKeys(KeyProperty.Opacity)
            : card?.GetKeys(param.Key) ?? [];

        public bool Animated => param.Animation != ParamAnimation.None && Keys.Count > 0;

        public double Value
        {
            get
            {
                if (Pending is { } pending)
                    return pending;
                if (bound is { } own)
                    return own.Get();
                if (card is not null && param.Bind != nameof(Layer.Opacity) && card.HasKeys(param.Key))
                    return Evaluate(card.GetKeys(param.Key), Math.Max(pane.ClipTime, 0));
                if (BoundProperty is { } property)
                    return property.GetValue(layer) switch { int whole => whole, double number => number, bool on => on ? 1 : 0, _ => param.Default };
                if (card is null)
                    return param.Default;
                return card.HasKeys(param.Key) ? Evaluate(card.GetKeys(param.Key), Math.Max(pane.ClipTime, 0)) : card.Get(param.Key);
            }
            set => pane.WriteValue(this, value);
        }

        public bool IsOn
        {
            get => Value >= 0.5;
            set => pane.WriteValue(this, value ? 1 : 0);
        }

        public string Text
        {
            get => BoundProperty is { } property ? property.GetValue(layer) as string ?? "" : card?.GetText(param.Key) ?? "";
            set => pane.WriteText(this, value ?? "");
        }

        public string Diamond => KeyEdit.HasKeyAt(Keys, Math.Max(pane.ClipTime, 0)) ? "◆" : "◇";

        public Visibility DiamondVisibility => Animated ? Visibility.Visible : Visibility.Collapsed;

        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    /// <summary>Range row binding model; when off, boxes show the default span (0 to target length).</summary>
    private sealed class RangeModel(EffectsPane pane, EffectInstance card) : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        // Default range length: clip duration (sequence length for the blur fill), or 5 s if unknown.
        private double Length => pane._layer is { IsBackground: true } && pane._vm is { SequenceSeconds: > 0.01 } vm ? vm.SequenceSeconds
            : pane._layer is { Duration: > 0.01 } clip ? clip.Duration : 5;

        public bool IsOn
        {
            get => card.Range is not null;
            set => Write(value ? (0, Length) : null, value ? "limit an effect to a range" : "let an effect work throughout");
        }

        public double Start
        {
            get => card.Range?.Start ?? 0;
            set => Write((Math.Clamp(value, 0, End - 0.01), End), "change an effect's range");
        }

        public double End
        {
            get => card.Range?.End ?? Length;
            set => Write((Start, Math.Max(value, Start + 0.01)), "change an effect's range");
        }

        /// <summary>Trims one range end to the playhead (enabling the range).</summary>
        public void TrimToPlayhead(bool end)
        {
            var at = Math.Max(pane.ClipTime, 0);
            var (start, stop) = card.Range ?? (0, Length);
            if (end && at > start + 0.01)
                Write((start, at), "trim an effect's range to the playhead");
            else if (!end && at < stop - 0.01)
                Write((at, stop), "trim an effect's range to the playhead");
            else if (pane._vm is { } vm)
                vm.StatusText = (end ? "The playhead is before the range's start: move it later to end the range there." : "The playhead is after the range's end: move it earlier to start the range there.");
        }

        private void Write((double Start, double End)? range, string what)
        {
            pane.WriteRange(card, range is { } r ? (Math.Round(r.Start, 3), Math.Round(r.End, 3)) : null, what);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }
}
