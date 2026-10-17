"""Analytics de TwitchClone (Fase 4).

ETL: lee dbo.* (SQL Server) y el chat/sesiones (MongoDB) y escribe SOLO en el esquema
analytics.*. Expone una API de crecimiento y tendencias en el puerto 8002.
El Dashboard de C# lee analytics.* directamente con Dapper.
"""

__version__ = "1.0.0"
