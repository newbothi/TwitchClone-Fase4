namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces
{
    // Quién está viendo cada canal (lo implementa el PresenceTracker de la capa Web).
    public interface IPresenceCounter
    {
        int GetCount(int channelId);

        // Ids de usuarios con sesión que están viendo el canal (sin anónimos).
        IReadOnlyCollection<string> GetUserIds(int channelId);
    }
}
