# Builds the downloads a Windows user can run without installing .NET:
# a self-contained single-file DiskSweeper.exe, the same plus dsweep in a zip,
# the win-arm64 equivalents, and DiskSweeper-Setup.exe.
# On GitHub Actions it also installs the x64 setup silently and uninstalls it.
[CmdletBinding()]
param(
    [string] $Version,
    [switch] $SkipInstallerSmoke
)

$ErrorActionPreference = 'Stop'
# Native tools (dotnet, ISCC, dsweep) write warnings to stderr. Don't treat that as a throw.
$PSNativeCommandUseErrorActionPreference = $false

$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

if (-not $Version) {
    if ($env:GITHUB_REF -match '^refs/tags/v(.+)$') {
        $Version = $Matches[1]
    }
    elseif ($env:GITHUB_RUN_NUMBER) {
        $Version = "0.0.0-ci.$($env:GITHUB_RUN_NUMBER)"
    }
    else {
        $Version = '0.0.0-local'
    }
}

if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)') {
    throw "Version '$Version' must start with major.minor.patch (tag the repo v1.0.0)."
}
$FileVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"

$PublishRoot = Join-Path $Root 'artifacts\publish'
$Dist = Join-Path $Root 'artifacts\dist'
if (Test-Path $PublishRoot) { Remove-Item $PublishRoot -Recurse -Force }
if (Test-Path $Dist) { Remove-Item $Dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Dist | Out-Null

Write-Host "Publishing DiskSweeper $Version (file version $FileVersion)"

function Invoke-Native {
    param(
        [Parameter(Mandatory)] [string] $File,
        [Parameter(Mandatory)] [string[]] $Arguments
    )
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$File $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Format-ByteCount([long] $Bytes) {
    if ($Bytes -ge 1MB) { return '{0:N1} MB' -f ($Bytes / 1MB) }
    if ($Bytes -ge 1KB) { return '{0:N1} KB' -f ($Bytes / 1KB) }
    return "$Bytes bytes"
}

function Assert-PeExecutable([string] $Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $mz0 = $stream.ReadByte()
        $mz1 = $stream.ReadByte()
        if ($mz0 -ne 0x4D -or $mz1 -ne 0x5A) {
            throw "$Path is not a Windows executable."
        }
    }
    finally {
        $stream.Dispose()
    }
}

if (-not ('DiskSweeper.ManifestReader' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace DiskSweeper
{
    public static class ManifestReader
    {
        const uint LoadAsDataFile = 0x00000002;
        const uint LoadAsImageResource = 0x00000020;

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

        [DllImport("kernel32", SetLastError = true)]
        static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);

        [DllImport("kernel32", SetLastError = true)]
        static extern IntPtr LoadResource(IntPtr module, IntPtr resource);

        [DllImport("kernel32")]
        static extern IntPtr LockResource(IntPtr data);

        [DllImport("kernel32", SetLastError = true)]
        static extern uint SizeofResource(IntPtr module, IntPtr resource);

        [DllImport("kernel32", SetLastError = true)]
        static extern bool FreeLibrary(IntPtr module);

        public static string Read(string path)
        {
            Exception last = null;
            uint[] flags = { LoadAsDataFile | LoadAsImageResource, LoadAsImageResource, LoadAsDataFile };
            foreach (uint flag in flags)
            {
                IntPtr module = LoadLibraryEx(path, IntPtr.Zero, flag);
                if (module == IntPtr.Zero)
                {
                    last = new Win32Exception(Marshal.GetLastWin32Error(), "LoadLibraryEx");
                    continue;
                }
                try
                {
                    // RT_MANIFEST is 24. Application manifests are resource id 1, sometimes 2.
                    foreach (int id in new[] { 1, 2 })
                    {
                        IntPtr resource = FindResource(module, (IntPtr)id, (IntPtr)24);
                        if (resource == IntPtr.Zero) continue;
                        uint size = SizeofResource(module, resource);
                        if (size == 0) continue;
                        IntPtr data = LockResource(LoadResource(module, resource));
                        if (data == IntPtr.Zero) continue;
                        byte[] bytes = new byte[size];
                        Marshal.Copy(data, bytes, 0, (int)size);
                        return Decode(bytes);
                    }
                    last = new InvalidOperationException("The executable has no manifest resource.");
                }
                finally
                {
                    FreeLibrary(module);
                }
            }
            throw last ?? new InvalidOperationException("Could not load the executable.");
        }

        static string Decode(byte[] bytes)
        {
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes);
            int zeros = 0;
            for (int i = 1; i < Math.Min(bytes.Length, 64); i += 2)
                if (bytes[i] == 0) zeros++;
            return zeros > 8 ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);
        }
    }
}
'@
}

function Get-ManifestTool {
    $roots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "$env:ProgramFiles\Windows Kits\10\bin"
    )
    $found = foreach ($root in $roots) {
        if (Test-Path $root) {
            Get-ChildItem -Path $root -Filter mt.exe -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\x64\\mt\.exe$' }
        }
    }
    return $found | Sort-Object FullName -Descending | Select-Object -First 1
}

function Read-ManifestWithMt([string] $Path) {
    $mt = Get-ManifestTool
    if (-not $mt) { throw "Windows SDK mt.exe was not found." }
    $errors = @()
    foreach ($id in @('#1', '#2')) {
        $out = Join-Path ([System.IO.Path]::GetTempPath()) ("disksweeper-manifest-" + [guid]::NewGuid().ToString('n') + '.xml')
        $output = & $mt.FullName -nologo "-inputresource:${Path};$id" "-out:$out" 2>&1 | Out-String
        try {
            if ((Test-Path $out) -and (Get-Item $out).Length -gt 0) {
                $text = Get-Content -Raw -Path $out
                if ($text -match 'assembly') { return $text }
            }
        }
        finally {
            Remove-Item $out -Force -ErrorAction SilentlyContinue
        }
        $errors += "mt.exe $id exited ${LASTEXITCODE}: $($output.Trim())"
    }
    throw ($errors -join ' ')
}

function Get-EmbeddedManifest([string] $Path) {
    $lastError = $null
    foreach ($try in 1..4) {
        try {
            return [DiskSweeper.ManifestReader]::Read($Path)
        }
        catch {
            $lastError = $_.Exception.Message
            # An arm64 exe cannot be loaded on this x64 runner. mt.exe only parses the file.
            if ($lastError -match 'not a valid Win32 application|BadImageFormat|193') { break }
            if ($lastError -match 'used by another process|error 32|sharing') {
                Start-Sleep -Seconds 2
                continue
            }
            break
        }
    }

    try {
        return Read-ManifestWithMt $Path
    }
    catch {
        throw "Could not read the manifest in ${Path}. Win32: $lastError. $($_.Exception.Message)"
    }
}

function Assert-AsInvoker([string] $Path) {
    $manifest = Get-EmbeddedManifest $Path
    if ($manifest -notmatch 'level="asInvoker"') {
        throw "$Path does not request asInvoker. The app must open as a normal user and elevate only when asked."
    }
    if ($manifest -match 'level="(requireAdministrator|highestAvailable)"') {
        throw "$Path requests elevation on every launch."
    }
    if ($manifest -notmatch 'longPathAware') {
        throw "$Path is missing longPathAware."
    }
}

function Publish-SingleFile {
    param(
        [Parameter(Mandatory)] [string] $Project,
        [Parameter(Mandatory)] [string] $Runtime,
        [Parameter(Mandatory)] [string] $Destination
    )

    $name = [System.IO.Path]::GetFileName($Destination)
    $out = Join-Path $PublishRoot "raw-$Runtime-$name"
    # Self-contained single file: the PC that runs the exe does not need a .NET install.
    # Native libraries are packed into the exe and extracted on launch.
    # Compression keeps the download smaller. Trimming is off because WPF cannot be trimmed.
    Invoke-Native 'dotnet' @(
        'publish', $Project,
        '-c', 'Release',
        '-r', $Runtime,
        '--self-contained', 'true',
        '-o', $out,
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:DebugType=none',
        '-p:DebugSymbols=false',
        '-p:PublishTrimmed=false',
        '-p:SatelliteResourceLanguages=en',
        "-p:Version=$Version",
        "-p:FileVersion=$FileVersion",
        "-p:AssemblyVersion=$FileVersion"
    )

    $produced = Join-Path $out $name
    if (-not (Test-Path -LiteralPath $produced)) {
        $listing = (Get-ChildItem $out -Force -ErrorAction SilentlyContinue | ForEach-Object Name) -join ', '
        throw "Publish did not produce $name. Files: $listing"
    }

    # A symbols file beside the exe is not shipped. Anything else means the publish is not one file.
    Get-ChildItem $out -Filter *.pdb -Force -ErrorAction SilentlyContinue | Remove-Item -Force
    $extras = @(Get-ChildItem $out -Force | Where-Object { $_.Name -ne $name })
    if ($extras.Count -gt 0) {
        throw "Single-file publish of $name left extra files: $($extras.Name -join ', ')"
    }

    $dir = Split-Path $Destination -Parent
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Copy-Item $produced $Destination -Force

    Assert-PeExecutable $Destination
    $size = (Get-Item $Destination).Length
    # A framework-dependent host is a few hundred KB. The bundled runtime is tens of MB.
    if ($size -lt 20MB) {
        throw "$Destination is only $(Format-ByteCount $size). The .NET runtime was not bundled."
    }

    $info = (Get-Item $Destination).VersionInfo
    $actual = [version] $info.FileVersionRaw
    if ($actual -ne [version] $FileVersion) {
        throw "$Destination file version is $actual, expected $FileVersion."
    }

    Assert-AsInvoker $Destination
    Write-Host ("Published {0} ({1})" -f $Destination, (Format-ByteCount $size))
}

function Compress-ReleaseZip {
    param(
        [Parameter(Mandatory)] [string] $Runtime,
        [Parameter(Mandatory)] [string] $ZipPath
    )
    $bundle = Join-Path $PublishRoot $Runtime
    $readme = Join-Path $bundle 'README.txt'
    $license = Join-Path $bundle 'LICENSE'
    Copy-Item (Join-Path $Root 'installer\Portable.txt') $readme -Force
    Copy-Item (Join-Path $Root 'LICENSE') $license -Force

    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    # Archive from inside the folder so the zip entries are the file names, not a path prefix.
    Push-Location $bundle
    try {
        Compress-Archive -Path @('DiskSweeper.exe', 'dsweep.exe', 'LICENSE', 'README.txt') -DestinationPath $ZipPath -CompressionLevel Optimal
    }
    finally {
        Pop-Location
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $names = @($zip.Entries | ForEach-Object {
            ($_.FullName.Replace('\', '/').TrimStart('./'))
        })
    }
    finally {
        $zip.Dispose()
    }
    foreach ($required in @('DiskSweeper.exe', 'dsweep.exe', 'LICENSE', 'README.txt')) {
        if ($names -notcontains $required) {
            throw "$ZipPath is missing $required (entries: $($names -join ', '))."
        }
    }
}

function Find-Iscc {
    $candidates = @(
        'C:\InnoSetup\ISCC.exe',
        "${env:ProgramFiles}\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Install-InnoSetup {
    $expected = '8a5a6711c3766a6e5fb74f280ba0e3551e719ce94538d901fdbd1f8f9920b3f0'
    $temp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [System.IO.Path]::GetTempPath() }
    $installer = Join-Path $temp 'innosetup-7.1.1-x64.exe'
    $url = 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_1/innosetup-7.1.1-x64.exe'
    Write-Host "Downloading Inno Setup 7.1.1"
    Invoke-WebRequest -Uri $url -OutFile $installer
    $hash = (Get-FileHash -Path $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $expected) {
        throw "Inno Setup installer hash is $hash, expected $expected."
    }
    $proc = Start-Process -FilePath $installer -ArgumentList @(
        '/VERYSILENT', '/NORESTART', '/SUPPRESSMSGBOXES', '/DIR=C:\InnoSetup'
    ) -Wait -PassThru
    if ($proc.ExitCode -ne 0) {
        throw "Inno Setup installer exited $($proc.ExitCode)."
    }
    if (-not (Test-Path 'C:\InnoSetup\ISCC.exe')) {
        throw "Inno Setup installed but C:\InnoSetup\ISCC.exe is missing."
    }
    return 'C:\InnoSetup\ISCC.exe'
}

function Test-InstalledSetup {
    $installDir = 'C:\DiskSweeperSmoke'
    $setup = Join-Path $Dist 'DiskSweeper-Setup.exe'
    $installed = $false
    try {
        if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force }
        $proc = Start-Process -FilePath $setup -ArgumentList @(
            '/VERYSILENT', '/NORESTART', '/SUPPRESSMSGBOXES', "/DIR=$installDir"
        ) -Wait -PassThru
        if ($proc.ExitCode -ne 0) {
            throw "DiskSweeper-Setup.exe exited $($proc.ExitCode)."
        }
        $installed = $true

        foreach ($file in @('DiskSweeper.exe', 'dsweep.exe', 'LICENSE', 'README.txt', 'unins000.exe')) {
            if (-not (Test-Path (Join-Path $installDir $file))) {
                throw "Installer did not place $file in $installDir."
            }
        }
        $shortcuts = @(Get-ChildItem -Path (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs') -Filter 'DiskSweeper.lnk' -Recurse -ErrorAction SilentlyContinue)
        if ($shortcuts.Count -ne 1) {
            throw "Expected one Start menu shortcut, found $($shortcuts.Count): $($shortcuts.FullName -join ', ')"
        }
        $shell = New-Object -ComObject WScript.Shell
        $target = $shell.CreateShortcut($shortcuts[0].FullName).TargetPath
        if ([string]::IsNullOrWhiteSpace($target)) {
            throw "Start menu shortcut '$($shortcuts[0].FullName)' has no target."
        }
        $target = [System.IO.Path]::GetFullPath($target)
        $expectedTarget = [System.IO.Path]::GetFullPath((Join-Path $installDir 'DiskSweeper.exe'))
        if ($target -ine $expectedTarget) {
            throw "Start menu shortcut points at '$target', expected '$expectedTarget'."
        }

        $installedExe = Join-Path $installDir 'DiskSweeper.exe'
        $installedVersion = [version] (Get-Item $installedExe).VersionInfo.FileVersionRaw
        if ($installedVersion -ne [version] $FileVersion) {
            throw "Installed DiskSweeper.exe version is $installedVersion, expected $FileVersion."
        }

        $uninstall = @(Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
            ForEach-Object { Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue } |
            Where-Object { $_.DisplayName -eq 'DiskSweeper' })
        if ($uninstall.Count -ne 1) {
            throw "Expected one Settings > Apps entry for DiskSweeper, found $($uninstall.Count)."
        }
        if ($uninstall[0].PSPath -match 'WOW6432Node') {
            throw "Installer registered a 32-bit uninstall entry."
        }

        $help = & (Join-Path $installDir 'dsweep.exe') --help | Out-String
        if ($LASTEXITCODE -ne 0 -or $help -notmatch 'command-line tool') {
            throw "Installed dsweep.exe --help failed: $help"
        }

        $uninstaller = Join-Path $installDir 'unins000.exe'
        $removed = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/NORESTART') -Wait -PassThru
        if ($removed.ExitCode -ne 0) {
            throw "Uninstaller exited $($removed.ExitCode)."
        }
        $installed = $false

        # unins000.exe removes itself after the process exits, so give it a moment.
        $deadline = (Get-Date).AddSeconds(20)
        do {
            $shortcutsLeft = @(Get-ChildItem -Path (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs') -Filter 'DiskSweeper.lnk' -Recurse -ErrorAction SilentlyContinue)
            $left = @(Get-ChildItem $installDir -Force -ErrorAction SilentlyContinue)
            if ($shortcutsLeft.Count -eq 0 -and $left.Count -eq 0) { break }
            Start-Sleep -Seconds 1
        } while ((Get-Date) -lt $deadline)
        if ($shortcutsLeft.Count -gt 0) {
            throw "Uninstall left the Start menu shortcut in place."
        }
        if ($left.Count -gt 0) {
            throw "Uninstall left files behind: $($left.Name -join ', ')"
        }
        Write-Host "Installer smoke test passed (install, Start menu shortcut, uninstall)."
    }
    finally {
        if ($installed -and (Test-Path (Join-Path $installDir 'unins000.exe'))) {
            Start-Process -FilePath (Join-Path $installDir 'unins000.exe') -ArgumentList @('/VERYSILENT', '/NORESTART') -Wait | Out-Null
        }
    }
}

Invoke-Native 'dotnet' @('restore', 'DiskSweeper.sln')

$runtimes = @('win-x64', 'win-arm64')
foreach ($runtime in $runtimes) {
    $bundle = Join-Path $PublishRoot $runtime
    Publish-SingleFile -Project 'src\DiskSweeper.App\DiskSweeper.App.csproj' -Runtime $runtime -Destination (Join-Path $bundle 'DiskSweeper.exe')
    Publish-SingleFile -Project 'src\DiskSweeper.Cli\DiskSweeper.Cli.csproj' -Runtime $runtime -Destination (Join-Path $bundle 'dsweep.exe')

    $suffix = $runtime.Substring(4) # win-x64 -> x64
    Copy-Item (Join-Path $bundle 'DiskSweeper.exe') (Join-Path $Dist "DiskSweeper-win-$suffix.exe") -Force
    Compress-ReleaseZip -Runtime $runtime -ZipPath (Join-Path $Dist "DiskSweeper-win-$suffix.zip")
}

# The x64 CLI can actually start on this runner. Help exits before any scan.
$help = & (Join-Path $PublishRoot 'win-x64\dsweep.exe') --help | Out-String
if ($LASTEXITCODE -ne 0 -or $help -notmatch 'command-line tool') {
    throw "dsweep.exe --help failed: $help"
}
Write-Host "dsweep.exe --help succeeded without a separate .NET install step."

$iscc = Find-Iscc
if (-not $iscc) { $iscc = Install-InnoSetup }
Write-Host "Compiling installer with $iscc"
$env:DISKSWEEPER_VERSION = $Version
$env:DISKSWEEPER_FILE_VERSION = $FileVersion
Invoke-Native $iscc @((Join-Path $Root 'installer\DiskSweeper.iss'))

$setup = Join-Path $Dist 'DiskSweeper-Setup.exe'
if (-not (Test-Path $setup)) {
    throw "ISCC did not write $setup."
}
Assert-PeExecutable $setup

if ($env:GITHUB_ACTIONS -eq 'true' -and -not $SkipInstallerSmoke) {
    Test-InstalledSetup
}

$sums = foreach ($file in (Get-ChildItem $Dist -File | Sort-Object Name)) {
    $hash = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    '{0}  {1}' -f $hash, $file.Name
}
$sumsPath = Join-Path $Dist 'SHA256SUMS.txt'
Set-Content -Path $sumsPath -Value $sums -Encoding ascii

$expected = @(
    'DiskSweeper-win-x64.exe',
    'DiskSweeper-win-x64.zip',
    'DiskSweeper-win-arm64.exe',
    'DiskSweeper-win-arm64.zip',
    'DiskSweeper-Setup.exe',
    'SHA256SUMS.txt'
)
foreach ($name in $expected) {
    $path = Join-Path $Dist $name
    if (-not (Test-Path $path)) { throw "Missing dist\$name." }
    $size = (Get-Item $path).Length
    Write-Host ("DISKSWEEPER_ARTIFACT {0} {1} {2}" -f $name, $size, (Format-ByteCount $size))
}

if ($env:GITHUB_STEP_SUMMARY) {
    $lines = @(
        '## Windows release files',
        '',
        '| File | Size |',
        '| --- | ---: |'
    )
    foreach ($name in $expected) {
        $item = Get-Item (Join-Path $Dist $name)
        $lines += '| `{0}` | {1} |' -f $name, (Format-ByteCount $item.Length)
    }
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value ($lines -join "`n")
}
