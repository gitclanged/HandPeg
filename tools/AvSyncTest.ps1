param(
    [Parameter(Mandatory)][string]$Project,
    [string]$Points = '100,300,600,1500',
    [ValidateSet('off', 'on', 'both')][string]$Proxies = 'both',
    [string]$MainProxy,
    [string]$Libmpv,
    [double]$Tolerance = 15,
    [switch]$Session,
    [switch]$NoBuild,
    [string]$Configuration = 'Debug'
)
# HandPeg's A/V sync harness (tools\AvSync): opens a project in the app's own view model (no window), builds the live
# graph the app would send the player with a seek to each moment, has libmpv play it headless and write what it plays
# (mpv's encoding mode, on the sequence's clock), then finds the output's sound in the file it came from (cross-correlation,
# to the sample) and matches its frames against the file's. One row per moment: the picture's and the sound's lateness and
# the A/V difference, PASS or FAIL against the tolerance (ms).
#
#   pwsh -NoProfile -File tools\AvSyncTest.ps1 -Project <file.hproj>                       every moment, proxies off and on
#   pwsh -NoProfile -File tools\AvSyncTest.ps1 -Project <file.hproj> -Points 20,40 -MainProxy <a proxy of the main video>
#   pwsh -NoProfile -File tools\AvSyncTest.ps1 -Project <file.hproj> -Session              the steps of a session, in real time
#
# -Session plays the app's in-session steps instead (a seek while paused, the sound settling 300 ms later, Play; a seek
# while playing; a graph rebuilt while playing) and reads mpv's clocks against plain playback of the main video.
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
if ($Libmpv) { $common += @('--libmpv', $Libmpv) }

if (-not $Session) {
    & $exe test @common --proxies $(if ($Proxies -eq 'both') { 'off,on' } else { $Proxies })
    exit $LASTEXITCODE
}

# A session plays with one setting of the proxies at a time.
$failed = 0
foreach ($setting in $(if ($Proxies -eq 'both') { @('off', 'on') } else { @($Proxies) })) {
    & $exe session @common --proxies $setting
    if ($LASTEXITCODE -ne 0) { $failed = $LASTEXITCODE }
}
exit $failed
