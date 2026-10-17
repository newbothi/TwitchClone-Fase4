using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Application.Services.Chat
{
    // Mensajes retenidos por AutoMod esperando que un moderador los apruebe o rechace.
    // En memoria (singleton): si nadie los revisa en 10 minutos se descartan, igual que en Twitch.
    public sealed class AutoModQueue
    {
        private static readonly TimeSpan Expiration = TimeSpan.FromMinutes(10);

        private readonly object _gate = new();
        private readonly Dictionary<string, (AutoModHeldDto Held, ChatMessage Pending)> _items = new();

        public AutoModHeldDto Hold(int channelId, ChatMessage pending, string category, string reason, DateTime nowUtc)
        {
            var held = new AutoModHeldDto
            {
                Id = Guid.NewGuid().ToString("N"),
                ChannelId = channelId,
                UserId = pending.UserId,
                DisplayName = pending.UserName,
                Text = pending.Content,
                Category = category,
                Reason = reason,
                HeldAt = nowUtc
            };

            lock (_gate)
            {
                Purge(nowUtc);
                _items[held.Id] = (held, pending);
            }
            return held;
        }

        public (AutoModHeldDto Held, ChatMessage Pending)? Take(int channelId, string heldId, DateTime nowUtc)
        {
            lock (_gate)
            {
                Purge(nowUtc);
                if (!_items.TryGetValue(heldId, out var item) || item.Held.ChannelId != channelId) return null;
                _items.Remove(heldId);
                return item;
            }
        }

        public IReadOnlyList<AutoModHeldDto> List(int channelId, DateTime nowUtc)
        {
            lock (_gate)
            {
                Purge(nowUtc);
                return _items.Values.Where(i => i.Held.ChannelId == channelId)
                    .Select(i => i.Held).OrderBy(h => h.HeldAt).ToList();
            }
        }

        private void Purge(DateTime nowUtc)
        {
            foreach (var key in _items.Where(kv => nowUtc - kv.Value.Held.HeldAt > Expiration).Select(kv => kv.Key).ToList())
                _items.Remove(key);
        }
    }
}
