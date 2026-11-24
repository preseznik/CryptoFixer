<#
.SYNOPSIS
    Builds a shippable, self-contained single-file CryptoFixer.exe and packages it.

.DESCRIPTION
    1. Runs the unit tests (skip with -SkipTests).
    2. Publishes src\CryptoFixer as a self-contained, compressed single-file exe. The target server doesn't need .NET installed.
    3. Checks the result: a single exe, the expected file version, the requireAdministrator manifest and the app icon.
    4. Packages dist\CryptoFixer-<version>-<runtime>\ (exe, README.md, docs screenshots), a zip of that folder,
       and a .sha256 file with the hashes of the exe and the zip.
    5. With -SmokeTest, starts the packaged exe in --read-only mode without elevation, waits for the main window, then closes it.

    The publish output, including the .pdb symbol files, is kept in artifacts\publish\<runtime>.

.PARAMETER Version
    Version to stamp into the exe and the package name. Defaults to <Version> in Directory.Build.props.

.PARAMETER Runtime
    Target runtime identifier. Defaults to win-x64.

.EXAMPLE
    .\scripts\publish.ps1

.EXAMPLE
    .\scripts\publish.ps1 -Version 1.1.0 -SmokeTest
#>
[CmdletBinding()]
param(
    [string]$Version,
    [ValidateSet('win-x64', 'win-arm64', 'win-x86')]
    [string]$Runtime = 'win-x64',
    [switch]$SkipTests,
    [switch]$SmokeTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $root 'src\CryptoFixer\CryptoFixer.csproj'
$solution = Join-Path $root 'CryptoFixer.sln'

function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args[0]) failed with exit code $LASTEXITCODE." }
}

function Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }

if (-not $Version) {
    $node = Select-Xml -Path (Join-Path $root 'Directory.Build.props') -XPath '//Version' | Select-Object -First 1
    if (-not $node) { throw 'No <Version> in Directory.Build.props. Pass -Version.' }
    $Version = $node.Node.InnerText.Trim()
}
if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z.-]+)?$') {
    throw "Version '$Version' must look like 1.2.3 or 1.2.3-beta.1."
}
$expectedFileVersion = '{0}.{1}.{2}.0' -f $Matches[1], $Matches[2], $Matches[3]

$name = "CryptoFixer-$Version-$Runtime"
$publishDir = Join-Path $root "artifacts\publish\$Runtime"
$distDir = Join-Path $root 'dist'
$packageDir = Join-Path $distDir $name
$zipPath = Join-Path $distDir "$name.zip"
$hashPath = Join-Path $distDir "$name.sha256"

if (-not $SkipTests) {
    Step 'Running tests'
    Invoke-Dotnet test $solution -c Release -nologo -v q
}

Step "Publishing $name"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
# ReadyToRun is left off: it made the exe ~5 MB bigger without a measurable startup gain. WPF doesn't support trimming.
Invoke-Dotnet publish $appProject -c Release -r $Runtime --self-contained true `
    '-p:PublishSingleFile=true' `
    '-p:IncludeNativeLibrariesForSelfExtract=true' `
    '-p:EnableCompressionInSingleFile=true' `
    '-p:PublishReadyToRun=false' `
    "-p:Version=$Version" `
    -o $publishDir -nologo -v q

Step 'Checking the executable'
$exe = Join-Path $publishDir 'CryptoFixer.exe'
if (-not (Test-Path $exe)) { throw "Publish produced no $exe." }
$extra = @(Get-ChildItem $publishDir -File | Where-Object { $_.Name -ne 'CryptoFixer.exe' -and $_.Extension -ne '.pdb' })
if ($extra.Count -gt 0) { throw "Publish output isn't a single file. Unexpected: $($extra.Name -join ', ')" }

$info = (Get-Item $exe).VersionInfo
if ($info.FileVersion -ne $expectedFileVersion) { throw "File version is $($info.FileVersion), expected $expectedFileVersion." }
if ($info.ProductVersion -ne $Version) { throw "Product version is $($info.ProductVersion), expected $Version." }
if ($info.ProductName -ne 'CryptoFixer') { throw "Product name is '$($info.ProductName)'." }

if (-not ('CryptoFixerPublish.PeResources' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace CryptoFixerPublish
{
    public static class PeResources
    {
        private const uint LoadAsDataFile = 0x2;
        private const uint LoadAsImageResource = 0x20;
        private delegate bool EnumNamesProc(IntPtr module, IntPtr type, IntPtr name, IntPtr param);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr module);
        [DllImport("kernel32.dll")]
        private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LockResource(IntPtr data);
        [DllImport("kernel32.dll")]
        private static extern uint SizeofResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll")]
        private static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumNamesProc callback, IntPtr param);

        private static IntPtr Open(string path)
        {
            IntPtr module = LoadLibraryEx(path, IntPtr.Zero, LoadAsDataFile | LoadAsImageResource);
            if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            return module;
        }

        public static string ReadManifest(string path)
        {
            IntPtr module = Open(path);
            try
            {
                IntPtr resource = FindResource(module, (IntPtr)1, (IntPtr)24);
                if (resource == IntPtr.Zero) return null;
                int size = (int)SizeofResource(module, resource);
                byte[] bytes = new byte[size];
                Marshal.Copy(LockResource(LoadResource(module, resource)), bytes, 0, size);
                return Encoding.UTF8.GetString(bytes);
            }
            finally { FreeLibrary(module); }
        }

        public static int CountResources(string path, int type)
        {
            IntPtr module = Open(path);
            try
            {
                int count = 0;
                EnumNamesProc callback = delegate { count++; return true; };
                EnumResourceNames(module, (IntPtr)type, callback, IntPtr.Zero);
                GC.KeepAlive(callback);
                return count;
            }
            finally { FreeLibrary(module); }
        }
    }
}
'@
}

$manifest = [CryptoFixerPublish.PeResources]::ReadManifest($exe)
if (-not $manifest -or $manifest -notmatch 'level="requireAdministrator"') { throw 'The exe has no requireAdministrator manifest.' }
if ([CryptoFixerPublish.PeResources]::CountResources($exe, 14) -lt 1) { throw 'The exe has no icon.' }
Write-Host "    File version $($info.FileVersion), product version $($info.ProductVersion), requireAdministrator manifest and icon present."

Step "Packaging dist\$name"
foreach ($old in $packageDir, $zipPath, $hashPath) { if (Test-Path $old) { Remove-Item $old -Recurse -Force } }
New-Item -ItemType Directory -Force (Join-Path $packageDir 'docs') | Out-Null
Copy-Item $exe $packageDir
Copy-Item (Join-Path $root 'README.md') $packageDir
Copy-Item (Join-Path $root 'docs\*.png') (Join-Path $packageDir 'docs')
foreach ($optional in 'LICENSE', 'LICENSE.txt', 'LICENSE.md') {
    $file = Join-Path $root $optional
    if (Test-Path $file) { Copy-Item $file $packageDir }
}

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
# Entries are added one by one because ZipFile.CreateFromDirectory in Windows PowerShell 5.1 writes '\' separators, which breaks other unzip tools.
$archive = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem $packageDir -Recurse -File) {
        $entryName = "$name/" + ($file.FullName.Substring($packageDir.Length + 1) -replace '\\', '/')
        $null = [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $archive.Dispose() }

$packagedExe = Join-Path $packageDir 'CryptoFixer.exe'
$outputs = foreach ($file in $packagedExe, $zipPath) {
    $item = Get-Item $file
    [pscustomobject]@{
        File   = $item.FullName.Substring($distDir.Length + 1)
        SizeMB = [Math]::Round($item.Length / 1MB, 1)
        SHA256 = (Get-FileHash $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$hashLines = $outputs | ForEach-Object { '{0}  {1}' -f $_.SHA256, ($_.File -replace '\\', '/') }
[System.IO.File]::WriteAllLines($hashPath, [string[]]$hashLines)

if ($SmokeTest) {
    Step 'Smoke test: starting the packaged exe with --read-only, without elevation'
    $previousLayer = $env:__COMPAT_LAYER
    $env:__COMPAT_LAYER = 'RunAsInvoker'
    try { $process = Start-Process $packagedExe -ArgumentList '--read-only' -PassThru }
    finally { $env:__COMPAT_LAYER = $previousLayer }

    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalSeconds -lt 60) {
        $process.Refresh()
        if ($process.HasExited -or $process.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 100
    }
    if ($process.HasExited) { throw "The exe exited during startup with code $($process.ExitCode)." }
    if ($process.MainWindowHandle -eq [IntPtr]::Zero) { $process.Kill(); throw 'No main window after 60 seconds.' }
    $startMs = $timer.ElapsedMilliseconds
    Start-Sleep -Seconds 3
    if ($process.HasExited) { throw "The exe exited right after startup with code $($process.ExitCode)." }
    $process.Refresh()
    $title = $process.MainWindowTitle
    $null = $process.CloseMainWindow()
    if (-not $process.WaitForExit(15000)) { $process.Kill(); throw 'The exe did not close.' }
    Write-Host "    Main window '$title' appeared after $startMs ms and closed normally."
}

Step 'Done'
$outputs | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
Write-Host "Hashes: $hashPath"
Write-Host "Symbols (.pdb): $publishDir"
