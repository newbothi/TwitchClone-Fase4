using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Simulation
{
    // Crea y carga las cuentas de bots (tabla Users, vía Identity).
    public sealed class BotAccountService : IBotAccountService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<BotAccountService> _logger;
        private readonly int _maxBots;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private volatile List<BotAccount>? _cache;
        private volatile HashSet<string> _ids = new();

        public BotAccountService(IServiceScopeFactory scopes, IOptions<SimulationSettings> settings, ILogger<BotAccountService> logger)
        {
            _scopes = scopes;
            _logger = logger;
            _maxBots = Math.Clamp(settings.Value.MaxBots, 1, 10_000);
        }

        public bool IsBot(string userId) => _ids.Contains(userId);

        public async Task<IReadOnlyList<BotAccount>> GetBotsAsync()
        {
            if (_cache != null) return _cache;
            await _gate.WaitAsync();
            try
            {
                if (_cache == null) await LoadAsync();
                return _cache!;
            }
            finally { _gate.Release(); }
        }

        public async Task<BotAccount?> FindByNumberAsync(int number)
            => (await GetBotsAsync()).FirstOrDefault(b => b.Number == number);

        public async Task<IReadOnlyList<BotAccount>> EnsureBotsAsync(int count, IProgress<int>? progress = null, CancellationToken ct = default)
        {
            count = Math.Clamp(count, 0, _maxBots);
            await _gate.WaitAsync(ct);
            try
            {
                if (_cache == null) await LoadAsync();
                if (_cache!.Count >= count) return _cache;

                // Se trabaja sobre copias y se publican al final: otros hilos pueden estar
                // recorriendo la lista (simuladores) o consultando IsBot mientras tanto.
                var list = new List<BotAccount>(_cache);
                var ids = new HashSet<string>(_ids);
                var usedNames = list.Select(b => b.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var usedNumbers = list.Select(b => b.Number).ToHashSet();
                var created = 0;

                using var scope = _scopes.CreateScope();
                var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

                try
                {
                    for (var n = 1; list.Count < count && n <= _maxBots * 2; n++)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (usedNumbers.Contains(n)) continue;

                        var name = BotNameGenerator.Generate(n, usedNames);
                        var user = new AppUser
                        {
                            UserName = BotAccountNaming.EmailFor(n),
                            Email = BotAccountNaming.EmailFor(n),
                            EmailConfirmed = true,
                            DisplayUsername = name,
                            CreatedAt = DateTime.Now.AddDays(-Random.Shared.Next(1, 900)), // cuentas "antiguas"
                            LockoutEnabled = false,
                            // Puntos acumulados "de antes" (pocos tienen mucho): así pueden donar desde el principio.
                            Points = (int)(Math.Pow(Random.Shared.NextDouble(), 3) * 5000)
                        };

                        var result = await users.CreateAsync(user); // sin contraseña: no pueden iniciar sesión
                        if (!result.Succeeded)
                        {
                            _logger.LogWarning("No se pudo crear el bot {Email}: {Errors}", user.Email, string.Join(", ", result.Errors.Select(e => e.Description)));
                            usedNumbers.Add(n);
                            continue;
                        }
                        await users.AddToRoleAsync(user, "Viewer");

                        usedNames.Add(name);
                        usedNumbers.Add(n);
                        list.Add(new BotAccount(n, user.Id, name));
                        ids.Add(user.Id);
                        created++;
                        if (created % 25 == 0) progress?.Report(list.Count);
                    }
                }
                finally
                {
                    // Aunque se cancele a mitad de camino, las cuentas ya creadas quedan registradas.
                    _cache = list.OrderBy(b => b.Number).ToList();
                    _ids = ids;
                    progress?.Report(_cache.Count);
                    if (created > 0) _logger.LogInformation("Se crearon {Created} cuentas de bots (total {Total}).", created, _cache.Count);
                }
                return _cache;
            }
            finally { _gate.Release(); }
        }

        private async Task LoadAsync()
        {
            using var scope = _scopes.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var rows = await users.Users
                .Where(u => u.Email != null && u.Email.EndsWith(BotAccountNaming.EmailDomain))
                .Select(u => new { u.Id, u.Email, u.DisplayUsername })
                .ToListAsync();

            _cache = rows
                .Select(r => new BotAccount(BotAccountNaming.ParseNumber(r.Email!), r.Id, r.DisplayUsername))
                .Where(b => b.Number > 0)
                .OrderBy(b => b.Number)
                .ToList();
            _ids = _cache.Select(b => b.Id).ToHashSet();
        }
    }
}
