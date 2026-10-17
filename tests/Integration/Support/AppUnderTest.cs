using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TwitchClonePorSeparadoConMongoMasSusurros.Application;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Controllers;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Hubs;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Realtime;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Integration.Support
{
    // La app levantada de verdad en http://127.0.0.1:{puerto libre}, con:
    //   • StreamHub, PresenceTracker y PresenceBroadcaster REALES (los de Web)
    //   • MediaHooksController REAL (lo que llama MediaMTX)
    //   • ChatService y StreamLifecycleService REALES (AddApplication)
    //   • SQL Server y MongoDB reemplazados por InMemoryPlatform
    //   • Login reemplazado por TestAuthHandler (encabezado X-Test-User)
    //
    // No usa Program.cs a propósito: Program.cs siembra roles, categorías y streamers
    // demo en SQL Server al arrancar, y eso obligaría a tener una base de datos.
    public sealed class AppUnderTest : IAsyncDisposable
    {
        public const string DefaultHookSecret = "secreto-de-pruebas";

        private readonly WebApplication _app;
        private readonly List<HubClient> _clients = new();
        private int _disposed;

        private AppUnderTest(WebApplication app, InMemoryPlatform db, string baseUrl, string hookSecret)
        {
            _app = app;
            Db = db;
            BaseUrl = baseUrl;
            HookSecret = hookSecret;
            Http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        }

        public InMemoryPlatform Db { get; }
        public string BaseUrl { get; }
        public string HookSecret { get; }
        public HttpClient Http { get; }
        public IServiceProvider Services => _app.Services;

        // arrange: para dejar datos listos ANTES de que la app arranque (p. ej. modo lento).
        // configureServices: para agregar o reemplazar servicios (p. ej. un observador del chat).
        public static async Task<AppUnderTest> StartAsync(string hookSecret = DefaultHookSecret, Action<InMemoryPlatform>? arrange = null,
            Action<IServiceCollection>? configureServices = null)
        {
            var db = InMemoryPlatform.WithDemoData();
            arrange?.Invoke(db);

            // ContentRoot = carpeta vacía: así NO se lee el appsettings.json de Web que se copia
            // junto a las pruebas (si ahí agregas endpoints de Kestrel o tu HookSecret real,
            // las pruebas no cambian de comportamiento).
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Pruebas",
                ContentRootPath = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "TwitchClone.Pruebas")).FullName
            });

            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            if (Environment.GetEnvironmentVariable("PRUEBAS_LOGS") == "1")
                builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Debug);

            // AddApplication registra servicios (UserService, ChannelService…) que estas
            // pruebas no usan y que dependen de SQL: no validarlos al construir.
            builder.Host.UseDefaultServiceProvider(o =>
            {
                o.ValidateScopes = true;
                o.ValidateOnBuild = false;
            });

            builder.Services.AddSignalR();
            builder.Services
                .AddControllers()
                .ConfigureApplicationPartManager(parts =>
                {
                    // Solo el controlador de MediaMTX: los demás necesitan vistas, Identity y SQL.
                    parts.ApplicationParts.Clear();
                    parts.ApplicationParts.Add(new AssemblyPart(typeof(MediaHooksController).Assembly));
                    parts.FeatureProviders.Remove(parts.FeatureProviders.OfType<ControllerFeatureProvider>().First());
                    parts.FeatureProviders.Add(new OnlyTheseControllers(typeof(MediaHooksController)));
                });

            builder.Services
                .AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            builder.Services.AddAuthorization();

            builder.Services.Configure<MediaServerSettings>(o => o.HookSecret = hookSecret);

            builder.Services.AddApplication();
            builder.Services.AddInMemoryPlatform(db);
            builder.Services.AddSingleton<PresenceTracker>();
            builder.Services.AddSingleton<IPresenceCounter>(sp => sp.GetRequiredService<PresenceTracker>());
            builder.Services.AddHostedService<PresenceBroadcaster>();
            builder.Services.AddSingleton<ChatEffectDispatcher>();
            builder.Services.AddSingleton<IChatEventObserver, AutoModFeedbackObserver>();
            configureServices?.Invoke(builder.Services);

            var app = builder.Build();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapControllers();
            app.MapHub<StreamHub>("/streamHub");

            await app.StartAsync();

            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.First();

            return new AppUnderTest(app, db, address.TrimEnd('/'), hookSecret);
        }

        // Abre una conexión de SignalR como lo hace la página Watch.
        // userId null = visitante anónimo.
        public async Task<HubClient> ConnectAsync(string? userId = null, params string[] roles)
        {
            var connection = new HubConnectionBuilder()
                .WithUrl($"{BaseUrl}/streamHub", options =>
                {
                    if (userId != null) options.Headers[TestAuthHandler.UserHeader] = userId;
                    if (roles.Length > 0) options.Headers[TestAuthHandler.RolesHeader] = string.Join(',', roles);
                })
                .Build();

            var client = new HubClient(connection, userId);
            await connection.StartAsync();
            lock (_clients) _clients.Add(client);
            return client;
        }

        // Atajo: conectarse y entrar a un canal.
        public async Task<HubClient> JoinAsync(int channelId, string? userId = null, params string[] roles)
        {
            var client = await ConnectAsync(userId, roles);
            await client.JoinAsync(channelId);
            return client;
        }

        // Lo que manda MediaMTX a authHTTPAddress (JSON en camelCase).
        public Task<HttpResponseMessage> PostAuthAsync(object body, string? secret = null) =>
            Http.PostAsJsonAsync($"/api/media/auth?secret={Uri.EscapeDataString(secret ?? HookSecret)}", body);

        // runOnAvailable / runOnUnavailable de mediamtx.yml.
        public Task<HttpResponseMessage> PostHookAsync(string hook, string path, string? secret = null) =>
            Http.PostAsync($"/api/media/hooks/{hook}?path={Uri.EscapeDataString(path)}&secret={Uri.EscapeDataString(secret ?? HookSecret)}", content: null);

        public async ValueTask DisposeAsync()
        {
            // Algunas pruebas reinician la app con otros datos: el segundo Dispose no hace nada.
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            List<HubClient> clients;
            lock (_clients) clients = _clients.ToList();
            foreach (var client in clients)
            {
                try { await client.DisposeAsync(); } catch { /* ya cerrado por la prueba */ }
            }

            Http.Dispose();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _app.StopAsync(timeout.Token);
            await _app.DisposeAsync();
        }

        private sealed class OnlyTheseControllers : ControllerFeatureProvider
        {
            private readonly HashSet<Type> _allowed;

            public OnlyTheseControllers(params Type[] allowed) => _allowed = allowed.ToHashSet();

            protected override bool IsController(TypeInfo typeInfo) => _allowed.Contains(typeInfo.AsType());
        }
    }
}
