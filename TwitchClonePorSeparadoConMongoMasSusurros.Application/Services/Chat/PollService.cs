using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Application.Services.Chat
{
    // Encuestas en vivo, una activa por canal (singleton en memoria).
    // Los votos no se difunden uno por uno (con 500 bots votando sería una lluvia de
    // mensajes): se marcan como "cambiadas" y el PollTicker las difunde 1 vez por segundo.
    public sealed class PollService : IPollService
    {
        public const int MinOptions = 2;
        public const int MaxOptions = 5;

        private readonly TimeProvider _time;
        private readonly object _gate = new();
        private readonly Dictionary<int, PollState> _active = new();
        private readonly HashSet<int> _changed = new();

        public PollService(TimeProvider time) => _time = time;

        public PollDto? GetActive(int channelId)
        {
            lock (_gate) return _active.TryGetValue(channelId, out var s) ? s.ToDto() : null;
        }

        public (PollDto? Poll, string? Error) Start(int channelId, string question, IReadOnlyList<string> options, TimeSpan duration, string createdBy)
        {
            question = (question ?? string.Empty).Trim();
            var cleanOptions = options.Select(o => o.Trim()).Where(o => o.Length > 0).Distinct().ToList();

            if (question.Length is 0 or > 120) return (null, "La pregunta debe tener entre 1 y 120 caracteres.");
            if (cleanOptions.Count < MinOptions || cleanOptions.Count > MaxOptions) return (null, $"La encuesta necesita entre {MinOptions} y {MaxOptions} opciones distintas.");
            if (cleanOptions.Any(o => o.Length > 40)) return (null, "Cada opción puede tener hasta 40 caracteres.");
            duration = TimeSpan.FromSeconds(Math.Clamp(duration.TotalSeconds, 15, 1800));

            lock (_gate)
            {
                if (_active.TryGetValue(channelId, out var existing) && !existing.Ended)
                    return (null, "Ya hay una encuesta activa. Termínala con /endpoll.");

                var state = new PollState
                {
                    Id = Guid.NewGuid().ToString("N")[..12],
                    ChannelId = channelId,
                    Question = question,
                    Options = cleanOptions,
                    EndsAt = _time.GetUtcNow().UtcDateTime.Add(duration),
                    CreatedBy = createdBy
                };
                _active[channelId] = state;
                return (state.ToDto(), null);
            }
        }

        public (PollDto? Poll, string? Error) Vote(int channelId, string pollId, string userId, int optionIndex)
        {
            lock (_gate)
            {
                if (!_active.TryGetValue(channelId, out var state) || state.Id != pollId || state.Ended)
                    return (null, "Esa encuesta ya terminó.");
                if (optionIndex < 0 || optionIndex >= state.Options.Count)
                    return (null, "Opción inválida.");

                state.Votes[userId] = optionIndex;
                _changed.Add(channelId);
                return (state.ToDto(), null);
            }
        }

        public PollDto? End(int channelId)
        {
            lock (_gate)
            {
                if (!_active.TryGetValue(channelId, out var state) || state.Ended) return null;
                state.Ended = true;
                state.EndsAt = _time.GetUtcNow().UtcDateTime;
                _changed.Remove(channelId);
                return state.ToDto();
            }
        }

        public IReadOnlyList<PollDto> DrainChanged()
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var result = new List<PollDto>();

            lock (_gate)
            {
                foreach (var state in _active.Values.Where(s => !s.Ended && s.EndsAt <= now))
                {
                    state.Ended = true;
                    _changed.Add(state.ChannelId);
                }

                foreach (var channelId in _changed)
                    if (_active.TryGetValue(channelId, out var state)) result.Add(state.ToDto());
                _changed.Clear();

                // Las encuestas terminadas se muestran un rato y después se olvidan.
                foreach (var key in _active.Where(kv => kv.Value.Ended && now - kv.Value.EndsAt > TimeSpan.FromMinutes(2)).Select(kv => kv.Key).ToList())
                    _active.Remove(key);
            }
            return result;
        }

        private sealed class PollState
        {
            public string Id = string.Empty;
            public int ChannelId;
            public string Question = string.Empty;
            public List<string> Options = new();
            public Dictionary<string, int> Votes = new();
            public DateTime EndsAt;
            public bool Ended;
            public string CreatedBy = string.Empty;

            public PollDto ToDto()
            {
                var counts = new int[Options.Count];
                foreach (var v in Votes.Values) counts[v]++;
                return new PollDto
                {
                    Id = Id,
                    ChannelId = ChannelId,
                    Question = Question,
                    Options = Options.Select((o, i) => new PollOptionDto { Text = o, Votes = counts[i] }).ToList(),
                    TotalVotes = Votes.Count,
                    EndsAt = DateTime.SpecifyKind(EndsAt, DateTimeKind.Utc),
                    Ended = Ended,
                    CreatedBy = CreatedBy
                };
            }
        }
    }
}
