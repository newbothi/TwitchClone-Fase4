-- Ejecutar UNA VEZ contra la base TwitchClonePorSeparadoConMongoMasSusurros en tu SQL Server,
-- antes de volver a arrancar la aplicación con el nuevo seed de Program.cs
-- (versión con jerarquía Category -> ParentCategory).
--
-- Qué hace:
--   1. Cualquier canal que ya tuviera una categoría vieja queda con
--      CategoryId = NULL, sin perder el canal ni ningún otro dato del streamer.
--   2. Limpia ParentCategoryId antes de borrar, porque esa relación es
--      auto-referencial con DeleteBehavior.Restrict (SQL Server no deja
--      borrar una fila mientras otra fila la referencia como padre).
--   3. Borra los vínculos CategoryTag y los Tags/Categories viejos.
--   4. Reinicia el contador de IDENTITY de las tres tablas para que el
--      seed nuevo empiece limpio (no es obligatorio, pero deja los IDs prolijos).
--
-- Es seguro re-ejecutarlo: si las tablas ya están vacías, simplemente no borra nada.

USE TwitchClonePorSeparadoConMongoMasSusurros;
GO

UPDATE Categories SET ParentCategoryId = NULL;
GO

DELETE FROM CategoryTags;
DELETE FROM Categories;
DELETE FROM Tags;
GO

DBCC CHECKIDENT ('CategoryTags', RESEED, 0);
DBCC CHECKIDENT ('Categories', RESEED, 0);
DBCC CHECKIDENT ('Tags', RESEED, 0);
GO

