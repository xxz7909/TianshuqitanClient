$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root "bin\Release\TianshuQitanLauncher.exe"

if (-not (Test-Path $exe)) {
    & (Join-Path $root "build.ps1")
}

Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe)
