using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// Tooltip showing a KeybindManager action's current bindings, replacing hard-coded keys in XAML that went stale on rebind.
/// <c>KeyTip.Text</c> holds the text with "{keys}" for " (F)" (empty while <c>KeyTip.Action</c> is unbound) and "{key:action.id}" for
/// another action's key inline (the action name when unbound; "{key:action.id|words}" supplies fallback words). Text is resolved on
/// each open, so Settings changes show immediately. Before that, the element holds a key-less stand-in built without touching
/// KeybindManager (even querying whether its map exists would build the action table before Frame 1 for every KeyTip); the map is
/// built on the first key press or tooltip. Text never starts with "{" (XAML would parse it as a markup extension).
/// OverflowStrip's "..." menu also reads it (<see cref="Render"/>).
/// </summary>
/// <remarks>
/// Two-stage display: text shaped "Name{keys}: description" (or a short name before ": ") first shows only name and keys; after
/// <see cref="DetailDwellMs"/> of hover the description (at most twelve words) expands below. Other text shows in full immediately.
/// </remarks>
public static class KeyTip
{
    /// <summary>Hover dwell in ms before the description expands below the name and keys.</summary>
    public const int DetailDwellMs = 1000;

    public static readonly DependencyProperty ActionProperty = DependencyProperty.RegisterAttached(
        "Action", typeof(string), typeof(KeyTip), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(KeyTip), new PropertyMetadata(null, OnChanged));

    // Per-element two-stage tooltip card, created on first open.
    private static readonly DependencyProperty CardProperty = DependencyProperty.RegisterAttached(
        "Card", typeof(ToolTip), typeof(KeyTip));

    // Shared dwell timer (only one tooltip is open at a time) and its target card; created on the first two-stage tooltip, after Frame 1.
    private static DispatcherTimer? _dwell;
    private static ToolTip? _dwelling;

    public static string? GetAction(DependencyObject element) => (string?)element.GetValue(ActionProperty);

    public static void SetAction(DependencyObject element, string? value) => element.SetValue(ActionProperty, value);

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || GetText(element) is not { } text)
            return;

        element.ToolTipOpening -= Opening;
        element.ToolTipOpening += Opening;
        // Key-less stand-in so a tooltip exists to open; Opening replaces it with the resolved keys before display.
        element.ToolTip = Fill(text, GetAction(element), keys: false);
    }

    // WPF reads ToolTip after raising ToolTipOpening, so the value set here is what displays.
    private static void Opening(object sender, ToolTipEventArgs e)
    {
        if (sender is FrameworkElement element && Prepare(element) is { } tip)
            element.ToolTip = tip;
    }

    /// <summary>
    /// Current tooltip content: the full text (one stage), or the card with name and keys and the description collapsed (two
    /// stages); null when the element has no KeyTip.
    /// </summary>
    internal static object? Prepare(FrameworkElement element)
    {
        if (Render(element) is not { } text)
            return null;
        var (name, detail) = Split(text);
        if (detail.Length == 0)
            return text;

        var card = CardFor(element);
        var (title, more) = Parts(card);
        title.Text = name;
        more.Text = detail;
        more.Visibility = Visibility.Collapsed;
        return card;
    }

    /// <summary>
    /// Splits KeyTip text into (name with keys, description); description is "" for one-stage text. Splits at the first ": " only
    /// when the prefix is a name: it ends with a single trailing "(keys)" group, or has at most five words and no parenthesis.
    /// </summary>
    internal static (string Name, string Detail) Split(string text)
    {
        var colon = text.IndexOf(": ", StringComparison.Ordinal);
        if (colon <= 0)
            return (text.TrimEnd('.'), "");

        var name = text[..colon];
        var isName = name.EndsWith(')') && name.Contains(" (", StringComparison.Ordinal) && !name[..name.LastIndexOf(" (", StringComparison.Ordinal)].Contains('(')
            || name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 5 && !name.Contains('(');
        if (!isName)
            return (text, "");

        var detail = text[(colon + 2)..].Trim();
        return (name, detail.Length == 0 ? "" : char.ToUpperInvariant(detail[0]) + detail[1..]);
    }

    /// <summary>Description word count with each brace token counted as one (microcopy limit: twelve).</summary>
    internal static int Words(string detail) => Regex.Replace(detail, @"\{[^}]*\}", "X").Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private static ToolTip CardFor(FrameworkElement element)
    {
        if (element.GetValue(CardProperty) is ToolTip made)
            return made;

        var title = new TextBlock();
        var more = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 280, Margin = new Thickness(0, 3, 0, 0), Opacity = 0.85, Visibility = Visibility.Collapsed };
        var card = new ToolTip { Content = new StackPanel { Children = { title, more } } };
        card.Opened += (_, _) => StartDwell(element, card);
        card.Closed += (_, _) =>
        {
            if (ReferenceEquals(_dwelling, card))
            {
                _dwelling = null;
                _dwell?.Stop();
            }

            Parts(card).More.Visibility = Visibility.Collapsed;
        };
        element.SetValue(CardProperty, card);
        return card;
    }

    private static (TextBlock Title, TextBlock More) Parts(ToolTip card) =>
        card.Content is StackPanel { Children: [TextBlock title, TextBlock more] } ? (title, more) : throw new InvalidOperationException("Not a KeyTip card.");

    // Expand DetailDwellMs after hover rest; the tooltip's own initial show delay is subtracted from the wait.
    private static void StartDwell(FrameworkElement element, ToolTip card)
    {
        _dwelling = card;
        var wait = Math.Max(0, DetailDwellMs - ToolTipService.GetInitialShowDelay(element));
        if (_dwell is null)
        {
            _dwell = new DispatcherTimer(DispatcherPriority.Input);
            _dwell.Tick += (_, _) =>
            {
                _dwell.Stop();
                if (_dwelling is { IsOpen: true } open)
                    Expand(open);
            };
        }

        _dwell.Stop();
        _dwell.Interval = TimeSpan.FromMilliseconds(Math.Max(wait, 1));
        _dwell.Start();
    }

    /// <summary>Expands a card's description (on dwell expiry; tests call it directly).</summary>
    internal static void Expand(ToolTip card) => Parts(card).More.Visibility = Visibility.Visible;

    /// <summary>KeyTip text resolved with current bindings; null when the element has none.</summary>
    public static string? Render(DependencyObject element) =>
        GetText(element) is { } text ? Fill(text, GetAction(element), keys: true) : null;

    // "{keys}" -> " (F)" or empty; "{key:id}" -> the key name (else the action name, else the words after "|"); other brace
    // tokens stay verbatim. With keys:false (stand-in), keys are omitted as if unbound and "{key:id}" without "|" words falls
    // back to the id, so the stand-in never touches KeybindManager (and is never displayed).
    private static string Fill(string text, string? action, bool keys)
    {
        if (text.IndexOf('{') < 0)
            return text;

        var built = new StringBuilder(text.Length + 16);
        var at = 0;
        while (at < text.Length)
        {
            var open = text.IndexOf('{', at);
            var close = open < 0 ? -1 : text.IndexOf('}', open + 1);
            if (close < 0)
            {
                built.Append(text, at, text.Length - at);
                break;
            }

            built.Append(text, at, open - at);
            var token = text.Substring(open + 1, close - open - 1);
            if (token == "keys")
            {
                if (keys && action is not null && KeybindManager.GestureText(action) is { Length: > 0 } gesture)
                    built.Append(" (").Append(gesture).Append(')');
            }
            else if (token.StartsWith("key:", StringComparison.Ordinal))
            {
                var bar = token.IndexOf('|');
                var id = bar < 0 ? token[4..] : token[4..bar];
                var gesture = keys ? KeybindManager.GestureText(id) : "";
                built.Append(gesture.Length > 0 ? gesture
                    : bar >= 0 ? token[(bar + 1)..]
                    : keys ? KeybindManager.Action(id)?.Label.TrimEnd('.') ?? id : id);
            }
            else
            {
                built.Append(text, open, close - open + 1);
            }

            at = close + 1;
        }

        return built.ToString();
    }
}
