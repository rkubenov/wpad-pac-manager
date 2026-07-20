<#
  WPAD File Manager - build script
  Builds with the IN-BOX C# compiler (csc.exe) only. No .NET SDK / MSBuild / NuGet required.
  Artifacts target .NET Framework 4.x (in-box on Win10/11), so the .exe is fully portable.

  Usage:
    powershell -ExecutionPolicy Bypass -File build/build.ps1            # Core.dll + Tests, run tests
    powershell -ExecutionPolicy Bypass -File build/build.ps1 -App       # also build WpadManager.exe
    powershell -ExecutionPolicy Bypass -File build/build.ps1 -NoTests   # skip running tests
#>
param(
    [switch]$App,
    [switch]$NoTests
)

$ErrorActionPreference = "Stop"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "In-box csc.exe not found at $csc" }

$root = Split-Path -Parent $PSScriptRoot
$src  = Join-Path $root "src"
$out  = Join-Path $root "build\out"
New-Item -ItemType Directory -Force -Path $out | Out-Null

function Get-Cs($dir) { Get-ChildItem -Path $dir -Recurse -Filter *.cs | ForEach-Object { $_.FullName } }

# --- Core library ---
$coreCs  = Get-Cs (Join-Path $src "WpadManager.Core")
$coreDll = Join-Path $out "WpadManager.Core.dll"
Write-Host "Compiling WpadManager.Core ($($coreCs.Count) files)..." -ForegroundColor Cyan
& $csc /nologo /target:library /out:"$coreDll" /langversion:5 $coreCs
if ($LASTEXITCODE -ne 0) { throw "Core build failed" }
Write-Host "  -> $coreDll" -ForegroundColor Green

# --- Tests ---
$testDir = Join-Path $root "tests\WpadManager.Tests"
if (Test-Path $testDir) {
    $testCs  = @(Get-Cs $testDir)
    if ($testCs.Count -gt 0) {
        $testExe = Join-Path $out "WpadManager.Tests.exe"
        Write-Host "Compiling WpadManager.Tests..." -ForegroundColor Cyan
        & $csc /nologo /target:exe /out:"$testExe" /langversion:5 /r:"$coreDll" $testCs
        if ($LASTEXITCODE -ne 0) { throw "Tests build failed" }
        if (-not $NoTests) {
            Write-Host "Running tests..." -ForegroundColor Cyan
            & $testExe
            if ($LASTEXITCODE -ne 0) { throw "TESTS FAILED" }
        }
    }
}

# --- App (single portable exe: Core + App compiled together) ---
if ($App) {
    $appDir = Join-Path $src "WpadManager.App"
    $appCs  = @(Get-Cs $appDir)
    if ($appCs.Count -gt 0) {
        $allCs  = @(Get-Cs (Join-Path $src "WpadManager.Core")) + $appCs
        $appExe = Join-Path $out "WpadManager.exe"
        # Embed the application icon when it has been generated (build/make-icon.ps1).
        $icon    = Join-Path $root "assets\app.ico"
        $iconArg = @()
        if (Test-Path $icon) { $iconArg = @("/win32icon:$icon") }
        Write-Host "Compiling WpadManager.exe (WinForms)..." -ForegroundColor Cyan
        & $csc /nologo /target:winexe /out:"$appExe" /langversion:5 /r:System.Windows.Forms.dll /r:System.Drawing.dll @iconArg $allCs
        if ($LASTEXITCODE -ne 0) { throw "App build failed" }
        Write-Host "  -> $appExe (portable)" -ForegroundColor Green
    }
}

Write-Host "BUILD OK" -ForegroundColor Green
