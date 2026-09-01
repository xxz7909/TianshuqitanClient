param(
    [string]$Profile
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root "bin\Release\TianshuQitanLauncher.exe"

if (-not (Test-Path $exe)) {
    & (Join-Path $root "build.ps1")
}

$arguments = @()
if (-not [string]::IsNullOrWhiteSpace($Profile)) {
    $arguments += "--profile"
    $arguments += $Profile
}

Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory (Split-Path -Parent $exe)
