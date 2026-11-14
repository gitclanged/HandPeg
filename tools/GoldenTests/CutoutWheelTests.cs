using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HandPegApp;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// Pass 5.5.1 (the user's): cutout-studio-wheel. The Cutout Studio's vertical wheel told apart (MaskDrawWindow.IsTouchpadWheel and WheelDoes,
/// pure): a touchpad's two-finger scroll (Windows says so, or it is no whole number of notches, or a touchpad's scroll came just before it)
/// with no key pans up and down; a mouse's notched wheel, Ctrl + either and a touchpad's pinch zoom; Shift pans sideways. Then on a real
/// window: a touchpad's scroll pans by what the fingers moved, its zoom kept, through WPF's own wheel event too; a notch zooms once the
/// gesture is over. The four lines of instructions are gone: a '?' beside Fit and 100% opens a card of eight short rows on a click (kept
/// open) or on hover (pinned by a click), placed by UiScale.PlaceAgainst, on the inspector card's frozen surface and border; a press
/// elsewhere closes it. Every line says what it proved.
/// </summary>
public static partial class CutoutTests
{
    private static GraphResult WheelAndShortcuts()
    {
        var text = new StringBuilder("# The Cutout Studio's wheel and shortcuts (Pass 5.5.1): a touchpad's two-finger scroll pans, a mouse's wheel and a pinch zoom; the instructions are a card behind a '?'.\n");
        var problems = new List<string>();
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "NOT: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        try
        {
            WheelKinds(Expect);
            WheelAndCardOnWindow(Expect);
        }
        catch (Exception ex)
        {
            problems.Add($"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return new GraphResult("cutout-studio-wheel", text.ToString(), problems, 0);
    }

    private static void WheelKinds(Action<bool, string> expect)
    {
        expect(!MaskDrawWindow.IsTouchpadWheel(120, false, false) && !MaskDrawWindow.IsTouchpadWheel(-240, false, false),
            "a mouse's notches (120, -240), Windows saying nothing of a touchpad and none just before: a mouse's wheel");
        expect(MaskDrawWindow.IsTouchpadWheel(120, true, false), "a whole notch Windows says came from a touchpad (IMDT_TOUCHPAD): a touchpad's");
        expect(MaskDrawWindow.IsTouchpadWheel(37, false, false) && MaskDrawWindow.IsTouchpadWheel(-8, false, false),
            "no whole number of notches (37, -8), whatever Windows says: a touchpad's two-finger scroll");
        expect(MaskDrawWindow.IsTouchpadWheel(120, false, true) && MaskDrawWindow.TouchpadStreamMs == 250,
            "a whole notch within 250 ms of a touchpad's scroll: the same gesture, still a touchpad's");

        expect(MaskDrawWindow.WheelDoes(ModifierKeys.None, touchpad: true) == MaskDrawWindow.WheelAction.PanUpDown, "a touchpad's scroll with no key pans up and down");
        expect(MaskDrawWindow.WheelDoes(ModifierKeys.Control, touchpad: true) == MaskDrawWindow.WheelAction.Zoom, "a touchpad's pinch (Ctrl + the wheel) zooms");
        expect(MaskDrawWindow.WheelDoes(ModifierKeys.None, touchpad: false) == MaskDrawWindow.WheelAction.Zoom
               && MaskDrawWindow.WheelDoes(ModifierKeys.Control, touchpad: false) == MaskDrawWindow.WheelAction.Zoom,
            "a mouse's wheel zooms, with Ctrl or without");
        expect(MaskDrawWindow.WheelDoes(ModifierKeys.Shift, touchpad: false) == MaskDrawWindow.WheelAction.PanSideways
               && MaskDrawWindow.WheelDoes(ModifierKeys.Shift, touchpad: true) == MaskDrawWindow.WheelAction.PanSideways,
            "Shift + the wheel pans sideways, a mouse's or a touchpad's");
    }

    private static void WheelAndCardOnWindow(Action<bool, string> expect)
    {
        const int W = 1920, H = 1080;
        const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
        var settings = AppSettings.Current;
        (settings.AutoUpdateDependencies, settings.SendCrashReports, settings.EnableExtraAnimations) = (false, false, false);
        var vm = Opened("D:/media/game.mkv", W, H, "h264");

        var window = new MaskDrawWindow(vm, null) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false };
        try
        {
            window.Show();
            Pump();
            var viewport = Find<Canvas>(window, c => c.ClipToBounds) ?? throw new InvalidOperationException("no viewport");
            var middle = new Point(viewport.ActualWidth / 2, viewport.ActualHeight / 2);
            static bool Near(double a, double b) => Math.Abs(a - b) < 0.01;

            // Zoomed in, the view in the picture's middle: room to pan every way.
            window.Wheel(8 * 120, middle, ModifierKeys.None);
            window.CenterOn(new Point(W / 2.0, H / 2.0));
            var (zoom, panX, panY) = window.View;

            window.Wheel(-60, middle, ModifierKeys.None, touchpad: true);
            expect(window.View.Zoom == zoom && Near(window.View.PanX, panX) && Near(window.View.PanY, panY - 48),
                "two fingers down half a notch (-60): the picture moves up 48 pixels, as a page scrolls, its zoom kept");
            window.Wheel(60, middle, ModifierKeys.None, touchpad: true);
            expect(window.View == (zoom, panX, panY), "and back up: where it was");
            window.Wheel(30, middle, ModifierKeys.Control, touchpad: true);
            expect(window.View.Zoom > zoom, "a touchpad's pinch spread apart (Ctrl + 30) zooms in");
            window.CenterOn(new Point(W / 2.0, H / 2.0));
            (zoom, panX, panY) = window.View;

            // Through WPF's wheel event (what the window's own message does, the same steps): no whole notch pans; a notch in the same
            // gesture pans too; a notch once the gesture is over zooms.
            void Raise(int delta) =>
                viewport.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
            Raise(-40);
            expect(window.View.Zoom == zoom && Near(window.View.PanY, panY - 32), "a wheel event of -40 (no whole notch: a touchpad's) pans 32 pixels, its zoom kept");
            Raise(120);
            expect(window.View.Zoom == zoom && Near(window.View.PanY, panY + 64), "a whole notch straight after it (the same gesture) pans 96 pixels back the other way");
            typeof(MaskDrawWindow).GetField("_touchpadWheelAt", Hidden)!.SetValue(window, long.MinValue / 2);
            Raise(120);
            expect(window.View.Zoom > zoom, "a notch once the touchpad's gesture is long over: a mouse's wheel, which zooms");

            // The instructions: no paragraph of words over the view any more.
            expect(Find<TextBlock>(window, t => t.Text.Length > 120 || t.Text.Contains("corner by corner", StringComparison.Ordinal)) is null,
                "no help paragraph is left in the window (no TextBlock of more than 120 characters, none of the old instructions)");

            // The '?': beside Fit and 100%, a short tooltip.
            var button = Find<ToggleButton>(window, b => AutomationProperties.GetName(b) == "Shortcuts") ?? throw new InvalidOperationException("no '?' button");
            var row = VisualTreeHelper.GetParent(button) as Panel;
            var index = row?.Children.IndexOf(button) ?? -1;
            expect(row is not null && index >= 2 && row.Children[index - 1] is Button { Content: "100%" } && row.Children[index - 2] is Button { Content: "Fit" },
                "the '?' sits right after Fit and 100%");
            var tip = button.ToolTip as string ?? "";
            expect(tip.Length > 0 && tip.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 12, $"its tooltip is 12 words at most (\"{tip}\")");
            expect(MaskDrawWindow.Shortcuts.Count is > 0 and <= 8 && MaskDrawWindow.Shortcuts.All(s => s.What.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4 && s.Keys.Length > 0),
                $"the card has {MaskDrawWindow.Shortcuts.Count} rows (8 at most), each named in 4 words at most, with its keys");

            // A click opens the card, placed against the button, on the inspector card's surface.
            var card = (Popup)typeof(MaskDrawWindow).GetField("_shortcutsCard", Hidden)!.GetValue(window)!;
            expect(!window.ShortcutsOpen, "the card is closed as the window opens");
            button.IsChecked = true;
            Pump();
            expect(window.ShortcutsOpen && card.Placement == PlacementMode.Custom && ReferenceEquals(card.PlacementTarget, window.Content),
                "a click opens it, placed by UiScale.PlaceAgainst (a custom placement from the window's content, never Relative or Bottom)");
            var border = card.Child as Border;
            expect(border is { Background: SolidColorBrush { IsFrozen: true } fill, BorderBrush: SolidColorBrush { IsFrozen: true } edge }
                   && fill.Color == Color.FromRgb(0x1E, 0x1E, 0x22) && edge.Color == Color.FromRgb(0x2A, 0x2A, 0x30) && border.BorderThickness == new Thickness(1),
                "on the inspector card's surface (#1E1E22) with its 1 px border (#2A2A30), both brushes frozen");
            var rows = border?.Child is Grid grid ? grid.Children.OfType<TextBlock>().Count() : 0;
            expect(rows == MaskDrawWindow.Shortcuts.Count * 2, $"a name and its keys a row ({rows} words in all)");
            viewport.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
            expect(!window.ShortcutsOpen && button.IsChecked == false, "a press elsewhere in the window closes it");

            // Hovering opens it too; a click on the '?' then keeps it open rather than closing it.
            var pinned = typeof(MaskDrawWindow).GetField("_shortcutsPinned", Hidden)!;
            button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseEnterEvent });
            Pump();
            expect(window.ShortcutsOpen && (bool)pinned.GetValue(window)! == false, "the pointer on the '?' opens it, not pinned (it closes as the pointer leaves)");
            var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
            button.RaiseEvent(press);
            expect(window.ShortcutsOpen && (bool)pinned.GetValue(window)! && press.Handled, "a click on the '?' while hovering keeps it open (pinned)");
            button.IsChecked = false;
            Pump();
            expect(!window.ShortcutsOpen && (bool)pinned.GetValue(window)! == false, "clicked again it closes");
        }
        finally
        {
            window.Close();
            Pump();
        }
    }
}
