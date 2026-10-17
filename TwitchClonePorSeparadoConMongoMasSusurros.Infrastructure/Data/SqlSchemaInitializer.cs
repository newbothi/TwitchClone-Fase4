using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Data
{
    // Crea al arrancar las tablas de dominio nuevas que se manejan con Dapper (no con
    // migraciones de EF). Es idempotente: si la tabla ya existe, no hace nada.
    // El mismo SQL está en sql/fase2_channel_roles.sql por si prefieres correrlo en SSMS.
    public class SqlSchemaInitializer : IHostedService
    {
        public const string ChannelRolesSql = """
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
            """;

        private readonly DbConnectionFactory _connectionFactory;
        private readonly ILogger<SqlSchemaInitializer> _logger;

        public SqlSchemaInitializer(DbConnectionFactory connectionFactory, ILogger<SqlSchemaInitializer> logger)
        {
            _connectionFactory = connectionFactory;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var connection = _connectionFactory.CreateConnection();
                await connection.ExecuteAsync(ChannelRolesSql);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo verificar/crear la tabla ChannelRoles. Córrela a mano con sql/fase2_channel_roles.sql.");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
