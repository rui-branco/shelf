$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root "src"
$bin  = Join-Path $root "bin"
$csc  = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }
if (-not (Test-Path $bin)) { New-Item -ItemType Directory -Path $bin | Out-Null }

$out = Join-Path $bin "shelf.exe"
$files = Get-ChildItem $src -Filter *.cs | ForEach-Object { $_.FullName }

# Generate the icon if it is missing, so a clean checkout still builds branded.
$ico = Join-Path $root "assets\shelf.ico"
if (-not (Test-Path $ico)) {
  Write-Host "Icon missing - generating..."
  & (Join-Path $root "tools\make-icon.ps1")
}

# UIA assemblies live in the WPF subfolder of the framework directory.
$wpf = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF"

$args = @(
  "/nologo",
  "/target:winexe",
  "/platform:anycpu",
  "/optimize+",
  "/win32icon:$ico",
  "/out:$out",
  "/reference:System.dll",
  "/reference:System.Core.dll",
  "/reference:System.Drawing.dll",
  "/reference:System.Windows.Forms.dll",
  "/reference:$wpf\WindowsBase.dll",
  "/reference:$wpf\UIAutomationClient.dll",
  "/reference:$wpf\UIAutomationTypes.dll"
) + $files

Write-Host "Compiling $($files.Count) files..."
& $csc $args
if ($LASTEXITCODE -ne 0) { throw "BUILD FAILED (csc exit $LASTEXITCODE)" }

if (-not (Test-Path $out)) { throw "BUILD FAILED: $out was not produced" }
$size = (Get-Item $out).Length
Write-Host "BUILD OK -> $out ($size bytes)"
