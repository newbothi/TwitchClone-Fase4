namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models
{
    // Rol de un usuario dentro de UN canal (tabla ChannelRoles).
    // El dueño del canal ("broadcaster") y los Admin de la plataforma no se guardan aquí:
    // se deducen de Channels.UserId y del rol de Identity.
    public enum ChannelRoleType
    {
        None = 0,
        Vip = 1,
        Moderator = 2
    }
}
