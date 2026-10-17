-- Fase 4: esquema "analytics".
--
-- Lo ESCRIBE solo el servicio Python (python/analytics, puerto 8002), que lo crea solo
-- al arrancar ejecutando este mismo archivo. La app de C# solo lo LEE (gráficos del Dashboard).
-- Nada de lo que hay aquí modifica las tablas de dbo.
--
-- Es idempotente: puedes correrlo varias veces en SSMS sin problemas.
-- Días y horas están en la hora local configurada en el servicio (ANALYTICS_TZ,
-- por defecto America/Santiago). Los instantes (…Utc) están en UTC.

IF SCHEMA_ID(N'analytics') IS NULL
    EXEC(N'CREATE SCHEMA analytics AUTHORIZATION dbo');
GO

-- Foto de los espectadores de cada canal EN VIVO, una por minuto.
-- Es la "historia de viewers": con esto se arman todos los gráficos de audiencia.
IF OBJECT_ID(N'analytics.ViewerSnapshots', N'U') IS NULL
BEGIN
    CREATE TABLE analytics.ViewerSnapshots (
        Id            BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ViewerSnapshots PRIMARY KEY,
        ChannelId     INT          NOT NULL,
        CapturedAtUtc DATETIME2(0) NOT NULL,
        Viewers       INT          NOT NULL,
        CategoryId    INT          NULL
    );
    CREATE INDEX IX_ViewerSnapshots_Channel_Time ON analytics.ViewerSnapshots (ChannelId, CapturedAtUtc) INCLUDE (Viewers);
    CREATE INDEX IX_ViewerSnapshots_Time ON analytics.ViewerSnapshots (CapturedAtUtc) INCLUDE (ChannelId, Viewers, CategoryId);
END
GO

-- Un resumen por canal y por día.
IF OBJECT_ID(N'analytics.ChannelDaily', N'U') IS NULL
BEGIN
    CREATE TABLE analytics.ChannelDaily (
        ChannelId         INT           NOT NULL,
        [Day]             DATE          NOT NULL,
        NewFollowers      INT           NOT NULL,
        TotalFollowers    INT           NOT NULL,   -- al final del día
        MinutesLive       INT           NOT NULL,
        AvgViewers        DECIMAL(10,2) NOT NULL,   -- promedio mientras estuvo en vivo
        PeakViewers       INT           NOT NULL,
        ViewerMinutes     BIGINT        NOT NULL,   -- espectadores x minutos (horas vistas x 60)
        ChatMessages      INT           NOT NULL,
        UniqueChatters    INT           NOT NULL,
        SimulatedMessages INT           NOT NULL,   -- de bots del simulador (Fase 3), ya incluidos en ChatMessages
        UpdatedAtUtc      DATETIME2(0)  NOT NULL,
        CONSTRAINT PK_ChannelDaily PRIMARY KEY (ChannelId, [Day])
    );
END
GO

-- Por canal, día y hora local (0 a 23): para "a qué hora te conviene transmitir".
IF OBJECT_ID(N'analytics.ChannelHourly', N'U') IS NULL
BEGIN
    CREATE TABLE analytics.ChannelHourly (
        ChannelId    INT           NOT NULL,
        [Day]        DATE          NOT NULL,
        [Hour]       TINYINT       NOT NULL,
        AvgViewers   DECIMAL(10,2) NOT NULL,
        PeakViewers  INT           NOT NULL,
        Samples      INT           NOT NULL,
        ChatMessages INT           NOT NULL,
        UpdatedAtUtc DATETIME2(0)  NOT NULL,
        CONSTRAINT PK_ChannelHourly PRIMARY KEY (ChannelId, [Day], [Hour])
    );
END
GO

-- Por categoría y día: para las tendencias de la plataforma.
IF OBJECT_ID(N'analytics.CategoryDaily', N'U') IS NULL
BEGIN
    CREATE TABLE analytics.CategoryDaily (
        CategoryId    INT           NOT NULL,
        [Day]         DATE          NOT NULL,
        ChannelsLive  INT           NOT NULL,
        ViewerMinutes BIGINT        NOT NULL,
        PeakViewers   INT           NOT NULL,
        UpdatedAtUtc  DATETIME2(0)  NOT NULL,
        CONSTRAINT PK_CategoryDaily PRIMARY KEY (CategoryId, [Day])
    );
END
GO

-- Tendencia de cada canal (regresión lineal sobre los últimos días), calculada por Python.
IF OBJECT_ID(N'analytics.ChannelTrends', N'U') IS NULL
BEGIN
    CREATE TABLE analytics.ChannelTrends (
        ChannelId             INT           NOT NULL CONSTRAINT PK_ChannelTrends PRIMARY KEY,
        WindowDays            INT           NOT NULL,
        FollowersPerDay       DECIMAL(10,3) NOT NULL,   -- pendiente de la recta de seguidores totales
        FollowersR2           DECIMAL(5,4)  NOT NULL,   -- qué tan bien se ajusta la recta (0 a 1)
        ProjectedFollowers7d  INT           NOT NULL,   -- seguidores estimados en 7 días más
        FollowersGrowthPct    DECIMAL(10,2) NULL,       -- nuevos seguidores vs. el período anterior
        AvgViewersPerDay      DECIMAL(10,3) NOT NULL,   -- pendiente del promedio diario de viewers
        ViewersGrowthPct      DECIMAL(10,2) NULL,       -- promedio de viewers vs. el período anterior
        UpdatedAtUtc          DATETIME2(0)  NOT NULL
    );
END
GO

-- Bitácora de cada ejecución del ETL (para saber si está corriendo y si falló).
IF OBJECT_ID(N'analytics.EtlRuns', N'U') IS NULL
BEGIN
    CREATE TABLE analytics.EtlRuns (
        Id            BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EtlRuns PRIMARY KEY,
        Job           NVARCHAR(40)   NOT NULL,
        StartedAtUtc  DATETIME2(0)   NOT NULL,
        FinishedAtUtc DATETIME2(0)   NULL,
        RowsWritten   INT            NULL,
        Ok            BIT            NULL,
        Error         NVARCHAR(1000) NULL
    );
    CREATE INDEX IX_EtlRuns_Job_Started ON analytics.EtlRuns (Job, StartedAtUtc DESC);
END
GO
