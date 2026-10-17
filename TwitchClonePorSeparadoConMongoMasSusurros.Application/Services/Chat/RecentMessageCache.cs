using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Application.Services.Chat
{
    // Últimos mensajes de cada canal en memoria (singleton).
    // Sirve para responder/fijar un mensaje recién enviado (que quizás todavía no se
    // escribió en Mongo, porque la escritura es por lotes) y para que el simulador
    // de bots sepa de qué se está hablando.
    public sealed class RecentMessageCache
    {
        private const int MaxPerChannel = 300;

        private readonly object _gate = new();
        private readonly Dictionary<int, LinkedList<ChatMessageDto>> _byChannel = new();

        public void Add(ChatMessageDto message)
        {
            lock (_gate)
            {
                if (!_byChannel.TryGetValue(message.ChannelId, out var list))
                {
                    list = new LinkedList<ChatMessageDto>();
                    _byChannel[message.ChannelId] = list;
                }
                list.AddLast(message);
                while (list.Count > MaxPerChannel) list.RemoveFirst();
            }
        }

        public ChatMessageDto? Find(int channelId, string messageId)
        {
            lock (_gate)
            {
                return _byChannel.TryGetValue(channelId, out var list)
                    ? list.FirstOrDefault(m => m.Id == messageId)
                    : null;
            }
        }

        public IReadOnlyList<ChatMessageDto> GetRecent(int channelId, int count)
        {
            lock (_gate)
            {
                if (!_byChannel.TryGetValue(channelId, out var list)) return Array.Empty<ChatMessageDto>();
                return list.Skip(Math.Max(0, list.Count - count)).ToList();
            }
        }

        public void Remove(int channelId, string messageId)
        {
            lock (_gate)
            {
                if (!_byChannel.TryGetValue(channelId, out var list)) return;
                var node = list.First;
                while (node != null)
                {
                    var next = node.Next;
                    if (node.Value.Id == messageId) list.Remove(node);
                    node = next;
                }
            }
        }

        public void RemoveUser(int channelId, string userId)
        {
            lock (_gate)
            {
                if (!_byChannel.TryGetValue(channelId, out var list)) return;
                var node = list.First;
                while (node != null)
                {
                    var next = node.Next;
                    if (node.Value.UserId == userId) list.Remove(node);
                    node = next;
                }
            }
        }

        public void Clear(int channelId)
        {
            lock (_gate) _byChannel.Remove(channelId);
        }
    }
}
