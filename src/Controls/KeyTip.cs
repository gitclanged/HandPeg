using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// A tooltip that names the keys of a KeybindManager action as they are bound now (4.4-BugHunt), instead of keys written
/// into the XAML that went stale when the keys changed. <c>KeyTip.Text</c> holds the words, with "{keys}" where " (F)" goes
/// (nothing while <c>KeyTip.Action</c> has no key) and "{key:action.id}" for another action's key inside a sentence (the
/// action's name while it has none; "{key:action.id|words}" says what to write instead). The tooltip is filled in each time
/// it opens, so keys changed in Settings show at once. Until then the element holds the words without keys, made without
/// asking KeybindManager anything (5.0: even asking whether its map was made made its table of actions, before the first
/// frame, for every KeyTip of the window and the timeline); the map is made at the first key or tooltip. Text never starts
/// with "{" (XAML would read it as a markup extension). OverflowStrip's "..." menu reads it too (<see cref="Render"/>).
/// </summary>
/// <remarks>
/// Two stages: a text of the form "Name{keys}: what it does" (or a short name before ": ") first shows only the name and its keys;
/// after <see cref="DetailDwellMs"/> of hover the description (twelve words at most) opens under them. Any other text shows whole at once.
/// </remarks>
public static class KeyTip
{
    /// <summary>How long the pointer rests on a control before its tooltip's description opens under its name and keys, ms.</summary>
    public const int DetailDwellMs = 1000;

    public static readonly DependencyProperty ActionProperty = DependencyProperty.RegisterAttached(
        "Action", typeof(string), typeof(KeyTip), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(KeyTip), new PropertyMetadata(null, OnChanged));

    // The element's two-stage tooltip, made the first time it opens.
    private static readonly DependencyProperty CardProperty = DependencyProperty.RegisterAttached(
        "Card", typeof(ToolTip), typeof(KeyTip));

    // The one dwell timer (one tooltip is open at a time) and the card it opens: made at the first two-stage tooltip, after Frame 1.
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
        // A stand-in, so there is a tooltip to open: Opening fills in the keys before it is ever seen.
        element.ToolTip = Fill(text, GetAction(element), keys: false);
    }

    // WPF reads the tooltip after raising ToolTipOpening, so what is set here is what shows.
    private static void Opening(object sender, ToolTipEventArgs e)
    {
        if (sender is FrameworkElement element && Prepare(element) is { } tip)
            element.ToolTip = tip;
    }

    /// <summary>
    /// What the element's tooltip shows now: its words whole (one stage), or its card with the name and keys and the description
    /// still closed (two stages); null when it has no KeyTip.
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
    /// The two stages of a KeyTip's words: the name with its keys, and what it does ("" when the words are one stage). A text splits at
    /// its first ": " when what comes before is a name: it ends with the keys, or is five words or fewer with no parenthesis.
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

    /// <summary>Words in a description, the keys' tokens counted as one: the microcopy law allows twelve.</summary>
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

    // The description opens DetailDwellMs after the pointer came to rest on the element: the tooltip itself opened its initial delay in.
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

    /// <summary>Opens a card's description (the dwell ran out; tests call it directly).</summary>
    internal static void Expand(ToolTip card) => Parts(card).More.Visibility = Visibility.Visible;

    /// <summary>The element's KeyTip words with the keys as bound now; null when it has none.</summary>
    public static string? Render(DependencyObject element) =>
        GetText(element) is { } text ? Fill(text, GetAction(element), keys: true) : null;

    // "{keys}" becomes " (F)" (or nothing), "{key:id}" the key's name (or the action's, or the words after "|"); anything else
    // in braces stays as it is. Without keys (the stand-in), every key is left out the same way an unbound one is, and an
    // action with no words after "|" is named by its id: the stand-in never touches KeybindManager, and is never seen.
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
