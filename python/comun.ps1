# Funciones compartidas por los scripts de esta carpeta.
# Funcionan en Windows PowerShell 5.1 y en PowerShell 7 (también en Linux/macOS, salvo -SqlServer).

$script:OnWindows = $env:OS -eq "Windows_NT"

function Find-Python {
    # Si "py" o "python" no sirven escriben en stderr; eso no debe cortar el script.
    $ErrorActionPreference = "Continue"
    # El lanzador "py" de Windows es lo más confiable; si no está, se usa "python".
    # (En Windows, "python" puede ser el atajo de Microsoft Store: falla y se ignora).
    foreach ($candidate in @(@("py", "-3"), @("python"), @("python3"))) {
        $exe = $candidate[0]
        if (Get-Command $exe -ErrorAction SilentlyContinue) {
            $version = & $exe @($candidate | Select-Object -Skip 1) -c "import sys; print('%d.%d' % sys.version_info[:2])" 2>$null
            if ($LASTEXITCODE -eq 0 -and $version) {
                $parts = $version.Trim().Split(".")
                if ([int]$parts[0] -eq 3 -and [int]$parts[1] -ge 10) { return ,$candidate }
                Write-Warning "Se encontró Python $version, pero se necesita 3.10 o superior."
            }
        }
    }
    throw "No encontré Python 3.10+. Instálalo con:  winget install Python.Python.3.12   (y abre una terminal nueva)."
}

# Devuelve la ruta al python del entorno virtual (lo crea e instala dependencias si hace falta).
# Todo lo que imprimen venv y pip va a la consola (Out-Host) para no mezclarse con el valor devuelto.
function Initialize-Venv([string]$Folder, [switch]$Dev) {
    $name = Split-Path $Folder -Leaf
    $venv = Join-Path $Folder ".venv"
    $python = if ($script:OnWindows) { Join-Path $venv "Scripts\python.exe" } else { Join-Path $venv "bin/python" }
    $requirements = if ($Dev) { "requirements-dev.txt" } else { "requirements.txt" }
    $stamp = Join-Path $venv ".instalado-$requirements"
    $source = Join-Path $Folder $requirements
    $upToDate = (Test-Path $python) -and (Test-Path $stamp) -and
                ((Get-Item $source).LastWriteTime -le (Get-Item $stamp -Force).LastWriteTime)
    if ($upToDate) { return $python }

    # Candado: dos ventanas instalando en el mismo entorno a la vez se pisan los archivos
    # (WinError 32 "el proceso no tiene acceso al archivo porque está siendo utilizado...").
    $lockPath = Join-Path $Folder ".venv.lock"
    try {
        $lock = [System.IO.File]::Open($lockPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    } catch {
        throw "Otra ventana está instalando el entorno de python\$name en este momento. Espera a que termine (o ciérrala) y vuelve a ejecutar el script."
    }
    try {
        if (-not (Test-Path $python)) {
            $base = Find-Python
            Write-Host "Creando entorno virtual en $venv ..." -ForegroundColor Yellow
            & $base[0] @($base | Select-Object -Skip 1) -m venv $venv | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "No se pudo crear el entorno virtual." }
        }
        Write-Host "Instalando dependencias ($requirements) ..." -ForegroundColor Yellow
        & $python -m pip install --disable-pip-version-check -q -r $source | Out-Host
        if ($LASTEXITCODE -ne 0) {
            # A veces el antivirus revisa los archivos recién descargados y pip choca con él.
            Write-Host "pip falló; reintento en 5 segundos..." -ForegroundColor Yellow
            Start-Sleep -Seconds 5
            & $python -m pip install --disable-pip-version-check -q -r $source | Out-Host
            if ($LASTEXITCODE -ne 0) {
                throw ("Falló pip install -r $requirements. Cierra las otras ventanas que usen python\$name, " +
                       "borra la carpeta $venv y vuelve a ejecutar el script.")
            }
        }
        Set-Content -Path $stamp -Value (Get-Date)
    } finally {
        $lock.Dispose()
    }
    return $python
}

function Test-PortInUse([int]$Port) {
    $client = New-Object System.Net.Sockets.TcpClient
    try { $client.Connect("127.0.0.1", $Port); return $true } catch { return $false } finally { $client.Dispose() }
}

function Get-SqlOdbcDriver {
    if (-not $script:OnWindows) { throw "-SqlServer solo está preparado para Windows. En otro sistema define ANALYTICS_TEST_SQL a mano." }
    $installed = (Get-OdbcDriver -Platform 64-bit -ErrorAction SilentlyContinue).Name
    foreach ($name in @("ODBC Driver 18 for SQL Server", "ODBC Driver 17 for SQL Server", "SQL Server")) {
        if ($installed -contains $name) { return $name }
    }
    throw "No hay un driver ODBC de SQL Server. Instala 'ODBC Driver 18 for SQL Server' (winget install Microsoft.msodbcsql.18)."
}

function Start-PythonService([string]$Folder, [string]$Module, [int]$Port) {
    if (Test-PortInUse $Port) {
        Write-Host "Ya hay algo escuchando en http://127.0.0.1:$Port : probablemente $Module ya está corriendo en otra ventana." -ForegroundColor Yellow
        Write-Host "Si no es así, cierra lo que use ese puerto y vuelve a intentarlo." -ForegroundColor Yellow
        return
    }
    $python = Initialize-Venv -Folder $Folder
    Write-Host "Iniciando $Module en http://127.0.0.1:$Port  (Ctrl+C para detenerlo)" -ForegroundColor Green
    Push-Location $Folder
    try { & $python -m $Module } finally { Pop-Location }
}
