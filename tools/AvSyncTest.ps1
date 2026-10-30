param(
    [Parameter(Mandatory)][string]$Project,
    [string]$Points = '100,300,600,1500',
    [ValidateSet('off', 'on', 'both')][string]$Proxies = 'both',
    [string]$MainProxy,
    [double]$Tolerance = 15,
    [double]$Seconds = 3,
    [switch]$Session,
    [switch]$Audible,
    [switch]$NoBuild,
    [string]$Configuration = 'Debug'
)
# HandPeg's A/V sync harness on the GPU engine (tools\AvSync): opens a project in the app's own view model (no window) and
# takes the state the player is given. At each moment it draws a few seconds of the picture with a real compositor worker
# (GpuScenePlan, every frame exact, read back off screen) and renders the same seconds of sound with the real mixer
# (AudioProgram, AudioMixer.Render), writes both into one file on the sequence's clock, then finds that sound in the file it
# came from (cross-correlation, to the sample) and matches its frames against the file's. One row per moment: the
# picture's lateness (from the scene plan, checked by the frames), the sound's, and the A/V difference, PASS or FAIL against
# the tolerance (ms); nominal is within 0.5 ms.
#
#   pwsh -NoProfile -File tools\AvSyncTest.ps1 -Project <file.hproj>                       every moment, proxies off and on
#   pwsh -NoProfile -File tools\AvSyncTest.ps1 -Project <file.hproj> -Points 20,40 -MainProxy <a proxy of the main video>
#   pwsh -NoProfile -File tools\AvSyncTest.ps1 -Project <file.hproj> -Session              the steps of a session, in real time
#
# -Session plays the app's in-session steps instead through a real GpuPreviewEngine and its WASAPI clock (a seek while
# paused then Play; a seek while playing; twice the speed and back; Loop's wrap at the end) on a window off every screen
# that never takes the focus, muted unless -Audible, and compares each frame put on the screen with the sound heard then.
# The project is only read; appsettings.json is put back byte for byte when the harness ends. Close HandPeg before building.
$ErrorActionPreference = 'Stop'
$harness = Join-Path $PSScriptRoot 'AvSync\AvSync.csproj'

if (-not $NoBuild) {
    $built = & dotnet build $harness -c $Configuration -nologo -v quiet -clp:ErrorsOnly 2>&1
    if ($LASTEXITCODE -ne 0) {
        $built | Write-Output
        Write-Output 'The A/V sync harness could not be built.'
        exit 2
    }
}

$exe = Join-Path $PSScriptRoot "AvSync\bin\$Configuration\net8.0-windows\AvSync.exe"
if (-not (Test-Path $exe)) {
    Write-Output "Not built yet: $exe (run without -NoBuild)."
    exit 2
}

$common = @('--project', (Resolve-Path -LiteralPath $Project).Path, '--points', $Points, '--tolerance', $Tolerance)
if ($MainProxy) { $common += @('--main-proxy', (Resolve-Path -LiteralPath $MainProxy).Path) }

if (-not $Session) {
    & $exe test @common --seconds $Seconds --proxies $(if ($Proxies -eq 'both') { 'off,on' } else { $Proxies })
    exit $LASTEXITCODE
}

# A session plays with one setting of the proxies at a time.
$failed = 0
foreach ($setting in $(if ($Proxies -eq 'both') { @('off', 'on') } else { @($Proxies) })) {
    $arguments = @('session') + $common + @('--measure', $Seconds, '--proxies', $setting)
    if ($Audible) { $arguments += '--audible' }
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { $failed = $LASTEXITCODE }
}
exit $failed
