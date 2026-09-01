param(
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$msbuild = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
$outDir = Join-Path $root "bin\Release"
$outExe = Join-Path $outDir "TianshuQitanLauncher.exe"
$testDir = Join-Path $root "bin\Tests"
$toolsDir = Join-Path $root ".tools"
$nuget = Join-Path $toolsDir "nuget-6.14.0.exe"
$packages = Join-Path $root "packages"
$nugetUrl = "https://dist.nuget.org/win-x86-commandline/v6.14.0/nuget.exe"
$nugetSha256 = "92dbed160ddee0f64b901e907439e021211b428e57c089ecc12fc38dcc4bd9a5"

if (-not (Test-Path $msbuild)) {
    throw "Cannot find .NET Framework MSBuild at $msbuild"
}

New-Item -ItemType Directory -Path $outDir -Force | Out-Null
New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null

if (-not (Test-Path $nuget)) {
    Invoke-WebRequest -Uri $nugetUrl -OutFile $nuget -UseBasicParsing
}

$actualNugetSha256 = (Get-FileHash -LiteralPath $nuget -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualNugetSha256 -ne $nugetSha256) {
    throw "NuGet checksum mismatch. Expected $nugetSha256 but got $actualNugetSha256"
}

& $nuget restore (Join-Path $root "packages.config") `
    -PackagesDirectory $packages `
    -ConfigFile (Join-Path $root "NuGet.config") `
    -NonInteractive

if ($LASTEXITCODE -ne 0) {
    throw "NuGet restore failed with exit code $LASTEXITCODE"
}

& $msbuild (Join-Path $root "tianshuqitan.sln") `
    /nologo `
    /t:Rebuild `
    /p:Configuration=Release `
    /p:Platform=x86 `
    /m

if ($LASTEXITCODE -ne 0) {
    throw "MSBuild failed with exit code $LASTEXITCODE"
}

Copy-Item -LiteralPath (Join-Path $packages "EasyHook.2.7.7097\content\net40\EasyHook32.dll") -Destination $outDir -Force
Copy-Item -LiteralPath (Join-Path $packages "EasyHook.2.7.7097\content\net40\EasyLoad32.dll") -Destination $outDir -Force

$sqliteX86 = Join-Path $outDir "x86"
New-Item -ItemType Directory -Path $sqliteX86 -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $packages "Stub.System.Data.SQLite.Core.NetFramework.1.0.119.0\build\net40\x86\SQLite.Interop.dll") -Destination $sqliteX86 -Force
Copy-Item -LiteralPath (Join-Path $packages "Stub.System.Data.SQLite.Core.NetFramework.1.0.119.0\build\net40\x86\SQLite.Interop.dll") -Destination $outDir -Force

New-Item -ItemType Directory -Path $testDir -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $packages "EasyHook.2.7.7097\content\net40\EasyHook32.dll") -Destination $testDir -Force
Copy-Item -LiteralPath (Join-Path $packages "EasyHook.2.7.7097\content\net40\EasyLoad32.dll") -Destination $testDir -Force
Copy-Item -LiteralPath (Join-Path $packages "Stub.System.Data.SQLite.Core.NetFramework.1.0.119.0\build\net40\x86\SQLite.Interop.dll") -Destination $testDir -Force

if (-not $SkipTests) {
    & (Join-Path $testDir "ProtocolWorkbench.Tests.exe")
    if ($LASTEXITCODE -ne 0) {
        throw "Protocol workbench tests failed with exit code $LASTEXITCODE"
    }
}

Write-Host "Built $outExe"
