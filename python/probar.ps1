# Corre las pruebas (pytest) de los dos servicios Python.
#
#   .\python\probar.ps1                                  pruebas normales (sin bases de datos)
#   .\python\probar.ps1 -SqlServer "localhost"           además, las de analytics contra tu SQL Server
#                                                        (crean y borran una base temporal)
param([string]$SqlServer = "")
$ErrorActionPreference = "Stop"
. "$PSScriptRoot\comun.ps1"

$failed = 0
foreach ($service in @("automod", "analytics")) {
    $folder = Join-Path $PSScriptRoot $service
    $python = Initialize-Venv -Folder $folder -Dev
    Write-Host "`n=== $service ===" -ForegroundColor Cyan
    # Carpeta temporal propia para cada ejecución: así no depende de %TEMP%\pytest-of-<usuario>,
    # que queda inaccesible si alguna vez se corrió pytest como administrador.
    $basetemp = Join-Path ([System.IO.Path]::GetTempPath()) ("twitchclone-pytest-$service-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
    Push-Location $folder
    try {
        if ($service -eq "analytics" -and $SqlServer) {
            $driver = Get-SqlOdbcDriver
            $env:ANALYTICS_TEST_SQL = "DRIVER={$driver};SERVER=$SqlServer;Trusted_Connection=yes;TrustServerCertificate=yes"
            & $python -m pytest --basetemp $basetemp
        } else {
            & $python -m pytest -m "not sqlserver" --basetemp $basetemp
        }
        if ($LASTEXITCODE -ne 0) { $failed++ }
    } finally {
        Pop-Location
        Remove-Item Env:ANALYTICS_TEST_SQL -ErrorAction SilentlyContinue
        Remove-Item $basetemp -Recurse -Force -ErrorAction SilentlyContinue
    }
}
if ($failed) { Write-Host "`n$failed servicio(s) con pruebas fallidas." -ForegroundColor Red; exit 1 }
Write-Host "`nTodas las pruebas de Python pasaron." -ForegroundColor Green
