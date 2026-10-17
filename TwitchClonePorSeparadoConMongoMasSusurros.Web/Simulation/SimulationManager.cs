using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Chat;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Hubs;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Realtime;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Simulation
{
    // Tarea larga lanzada desde el panel (crear bots, simulación completa).
    public sealed class SimulationJob
    {
        public SimulationJob(string name, DateTimeOffset startedAt)
        {
            Name = name;
            StartedAt = startedAt;
            Message = "Iniciando…";
        }

        public string Name { get; }
        public DateTimeOffset StartedAt { get; }
        public DateTimeOffset? FinishedAt { get; private set; }
        public bool Running => FinishedAt == null;
        public string Message { get; set; }
        public string? Error { get; private set; }

        public void Finish(DateTimeOffset at, string? error)
        {
            Error = error;
            if (error != null) Message = error;
            FinishedAt = at;
        }
    }

    public sealed record SimulatorStatus(
        int BotAccounts,
        int BotsInUse,
        IReadOnlyList<ChatSimulationStatus> Chats,
        IReadOnlyList<SimulatedStreamStatus> Streams,
        FfmpegProbe Ffmpeg,
        SimulationJob? Job);

    // Coordina todas las simulaciones:
    //  • un ChannelSimulation por canal (bots en el chat y en el contador de viewers),
    //  • el "pool" de cuentas de bots (un bot está en un solo canal a la vez),
    //  • raids entre canales simulados (los bots se mudan de canal),
    //  • el estado EN VIVO de cada canal (consulta SQL cada 5 s),
    //  • tareas largas del panel (crear bots, simulación completa).
    //
    // Es IChatEventObserver: ChatEffectDispatcher le avisa de todo lo que pasa en el chat
    // (mensajes de personas, encuestas, bans, raids...) y él se lo pasa a cada simulación.
    public sealed class SimulationManager : IChatEventObserver, IHostedService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly IBotAccountService _bots;
        private readonly PresenceTracker _presence;
        private readonly ChatEffectDispatcher _dispatcher;
        private readonly IHubContext<StreamHub> _hub;
        private readonly StreamSimulator _streams;
        private readonly TimeProvider _time;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<SimulationManager> _logger;
        private readonly SimulationSettings _settings;

        private readonly ConcurrentDictionary<int, ChannelSimulation> _sims = new();
        private readonly SemaphoreSlim _control = new(1, 1);
        private readonly object _poolGate = new();
        private readonly HashSet<string> _inUse = new();
        private volatile IReadOnlyList<BotAccount> _allBots = Array.Empty<BotAccount>();
        private volatile SimulationJob? _job;
        private CancellationTokenSource _cts = new();
        private Task _poller = Task.CompletedTask;

        public SimulationManager(
            IServiceScopeFactory scopes,
            IBotAccountService bots,
            PresenceTracker presence,
            ChatEffectDispatcher dispatcher,
            IHubContext<StreamHub> hub,
            StreamSimulator streams,
            TimeProvider time,
            IOptions<SimulationSettings> settings,
            ILoggerFactory loggerFactory)
        {
            _scopes = scopes;
            _bots = bots;
            _presence = presence;
            _dispatcher = dispatcher;
            _hub = hub;
            _streams = streams;
            _time = time;
            _settings = settings.Value;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<SimulationManager>();
        }

        public int MaxBots => Math.Clamp(_settings.MaxBots, 1, 10_000);

        // ════════════════════════════════════════════════════════════════
        //  Chat simulado por canal
        // ════════════════════════════════════════════════════════════════

        // Devuelve un mensaje de error para el panel, o null si arrancó (o se actualizó).
        public async Task<string?> StartChatAsync(int channelId, int viewers, double activity)
        {
            viewers = Math.Clamp(viewers, 0, MaxBots);
            activity = Math.Clamp(activity, 0.1, 5);

            await _control.WaitAsync();
            try
            {
                if (_sims.TryGetValue(channelId, out var existing) && existing.IsRunning)
                {
                    existing.SetTarget(viewers, activity);
                    return null;
                }

                _allBots = await _bots.GetBotsAsync();
                if (_allBots.Count == 0)
                    return "Todavía no hay cuentas de bots. Créalas primero con «Crear bots».";

                var info = await LoadChannelInfoAsync(channelId);
                if (info == null) return "El canal no existe.";

                ChatSettingsDto settings;
                bool aiEnabled;
                await using (var scope = _scopes.CreateAsyncScope())
                {
                    settings = await scope.ServiceProvider.GetRequiredService<IChatService>().GetSettingsAsync(channelId);
                    aiEnabled = scope.ServiceProvider.GetService<IAiChatGenerator>()?.IsAvailable == true;
                }

                var sim = new ChannelSimulation(
                    channelId, info, settings, viewers, activity, aiEnabled,
                    this, _scopes, _presence, _dispatcher, _hub, _bots, _time,
                    _loggerFactory.CreateLogger($"{typeof(ChannelSimulation).FullName}[{channelId}]"));

                _sims[channelId] = sim;
                sim.Start();
                _logger.LogInformation("Simulación de chat iniciada en el canal {ChannelId} ({Viewers} viewers, actividad {Activity:0.##}).", channelId, viewers, activity);
                return null;
            }
            finally
            {
                _control.Release();
            }
        }

        public async Task StopChatAsync(int channelId)
        {
            if (_sims.TryRemove(channelId, out var sim))
            {
                await sim.StopAsync();
                _logger.LogInformation("Simulación de chat detenida en el canal {ChannelId}.", channelId);
            }
        }

        public async Task StopAllAsync()
        {
            var sims = _sims.Values.ToList();
            _sims.Clear();
            await Task.WhenAll(sims.Select(s => s.StopAsync()));
            await _streams.StopAllAsync();
        }

        public bool HasChat(int channelId) => _sims.TryGetValue(channelId, out var s) && s.IsRunning;

        internal async Task<SimChannelInfo?> LoadChannelInfoAsync(int channelId)
        {
            await using var scope = _scopes.CreateAsyncScope();
            var channels = scope.ServiceProvider.GetRequiredService<IChannelRepository>();
            var info = await channels.GetStreamInfoAsync(channelId);
            if (info == null) return null;
            var emotes = (await channels.GetEmotesAsync(channelId)).Select(e => e.Code).Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
            return new SimChannelInfo(info.Name, info.UserId, info.OwnerDisplayName, info.CategoryName, info.IsLive, emotes);
        }

        // ════════════════════════════════════════════════════════════════
        //  Pool de bots
        // ════════════════════════════════════════════════════════════════

        internal List<BotAccount> RentBots(int count)
        {
            var result = new List<BotAccount>();
            if (count <= 0) return result;

            var all = _allBots;
            lock (_poolGate)
            {
                var free = all.Where(b => !_inUse.Contains(b.Id)).ToList();
                // Fisher-Yates parcial: los que entran son al azar (no siempre bot0001, bot0002...).
                for (var i = 0; i < count && i < free.Count; i++)
                {
                    var j = Random.Shared.Next(i, free.Count);
                    (free[i], free[j]) = (free[j], free[i]);
                    _inUse.Add(free[i].Id);
                    result.Add(free[i]);
                }
            }
            return result;
        }

        internal void ReturnBots(IEnumerable<BotAccount> bots)
        {
            lock (_poolGate)
            {
                foreach (var bot in bots) _inUse.Remove(bot.Id);
            }
        }

        private int BotsInUse
        {
            get { lock (_poolGate) return _inUse.Count; }
        }

        // ════════════════════════════════════════════════════════════════
        //  Eventos del chat (IChatEventObserver)
        // ════════════════════════════════════════════════════════════════

        public Task OnEffectsAsync(int channelId, IReadOnlyList<ChatEffect> effects)
        {
            if (_sims.TryGetValue(channelId, out var sim)) sim.Post(new ChatEffectsEvent(effects));

            foreach (var effect in effects)
            {
                if (effect is RaidEffect raid && raid.SourceChannelId == channelId) HandleRaid(raid);
            }
            return Task.CompletedTask;
        }

        // Después de la cuenta regresiva (la misma que ven los navegadores), una parte de los
        // bots del canal de origen se muda al canal destino. Si el destino no tenía simulación,
        // se crea una con esos raiders.
        private void HandleRaid(RaidEffect raid)
        {
            _sims.TryGetValue(raid.SourceChannelId, out var source);
            if (source == null || !source.IsRunning)
            {
                if (_sims.TryGetValue(raid.TargetChannelId, out var welcoming))
                    welcoming.Post(new RaidIncomingEvent(raid.SourceName, raid.Viewers));
                return;
            }

            var token = _cts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(ChatEffectDispatcher.RaidCountdownSeconds), _time, token);
                    var moved = await source.TakeRaidersAsync(0.6 + Random.Shared.NextDouble() * 0.3);
                    if (moved.Count == 0) return;

                    if (!HasChat(raid.TargetChannelId))
                    {
                        var error = await StartChatAsync(raid.TargetChannelId, 0, source.Activity);
                        if (error != null) _logger.LogWarning("No se pudo simular el raid hacia {Target}: {Error}", raid.TargetChannelId, error);
                    }

                    if (_sims.TryGetValue(raid.TargetChannelId, out var target) && target.IsRunning)
                    {
                        target.Post(new AcceptRaidersEvent(moved, raid.SourceName));
                        _logger.LogInformation("Raid simulado: {Count} bots de {Source} a {Target}.", moved.Count, raid.SourceChannelId, raid.TargetChannelId);
                    }
                    else
                    {
                        ReturnBots(moved);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _logger.LogWarning(ex, "Falló el raid simulado {Source} → {Target}.", raid.SourceChannelId, raid.TargetChannelId); }
            }, token);
        }

        // ════════════════════════════════════════════════════════════════
        //  Tareas largas del panel
        // ════════════════════════════════════════════════════════════════

        public string? CreateBots(int count)
        {
            count = Math.Clamp(count, 1, MaxBots);
            return StartJob($"Crear {count} cuentas de bots", async (job, ct) =>
            {
                await EnsureBotsAsync(count, job, ct);
                job.Message = $"Listo: {_allBots.Count} cuentas de bots.";
            });
        }

        // Varios canales en vivo a la vez, con audiencias repartidas como en la vida real
        // (pocos canales grandes y muchos chicos: ley de potencias / Zipf).
        public string? StartFullSimulation(int channelCount, int totalViewers, double activity, bool withStreams)
        {
            channelCount = Math.Clamp(channelCount, 1, 50);
            totalViewers = Math.Clamp(totalViewers, channelCount, MaxBots);

            return StartJob("Simulación completa", async (job, ct) =>
            {
                job.Message = "Buscando canales…";
                List<ChannelSummary> channels;
                await using (var scope = _scopes.CreateAsyncScope())
                {
                    channels = (await scope.ServiceProvider.GetRequiredService<IChannelRepository>().GetChannelSummariesAsync())
                        .Where(c => !c.OwnerIsSuspended)
                        .OrderByDescending(c => c.IsLive)
                        .ThenBy(c => c.Id)
                        .Take(channelCount)
                        .ToList();
                }
                if (channels.Count == 0) throw new InvalidOperationException("No hay canales para simular. Crea al menos uno.");

                await EnsureBotsAsync(totalViewers, job, ct);

                var shares = DistributeViewers(Math.Min(totalViewers, _allBots.Count), channels.Count);
                Shuffle(channels); // el canal "grande" no es siempre el mismo

                if (withStreams)
                {
                    var probe = await _streams.ProbeAsync();
                    if (!probe.Available)
                    {
                        job.Message = "ffmpeg no está disponible: se simula solo el chat.";
                        withStreams = false;
                    }
                }

                for (var i = 0; i < channels.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var channel = channels[i];
                    job.Message = $"Canal {i + 1} de {channels.Count}: {channel.Name}";
                    if (withStreams && !channel.IsLive)
                    {
                        var streamError = await _streams.StartAsync(channel.Id);
                        if (streamError != null) _logger.LogWarning("Stream simulado del canal {ChannelId}: {Error}", channel.Id, streamError);
                    }
                    var chatError = await StartChatAsync(channel.Id, shares[i], activity);
                    if (chatError != null) throw new InvalidOperationException(chatError);
                }

                job.Message = $"Listo: {channels.Count} canales con {shares.Sum()} viewers simulados" + (withStreams ? " y video." : ".");
            });
        }

        internal static int[] DistributeViewers(int total, int channels)
        {
            var weights = Enumerable.Range(0, channels).Select(i => 1.0 / Math.Pow(i + 1, 1.1)).ToArray();
            var sum = weights.Sum();
            var shares = weights.Select(w => Math.Max(1, (int)Math.Floor(total * w / sum))).ToArray();
            // Lo que sobra por redondeo va al canal más grande.
            shares[0] += Math.Max(0, total - shares.Sum());
            return shares;
        }

        private static void Shuffle<T>(IList<T> list)
        {
            for (var i = list.Count - 1; i > 0; i--)
            {
                var j = Random.Shared.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private async Task EnsureBotsAsync(int count, SimulationJob job, CancellationToken ct)
        {
            count = Math.Min(count, MaxBots);
            var current = await _bots.GetBotsAsync();
            if (current.Count < count)
            {
                job.Message = $"Creando cuentas de bots: {current.Count} de {count}…";
                await _bots.EnsureBotsAsync(count, new InlineProgress<int>(n => job.Message = $"Creando cuentas de bots: {n} de {count}…"), ct);
            }
            _allBots = await _bots.GetBotsAsync();
        }

        private string? StartJob(string name, Func<SimulationJob, CancellationToken, Task> work)
        {
            var current = _job;
            if (current is { Running: true }) return $"Ya hay una tarea en curso: {current.Name}.";

            var job = new SimulationJob(name, _time.GetUtcNow());
            _job = job;
            var token = _cts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await work(job, token);
                    job.Finish(_time.GetUtcNow(), null);
                }
                catch (OperationCanceledException)
                {
                    job.Finish(_time.GetUtcNow(), "Cancelada.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "La tarea del simulador «{Name}» falló.", name);
                    job.Finish(_time.GetUtcNow(), ex.Message);
                }
            });
            return null;
        }

        // IProgress síncrono (Progress<T> encola en el thread pool y los mensajes pueden llegar desordenados).
        private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
        {
            public void Report(T value) => report(value);
        }

        // ════════════════════════════════════════════════════════════════
        //  Estado
        // ════════════════════════════════════════════════════════════════

        public SimulatorStatus GetStatus() => new(
            _allBots.Count,
            BotsInUse,
            _sims.Values.Select(s => s.Status).OrderBy(s => s.ChannelId).ToList(),
            _streams.GetStatus(),
            _streams.CachedProbe,
            _job);

        public async Task RefreshBotsAsync() => _allBots = await _bots.GetBotsAsync();

        // ════════════════════════════════════════════════════════════════
        //  IHostedService
        // ════════════════════════════════════════════════════════════════

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _cts = new CancellationTokenSource();
            _poller = Task.Run(() => PollLiveStateAsync(_cts.Token));
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _cts.Cancel();
            var sims = _sims.Values.ToList();
            _sims.Clear();
            await Task.WhenAll(sims.Select(s => s.StopAsync()));
            try { await _poller.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); } catch { /* apagando */ }
        }

        // Cada 5 s: qué canales simulados están EN VIVO (para que los bots lleguen
        // cuando empieza el stream y se vayan cuando termina).
        private async Task PollLiveStateAsync(CancellationToken ct)
        {
            // Carga inicial de las cuentas de bots (si SQL no está listo, se reintenta después).
            try { _allBots = await _bots.GetBotsAsync(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Todavía no se pudieron cargar las cuentas de bots."); }

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), _time);
            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    // Limpia simulaciones que terminaron solas (por ejemplo, el canal se borró).
                    foreach (var (id, sim) in _sims)
                    {
                        if (!sim.IsRunning) _sims.TryRemove(new KeyValuePair<int, ChannelSimulation>(id, sim));
                    }
                    try
                    {
                        // Las cuentas pueden crearse por fuera (API de la prueba de carga): se refresca siempre.
                        _allBots = await _bots.GetBotsAsync();
                        if (_sims.IsEmpty) continue;

                        HashSet<int> live;
                        await using (var scope = _scopes.CreateAsyncScope())
                        {
                            live = (await scope.ServiceProvider.GetRequiredService<IChannelRepository>().GetLiveChannelIdsAsync()).ToHashSet();
                        }
                        foreach (var sim in _sims.Values) sim.Post(new LiveStateEvent(live.Contains(sim.ChannelId)));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "No se pudo consultar qué canales están en vivo.");
                    }
                }
            }
            catch (OperationCanceledException) { }
        }
    }
}
