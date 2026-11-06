<#
.SYNOPSIS
    Launch-time benchmark: HandPeg as a user starts it (through Explorer), compared build against build.

.DESCRIPTION
    Each round launches every build once, in turn, through explorer.exe (so the launch, the start-up feedback and the
    foreground rules are a user's), and records for each launch:
      - ms to the first frame: the "window drawn N ms after the process started" line HandPeg logs on every start;
      - ms of busy pointer: how long Windows showed the "working in background" or wait pointer, polled every 20 ms
        while the build runs (a child process started without STARTF_FORCEOFFFEEDBACK shows up here: Pass 4.1C).
    Each build is closed through UI Automation after -Seconds and the settings file is put back after every launch
    (a backup is taken first and restored at the end, hash checked). Builds alternate, so a busy machine affects all
    of them alike; compare only numbers from one run. The first launch of a new build records its multi-core JIT
    profile: -Warm launches each build once, unrecorded, before the rounds.

    Release (ReadyToRun) builds are what to compare: dotnet publish -c Release -r win-x64 --self-contained false
    -p:DebugType=none -o <folder outside the repo>. A Debug build compiles everything at run time and is slower.

    -Log and -Settings name another copy's log and settings file (Pass 5.2D): a self-contained copy
    (a portable\ folder beside HandPeg.exe) writes <app>\portable\data\logs\HandPeg.log and reads
    <app>\portable\data\Settings\appsettings.json. Left out, they are the installed places.

.EXAMPLE
    powershell -NoProfile -File tools\StartupBench.ps1 -Builds "C:\rel\4.0.0\HandPeg.exe;C:\rel\now\HandPeg.exe" -Rounds 7 -Warm

.EXAMPLE
    powershell -NoProfile -File StartupBench.ps1 -Builds C:\run\HandPeg\HandPeg.exe -Log C:\run\HandPeg\portable\data\logs\HandPeg.log -Settings C:\run\HandPeg\portable\data\Settings\appsettings.json
#>
param(
    [Parameter(Mandatory)] [string[]]$Builds,
    [int]$Rounds = 5,
    [double]$Seconds = 8,
    [switch]$Warm,
    [string]$Log = '',
    [string]$Settings = ''
)

$ErrorActionPreference = 'Stop'

# From another shell (-File) a list arrives as one string: "a.exe,b.exe" or "a.exe;b.exe".
$Builds = @($Builds | ForEach-Object { $_ -split '[,;]' } | ForEach-Object { $_.Trim().Trim('"') } | Where-Object { $_ })
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class BenchPointer {
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct CursorInfo { public int Size; public int Flags; public IntPtr Cursor; public Point Where; }
    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr instance, IntPtr id);
    static readonly IntPtr AppStarting = LoadCursor(IntPtr.Zero, (IntPtr)32650), Wait = LoadCursor(IntPtr.Zero, (IntPtr)32514);
    // Milliseconds of busy pointer seen in the next `seconds`, polled every 20 ms.
    public static double Watch(double seconds) {
        var clock = System.Diagnostics.Stopwatch.StartNew(); double busy = 0, last = 0;
        while (clock.Elapsed.TotalSeconds < seconds) {
            var info = new CursorInfo(); info.Size = Marshal.SizeOf(info); GetCursorInfo(ref info);
            var now = clock.Elapsed.TotalMilliseconds;
            if (info.Cursor == AppStarting || info.Cursor == Wait) busy += now - last;
            last = now; System.Threading.Thread.Sleep(20);
        }
        return busy;
    }
}
'@

# The installed copy's log and settings, unless another copy's are named ($log is $Log: PowerShell's names ignore case).
if (-not $Log) { $Log = Join-Path $env:LOCALAPPDATA 'HandPeg-Data\logs\HandPeg.log' }
if (-not $Settings) { $Settings = Join-Path $env:APPDATA 'HandPeg\appsettings.json' }
$backup = Join-Path ([IO.Path]::GetTempPath()) ("HandPeg-bench-settings-{0}.json" -f [Guid]::NewGuid().ToString('N'))
function Get-Sha([string]$path) { [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([IO.File]::ReadAllBytes($path))) }

foreach ($build in $Builds) { if (-not (Test-Path $build)) { throw "No such build: $build" } }
if (Get-Process HandPeg -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 }) { throw 'HandPeg is running: close it first.' }
if (Test-Path $settings) { Copy-Item $settings $backup -Force }

function Read-NewLog([long]$from) {
    if (-not (Test-Path $log)) { return '' }
    $stream = [IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
    try { [void]$stream.Seek([Math]::Min($from, $stream.Length), 'Begin'); (New-Object IO.StreamReader $stream).ReadToEnd() } finally { $stream.Dispose() }
}

function Invoke-Launch([string]$build) {
    $before = if (Test-Path $log) { (Get-Item $log).Length } else { 0 }
    Start-Process explorer.exe "`"$build`""
    $busy = [BenchPointer]::Watch($Seconds)
    foreach ($process in Get-Process HandPeg -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $build -and $_.MainWindowHandle -ne 0 }) {
        # UI Automation's Close first (the close button, as a user presses it); then the window's own WM_CLOSE; only then the
        # process ended (which its crash sentinel reports as a crash: 5.2E, a test PC's five).
        $why = ''
        try { [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle).GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() }
        catch { $why = "UI Automation's Close failed: $($_.Exception.Message)" }
        if (-not $process.WaitForExit(8000)) {
            [void]$process.CloseMainWindow()
            if (-not $process.WaitForExit(8000)) {
                # What it had open (a dialog on top keeps the window from closing): its top-level windows, by title and class.
                $open = ''
                try {
                    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
                    $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $condition)
                    # An owned dialog is listed under its owner, not under the desktop.
                    $isWindow = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
                    $open = (@($windows | ForEach-Object {
                        $owned = @($_.FindAll([System.Windows.Automation.TreeScope]::Children, $isWindow) | ForEach-Object { '"' + $_.Current.Name + '"' }) -join ', '
                        '"' + $_.Current.Name + '" (' + $_.Current.ClassName + ')' + $(if ($owned) { ' over which: ' + $owned } else { '' })
                    }) -join '; ')
                }
                catch { $open = "(its windows could not be listed: $($_.Exception.Message))" }
                Write-Warning "HandPeg did not close (neither UI Automation's Close nor WM_CLOSE); ended it. $why Open: $open"
                $process.Kill()
            }
            else { Write-Warning "HandPeg closed only on WM_CLOSE. $why" }
        }
    }
    Start-Sleep -Milliseconds 1500
    if (Test-Path $backup) { Copy-Item $backup $settings -Force }
    $text = Read-NewLog $before
    $frame = if ($text -match 'window drawn (\d+) ms after the process started') { [double]$Matches[1] } else { $null }
    [pscustomobject]@{ Frame = $frame; Busy = $busy }
}

$results = [ordered]@{}
foreach ($build in $Builds) { $results[$build] = New-Object System.Collections.Generic.List[object] }
try {
    if ($Warm) { foreach ($build in $Builds) { [void](Invoke-Launch $build) } }
    for ($round = 1; $round -le $Rounds; $round++) {
        foreach ($build in $Builds) {
            $r = Invoke-Launch $build
            $results[$build].Add($r)
            '{0,2}  {1,6} ms to the first frame  {2,6:F0} ms busy pointer  {3}' -f $round, $(if ($null -ne $r.Frame) { $r.Frame } else { '?' }), $r.Busy, $build
        }
    }
}
finally {
    if (Test-Path $backup) {
        Copy-Item $backup $settings -Force
        $same = (Get-Sha $settings) -eq (Get-Sha $backup)
        Remove-Item $backup -Force
        "settings restored: $same"
    }
}

''
foreach ($build in $Builds) {
    $frames = @($results[$build] | Where-Object { $null -ne $_.Frame } | ForEach-Object { $_.Frame } | Sort-Object)
    $busy = @($results[$build] | ForEach-Object { $_.Busy } | Sort-Object)
    if ($frames.Count -eq 0) { "no first-frame lines for $build"; continue }
    '{0,6} ms median to the first frame (best {1}, worst {2}), {3,5:F0} ms median busy pointer, n={4}  {5}' -f `
        $frames[[int][Math]::Floor($frames.Count / 2)], $frames[0], $frames[-1], $busy[[int][Math]::Floor($busy.Count / 2)], $frames.Count, $build
}
