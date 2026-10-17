using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Simulation
{
    // Autenticación SOLO PARA DESARROLLO que usa la prueba de carga (proyecto LoadTest):
    // cada conexión se presenta como un bot con
    //     X-Bot-Key: {Simulation:BotApiKey}   X-Bot-User: 17
    // (o ?bot_key=...&bot_user=17 en la URL, porque los WebSocket del navegador no
    // pueden mandar headers). Así se prueban miles de usuarios distintos sin tener
    // que iniciar sesión mil veces.
    //
    // Está desactivada si la app no corre en Development o si la clave está vacía
    // (o es corta). La cookie de Identity le reenvía la autenticación solo cuando la
    // petición trae la clave (ForwardDefaultSelector en Program.cs).
    public sealed class BotKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "BotKey";
        public const string HeaderKey = "X-Bot-Key";
        public const string HeaderUser = "X-Bot-User";
        public const string QueryKey = "bot_key";
        public const string QueryUser = "bot_user";

        private readonly IBotAccountService _bots;
        private readonly SimulationSettings _settings;
        private readonly IWebHostEnvironment _env;

        public BotKeyAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder,
            IBotAccountService bots,
            IOptions<SimulationSettings> settings,
            IWebHostEnvironment env)
            : base(options, logger, encoder)
        {
            _bots = bots;
            _settings = settings.Value;
            _env = env;
        }

        public static bool HasBotKey(HttpRequest request) =>
            request.Headers.ContainsKey(HeaderKey) || request.Query.ContainsKey(QueryKey);

        public static bool IsEnabled(IWebHostEnvironment env, SimulationSettings settings) =>
            env.IsDevelopment() && (settings.BotApiKey?.Length ?? 0) >= 16;

        public static bool KeyMatches(string? provided, string expected)
        {
            if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected)) return false;
            // Comparación en tiempo constante (no revela cuántos caracteres coinciden).
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));
        }

        public static string? ProvidedKey(HttpRequest request) =>
            request.Headers.TryGetValue(HeaderKey, out var h) && !string.IsNullOrEmpty(h) ? h.ToString() : request.Query[QueryKey].ToString();

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!HasBotKey(Request)) return AuthenticateResult.NoResult();
            if (!IsEnabled(_env, _settings)) return AuthenticateResult.Fail("La autenticación de bots está desactivada (solo Development con Simulation:BotApiKey de 16+ caracteres).");
            if (!KeyMatches(ProvidedKey(Request), _settings.BotApiKey)) return AuthenticateResult.Fail("Clave de bots inválida.");

            var rawUser = Request.Headers.TryGetValue(HeaderUser, out var h) && !string.IsNullOrEmpty(h) ? h.ToString() : Request.Query[QueryUser].ToString();
            if (!int.TryParse(rawUser, out var number) || number <= 0) return AuthenticateResult.Fail("Falta el número de bot (X-Bot-User / bot_user).");

            var bot = await _bots.FindByNumberAsync(number);
            if (bot == null) return AuthenticateResult.Fail($"No existe el bot {number}. Créalo con POST /api/simulation/bots?count=N.");

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, bot.Id),
                new Claim(ClaimTypes.Name, BotAccountNaming.EmailFor(number)),
                new Claim(ClaimTypes.Role, "Viewer")
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
            return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
        }
    }
}
