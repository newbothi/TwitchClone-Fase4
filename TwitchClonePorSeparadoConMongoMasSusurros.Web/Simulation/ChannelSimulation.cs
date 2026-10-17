using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Chat;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Hubs;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Realtime;
using ChannelRoleType = TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models.ChannelRoleType;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Simulation
{
    // Estado de la simulación de chat de un canal (lo que muestra el panel).
    public sealed record ChatSimulationStatus(
        int ChannelId,
        string ChannelName,
        bool Running,
        bool IsLive,
        int TargetViewers,
        int PresentBots,
        int ActivityPercent,
        int MessagesPerMinute,
        int Sent,
        int Rejected,
        int HeldByAutoMod,
        int Follows,
        int Donations,
        int AiQueue,
        bool AiEnabled,
        bool NotEnoughBots,
        string? TopRejection,
        string? Wave,
        string? LastError,
        DateTimeOffset StartedAt);

    // Datos del canal que usa la simulación (se refrescan cada minuto).
    internal sealed record SimChannelInfo(string Name, string OwnerUserId, string OwnerDisplayName, string? Category, bool IsLive, IReadOnlyList<string> Emotes);

    // ── Eventos que recibe el bucle de la simulación ───────────────────────
    // Todo el estado vive en UN hilo lógico (el bucle); el resto del sistema solo
    // le manda eventos. Así no hacen falta locks y no hay carreras.
    internal abstract record SimEvent;
    internal sealed record ChatEffectsEvent(IReadOnlyList<ChatEffect> Effects) : SimEvent;
    internal sealed record LiveStateEvent(bool IsLive) : SimEvent;
    internal sealed record SetTargetEvent(int Viewers, double Activity) : SimEvent;
    internal sealed record SendResultEvent(string BotId, bool Ok, bool Held, string? ErrorCode, int? RetryAfterSeconds) : SimEvent;
    internal sealed record FollowResultEvent(string BotId, bool Following, bool IsNew) : SimEvent;
    internal sealed record DonationResultEvent(string BotId, bool Ok, int Amount) : SimEvent;
    internal sealed record AiBatchEvent(IReadOnlyList<string> Lines) : SimEvent;
    internal sealed record InfoEvent(SimChannelInfo Info) : SimEvent;
    internal sealed record AcceptRaidersEvent(IReadOnlyList<BotAccount> Bots, string SourceName) : SimEvent;
    internal sealed record TakeRaidersEvent(double Fraction, TaskCompletionSource<List<BotAccount>> Result) : SimEvent;
    internal sealed record RaidIncomingEvent(string SourceName, int Viewers) : SimEvent;

    // Un bot dentro del canal.
    internal sealed class SimBot
    {
        public SimBot(BotAccount account, BotPersona persona, double ratePerMinute)
        {
            Account = account;
            Persona = persona;
            RatePerMinute = ratePerMinute;
        }

        public BotAccount Account { get; }
        public BotPersona Persona { get; }
        public double RatePerMinute { get; }
        public int Index;                     // posición en la lista (para sacar al azar en O(1))
        public DateTimeOffset NextAllowedAt;  // modo lento / límite / "está escribiendo"
        public DateTimeOffset MutedUntil;     // timeout
        public bool Banned;
        public bool Following;
        public bool FollowPending;
        public bool DonationTried;
        public string? RaidSource;
    }

    // Acciones programadas para un momento futuro (respuestas, votos, follows...).
    internal abstract record SimAction(SimBot Bot);
    internal sealed record SendAction(SimBot Bot, string Text, string? ReplyToId) : SimAction(Bot);
    internal sealed record VoteAction(SimBot Bot, string PollId, int Option) : SimAction(Bot);
    internal sealed record FollowAction(SimBot Bot) : SimAction(Bot);

    // Simula la audiencia de UN canal: bots que entran y salen (cuentan en el contador
    // de viewers), escriben según su personalidad, reaccionan a lo que pasa en el chat,
    // votan encuestas, siguen el canal, donan puntos y se van de raid.
    //
    // Los bots no tienen atajos: cada mensaje pasa por IChatService.SendAsync igual que
    // el de una persona, así que el modo lento, los bans, AutoMod y los filtros los afectan.
    internal sealed class ChannelSimulation
    {
        private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(200);
        private const int MaxInFlight = 24;               // envíos simultáneos como máximo
        private const double MaxMessagesPerSecond = 12;   // tope del chat de un canal (los chats gigantes de Twitch rondan esto)
        private const int RecentCapacity = 40;

        private static readonly Regex MentionRegex = new(@"@([\p{L}\p{N}_.\-]{2,25})", RegexOptions.Compiled);

        private readonly SimulationManager _manager;
        private readonly IServiceScopeFactory _scopes;
        private readonly PresenceTracker _presence;
        private readonly ChatEffectDispatcher _dispatcher;
        private readonly IHubContext<StreamHub> _hub;
        private readonly IBotAccountService _botAccounts;
        private readonly TimeProvider _time;
        private readonly ILogger _logger;
        private readonly Random _rng = new();
        private readonly Channel<SimEvent> _events = Channel.CreateUnbounded<SimEvent>(new UnboundedChannelOptions { SingleReader = true });
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _sendGate = new(6, 6);
        private readonly bool _aiEnabled;
        private Task _loop = Task.CompletedTask;
        private int _inFlight;

        // ── Estado del bucle (solo se toca desde el bucle) ───────────────
        private readonly Dictionary<string, SimBot> _present = new();
        private readonly List<SimBot> _presentList = new();
        private readonly Dictionary<string, SimBot> _byName = new(StringComparer.OrdinalIgnoreCase);
        private readonly PriorityQueue<SimAction, DateTimeOffset> _scheduled = new();
        private readonly List<(string Id, string UserId, string Name, string Text, bool IsBot)> _recent = new();
        private readonly List<(DateTimeOffset At, string Key, string Text, string UserId)> _shortRecent = new();
        private readonly Queue<string> _aiLines = new();
        private readonly HashSet<string> _seenPolls = new();
        private readonly HashSet<string> _endedPolls = new();
        private readonly Queue<DateTimeOffset> _sentTimes = new();
        private readonly Dictionary<string, int> _rejections = new();
        private SimChannelInfo _info;
        private ContentContext _ctx;
        private ContentTopic _topic;
        private ChatSettingsDto _settings;
        private int _target;
        private double _activity;
        private bool _isLive;
        private bool _drainAfterStream;
        private bool _notEnoughBots;
        private double _joinBudget, _leaveBudget, _churnBudget, _sendBudget = MaxMessagesPerSecond;
        private DateTimeOffset _waveUntil, _nextWaveAt;
        private string? _waveToken;
        private DateTimeOffset _lastAiRequest, _lastInfoRefresh, _lastStatus;
        private bool _aiPending, _infoPending;
        private int _sent, _rejected, _held, _follows, _donations;
        private string? _lastError;
        private readonly DateTimeOffset _startedAt;

        private volatile ChatSimulationStatus _status;

        public ChannelSimulation(
            int channelId, SimChannelInfo info, ChatSettingsDto settings, int targetViewers, double activity, bool aiEnabled,
            SimulationManager manager, IServiceScopeFactory scopes, PresenceTracker presence, ChatEffectDispatcher dispatcher,
            IHubContext<StreamHub> hub, IBotAccountService botAccounts, TimeProvider time, ILogger logger)
        {
            ChannelId = channelId;
            _info = info;
            _settings = settings;
            _target = Math.Max(0, targetViewers);
            _activity = activity;
            _aiEnabled = aiEnabled;
            _manager = manager;
            _scopes = scopes;
            _presence = presence;
            _dispatcher = dispatcher;
            _hub = hub;
            _botAccounts = botAccounts;
            _time = time;
            _logger = logger;
            _isLive = info.IsLive;
            _startedAt = time.GetUtcNow();
            _lastInfoRefresh = _startedAt;
            _ctx = BuildContext(info);
            _topic = ChatContentLibrary.TopicFor(info.Category);
            ScheduleNextWave(_startedAt);
            _status = BuildStatus(running: true);
        }

        public int ChannelId { get; }
        public ChatSimulationStatus Status => _status;
        public bool IsRunning => !_loop.IsCompleted;
        public double Activity => _status.ActivityPercent / 100.0;
        public int TargetViewers => _status.TargetViewers;

        public void Start() => _loop = Task.Run(() => RunAsync(_cts.Token));

        public async Task StopAsync()
        {
            _cts.Cancel();
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        }

        public void Post(SimEvent e)
        {
            if (!_events.Writer.TryWrite(e)) Reject(e);
        }

        public void SetTarget(int viewers, double activity) => Post(new SetTargetEvent(viewers, activity));

        // Saca una parte de los bots (para llevarlos de raid a otro canal). Siguen "alquilados".
        public async Task<List<BotAccount>> TakeRaidersAsync(double fraction)
        {
            var tcs = new TaskCompletionSource<List<BotAccount>>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(new TakeRaidersEvent(fraction, tcs));
            try { return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { return new List<BotAccount>(); }
        }

        // Eventos que llegan cuando la simulación ya terminó: no se pierden bots ni quedan tareas colgadas.
        private void Reject(SimEvent e)
        {
            switch (e)
            {
                case TakeRaidersEvent t: t.Result.TrySetResult(new List<BotAccount>()); break;
                case AcceptRaidersEvent a: _manager.ReturnBots(a.Bots); break;
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  Bucle principal
        // ════════════════════════════════════════════════════════════════

        private async Task RunAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TickInterval, _time);
            var last = _time.GetTimestamp();
            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    var nowTs = _time.GetTimestamp();
                    var dt = Math.Min(_time.GetElapsedTime(last, nowTs).TotalSeconds, 2.0);
                    last = nowTs;
                    var now = _time.GetUtcNow();

                    try
                    {
                        DrainEvents(now);
                        UpdatePresence(now, dt);
                        GenerateMessages(now, dt);
                        RunScheduled(now);
                        MaybeChannelEvents(now, dt);
                        MaybeRequestAi(now);
                        MaybeRefreshInfo(now);
                        if (now - _lastStatus >= TimeSpan.FromSeconds(1))
                        {
                            _lastStatus = now;
                            _status = BuildStatus(running: true);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _lastError = ex.Message;
                        _logger.LogWarning(ex, "Error en la simulación del canal {ChannelId}.", ChannelId);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _logger.LogError(ex, "La simulación del canal {ChannelId} se detuvo por un error.", ChannelId);
            }
            finally
            {
                foreach (var bot in _presentList) _presence.Leave(ConnectionId(bot.Account));
                _manager.ReturnBots(_presentList.Select(b => b.Account).ToList());
                _present.Clear();
                _presentList.Clear();
                _byName.Clear();

                _events.Writer.TryComplete();
                while (_events.Reader.TryRead(out var pending)) Reject(pending);

                _status = BuildStatus(running: false);
            }
        }

        private string ConnectionId(BotAccount bot) => PresenceTracker.BotConnectionId(ChannelId, bot.Id);

        private void DrainEvents(DateTimeOffset now)
        {
            while (_events.Reader.TryRead(out var ev))
            {
                switch (ev)
                {
                    case ChatEffectsEvent e:
                        foreach (var effect in e.Effects) HandleEffect(effect, now);
                        break;

                    case LiveStateEvent e:
                        HandleLiveState(e.IsLive, now);
                        break;

                    case SetTargetEvent e:
                        _target = Math.Max(0, e.Viewers);
                        _activity = Math.Clamp(e.Activity, 0.1, 5);
                        break;

                    case SendResultEvent e:
                        HandleSendResult(e, now);
                        break;

                    case FollowResultEvent e:
                        if (_present.TryGetValue(e.BotId, out var follower))
                        {
                            follower.FollowPending = false;
                            follower.Following = e.Following;
                        }
                        if (e.IsNew) _follows++;
                        break;

                    case DonationResultEvent e:
                        if (e.Ok) _donations++;
                        break;

                    case AiBatchEvent e:
                        _aiPending = false;
                        foreach (var line in e.Lines) _aiLines.Enqueue(line);
                        while (_aiLines.Count > 30) _aiLines.Dequeue();
                        break;

                    case InfoEvent e:
                        _infoPending = false;
                        _info = e.Info;
                        _ctx = BuildContext(e.Info);
                        _topic = ChatContentLibrary.TopicFor(e.Info.Category);
                        break;

                    case AcceptRaidersEvent e:
                        AcceptRaiders(e.Bots, e.SourceName, now);
                        break;

                    case TakeRaidersEvent e:
                        e.Result.TrySetResult(TakeRaiders(e.Fraction));
                        break;

                    case RaidIncomingEvent e:
                        // Raid desde un canal sin simulación: los bots de aquí dan la bienvenida.
                        Burst(2 + _rng.Next(5), 1, 10, now, _ => ChatContentLibrary.RaidWelcomeLine(e.SourceName, _ctx, _rng));
                        StartWave("PogChamp", now, 6);
                        break;
                }
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  Presencia: entran, se quedan un rato y se van
        // ════════════════════════════════════════════════════════════════

        private int EffectiveTarget(DateTimeOffset now)
        {
            var baseTarget = _drainAfterStream ? (int)Math.Ceiling(_target * 0.1) : _target;
            // Pequeña oscilación natural (la audiencia nunca está quieta).
            var t = (now - _startedAt).TotalSeconds;
            var wobble = 1 + 0.06 * Math.Sin(2 * Math.PI * t / 420) + 0.03 * Math.Sin(2 * Math.PI * t / 97);
            return Math.Max(0, (int)Math.Round(baseTarget * wobble));
        }

        private void UpdatePresence(DateTimeOffset now, double dt)
        {
            var target = EffectiveTarget(now);

            // Rotación: en promedio cada viewer se queda ~10 minutos y lo reemplaza otro.
            _churnBudget += _present.Count / 600.0 * dt;
            while (_churnBudget >= 1 && _presentList.Count > 0)
            {
                _churnBudget -= 1;
                var leaving = _presentList[_rng.Next(_presentList.Count)];
                if (leaving.RaidSource == null || _rng.NextDouble() < 0.5) Leave(leaving);
            }

            var diff = target - _present.Count;
            if (diff > 0)
            {
                // Llegan rápido al empezar (≈ 15 s hasta el objetivo) y de a poco después.
                _joinBudget = Math.Min(_joinBudget + Math.Max(2.0, _target / 15.0) * dt, 200);
                var n = Math.Min(diff, (int)_joinBudget);
                if (n > 0)
                {
                    _joinBudget -= n;
                    var bots = _manager.RentBots(n);
                    foreach (var bot in bots) Join(bot, now, raidSource: null);
                    _notEnoughBots = bots.Count < n;
                }
            }
            else
            {
                _joinBudget = 0;
                _notEnoughBots = false;
                if (diff < 0)
                {
                    // Cuando termina el stream se van rápido; si baja el objetivo, de a poco.
                    var speed = Math.Max(1.0, _present.Count / (_drainAfterStream ? 15.0 : 60.0));
                    _leaveBudget = Math.Min(_leaveBudget + speed * dt, 200);
                    var n = Math.Min(-diff, (int)_leaveBudget);
                    _leaveBudget -= n;
                    for (var i = 0; i < n && _presentList.Count > 0; i++)
                        Leave(_presentList[_rng.Next(_presentList.Count)]);
                }
                else _leaveBudget = 0;
            }
        }

        private SimBot Join(BotAccount account, DateTimeOffset now, string? raidSource)
        {
            var (persona, rate) = PersonaFor(account);
            var bot = new SimBot(account, persona, rate)
            {
                // Nadie escribe apenas entra: primero mira un rato.
                NextAllowedAt = now.AddSeconds(3 + _rng.NextDouble() * 20),
                RaidSource = raidSource,
                Index = _presentList.Count
            };

            _present[account.Id] = bot;
            _presentList.Add(bot);
            _byName[account.DisplayName] = bot;
            _presence.Join(ChannelId, ConnectionId(account), PresenceTracker.UserKey(account.Id), account.DisplayName, isSimulated: true);

            if (raidSource == null && !_settings.EmoteOnly && _rng.NextDouble() < 0.07)
                Schedule(new SendAction(bot, ChatContentLibrary.Greeting(_ctx, _rng), null), now.AddSeconds(2 + _rng.NextDouble() * 12));

            return bot;
        }

        private void Leave(SimBot bot, bool returnToPool = true)
        {
            if (!_present.Remove(bot.Account.Id)) return;

            var last = _presentList[^1];
            _presentList[bot.Index] = last;
            last.Index = bot.Index;
            _presentList.RemoveAt(_presentList.Count - 1);

            if (_byName.TryGetValue(bot.Account.DisplayName, out var named) && ReferenceEquals(named, bot))
                _byName.Remove(bot.Account.DisplayName);

            _presence.Leave(ConnectionId(bot.Account));
            if (returnToPool) _manager.ReturnBots(new[] { bot.Account });
        }

        // La personalidad depende del número del bot: el mismo bot es siempre el mismo
        // tipo de viewer, en cualquier canal (como en la vida real).
        private static (BotPersona Persona, double RatePerMinute) PersonaFor(BotAccount bot)
        {
            var rng = new Random(bot.Number * 104729 + 17);
            var roll = rng.NextDouble();
            var persona = roll switch
            {
                < 0.55 => BotPersona.Lurker,
                < 0.80 => BotPersona.Casual,
                < 0.88 => BotPersona.Hype,
                < 0.94 => BotPersona.Questioner,
                < 0.99 => BotPersona.EmoteSpammer,
                _ => BotPersona.Troll
            };
            var baseRate = persona switch
            {
                BotPersona.Lurker => 0.03,
                BotPersona.Casual => 0.6,
                BotPersona.Hype => 1.6,
                BotPersona.Questioner => 0.5,
                BotPersona.EmoteSpammer => 2.0,
                _ => 0.7
            };
            return (persona, baseRate * (0.5 + rng.NextDouble()));
        }

        // ════════════════════════════════════════════════════════════════
        //  Mensajes espontáneos
        // ════════════════════════════════════════════════════════════════

        private void GenerateMessages(DateTimeOffset now, double dt)
        {
            _sendBudget = Math.Min(MaxMessagesPerSecond, _sendBudget + MaxMessagesPerSecond * dt);
            if (_presentList.Count == 0) return;

            if (_isLive && now >= _nextWaveAt)
            {
                StartWave(ChatContentLibrary.RandomWaveToken(_ctx, _rng), now, 6 + _rng.NextDouble() * 8);
                ScheduleNextWave(now);
            }

            var waving = now < _waveUntil && _waveToken != null;
            // En chats grandes cada persona escribe menos (nadie alcanza a leer).
            var sizeFactor = 1.0 / (1.0 + _presentList.Count / 800.0);
            var liveFactor = _isLive ? 1.0 : 0.35;

            foreach (var bot in _presentList)
            {
                if (!CanTalk(bot, now)) continue;

                var rate = bot.RatePerMinute / 60.0 * _activity * sizeFactor * liveFactor;
                if (waving) rate *= bot.Persona switch { BotPersona.Lurker => 3, BotPersona.Questioner or BotPersona.Troll => 1, _ => 5 };
                if (_rng.NextDouble() >= rate * dt) continue;

                if (_sendBudget < 1 || Volatile.Read(ref _inFlight) >= MaxInFlight) break;

                var text = Compose(bot, waving);
                if (string.IsNullOrWhiteSpace(text)) continue;

                string? replyTo = null;
                if (!waving && _recent.Count > 3 && _rng.NextDouble() < 0.04)
                {
                    // De vez en cuando le responde a otro (hilos de respuesta como en Twitch).
                    var target = _recent[_rng.Next(Math.Max(0, _recent.Count - 10), _recent.Count)];
                    if (target.UserId != bot.Account.Id)
                    {
                        replyTo = target.Id;
                        text = "@" + target.Name + " " + text;
                    }
                }

                SendNow(bot, text, replyTo, now);
            }
        }

        private string? Compose(SimBot bot, bool waving)
        {
            if (_settings.EmoteOnly) return ChatContentLibrary.ChannelEmotesOnly(_ctx, _rng);

            if (waving && bot.Persona != BotPersona.Questioner && _rng.NextDouble() < 0.75)
                return ChatContentLibrary.WaveMessage(_waveToken!, _rng);

            if (bot.Persona is BotPersona.Casual or BotPersona.Questioner && _aiLines.Count > 0 && _rng.NextDouble() < 0.6)
                return _aiLines.Dequeue();

            return ChatContentLibrary.ForPersona(bot.Persona, _topic, _ctx, _rng);
        }

        private bool CanTalk(SimBot bot, DateTimeOffset now) =>
            !bot.Banned && bot.MutedUntil <= now && bot.NextAllowedAt <= now;

        private void SendNow(SimBot bot, string text, string? replyToId, DateTimeOffset now)
        {
            _sendBudget -= 1;
            Interlocked.Increment(ref _inFlight);

            // Los bots respetan el modo lento casi siempre (como la gente: a veces lo olvidan).
            bot.NextAllowedAt = _settings.SlowModeSeconds > 0 && _rng.NextDouble() < 0.9
                ? now.AddSeconds(_settings.SlowModeSeconds + _rng.Next(0, 5))
                : now.AddSeconds(1.5 + _rng.NextDouble() * 2);

            _ = SendAsync(bot.Account, text, replyToId);
        }

        private async Task SendAsync(BotAccount bot, string text, string? replyToId)
        {
            try
            {
                await _sendGate.WaitAsync(_cts.Token);
                try
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    var chat = scope.ServiceProvider.GetRequiredService<IChatService>();
                    var result = await chat.SendAsync(new ChatSender(bot.Id, IsAdmin: false, IsSimulated: true), ChannelId, text, replyToId, _cts.Token);
                    if (result.Effects.Count > 0) await _dispatcher.DispatchAsync(ChannelId, result.Effects);
                    Post(new SendResultEvent(bot.Id, result.Ok, result.HeldForReview, result.ErrorCode, result.RetryAfterSeconds));
                }
                finally
                {
                    _sendGate.Release();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Un bot no pudo enviar su mensaje en el canal {ChannelId}.", ChannelId);
                Post(new SendResultEvent(bot.Id, false, false, "error", null));
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private void HandleSendResult(SendResultEvent e, DateTimeOffset now)
        {
            if (e.Ok)
            {
                if (e.Held) _held++;
                else
                {
                    _sent++;
                    _sentTimes.Enqueue(now);
                }
                return;
            }

            _rejected++;
            var code = e.ErrorCode ?? "error";
            _rejections[code] = _rejections.GetValueOrDefault(code) + 1;

            if (!_present.TryGetValue(e.BotId, out var bot)) return;
            switch (code)
            {
                case "slow_mode":
                case "rate_limited":
                    bot.NextAllowedAt = now.AddSeconds((e.RetryAfterSeconds ?? 5) + _rng.Next(1, 6));
                    break;
                case "timed_out":
                    bot.MutedUntil = now.AddSeconds(e.RetryAfterSeconds ?? 60);
                    break;
                case "banned":
                case "suspended":
                case "user_not_found":
                    bot.Banned = true;
                    break;
                case "followers_only":
                    bot.NextAllowedAt = now.AddSeconds(30 + _rng.Next(60));
                    // Como la gente: algunos siguen el canal para poder escribir.
                    if (!bot.Following && !bot.FollowPending && _rng.NextDouble() < 0.35)
                    {
                        bot.FollowPending = true;
                        Schedule(new FollowAction(bot), now.AddSeconds(2 + _rng.Next(10)));
                    }
                    break;
                case "emote_only":
                    bot.NextAllowedAt = now.AddSeconds(20 + _rng.Next(20));
                    break;
                case "channel_not_found":
                    _cts.Cancel();
                    break;
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  Reacciones a lo que pasa en el chat
        // ════════════════════════════════════════════════════════════════

        private void HandleEffect(ChatEffect effect, DateTimeOffset now)
        {
            switch (effect)
            {
                case BroadcastMessageEffect b when b.Message.ChannelId == ChannelId:
                {
                    var m = b.Message;
                    var text = m.PlainText();
                    var isBot = _botAccounts.IsBot(m.UserId);
                    AddRecent(m.Id, m.UserId, m.DisplayName, text, isBot);
                    if (!isBot) ReactToHuman(m, text, now);
                    TrackMomentum(text, m.UserId, now);
                    break;
                }

                case UserSanctionedEffect s:
                    if (_present.TryGetValue(s.UserId, out var sanctioned))
                    {
                        if (s.IsBan) sanctioned.Banned = true;
                        else sanctioned.MutedUntil = now.AddSeconds(Math.Max(1, s.Seconds));
                    }
                    else if (!_botAccounts.IsBot(s.UserId) && _rng.NextDouble() < 0.7)
                    {
                        Burst(1 + _rng.Next(4), 1, 6, now, _ => ChatContentLibrary.SanctionReaction(_rng));
                    }
                    break;

                case ChatClearedEffect:
                    Burst(1 + _rng.Next(4), 1, 5, now, _ => ChatContentLibrary.ClearReaction(_rng));
                    break;

                case SettingsChangedEffect s:
                    _settings = s.Settings;
                    break;

                case RoleChangedEffect r when r.Role != ChannelRoleType.None && !_botAccounts.IsBot(r.UserId):
                    Burst(1 + _rng.Next(3), 1, 6, now, _ => ChatContentLibrary.NewModReaction(r.DisplayName, _ctx, _rng));
                    break;

                case PollEffect p:
                    HandlePoll(p.Poll, now);
                    break;

                case RaidEffect r when r.SourceChannelId == ChannelId:
                    // Este canal se va de raid: el chat se emociona antes de partir.
                    Burst(3 + _rng.Next(6), 0.5, 8, now, _ => ChatContentLibrary.RaidOutgoingLine(r.TargetName, _ctx, _rng));
                    break;
            }
        }

        private static bool IsGreeting(string normalized) =>
            Regex.IsMatch(normalized, @"^(hola+|holi+|buenas+|wena+s?|hello|hi|hey|o/|buen(os|as) (dias|tardes|noches))\b");

        private void ReactToHuman(ChatMessageDto m, string text, DateTimeOffset now)
        {
            if (_presentList.Count == 0 || _settings.EmoteOnly) return;

            var normalized = ChatContentLibrary.Normalize(text);
            var isOwner = m.UserId == _info.OwnerUserId;
            var crowd = Math.Clamp(_presentList.Count / 50.0, 0.5, 4.0); // más gente → más reacciones

            // 1) Lo nombraron: el bot responde (con "responder", como en Twitch).
            var answeredMention = false;
            foreach (Match match in MentionRegex.Matches(text))
            {
                if (_byName.TryGetValue(match.Groups[1].Value, out var mentioned) && !mentioned.Banned && _rng.NextDouble() < 0.85)
                {
                    Schedule(new SendAction(mentioned, ChatContentLibrary.ReplyMention(m.DisplayName, _ctx, _rng), m.Id), now.AddSeconds(2 + _rng.NextDouble() * 6));
                    answeredMention = true;
                }
            }

            // 2) Saludo
            if (IsGreeting(normalized))
            {
                if (isOwner)
                    Burst((int)(3 + crowd * 2), 1, 8, now, _ => ChatContentLibrary.Greeting(_ctx, _rng));
                else
                    Burst(1 + _rng.Next((int)Math.Ceiling(crowd)), 1, 7, now, _ => ChatContentLibrary.GreetBackTo(m.DisplayName, _ctx, _rng));
                return;
            }

            // 3) Pregunta
            if (text.TrimEnd().EndsWith('?'))
            {
                if (isOwner)
                    Burst((int)(2 + crowd * 2), 1.5, 9, now, _ => ChatContentLibrary.Answer(_ctx, _rng));
                else if (!answeredMention && _rng.NextDouble() < 0.5)
                    Burst(1, 2, 9, now, bot => ChatContentLibrary.ReplyQuestion(m.DisplayName, _ctx, _rng), replyToId: m.Id);
                return;
            }

            // 4) El streamer dice algo: el chat reacciona.
            if (isOwner)
            {
                if (_rng.NextDouble() < 0.6) Burst(1 + (int)(_rng.NextDouble() * crowd * 2), 1, 7, now, _ => ChatContentLibrary.StreamerReaction(_ctx, _rng));
                return;
            }

            // 5) Risas / hype: algunos se suman.
            if (normalized.Contains("jaja") || normalized.Contains("kekw") || normalized.Contains("lul") || normalized == "gg" || normalized.Contains("pog"))
            {
                var echo = normalized.Contains("jaja") ? "jajaja" : text.Trim().Split(' ')[0];
                Burst(1 + _rng.Next(3), 0.8, 5, now, _ => ChatContentLibrary.WaveMessage(echo, _rng));
                return;
            }

            // 6) Cualquier otra cosa: a veces alguien comenta.
            if (!answeredMention && _rng.NextDouble() < 0.12)
                Burst(1, 2, 8, now, _ => ChatContentLibrary.Casual(_topic, _ctx, _rng));
        }

        // Si 3 personas distintas escriben lo mismo en 10 s, arranca una "ola" (todos se suman).
        private void TrackMomentum(string text, string userId, DateTimeOffset now)
        {
            _shortRecent.RemoveAll(x => now - x.At > TimeSpan.FromSeconds(10));
            var trimmed = text.Trim();
            if (trimmed.Length is 0 or > 20) return;

            var key = ChatContentLibrary.Normalize(trimmed);
            _shortRecent.Add((now, key, trimmed, userId));
            if (now < _waveUntil) return;

            var distinctUsers = _shortRecent.Where(x => x.Key == key).Select(x => x.UserId).Distinct().Count();
            if (distinctUsers >= 3) StartWave(trimmed, now, 5 + _rng.NextDouble() * 4);
        }

        private void StartWave(string token, DateTimeOffset now, double seconds)
        {
            _waveToken = token;
            _waveUntil = now.AddSeconds(seconds);
        }

        private void ScheduleNextWave(DateTimeOffset now)
        {
            // Una ola cada ~2,5 min en promedio (distribución exponencial), más seguido con más actividad.
            var mean = 150.0 / Math.Max(0.2, _activity);
            var wait = Math.Max(40, -Math.Log(1 - _rng.NextDouble()) * mean);
            _nextWaveAt = now.AddSeconds(wait);
        }

        private void HandlePoll(PollDto poll, DateTimeOffset now)
        {
            if (poll.ChannelId != ChannelId || poll.Options.Count == 0) return;

            if (poll.Ended)
            {
                if (_seenPolls.Contains(poll.Id) && _endedPolls.Add(poll.Id))
                    Burst(1 + _rng.Next(4), 0.5, 5, now, _ => ChatContentLibrary.PollEndReaction(_rng));
                return;
            }

            if (!_seenPolls.Add(poll.Id)) return;

            // Preferencias del chat: una opción suele ser la favorita.
            var weights = poll.Options.Select(_ => 0.3 + _rng.NextDouble()).ToArray();
            weights[_rng.Next(weights.Length)] += 1.5;
            var turnout = 0.35 + _rng.NextDouble() * 0.4;
            var remaining = (poll.EndsAt - now.UtcDateTime).TotalSeconds;
            var window = Math.Clamp(remaining - 2, 3, 40);

            foreach (var bot in _presentList)
            {
                if (bot.Banned || _rng.NextDouble() >= turnout) continue;
                var option = WeightedPick(weights);
                // La mayoría vota en los primeros segundos.
                var delay = window * Math.Pow(_rng.NextDouble(), 1.8);
                Schedule(new VoteAction(bot, poll.Id, option), now.AddSeconds(0.5 + delay));
            }

            Burst(2 + _rng.Next(4), 2, 15, now, _ => ChatContentLibrary.PollReaction(poll.Options[WeightedPick(weights)].Text, _ctx, _rng));
        }

        private int WeightedPick(double[] weights)
        {
            var total = weights.Sum();
            var roll = _rng.NextDouble() * total;
            for (var i = 0; i < weights.Length; i++)
            {
                roll -= weights[i];
                if (roll <= 0) return i;
            }
            return weights.Length - 1;
        }

        private void HandleLiveState(bool isLive, DateTimeOffset now)
        {
            if (isLive == _isLive) return;

            if (_isLive && !isLive)
            {
                // Terminó el stream: se despiden y la mayoría se va.
                _drainAfterStream = true;
                Burst(2 + _rng.Next(6), 0.5, 10, now, _ => ChatContentLibrary.StreamEndLine(_ctx, _rng));
            }
            else
            {
                _drainAfterStream = false;
                Burst(3 + _rng.Next(6), 1, 12, now, _ => ChatContentLibrary.StreamStartLine(_ctx, _rng));
            }
            _isLive = isLive;
        }

        // Varios bots al azar dicen algo en los próximos segundos.
        private void Burst(int count, double minDelay, double maxDelay, DateTimeOffset now, Func<SimBot, string> text, string? replyToId = null)
        {
            if (_settings.EmoteOnly) return;
            for (var i = 0; i < count; i++)
            {
                var bot = PickTalker(now);
                if (bot == null) return;
                Schedule(new SendAction(bot, text(bot), replyToId), now.AddSeconds(minDelay + _rng.NextDouble() * (maxDelay - minDelay)));
            }
        }

        private SimBot? PickTalker(DateTimeOffset now)
        {
            if (_presentList.Count == 0) return null;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var bot = _presentList[_rng.Next(_presentList.Count)];
                if (bot.Banned || bot.MutedUntil > now) continue;
                // Los que casi no escriben a veces sí reaccionan, pero menos.
                if (bot.Persona == BotPersona.Lurker && _rng.NextDouble() < 0.7) continue;
                return bot;
            }
            return null;
        }

        private void AddRecent(string id, string userId, string name, string text, bool isBot)
        {
            _recent.Add((id, userId, name, text, isBot));
            if (_recent.Count > RecentCapacity) _recent.RemoveRange(0, _recent.Count - RecentCapacity);
        }

        // ════════════════════════════════════════════════════════════════
        //  Acciones programadas
        // ════════════════════════════════════════════════════════════════

        private void Schedule(SimAction action, DateTimeOffset at)
        {
            // Tope de seguridad (con 2.000 bots una encuesta programa ~1.000 votos).
            if (_scheduled.Count < 5000) _scheduled.Enqueue(action, at);
        }

        private bool IsPresent(SimBot bot) => _present.TryGetValue(bot.Account.Id, out var current) && ReferenceEquals(current, bot);

        private void RunScheduled(DateTimeOffset now)
        {
            while (_scheduled.TryPeek(out var action, out var due) && due <= now)
            {
                _scheduled.Dequeue();
                if (!IsPresent(action.Bot)) continue;

                switch (action)
                {
                    case SendAction s:
                        if (s.Bot.Banned || s.Bot.MutedUntil > now) break;
                        if (_settings.SlowModeSeconds > 0 && s.Bot.NextAllowedAt > now) break;
                        if (_sendBudget < 1 || Volatile.Read(ref _inFlight) >= MaxInFlight) break;
                        SendNow(s.Bot, s.Text, s.ReplyToId, now);
                        break;

                    case VoteAction v:
                        if (!v.Bot.Banned) _ = VoteAsync(v.Bot.Account, v.PollId, v.Option);
                        break;

                    case FollowAction f:
                        _ = FollowAsync(f.Bot.Account, _info.OwnerUserId);
                        break;
                }
            }
        }

        private async Task VoteAsync(BotAccount bot, string pollId, int option)
        {
            try
            {
                await _sendGate.WaitAsync(_cts.Token);
                try
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    var chat = scope.ServiceProvider.GetRequiredService<IChatService>();
                    var result = await chat.VotePollAsync(ChannelId, pollId, option, new ChatSender(bot.Id, false, true));
                    if (result.Effects.Count > 0) await _dispatcher.DispatchAsync(ChannelId, result.Effects);
                }
                finally { _sendGate.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogDebug(ex, "Voto simulado fallido en el canal {ChannelId}.", ChannelId); }
        }

        // ════════════════════════════════════════════════════════════════
        //  Follows y donaciones (alertas del stream)
        // ════════════════════════════════════════════════════════════════

        private void MaybeChannelEvents(DateTimeOffset now, double dt)
        {
            if (!_isLive || _presentList.Count == 0) return;
            var perHundred = _presentList.Count / 100.0 * _activity;

            // ~1 follow cada 2,5 min por cada 100 viewers.
            if (_rng.NextDouble() < perHundred / 150.0 * dt)
            {
                var bot = PickRandom(b => !b.Following && !b.FollowPending && !b.Banned);
                if (bot != null)
                {
                    bot.FollowPending = true;
                    _ = FollowAsync(bot.Account, _info.OwnerUserId);
                }
            }

            // ~1 donación cada 7 min por cada 100 viewers (si al bot le alcanzan los puntos).
            if (_rng.NextDouble() < perHundred / 420.0 * dt)
            {
                var bot = PickRandom(b => !b.DonationTried && !b.Banned);
                if (bot != null)
                {
                    bot.DonationTried = true;
                    int[] amounts = { 10, 20, 50, 50, 100, 100, 100, 200, 250, 500, 1000 };
                    var amount = amounts[_rng.Next(amounts.Length)];
                    _ = DonateAsync(bot.Account, amount, ChatContentLibrary.DonationMessage(_ctx, _rng));
                }
            }
        }

        private SimBot? PickRandom(Func<SimBot, bool> predicate)
        {
            for (var attempt = 0; attempt < 12 && _presentList.Count > 0; attempt++)
            {
                var bot = _presentList[_rng.Next(_presentList.Count)];
                if (predicate(bot)) return bot;
            }
            return null;
        }

        private async Task FollowAsync(BotAccount bot, string ownerUserId)
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var follows = scope.ServiceProvider.GetRequiredService<IFollowService>();
                if (await follows.IsFollowingAsync(bot.Id, ownerUserId))
                {
                    Post(new FollowResultEvent(bot.Id, Following: true, IsNew: false));
                    return;
                }

                await follows.FollowAsync(bot.Id, ownerUserId);
                Post(new FollowResultEvent(bot.Id, Following: true, IsNew: true));

                // Lo mismo que hace un follow real (notificación + alerta), sin frenar si Mongo no está.
                try
                {
                    await scope.ServiceProvider.GetRequiredService<INotificationService>()
                        .CreateNotificationAsync(ownerUserId, $"{bot.DisplayName} te empezó a seguir", "NewFollower", null, null);
                    await scope.ServiceProvider.GetRequiredService<IAlertEventService>()
                        .CreateAlertAsync(ChannelId.ToString(), "Follow", bot.DisplayName, string.Empty);
                }
                catch (Exception ex) { _logger.LogDebug(ex, "No se pudo guardar la notificación del follow simulado."); }

                await _hub.Clients.User(ownerUserId).SendAsync("NewFollower", bot.DisplayName);
                await _hub.Clients.Group(StreamHub.ChannelGroup(ChannelId)).SendAsync("FollowAlert", bot.DisplayName);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Follow simulado fallido en el canal {ChannelId}.", ChannelId);
                Post(new FollowResultEvent(bot.Id, Following: false, IsNew: false));
            }
        }

        private async Task DonateAsync(BotAccount bot, int amount, string message)
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var users = scope.ServiceProvider.GetRequiredService<IChatUserRepository>();
                if (!await users.TryDonatePointsAsync(bot.Id, ChannelId, amount, message))
                {
                    Post(new DonationResultEvent(bot.Id, false, amount));
                    return;
                }

                try
                {
                    await scope.ServiceProvider.GetRequiredService<IAlertEventService>()
                        .CreateAlertAsync(ChannelId.ToString(), "Donation", bot.DisplayName, message, amount);
                }
                catch (Exception ex) { _logger.LogDebug(ex, "No se pudo guardar la alerta de la donación simulada."); }

                await _hub.Clients.Group(StreamHub.ChannelGroup(ChannelId)).SendAsync("DonationAlert", bot.DisplayName, amount, message);
                Post(new DonationResultEvent(bot.Id, true, amount));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Donación simulada fallida en el canal {ChannelId}.", ChannelId);
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  Raids
        // ════════════════════════════════════════════════════════════════

        private List<BotAccount> TakeRaiders(double fraction)
        {
            var count = (int)Math.Round(_presentList.Count * Math.Clamp(fraction, 0, 1));
            var moved = new List<BotAccount>(count);
            for (var i = 0; i < count && _presentList.Count > 0; i++)
            {
                var bot = _presentList[_rng.Next(_presentList.Count)];
                Leave(bot, returnToPool: false);
                moved.Add(bot.Account);
            }
            _target = Math.Max(0, _target - moved.Count);
            return moved;
        }

        private void AcceptRaiders(IReadOnlyList<BotAccount> bots, string sourceName, DateTimeOffset now)
        {
            // Parte de los raiders se queda (el objetivo sube) y el resto se va de a poco.
            _target += (int)Math.Round(bots.Count * 0.6);
            foreach (var account in bots)
            {
                if (_present.ContainsKey(account.Id))
                {
                    _manager.ReturnBots(new[] { account });
                    continue;
                }
                var bot = Join(account, now, sourceName);
                if (!_settings.EmoteOnly && _rng.NextDouble() < 0.5)
                    Schedule(new SendAction(bot, ChatContentLibrary.Raid(sourceName, _ctx, _rng), null), now.AddSeconds(1 + _rng.NextDouble() * 14));
            }
            StartWave("PogChamp", now, 8);
        }

        // ════════════════════════════════════════════════════════════════
        //  IA y datos del canal
        // ════════════════════════════════════════════════════════════════

        private void MaybeRequestAi(DateTimeOffset now)
        {
            if (!_aiEnabled || _aiPending || _aiLines.Count >= 6 || _presentList.Count == 0) return;
            if (now - _lastAiRequest < TimeSpan.FromSeconds(15)) return;

            _aiPending = true;
            _lastAiRequest = now;
            var title = _info.Name;
            var category = _info.Category ?? "Charlando";
            var recent = _recent.TakeLast(15).Select(r => $"{r.Name}: {r.Text}").ToList();

            _ = Task.Run(async () =>
            {
                IReadOnlyList<string> lines = Array.Empty<string>();
                try
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    var ai = scope.ServiceProvider.GetRequiredService<IAiChatGenerator>();
                    var generated = await ai.GenerateAsync(title, category, recent, 10, _cts.Token);
                    lines = generated
                        .Select(l => l.Trim())
                        .Where(l => l.Length is > 0 and <= 200 && !l.StartsWith('/'))
                        .ToList();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "El generador de IA no respondió para el canal {ChannelId}.", ChannelId);
                }
                catch (OperationCanceledException) { }
                Post(new AiBatchEvent(lines));
            });
        }

        private void MaybeRefreshInfo(DateTimeOffset now)
        {
            if (_infoPending || now - _lastInfoRefresh < TimeSpan.FromMinutes(1)) return;
            _infoPending = true;
            _lastInfoRefresh = now;

            _ = Task.Run(async () =>
            {
                try
                {
                    var info = await _manager.LoadChannelInfoAsync(ChannelId);
                    if (info != null) Post(new InfoEvent(info));
                    else Post(new InfoEvent(_info));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "No se pudieron refrescar los datos del canal {ChannelId}.", ChannelId);
                    Post(new InfoEvent(_info));
                }
            });
        }

        private ContentContext BuildContext(SimChannelInfo info) => new()
        {
            Streamer = info.OwnerDisplayName,
            Category = info.Category ?? "el stream",
            ChannelEmotes = info.Emotes,
            RandomChatter = () =>
            {
                if (_recent.Count == 0) return null;
                var pick = _recent[_rng.Next(_recent.Count)];
                return pick.UserId == info.OwnerUserId ? null : pick.Name;
            }
        };

        private ChatSimulationStatus BuildStatus(bool running)
        {
            var now = _time.GetUtcNow();
            while (_sentTimes.Count > 0 && now - _sentTimes.Peek() > TimeSpan.FromMinutes(1)) _sentTimes.Dequeue();
            KeyValuePair<string, int>? top = _rejections.Count == 0 ? null : _rejections.MaxBy(kv => kv.Value);

            return new ChatSimulationStatus(
                ChannelId,
                _info.Name,
                running,
                _isLive,
                _target,
                _presentList.Count,
                (int)Math.Round(_activity * 100),
                _sentTimes.Count,
                _sent,
                _rejected,
                _held,
                _follows,
                _donations,
                _aiLines.Count,
                _aiEnabled,
                _notEnoughBots,
                top == null ? null : $"{top.Value.Key} ({top.Value.Value})",
                now < _waveUntil ? _waveToken : null,
                _lastError,
                _startedAt);
        }
    }
}
