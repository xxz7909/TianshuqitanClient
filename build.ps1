$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
$outDir = Join-Path $root "bin\Release"
$outExe = Join-Path $outDir "TianshuQitanLauncher.exe"

if (-not (Test-Path $csc)) {
    throw "Cannot find .NET Framework C# compiler at $csc"
}

New-Item -ItemType Directory -Path $outDir -Force | Out-Null

& $csc `
    /nologo `
    /target:winexe `
    /platform:x86 `
    /optimize+ `
    /win32manifest:"$root\app.manifest" `
    /out:$outExe `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    "$root\src\Program.cs" `
    "$root\src\BrowserFeatureControl.cs" `
    "$root\src\LauncherConfig.cs" `
    "$root\src\Logger.cs" `
    "$root\src\MouseDiagnostics.cs" `
    "$root\src\NativeMethods.cs" `
    "$root\src\MainForm.cs"

if ($LASTEXITCODE -ne 0) {
    throw "C# compiler failed with exit code $LASTEXITCODE"
}

Copy-Item -Path (Join-Path $root "config.ini") -Destination (Join-Path $outDir "config.ini") -Force
Write-Host "Built $outExe"
