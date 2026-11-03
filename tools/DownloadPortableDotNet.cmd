@echo off
rem HandPeg: Download Portable .NET (No Admin)
rem
rem For a PC without the .NET 8 Desktop Runtime (a locked-down work PC): double-click this file once. It downloads
rem Microsoft's own .NET 8 runtime and Windows Desktop runtime zips for this build's architecture (x64 or ARM64, read
rem from HandPeg.exe), checks each against the SHA-512 Microsoft publishes, and unpacks them into this folder (a
rem "shared" folder and hostfxr.dll beside HandPeg.exe). Nothing is installed on the PC, no admin rights are asked
rem for, and no environment variable is set: from then on HandPeg.exe starts directly, on its own .NET, from this
rem folder, which can be carried anywhere. Then it starts HandPeg. Run it again to take a newer .NET 8 patch.
setlocal
set "HP_SELF=%~f0"
rem Windows PowerShell builds its own module path: one inherited from a newer PowerShell would hide its modules.
set "PSModulePath="
powershell.exe -NoProfile -Command "$text = [IO.File]::ReadAllText($env:HP_SELF); Invoke-Expression $text.Substring($text.IndexOf('<' + '#PS#' + '>'))"
if errorlevel 1 (
    echo.
    pause
    exit /b 1
)
exit /b 0
<#PS#>
# Windows PowerShell 5.1 (every Windows 10 and 11 has it): no newer syntax below.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$app = Split-Path -Parent $env:HP_SELF
$exe = Join-Path $app 'HandPeg.exe'
$shared = Join-Path $app 'shared'
$sites = @('https://builds.dotnet.microsoft.com/dotnet/release-metadata', 'https://dotnetcli.blob.core.windows.net/dotnet/release-metadata')

function Read-Json([string]$path) {
    foreach ($site in $sites) {
        try { return Invoke-RestMethod -Uri "$site/$path" -UseBasicParsing } catch { $last = $_ }
    }
    throw "Microsoft's .NET download site could not be reached ($($last.Exception.Message))."
}

try {
    if (-not (Test-Path -LiteralPath $exe)) { throw 'HandPeg.exe is not beside this file: keep it in the folder you unzipped.' }

    # The architecture this copy of HandPeg is built for, from its own header.
    $head = New-Object byte[] 4096
    $stream = [IO.File]::OpenRead($exe)
    try { [void]$stream.Read($head, 0, $head.Length) } finally { $stream.Dispose() }
    $machine = [BitConverter]::ToUInt16($head, [BitConverter]::ToInt32($head, 0x3C) + 4)
    if ($machine -eq 0x8664) { $arch = 'x64' } elseif ($machine -eq 0xAA64) { $arch = 'arm64' } else { throw ('HandPeg.exe is for an architecture this script does not know (0x{0:X4}).' -f $machine) }
    $rid = "win-$arch"

    # The latest .NET 8 release and its files, with Microsoft's hashes.
    $index = Read-Json 'releases-index.json'
    $channel = $index.'releases-index' | Where-Object { $_.'channel-version' -eq '8.0' } | Select-Object -First 1
    if (-not $channel) { throw 'Microsoft lists no .NET 8 release.' }
    $version = $channel.'latest-release'
    $release = (Read-Json "8.0/$version/release.json").release

    $installed = @(Get-ChildItem -LiteralPath (Join-Path $shared 'Microsoft.WindowsDesktop.App') -Directory -ErrorAction SilentlyContinue | ForEach-Object Name)
    if ((Test-Path -LiteralPath (Join-Path $app 'hostfxr.dll')) -and ($installed -contains $release.windowsdesktop.version)) {
        Write-Host "This copy of HandPeg already has its own .NET $($release.windowsdesktop.version): starting it."
        Start-Process -FilePath $exe -WorkingDirectory $app
        exit 0
    }

    Write-Host "Downloading .NET $version for $arch from Microsoft (about 70 MB) into this folder."
    Write-Host 'Nothing is installed on this PC and no admin rights are needed.'
    $work = Join-Path $app 'portable\dotnet-download'
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
    $unpacked = Join-Path $work 'unpacked'
    New-Item -ItemType Directory -Force -Path $unpacked | Out-Null
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

    foreach ($part in @(@{ Name = '.NET runtime'; Files = $release.runtime.files }, @{ Name = 'Windows Desktop runtime'; Files = $release.windowsdesktop.files })) {
        $file = $part.Files | Where-Object { $_.rid -eq $rid -and $_.name -like '*.zip' } | Select-Object -First 1
        if (-not $file) { throw "Microsoft lists no $($part.Name) zip for $rid." }
        $zip = Join-Path $work ([IO.Path]::GetFileName(([Uri]$file.url).AbsolutePath))
        Write-Host "  $($part.Name) $version..."
        Invoke-WebRequest -Uri $file.url -OutFile $zip -UseBasicParsing
        # Hashed by .NET itself: no module to load (a PowerShell started from another one can miss its own modules).
        $sha = [Security.Cryptography.SHA512]::Create()
        $read = [IO.File]::OpenRead($zip)
        try { $hash = [BitConverter]::ToString($sha.ComputeHash($read)).Replace('-', '') } finally { $read.Dispose(); $sha.Dispose() }
        if ($hash -ne $file.hash.ToUpperInvariant()) {
            throw "$([IO.Path]::GetFileName($zip)) does not match the SHA-512 Microsoft publishes for it: it was not used."
        }

        $archive = [IO.Compression.ZipFile]::OpenRead($zip)
        try {
            foreach ($entry in $archive.Entries) {
                if (-not $entry.Name) { continue }
                $to = [IO.Path]::GetFullPath((Join-Path $unpacked $entry.FullName))
                if (-not $to.StartsWith($unpacked, [StringComparison]::OrdinalIgnoreCase)) { throw 'The runtime zip holds a path outside its folder: it was not used.' }
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $to) | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $to, $true)
            }
        }
        finally { $archive.Dispose() }
    }

    $fxr = Get-ChildItem -LiteralPath (Join-Path $unpacked 'host\fxr') -Recurse -Filter 'hostfxr.dll' | Select-Object -First 1
    if (-not $fxr) { throw 'The runtime has no hostfxr.dll.' }

    # Into place: each framework version under "shared", and hostfxr.dll beside HandPeg.exe, which then finds them itself.
    foreach ($framework in Get-ChildItem -LiteralPath (Join-Path $unpacked 'shared') -Directory) {
        foreach ($folder in Get-ChildItem -LiteralPath $framework.FullName -Directory) {
            $to = Join-Path $shared (Join-Path $framework.Name $folder.Name)
            if (Test-Path -LiteralPath $to) { Remove-Item -LiteralPath $to -Recurse -Force }
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $to) | Out-Null
            Move-Item -LiteralPath $folder.FullName -Destination $to
        }
    }
    Copy-Item -LiteralPath $fxr.FullName -Destination (Join-Path $app 'hostfxr.dll') -Force
    foreach ($notice in 'LICENSE.txt', 'ThirdPartyNotices.txt') {
        $from = Join-Path $unpacked $notice
        if (Test-Path -LiteralPath $from) { Copy-Item -LiteralPath $from -Destination (Join-Path $shared ".NET $notice") -Force }
    }
    Remove-Item -LiteralPath $work -Recurse -Force

    Write-Host "Done: HandPeg.exe now starts on its own .NET $version from this folder. Starting HandPeg."
    Start-Process -FilePath $exe -WorkingDirectory $app
    exit 0
}
catch {
    Write-Host ''
    Write-Host ".NET could not be set up: $($_.Exception.Message)" -ForegroundColor Red
    if ($work -and (Test-Path -LiteralPath $work)) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
    exit 1
}
