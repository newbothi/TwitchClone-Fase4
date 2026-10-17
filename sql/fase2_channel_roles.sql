-- Fase 2: moderadores y VIP por canal (tabla de dominio manejada con Dapper).
-- La app la crea sola al arrancar (SqlSchemaInitializer); este script es por si
-- prefieres crearla a mano en SSMS. Es idempotente: puedes correrlo varias veces.

IF OBJECT_ID(N'dbo.ChannelRoles', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ChannelRoles (
        Id          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ChannelRoles PRIMARY KEY,
        ChannelId   INT            NOT NULL,
        UserId      NVARCHAR(450)  NOT NULL,
        Role        TINYINT        NOT NULL,   -- 1 = VIP, 2 = Moderador
        GrantedById NVARCHAR(450)  NULL,
        CreatedAt   DATETIME2      NOT NULL CONSTRAINT DF_ChannelRoles_CreatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_ChannelRoles_Channels FOREIGN KEY (ChannelId) REFERENCES dbo.Channels(Id) ON DELETE CASCADE,
        CONSTRAINT FK_ChannelRoles_Users    FOREIGN KEY (UserId)    REFERENCES dbo.Users(Id),
        CONSTRAINT UQ_ChannelRoles_Channel_User UNIQUE (ChannelId, UserId)
    );
END

GO
