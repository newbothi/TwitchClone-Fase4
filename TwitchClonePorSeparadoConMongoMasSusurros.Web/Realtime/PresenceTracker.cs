using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Realtime
{
    // Quién está mirando cada canal (conexiones de SignalR de la página Watch).
    //
    // Cambios respecto a la versión anterior (diccionarios estáticos dentro del hub):
    //  • Un usuario con 3 pestañas abiertas cuenta como 1 viewer (se agrupa por usuario).
    //  • Los anónimos cuentan por conexión, y se muestran como "N espectadores anónimos".
    //  • No se reenvía la lista completa a todos en cada entrada/salida: el conteo se
    //    difunde cada ~2 s (PresenceBroadcaster) y la lista se pide solo al abrir el modal.
    //
    // Vive en memoria de UNA instancia. En la Fase 2 pasa a Redis.
    public sealed class PresenceTracker : IPresenceCounter
    {
        public sealed record Viewer(string ViewerKey, string? DisplayName, bool IsSimulated);

        private readonly object _gate = new();
        private readonly Dictionary<int, Dictionary<string, Viewer>> _byChannel = new();
        private readonly Dictionary<string, int> _channelByConnection = new();
        private readonly HashSet<int> _dirty = new();

        public static string UserKey(string userId) => $"u:{userId}";
        public static string AnonymousKey(string connectionId) => $"c:{connectionId}";
        public static string SimulatedKey(string connectionId) => $"s:{connectionId}";

        // Los bots del simulador (Fase 3) no tienen conexión real: usan una "conexión"
        // sintética por bot y canal, y cuentan como usuarios con sesión (clave u:{id}).
        public static string BotConnectionId(int channelId, string botUserId) => $"bot:{channelId}:{botUserId}";

        // Registra la conexión en el canal. Devuelve el canal anterior si la conexión cambió de canal.
        public int? Join(int channelId, string connectionId, string viewerKey, string? displayName, bool isSimulated = false)
        {
            lock (_gate)
            {
                int? previous = null;
                if (_channelByConnection.TryGetValue(connectionId, out var oldChannel) && oldChannel != channelId)
                {
                    RemoveUnsafe(oldChannel, connectionId);
                    previous = oldChannel;
                }

                if (!_byChannel.TryGetValue(channelId, out var viewers))
                {
                    viewers = new Dictionary<string, Viewer>();
                    _byChannel[channelId] = viewers;
                }

                viewers[connectionId] = new Viewer(viewerKey, displayName, isSimulated);
                _channelByConnection[connectionId] = channelId;
                _dirty.Add(channelId);
                return previous;
            }
        }

        // Quita la conexión de donde esté. Devuelve el canal del que salió (o null).
        public int? Leave(string connectionId)
        {
            lock (_gate)
            {
                if (!_channelByConnection.TryGetValue(connectionId, out var channelId)) return null;
                RemoveUnsafe(channelId, connectionId);
                return channelId;
            }
        }

        public int GetCount(int channelId)
        {
            lock (_gate)
            {
                return _byChannel.TryGetValue(channelId, out var viewers)
                    ? viewers.Values.Select(v => v.ViewerKey).Distinct().Count()
                    : 0;
            }
        }

        public IReadOnlyCollection<string> GetUserIds(int channelId)
        {
            lock (_gate)
            {
                if (!_byChannel.TryGetValue(channelId, out var viewers)) return Array.Empty<string>();
                return viewers.Values
                    .Where(v => v.ViewerKey.StartsWith("u:", StringComparison.Ordinal))
                    .Select(v => v.ViewerKey[2..])
                    .Distinct()
                    .ToList();
            }
        }

        public ViewerListDto GetViewerList(int channelId, int maxNames = 200)
        {
            lock (_gate)
            {
                if (!_byChannel.TryGetValue(channelId, out var viewers))
                    return new ViewerListDto(0, new List<string>(), 0);

                var unique = viewers.Values.GroupBy(v => v.ViewerKey).Select(g => g.First()).ToList();
                var names = unique
                    .Where(v => !string.IsNullOrEmpty(v.DisplayName))
                    .Select(v => v.DisplayName!)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .Take(maxNames)
                    .ToList();
                var anonymous = unique.Count(v => string.IsNullOrEmpty(v.DisplayName));

                return new ViewerListDto(unique.Count, names, anonymous);
            }
        }

        // Canales cuyo conteo cambió desde la última vez (y limpia la marca).
        public IReadOnlyList<int> DrainDirty()
        {
            lock (_gate)
            {
                var list = _dirty.ToList();
                _dirty.Clear();
                return list;
            }
        }

        // Conteo actual de todos los canales con al menos un viewer.
        public Dictionary<int, int> SnapshotCounts()
        {
            lock (_gate)
            {
                return _byChannel.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.Values.Select(v => v.ViewerKey).Distinct().Count());
            }
        }

        private void RemoveUnsafe(int channelId, string connectionId)
        {
            _channelByConnection.Remove(connectionId);
            if (_byChannel.TryGetValue(channelId, out var viewers) && viewers.Remove(connectionId))
            {
                _dirty.Add(channelId);
                if (viewers.Count == 0) _byChannel.Remove(channelId);
            }
        }
    }

    public record ViewerListDto(int Count, List<string> Names, int AnonymousCount);
}
