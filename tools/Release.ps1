# Builds a HandPeg release from clean obj and bin folders: the framework-dependent win-x64 and win-arm64 publishes, the NativeAOT
# bootstrapper of each processor (tools\PortableBootstrap) as HandPeg.exe in both kinds of release file - the two portable zips (with an
# empty portable folder) and the Velopack packages, one channel per processor (win: HandPeg-win-Setup.exe, releases.win.json;
# win-arm64: HandPeg-win-arm64-Setup.exe, releases.win-arm64.json) - and checks every file before it is packed.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\Release.ps1 [-OutDir <folder>] [-Scorecard <scorecard.json>] [-SkipTests]
#
# Gates, in order (each refuses the release with a message):
#   1. One version: HandPegApp.csproj's Version (X.Y.Z) with AssemblyVersion and FileVersion X.Y.Z.0, the same in HandPegBootstrap.csproj.
#   2. A clean tree: no change to a tracked file and no untracked file under src\ (the SDK compiles every .cs there); the tag vX.Y.Z,
#      when it already exists, must point at HEAD.
#   3. Tests: a passing full AutoTest scorecard for HEAD (-Scorecard), else tools\GoldenTests.ps1 run here. -SkipTests skips this gate
#      and says so in the release folder's release.txt.
# No icon (HandPeg ships none until a designed one is made) and no --framework (Setup must never install .NET machine-wide by itself).
# Needs the .NET 10 SDK, vpk (dotnet tool), and Visual Studio's C++ build tools (ARM64 and x64 compilers) for the NativeAOT linker of the
# bootstrapper and of Explorer's thumbnail handler (src\ShellThumbnails; HandPegApp.csproj's Release publish puts it in shell\ and only
# warns when the tools are missing, so it is checked here). -SkipVeloAppCheck passes vpk's --skipVeloAppCheck, for when vpk cannot see
# VelopackApp.Run in a package whose HandPeg.exe is native (the hooks are in HandPeg.dll, which the bootstrapper runs).
param([string]$OutDir, [string]$Scorecard, [switch]$SkipTests, [switch]$SkipVeloAppCheck)
$ErrorActionPreference = 'Stop'

# The repository: the first folder up from this script holding HandPegApp.csproj.
$repo = $PSScriptRoot
while ($repo -and -not (Test-Path -LiteralPath (Join-Path $repo 'HandPegApp.csproj'))) { $repo = Split-Path -Parent $repo }
if (-not $repo) { throw "HandPegApp.csproj was not found above $PSScriptRoot" }
$boot = Join-Path $repo 'tools\PortableBootstrap'
$vpk = Join-Path $env:USERPROFILE '.dotnet\tools\vpk.exe'

function Run([string]$exe, [string[]]$arguments) {
    Write-Host ("> " + $exe + ' ' + ($arguments -join ' '))
    $previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { & $exe @arguments 2>&1 | ForEach-Object { "$_" } | Where-Object { $_ -match 'warning|error|->|Successfully|Completed|Creating|Done|WARN|WRN|ERR|VelopackApp' } | ForEach-Object { Write-Host $_ } }
    finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) { throw "$exe failed with exit code $LASTEXITCODE" }
}

function Git([string[]]$arguments) {
    $previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    # git.exe, not git: PowerShell resolves names to functions before programs (case-insensitively), so 'git' is this function.
    try { $output = & git.exe -C $repo @arguments 2>$null } finally { $ErrorActionPreference = $previous }
    return @($output)
}

# ----- Gate 1: one version -----
function VersionTrio([string]$project) {
    $xml = [xml](Get-Content -LiteralPath $project -Raw)
    $group = @($xml.Project.PropertyGroup | Where-Object { $_.Version })[0]
    return @([string]$group.Version, [string]$group.AssemblyVersion, [string]$group.FileVersion)
}
$app = VersionTrio (Join-Path $repo 'HandPegApp.csproj')
$bootVersion = VersionTrio (Join-Path $boot 'HandPegBootstrap.csproj')
$Version = $app[0]
if ($Version -notmatch '^\d+\.\d+\.\d+$' -or $app[1] -ne "$Version.0" -or $app[2] -ne "$Version.0") {
    throw "HandPegApp.csproj: Version/AssemblyVersion/FileVersion must be X.Y.Z/X.Y.Z.0/X.Y.Z.0, not $($app -join '/')"
}
if (($bootVersion -join '/') -ne ($app -join '/')) {
    throw "HandPegBootstrap.csproj's version ($($bootVersion -join '/')) differs from HandPegApp.csproj's ($($app -join '/'))"
}

# ----- Gate 2: a clean tree -----
$head = (Git @('rev-parse', 'HEAD'))[0]
if (-not $head) { throw "$repo is not a git repository" }
$changed = Git @('status', '--porcelain', '--untracked-files=no')
if ($changed.Count -gt 0) { throw "Tracked files are changed (commit first):`n$($changed -join "`n")" }
$untracked = Git @('ls-files', '--others', '--exclude-standard', '--', 'src')
if ($untracked.Count -gt 0) { throw "Untracked files under src\ would be compiled into the release:`n$($untracked -join "`n")" }
$tagged = (Git @('rev-parse', '-q', '--verify', "refs/tags/v$Version^{commit}"))
if ($tagged.Count -gt 0 -and $tagged[0] -ne $head) { throw "Tag v$Version already exists at $($tagged[0]), not at HEAD $head" }

# ----- Gate 3: tests -----
$testNote = ''
if ($SkipTests) {
    $testNote = 'tests skipped (-SkipTests)'
    Write-Host "WARNING: $testNote"
}
elseif ($Scorecard) {
    $card = Get-Content -LiteralPath $Scorecard -Raw | ConvertFrom-Json
    if ($card.result -ne 'PASS' -or $card.mode -ne 'Full' -or $card.filter -or $card.commit -ne $head -or $card.dirty) {
        throw "The scorecard $Scorecard is not a passing, unfiltered Full run of a clean HEAD $head (result $($card.result), mode $($card.mode), filter '$($card.filter)', commit $($card.commit), dirty $($card.dirty))"
    }
    $testNote = "AutoTest Full PASS ($Scorecard)"
}
else {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'tools\GoldenTests.ps1')
    if ($LASTEXITCODE -ne 0) { throw "The golden tests failed (exit $LASTEXITCODE)" }
    $testNote = 'tools\GoldenTests.ps1 passed'
}

# The output folder: local\releases\HandPeg-<version>-release in the workspace (the repository itself, or its parent when the
# repository is a child folder of the workspace).
if (-not $OutDir) {
    $workspace = $repo
    if (-not (Test-Path -LiteralPath (Join-Path $repo 'local')) -and (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $repo) 'local'))) { $workspace = Split-Path -Parent $repo }
    $OutDir = Join-Path $workspace "local\releases\HandPeg-$Version-release"
}
$out = $OutDir

# NativeAOT finds the C++ linker through vswhere.exe, which the Visual Studio Installer does not put on PATH.
$env:PATH += ';' + (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer')

# ----- PE facts -----

# Where an address in a PE file's loaded image is in the file (through the section that holds it); -1 when no section does.
function RvaToOffset([byte[]]$bytes, [int]$sections, [int]$count, [uint32]$rva) {
    for ($i = 0; $i -lt $count; $i++) {
        $s = $sections + $i * 40
        $start = [BitConverter]::ToUInt32($bytes, $s + 12)
        $size = [Math]::Max([BitConverter]::ToUInt32($bytes, $s + 8), [BitConverter]::ToUInt32($bytes, $s + 16))
        if ($rva -ge $start -and $rva -lt $start + $size) { return [int]($rva - $start + [BitConverter]::ToUInt32($bytes, $s + 20)) }
    }
    return -1
}

# A PE file's machine (0x8664 x64, 0xAA64 ARM64), subsystem (2: a window program), whether it has a .NET header and is a DLL, the names
# it exports, whether it is marked CET-compatible (the debug directory's IMAGE_DEBUG_TYPE_EX_DLLCHARACTERISTICS entry, type 20, bit 0x1;
# ARM64 has none) and whether it is linked with Control Flow Guard (IMAGE_DLLCHARACTERISTICS_GUARD_CF, 0x4000).
function PeHeader([string]$path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    $pe = [BitConverter]::ToInt32($bytes, 0x3C)
    $count = [BitConverter]::ToUInt16($bytes, $pe + 6)
    $optional = $pe + 24
    $sections = $optional + [BitConverter]::ToUInt16($bytes, $pe + 20)
    $magic = [BitConverter]::ToUInt16($bytes, $optional)
    $directories = $optional + $(if ($magic -eq 0x20B) { 112 } else { 96 })

    $exports = @()
    $exportRva = [BitConverter]::ToUInt32($bytes, $directories)
    if ($exportRva -ne 0) {
        $table = RvaToOffset $bytes $sections $count $exportRva
        $names = RvaToOffset $bytes $sections $count ([BitConverter]::ToUInt32($bytes, $table + 32))
        for ($i = 0; $i -lt [BitConverter]::ToUInt32($bytes, $table + 24); $i++) {
            $at = RvaToOffset $bytes $sections $count ([BitConverter]::ToUInt32($bytes, $names + $i * 4))
            $end = [Array]::IndexOf($bytes, [byte]0, $at)
            $exports += [Text.Encoding]::ASCII.GetString($bytes, $at, $end - $at)
        }
    }

    $cet = $false
    $debugRva = [BitConverter]::ToUInt32($bytes, $directories + 6 * 8)
    if ($debugRva -ne 0) {
        $debug = RvaToOffset $bytes $sections $count $debugRva
        $entries = [Math]::Floor([BitConverter]::ToUInt32($bytes, $directories + 6 * 8 + 4) / 28)
        for ($i = 0; $i -lt $entries; $i++) {
            $entry = $debug + $i * 28
            if ([BitConverter]::ToUInt32($bytes, $entry + 12) -eq 20) {
                $cet = ([BitConverter]::ToUInt32($bytes, [int][BitConverter]::ToUInt32($bytes, $entry + 24)) -band 1) -ne 0
            }
        }
    }

    return @{ Machine = [BitConverter]::ToUInt16($bytes, $pe + 4); Subsystem = [BitConverter]::ToUInt16($bytes, $optional + 68)
              Clr = [BitConverter]::ToUInt32($bytes, $directories + 14 * 8) -ne 0; Dll = ([BitConverter]::ToUInt16($bytes, $pe + 22) -band 0x2000) -ne 0
              Exports = $exports; Cet = $cet; Cfg = ([BitConverter]::ToUInt16($bytes, $optional + 70) -band 0x4000) -ne 0 }
}

# ----- Build -----

foreach ($rid in 'win-x64', 'win-arm64') {
    foreach ($dir in "obj\Release\net10.0-windows\$rid", "bin\Release\net10.0-windows\$rid", "src\ShellThumbnails\obj\Release\net10.0-windows\$rid", "src\ShellThumbnails\bin\Release\net10.0-windows\$rid",
                     "tools\PortableBootstrap\obj\Release\net10.0\$rid", "tools\PortableBootstrap\bin\Release\net10.0\$rid") {
        $path = Join-Path $repo $dir
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
}

foreach ($dir in 'publish-x64', 'publish-arm64', 'pack-x64', 'pack-arm64', 'velopack', 'zip-x64', 'zip-arm64', 'check') {
    $path = Join-Path $out $dir
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
New-Item -ItemType Directory -Force -Path $out | Out-Null
@("HandPeg $Version", "commit $head", $testNote) | Out-File -FilePath (Join-Path $out 'release.txt') -Encoding ascii

Run 'dotnet' @('publish', (Join-Path $repo 'HandPegApp.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-p:DebugType=none', '-o', (Join-Path $out 'publish-x64'))
Run 'dotnet' @('publish', (Join-Path $repo 'HandPegApp.csproj'), '-c', 'Release', '-r', 'win-arm64', '--self-contained', 'false', '-p:DebugType=none', '-o', (Join-Path $out 'publish-arm64'))

# Explorer's thumbnail handler in each publish's shell\: HandPeg.ShellThumbnails.dll alone, a native DLL of the publish's processor
# exporting DllGetClassObject and DllCanUnloadNow, and CET-compatible on x64 (Explorer's surrogate may enforce shadow stacks).
foreach ($pair in @(@{ Arch = 'x64'; Machine = 0x8664 }, @{ Arch = 'arm64'; Machine = 0xAA64 })) {
    $shell = Join-Path $out ("publish-{0}\shell" -f $pair.Arch)
    $files = @(Get-ChildItem -LiteralPath $shell -File -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
    if ($files.Count -ne 1 -or $files[0] -ne 'HandPeg.ShellThumbnails.dll') {
        throw ("publish-{0}\shell must hold HandPeg.ShellThumbnails.dll alone (are the C++ build tools for {0} installed?), not: {1}" -f $pair.Arch, ($files -join ', '))
    }
    $dll = Join-Path $shell 'HandPeg.ShellThumbnails.dll'
    $header = PeHeader $dll
    if (-not $header.Dll -or $header.Clr -or $header.Machine -ne $pair.Machine -or $header.Exports -notcontains 'DllGetClassObject' -or $header.Exports -notcontains 'DllCanUnloadNow') {
        throw ('{0} is not the native thumbnail handler for {1} (machine 0x{2:X4}, DLL {3}, .NET header {4}, exports {5})' -f $dll, $pair.Arch, $header.Machine, $header.Dll, $header.Clr, ($header.Exports -join ' '))
    }
    if ($pair.Arch -eq 'x64' -and -not $header.Cet) { throw "$dll lost its CET-compatible mark" }
    Write-Host ('{0}: native, machine 0x{1:X4}, CET-compatible {2}, {3:N0} KB' -f $dll, $header.Machine, $header.Cet, ((Get-Item -LiteralPath $dll).Length / 1KB))
}

# The HandPeg.exe of each processor: a native window program of it, linked with Control Flow Guard, and CET-compatible on x64
# (docs/DECISIONS.md D-07), checked before it goes into a zip or a package.
$bootstraps = @{}
foreach ($pair in @(@{ Arch = 'x64'; Rid = 'win-x64'; Machine = 0x8664 }, @{ Arch = 'arm64'; Rid = 'win-arm64'; Machine = 0xAA64 })) {
    Run 'dotnet' @('publish', (Join-Path $boot 'HandPegBootstrap.csproj'), '-c', 'Release', '-r', $pair.Rid)
    $exe = Join-Path $boot ("bin\Release\net10.0\{0}\publish\HandPegBootstrap.exe" -f $pair.Rid)
    $header = PeHeader $exe
    if ($header.Machine -ne $pair.Machine -or $header.Subsystem -ne 2 -or $header.Clr) {
        throw ('{0} is not a native window program for {1} (machine 0x{2:X4}, subsystem {3}, .NET header {4})' -f $exe, $pair.Rid, $header.Machine, $header.Subsystem, $header.Clr)
    }
    if (-not $header.Cfg) { throw "$exe is not linked with Control Flow Guard" }
    if ($pair.Arch -eq 'x64' -and -not $header.Cet) { throw "$exe is not marked CET-compatible (an obj folder left from an earlier build?)" }
    Write-Host ('{0}: machine 0x{1:X4}, CET-compatible {2}, CFG {3}, {4:N0} KB' -f $exe, $header.Machine, $header.Cet, $header.Cfg, ((Get-Item -LiteralPath $exe).Length / 1KB))
    $bootstraps[$pair.Arch] = $exe
}

# ----- Portable zips -----
# The publish at the zip's root with the bootstrapper as its HandPeg.exe, and an empty portable folder. No script.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
foreach ($arch in 'x64', 'arm64') {
    $stage = Join-Path $out "zip-$arch"
    Copy-Item -LiteralPath (Join-Path $out "publish-$arch") -Destination $stage -Recurse
    Copy-Item -LiteralPath $bootstraps[$arch] -Destination (Join-Path $stage 'HandPeg.exe') -Force
    New-Item -ItemType Directory -Force -Path (Join-Path $stage 'portable') | Out-Null
    $scripts = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object { $_.Extension -in '.cmd', '.bat', '.ps1' })
    if ($scripts.Count -gt 0) { throw "A script would ship in the $arch zip: $($scripts.Name -join ', ')" }
    $tracing = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object { $_.Name -in 'preview-trace.flag', 'dev-verbose.flag' })
    if ($tracing.Count -gt 0) { throw "A tracing flag would ship in the $arch zip: $($tracing.Name -join ', ')" }
    $zip = Join-Path $out "HandPeg-$Version-win-$arch-Portable.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    Write-Host ('{0}: {1:N2} MB' -f $zip, ((Get-Item -LiteralPath $zip).Length / 1MB))
}

# ----- Installer packages -----
# The publish with the bootstrapper as its HandPeg.exe (HandPeg.dll, its runtimeconfig and deps kept: the bootstrapper runs them).
# Never a portable folder or portable.txt: either makes an installed copy self-contained (AppPaths.FindLocalRoot).
foreach ($pair in @(@{ Arch = 'x64'; Machine = 0x8664 }, @{ Arch = 'arm64'; Machine = 0xAA64 })) {
    $arch = $pair.Arch
    $pack = Join-Path $out "pack-$arch"
    Copy-Item -LiteralPath (Join-Path $out "publish-$arch") -Destination $pack -Recurse
    Copy-Item -LiteralPath $bootstraps[$arch] -Destination (Join-Path $pack 'HandPeg.exe') -Force
    if ((Test-Path -LiteralPath (Join-Path $pack 'portable')) -or (Test-Path -LiteralPath (Join-Path $pack 'portable.txt'))) { throw "pack-$arch would install as a portable copy" }
    foreach ($file in 'HandPeg.dll', 'HandPeg.runtimeconfig.json', 'HandPeg.deps.json', 'Velopack.dll') {
        if (-not (Test-Path -LiteralPath (Join-Path $pack $file))) { throw "pack-$arch lacks $file" }
    }
    $header = PeHeader (Join-Path $pack 'HandPeg.exe')
    if ($header.Clr -or $header.Machine -ne $pair.Machine -or $header.Subsystem -ne 2) { throw "pack-$arch's HandPeg.exe is not the native bootstrapper of its processor" }

    # HandPeg.dll must ask for the .NET major the bootstrapper looks for (10): a framework of another major would never be found.
    $config = Get-Content -LiteralPath (Join-Path $pack 'HandPeg.runtimeconfig.json') -Raw | ConvertFrom-Json
    $frameworks = @(@($config.runtimeOptions.frameworks) + @($config.runtimeOptions.framework) | Where-Object { $null -ne $_ })
    if ($frameworks.Count -eq 0 -or @($frameworks | Where-Object { -not ([string]$_.version).StartsWith('10.') }).Count -gt 0) {
        throw "pack-$arch's HandPeg.runtimeconfig.json does not ask for .NET 10: $(@($frameworks | ForEach-Object { '{0} {1}' -f $_.name, $_.version }) -join ', ')"
    }
}

# The installers and update feeds (Velopack), one channel per processor, into one folder.
foreach ($pack in @(@{ Rid = 'win-x64'; Channel = 'win'; Dir = 'pack-x64' }, @{ Rid = 'win-arm64'; Channel = 'win-arm64'; Dir = 'pack-arm64' })) {
    $vpkArguments = @('pack', '--packId', 'HandPeg', '--packVersion', $Version, '--packDir', (Join-Path $out $pack.Dir), '--mainExe', 'HandPeg.exe',
                      '--packTitle', 'HandPeg', '--packAuthors', 'gitclanged', '--runtime', $pack.Rid, '--channel', $pack.Channel,
                      '--outputDir', (Join-Path $out 'velopack'))
    if ($SkipVeloAppCheck) { $vpkArguments += '--skipVeloAppCheck' }
    Run $vpk $vpkArguments
}

# Each full package's app folder, as Setup installs it: the native bootstrapper of its processor as HandPeg.exe, HandPeg.dll beside it,
# the thumbnail handler alone in shell\, and no portable folder or portable.txt.
$check = Join-Path $out 'check'
New-Item -ItemType Directory -Force -Path $check | Out-Null
$packages = @(Get-ChildItem -LiteralPath (Join-Path $out 'velopack') -Filter "HandPeg-$Version*-full.nupkg")
if ($packages.Count -ne 2) { throw "Expected two full packages of $Version, found $($packages.Count): $($packages.Name -join ', ')" }
foreach ($package in $packages) {
    $machine = if ($package.Name -like '*-win-arm64-full.nupkg') { 0xAA64 } else { 0x8664 }
    $exe = Join-Path $check ($package.BaseName + '-HandPeg.exe')
    $archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $names = @($archive.Entries | ForEach-Object { $_.FullName })
        if (@($names | Where-Object { $_ -like 'lib/app/portable/*' -or $_ -eq 'lib/app/portable.txt' }).Count -gt 0) { throw "$($package.Name) would install as a portable copy" }
        if ($names -notcontains 'lib/app/HandPeg.dll') { throw "$($package.Name) has no lib/app/HandPeg.dll" }
        if ($names -notcontains 'lib/app/shell/HandPeg.ShellThumbnails.dll' -or @($names | Where-Object { $_ -like 'lib/app/shell/*' }).Count -ne 1) {
            throw "$($package.Name) should carry lib/app/shell/HandPeg.ShellThumbnails.dll alone in shell: $(@($names | Where-Object { $_ -like 'lib/app/shell/*' }) -join ', ')"
        }
        $entry = $archive.GetEntry('lib/app/HandPeg.exe')
        if ($null -eq $entry) { throw "$($package.Name) has no lib/app/HandPeg.exe" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $exe, $true)
    }
    finally { $archive.Dispose() }
    $header = PeHeader $exe
    if ($header.Clr -or $header.Machine -ne $machine -or $header.Subsystem -ne 2) {
        throw ('{0}: lib/app/HandPeg.exe is not the native bootstrapper (machine 0x{1:X4}, subsystem {2}, .NET header {3})' -f $package.Name, $header.Machine, $header.Subsystem, $header.Clr)
    }
    Write-Host ('{0}: lib/app/HandPeg.exe native, machine 0x{1:X4}' -f $package.Name, $header.Machine)
}
Remove-Item -LiteralPath $check -Recurse -Force

Get-ChildItem -LiteralPath (Join-Path $out 'velopack') | ForEach-Object { '{0} {1:N0}' -f $_.Name, $_.Length }
Write-Host "Release $Version of $head in $out ($testNote)"
