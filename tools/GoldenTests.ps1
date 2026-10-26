param([switch]$Update, [string]$Filter, [switch]$NoBuild, [string]$Configuration = 'Debug')
# HandPeg's golden-file tests (tools\GoldenTests): what FilterGraphBuilder writes for each fixture, the .hproj format's
# reading and writing, and how FfmpegRunner hands a command to FFmpeg, compared with the files in tools\GoldenTests\golden,
# plus the checks made by code (the A/V invariant, the project round trip, the quoting round trip).
#
#   pwsh -NoProfile -File tools\GoldenTests.ps1            build once, then run every test
#   pwsh -NoProfile -File tools\GoldenTests.ps1 -Update    write the golden files anew from what the code makes now
#   pwsh -NoProfile -File tools\GoldenTests.ps1 -Filter live -NoBuild
#
# A golden file that differs is shown as a line diff (- golden, + now). After a change that is meant to change the graphs,
# run with -Update and read the diff of the golden files in git before committing them: that diff is the review.
# Close HandPeg first: the build writes HandPeg.dll, which a running copy holds.
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'GoldenTests\GoldenTests.csproj'

if (-not $NoBuild) {
    $built = & dotnet build $project -c $Configuration -nologo -v quiet -clp:ErrorsOnly 2>&1
    if ($LASTEXITCODE -ne 0) {
        $built | Write-Output
        Write-Output 'The golden tests could not be built.'
        exit 2
    }
}

$exe = Join-Path $PSScriptRoot "GoldenTests\bin\$Configuration\net8.0-windows\HandPeg.GoldenTests.exe"
if (-not (Test-Path $exe)) {
    Write-Output "Not built yet: $exe (run without -NoBuild)."
    exit 2
}

$arguments = @('--root', (Join-Path $PSScriptRoot 'GoldenTests'))
if ($Update) { $arguments += '--update' }
if ($Filter) { $arguments += @('--filter', $Filter) }

# The program says how long the tests themselves took (the build is not counted).
& $exe @arguments
exit $LASTEXITCODE
