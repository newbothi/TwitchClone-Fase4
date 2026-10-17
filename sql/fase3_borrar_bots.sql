-- Fase 3: borra TODAS las cuentas de bots del simulador y lo que generaron en SQL Server
-- (follows, donaciones, roles, bans, mensajes). Los bots se reconocen por el email
-- bot0001@bots.twitchclone.local, bot0002@...
--
-- Detén la simulación antes de correrlo. Los mensajes de chat de los bots están en
-- MongoDB (colección ChatMessages, campo IsSimulated = true): ver el final del archivo.

SET XACT_ABORT ON;
BEGIN TRAN;

DECLARE @Bots TABLE (Id NVARCHAR(450) PRIMARY KEY);
INSERT INTO @Bots (Id)
SELECT Id FROM dbo.Users WHERE Email LIKE N'%@bots.twitchclone.local';

DELETE FROM dbo.Follows      WHERE FollowerId IN (SELECT Id FROM @Bots) OR FollowedId IN (SELECT Id FROM @Bots);
DELETE FROM dbo.Donations    WHERE FromUserId IN (SELECT Id FROM @Bots);
DELETE FROM dbo.BannedUsers  WHERE UserId     IN (SELECT Id FROM @Bots) OR BannedById IN (SELECT Id FROM @Bots);
DELETE FROM dbo.Messages     WHERE UserId     IN (SELECT Id FROM @Bots);
IF OBJECT_ID(N'dbo.ChannelRoles', N'U') IS NOT NULL
    DELETE FROM dbo.ChannelRoles WHERE UserId IN (SELECT Id FROM @Bots);
DELETE FROM dbo.UserRoles    WHERE UserId     IN (SELECT Id FROM @Bots);
DELETE FROM dbo.UserClaims   WHERE UserId     IN (SELECT Id FROM @Bots);
DELETE FROM dbo.UserLogins   WHERE UserId     IN (SELECT Id FROM @Bots);
DELETE FROM dbo.UserTokens   WHERE UserId     IN (SELECT Id FROM @Bots);
DELETE FROM dbo.Users        WHERE Id         IN (SELECT Id FROM @Bots);

SELECT @@ROWCOUNT AS BotsBorrados;
COMMIT;
GO

-- En MongoDB (mongosh), para borrar sus mensajes de chat:
--   use TwitchClonePorSeparadoConMongoMasSusurros
--   db.ChatMessages.deleteMany({ IsSimulated: true })
