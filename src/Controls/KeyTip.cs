using System.Text;
using System.Windows;
using HandPegApp.Services;

namespace HandPegApp;

/// <summary>
/// A tooltip that names the keys of a KeybindManager action as they are bound now (4.4-BugHunt), instead of keys written
/// into the XAML that went stale when the keys changed. <c>KeyTip.Text</c> holds the words, with "{keys}" where " (F)" goes
/// (nothing while <c>KeyTip.Action</c> has no key) and "{key:action.id}" for another action's key inside a sentence (the
/// action's name while it has none; "{key:action.id|words}" says what to write instead). The tooltip is filled in each time
/// it opens, so keys changed in Settings show at once; one set before the key map is made holds the words without keys
/// until it first opens (the map is made at the first key or tooltip, never before the first frame). Text never starts
/// with "{" (XAML would read it as a markup extension). OverflowStrip's "..." menu reads it too (<see cref="Render"/>).
/// </summary>
public static class KeyTip
{
    public static readonly DependencyProperty ActionProperty = DependencyProperty.RegisterAttached(
        "Action", typeof(string), typeof(KeyTip), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(KeyTip), new PropertyMetadata(null, OnChanged));

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
        element.ToolTip = Fill(text, GetAction(element), keys: KeybindManager.IsMapMade);
    }

    // WPF reads the tooltip after raising ToolTipOpening, so what is set here is what shows.
    private static void Opening(object sender, System.Windows.Controls.ToolTipEventArgs e)
    {
        if (sender is FrameworkElement element && Render(element) is { } text)
            element.ToolTip = text;
    }

    /// <summary>The element's KeyTip words with the keys as bound now; null when it has none.</summary>
    public static string? Render(DependencyObject element) =>
        GetText(element) is { } text ? Fill(text, GetAction(element), keys: true) : null;

    // "{keys}" becomes " (F)" (or nothing), "{key:id}" the key's name (or the action's, or the words after "|"); anything else
    // in braces stays as it is. Without keys (before the map is made), every key is left out the same way an unbound one is.
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
                    : KeybindManager.Action(id)?.Label.TrimEnd('.') ?? id);
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
