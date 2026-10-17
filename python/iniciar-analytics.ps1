# Inicia analytics (python/analytics) en http://127.0.0.1:8002
#
#   .\python\iniciar-analytics.ps1          (desde la carpeta de la solución)
#
# Lee las conexiones de SQL Server y MongoDB del appsettings.json de la app web.
# Al arrancar crea el esquema analytics (sql/fase4_analytics.sql) y empieza a juntar datos:
# una foto de los espectadores por minuto y un resumen cada 5 minutos.
$ErrorActionPreference = "Stop"
. "$PSScriptRoot\comun.ps1"
Start-PythonService -Folder "$PSScriptRoot\analytics" -Module "analytics_service" -Port 8002
