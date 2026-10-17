# Inicia el AutoMod con machine learning (python/automod) en http://127.0.0.1:8001
#
#   .\python\iniciar-automod.ps1            (desde la carpeta de la solución)
#
# La primera vez crea un entorno virtual (.venv) e instala lo necesario (~1 minuto).
# Para que la app lo use, en appsettings.json:  "AutoMod": { "Provider": "Http", ... }
$ErrorActionPreference = "Stop"
. "$PSScriptRoot\comun.ps1"
Start-PythonService -Folder "$PSScriptRoot\automod" -Module "automod_service" -Port 8001
