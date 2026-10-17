<#
.SYNOPSIS
    Transmite una señal de prueba (o un video en bucle) a tu canal, sin abrir OBS.

.DESCRIPTION
    Sirve para probar el flujo completo de la Fase 1 (MediaMTX valida la clave →
    el canal pasa a EN VIVO → el reproductor muestra el video) y para tener varios
    canales "en vivo" a la vez mientras desarrollas.

    Requiere ffmpeg en el PATH. En Windows:  winget install Gyan.FFmpeg

.PARAMETER StreamKey
    La "Clave de transmisión" tal cual aparece en tu Dashboard, por ejemplo:  12?key=sk_AbC123...

.PARAMETER Server
    El "Servidor" del Dashboard. Por defecto rtmp://localhost/live

.PARAMETER Video
    (Opcional) Ruta a un .mp4 para transmitirlo en bucle en vez de la señal de prueba.

.EXAMPLE
    .\scripts\stream-de-prueba.ps1 -StreamKey "12?key=sk_AbC123..."

.EXAMPLE
    .\scripts\stream-de-prueba.ps1 -StreamKey "12?key=sk_AbC123..." -Video "C:\videos\gameplay.mp4"
#>
param(
    [Parameter(Mandatory = $true)][string]$StreamKey,
    [string]$Server = "rtmp://localhost/live",
    [string]$Video = ""
)

if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) {
    Write-Error "No se encontró ffmpeg en el PATH. Instálalo con:  winget install Gyan.FFmpeg  (y abre una terminal nueva)"
    exit 1
}

$url = "$($Server.TrimEnd('/'))/$StreamKey"

# Mismos parámetros que se recomiendan en OBS: CBR, fotograma clave cada 2 s (-g 60 a 30 fps), AAC 48 kHz.
$encode = @(
    "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency", "-pix_fmt", "yuv420p",
    "-r", "30", "-g", "60", "-keyint_min", "60", "-sc_threshold", "0",
    "-b:v", "2500k", "-maxrate", "2500k", "-bufsize", "5000k",
    "-c:a", "aac", "-b:a", "128k", "-ar", "48000",
    "-f", "flv", $url
)

if ($Video -ne "") {
    if (-not (Test-Path $Video)) { Write-Error "No existe el archivo: $Video"; exit 1 }
    Write-Host "Transmitiendo '$Video' en bucle a $Server ... (Ctrl+C para detener)" -ForegroundColor Cyan
    & ffmpeg -hide_banner -loglevel warning -re -stream_loop -1 -i $Video @encode
}
else {
    Write-Host "Transmitiendo señal de prueba a $Server ... (Ctrl+C para detener)" -ForegroundColor Cyan
    & ffmpeg -hide_banner -loglevel warning -re `
        -f lavfi -i "testsrc2=size=1280x720:rate=30" `
        -f lavfi -i "sine=frequency=440:sample_rate=48000" `
        @encode
}

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "ffmpeg terminó con error. Causas típicas:" -ForegroundColor Yellow
    Write-Host " - La clave no es válida (MediaMTX responde 'authentication failed'). Cópiala de nuevo del Dashboard."
    Write-Host " - MediaMTX no está corriendo, o la app web no está corriendo (MediaMTX le pregunta por cada publicación)."
    Write-Host " - El secreto de mediamtx.yml no coincide con MediaServer:HookSecret de appsettings.json."
}
