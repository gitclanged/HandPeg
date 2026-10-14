# Small UI driver for testing HandPeg by hand-like input: screenshots, real mouse moves and clicks, and
# finding elements through UI Automation. Dot-source it: . .\UiDriver.ps1
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Native {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hwnd, int x, int y, int w, int h, bool repaint);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@ -ErrorAction SilentlyContinue
[void][Native]::SetProcessDpiAwarenessContext([IntPtr](-4))

$Scratch = Join-Path $env:TEMP 'handpeg-ui'; New-Item -ItemType Directory -Force $Scratch | Out-Null

function Shot([string]$name, [int]$width = 1600) {
    $bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $bitmap.Size)
    $scale = [Math]::Min(1.0, $width / $bounds.Width)
    $small = New-Object System.Drawing.Bitmap ([int]($bounds.Width * $scale)), ([int]($bounds.Height * $scale))
    $g2 = [System.Drawing.Graphics]::FromImage($small)
    $g2.InterpolationMode = 'HighQualityBicubic'
    $g2.DrawImage($bitmap, 0, 0, $small.Width, $small.Height)
    $path = Join-Path $Scratch "$name.png"
    $small.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $g2.Dispose(); $bitmap.Dispose(); $small.Dispose()
    "screenshot: $path (virtual screen $($bounds.Left),$($bounds.Top) $($bounds.Width)x$($bounds.Height), scale $scale)"
}

function MoveTo([int]$x, [int]$y) { [void][Native]::SetCursorPos($x, $y) }
function Down { [Native]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero) }
function Up { [Native]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero) }
function Click([int]$x, [int]$y) { MoveTo $x $y; Start-Sleep -Milliseconds 120; Down; Start-Sleep -Milliseconds 60; Up }

# A slow, stepped move with the button held: what a drag looks like to Windows.
function Glide([int]$x0, [int]$y0, [int]$x1, [int]$y1, [int]$steps = 30, [int]$pause = 25) {
    for ($i = 1; $i -le $steps; $i++) {
        MoveTo ([int]($x0 + ($x1 - $x0) * $i / $steps)) ([int]($y0 + ($y1 - $y0) * $i / $steps))
        Start-Sleep -Milliseconds $pause
    }
}

function Root { [System.Windows.Automation.AutomationElement]::RootElement }

function FindWindow([string]$titleLike) {
    $cond = [System.Windows.Automation.Condition]::TrueCondition
    (Root).FindAll([System.Windows.Automation.TreeScope]::Children, $cond) | Where-Object { $_.Current.Name -like $titleLike } | Select-Object -First 1
}

function FindByName($parent, [string]$nameLike) {
    $cond = [System.Windows.Automation.Condition]::TrueCondition
    $parent.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond) | Where-Object { $_.Current.Name -like $nameLike } | Select-Object -First 1
}

function Center($element) {
    $r = $element.Current.BoundingRectangle
    [pscustomobject]@{ X = [int]($r.X + $r.Width / 2); Y = [int]($r.Y + $r.Height / 2); Rect = $r }
}
