using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TwitchClonePorSeparadoConMongoMasSusurros.Application;
using TwitchClonePorSeparadoConMongoMasSusurros.Application.Services;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;
using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure;
using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Data;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Hubs;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Realtime;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Simulation;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.MaximumReceiveMessageSize = 16 * 1024; // un mensaje de chat son ≤ 500 caracteres
});

// Infrastructure (DbContext + Identity + Repositories + MongoDB + MediaMTX)
builder.Services.AddInfrastructure(builder.Configuration);

// Application (Services)
builder.Services.AddApplication();

// Tiempo real: presencia de viewers, difusión del conteo y sincronización con MediaMTX
builder.Services.AddSingleton<PresenceTracker>();
builder.Services.AddSingleton<IPresenceCounter>(sp => sp.GetRequiredService<PresenceTracker>());
builder.Services.AddHostedService<PresenceBroadcaster>();
builder.Services.AddHostedService<LiveStateReconciler>();

// Fase 2: difusión de efectos del chat, encuestas y puntos del canal
builder.Services.AddSingleton<ChatEffectDispatcher>();
builder.Services.AddHostedService<PollTicker>();
builder.Services.AddHostedService<PointsAccrualService>();

// Fase 3: simulación (bots en el chat, streams con ffmpeg, miniaturas, prueba de carga)
builder.Services.AddSingleton<IBotAccountService, BotAccountService>();
builder.Services.AddSingleton<StreamSimulator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StreamSimulator>());
builder.Services.AddSingleton<SimulationManager>();
builder.Services.AddSingleton<IChatEventObserver>(sp => sp.GetRequiredService<SimulationManager>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<SimulationManager>());
builder.Services.AddHostedService<ThumbnailService>();

// Fase 4: las decisiones de los moderadores entrenan al AutoMod de Python (python/automod).
builder.Services.AddSingleton<IChatEventObserver, AutoModFeedbackObserver>();

// Autenticación de bots para la prueba de carga (solo Development y con Simulation:BotApiKey).
builder.Services.AddAuthentication()
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, BotKeyAuthenticationHandler>(BotKeyAuthenticationHandler.SchemeName, null);

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
    // Si la petición trae la clave de bots, la autentica BotKey en vez de la cookie.
    options.ForwardDefaultSelector = context =>
        BotKeyAuthenticationHandler.HasBotKey(context.Request) ? BotKeyAuthenticationHandler.SchemeName : null;
});

var app = builder.Build();

// Sembrar roles de Identity si no existen (evita InvalidOperationException en AddToRoleAsync)
using (var scope = app.Services.CreateScope())
{
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    string[] roles = { "Viewer", "Streamer", "Admin" };

    foreach (var role in roles)
    {
        if (!await roleManager.RoleExistsAsync(role))
            await roleManager.CreateAsync(new IdentityRole(role));
    }
}

// Sembrar tags y categorías por defecto si las tablas están vacías
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    if (!await db.Tags.AnyAsync())
    {
        var tags = new Dictionary<string, Tag>
        {
            ["Juegos"] = new Tag { Name = "Juegos", Description = "Videojuegos y gaming en vivo" },
            ["IRL"] = new Tag { Name = "IRL", Description = "Transmisiones \"in real life\", fuera del juego" },
            ["Música y DJ"] = new Tag { Name = "Música y DJ", Description = "Sets en vivo, producción musical y DJ sets" },
            ["Creative"] = new Tag { Name = "Creative", Description = "Arte digital, diseño y contenido creativo" },
            ["Esports"] = new Tag { Name = "Esports", Description = "Torneos y competiciones de esports" },
            ["Just Chatting"] = new Tag { Name = "Just Chatting", Description = "Charlas, preguntas y respuestas, sin juego de por medio" },
            ["Lucha"] = new Tag { Name = "Lucha", Description = "Juegos de pelea y lucha" },
            ["Arcade"] = new Tag { Name = "Arcade", Description = "Juegos de estilo arcade" }
        };

        db.Tags.AddRange(tags.Values);
        await db.SaveChangesAsync();

        var rootCategoriesWithTags = new (Category Category, string[] TagNames)[]
        {
            (new Category { Name = "Charlando", Description = "Interacción directa con la audiencia: preguntas, respuestas y charla libre" }, new[] { "Just Chatting" }),
            (new Category { Name = "Videojuegos", Description = "Streams de videojuegos, con listas específicas por título" }, new[] { "Juegos" }),
            (new Category { Name = "Música", Description = "Conciertos en vivo, DJ sets o producción musical" }, new[] { "Música y DJ" }),
            (new Category { Name = "Deportes", Description = "Comentarios de torneos, fitness y entrenamiento" }, new[] { "Esports", "IRL" }),
            (new Category { Name = "Arte", Description = "Ilustración, pintura, animación y diseño" }, new[] { "Creative" }),
            (new Category { Name = "Talk Shows y Podcasts", Description = "Entrevistas y debates" }, new[] { "Just Chatting" }),
            (new Category { Name = "Viajes y Aire Libre", Description = "Blogs de viajes o caminatas" }, new[] { "IRL" }),
            (new Category { Name = "ASMR", Description = "Contenido de respuesta sensorial meridiana" }, new[] { "IRL" }),
            (new Category { Name = "Comida y Bebida", Description = "Clases de cocina o degustaciones" }, new[] { "IRL" }),
            (new Category { Name = "Ciencia y Tecnología", Description = "Programación, ciencia o reseñas de hardware" }, new[] { "Creative" })
        };

        foreach (var (category, _) in rootCategoriesWithTags)
        {
            db.Categories.Add(category);
        }
        await db.SaveChangesAsync();

        var rootCategories = rootCategoriesWithTags.ToDictionary(x => x.Category.Name, x => x.Category);

        // Sub-categorías de ejemplo dentro de "Videojuegos" (no se ofrecen al registrarse,
        // solo sirven para navegar/filtrar dentro del directorio de Videojuegos)
        var subCategoriesWithTags = new (Category Category, string[] TagNames)[]
        {
            (new Category { Name = "Street Fighter 6", Description = "Juego de lucha de Capcom", ParentCategoryId = rootCategories["Videojuegos"].Id }, new[] { "Juegos", "Lucha", "Arcade" }),
            (new Category { Name = "League of Legends", Description = "MOBA competitivo de Riot Games", ParentCategoryId = rootCategories["Videojuegos"].Id }, new[] { "Juegos", "Esports" })
        };

        foreach (var (category, _) in subCategoriesWithTags)
        {
            db.Categories.Add(category);
        }
        await db.SaveChangesAsync();

        var categoriesWithTags = rootCategoriesWithTags.Concat(subCategoriesWithTags).ToArray();

        foreach (var (category, tagNames) in categoriesWithTags)
        {
            foreach (var tagName in tagNames)
            {
                db.CategoryTags.Add(new CategoryTag { CategoryId = category.Id, TagId = tags[tagName].Id });
            }
        }
        await db.SaveChangesAsync();
    }
}

// Sembrar 6 streamers demo (con su canal), solo en Development, para tener
// canales con los que probar sin registrar cuentas a mano.
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

    var alreadySeeded = await db.Users.AnyAsync(u => u.Email == "demo.streamer1@twitchclone.local");

    if (!alreadySeeded)
    {
        var categoriasPorNombre = await db.Categories.ToDictionaryAsync(c => c.Name, c => c.Id);

        (string Email, string Username, string CategoryName, string ChannelName, string Description)[] demoStreamers =
        {
            ("demo.streamer1@twitchclone.local", "PixelQueen", "Videojuegos", "PixelQueen juega Elden Ring", "Explorando el DLC sin morir (mucho)"),
            ("demo.streamer2@twitchclone.local", "ChefRodrigo", "Comida y Bebida", "Cocinando pasta desde cero", "Receta de la abuela, edición streaming"),
            ("demo.streamer3@twitchclone.local", "LoFiLuna", "Música", "Sesión de lo-fi en vivo", "Beats para programar toda la noche"),
            ("demo.streamer4@twitchclone.local", "CharlaConAna", "Charlando", "Charlando con la comunidad", "Preguntas y respuestas de la semana"),
            ("demo.streamer5@twitchclone.local", "ArteDigitalMax", "Arte", "Ilustrando personajes originales", "Dibujo digital con Procreate"),
            ("demo.streamer6@twitchclone.local", "RetoFitDaily", "Deportes", "Entrenamiento de piernas en vivo", "Rutina de hoy: piernas y glúteos")
        };

        foreach (var demo in demoStreamers)
        {
            var user = new AppUser
            {
                UserName = demo.Email,
                Email = demo.Email,
                EmailConfirmed = true,
                DisplayUsername = demo.Username,
                CreatedAt = DateTime.Now
            };

            var result = await userManager.CreateAsync(user, "Demo123!");
            if (!result.Succeeded) continue;

            await userManager.AddToRoleAsync(user, "Streamer");

            categoriasPorNombre.TryGetValue(demo.CategoryName, out var categoryId);

            // Fase 1: los canales demo se crean OFFLINE. Antes se marcaban "en vivo" con
            // viewers inventados pero sin video. Para verlos en vivo de verdad, transmite
            // a uno con OBS o con scripts/stream-de-prueba.ps1 (Fase 3: streams simulados).
            db.Channels.Add(new Channel
            {
                UserId = user.Id,
                Name = demo.ChannelName,
                Description = demo.Description,
                StreamKey = StreamKeyGenerator.NewKey(),
                IsLive = false,
                ViewerCount = 0,
                CategoryId = categoryId == 0 ? null : categoryId,
                CreatedAt = DateTime.Now
            });
        }

        await db.SaveChangesAsync();
    }
}

// Aviso temprano si el secreto compartido con MediaMTX no está configurado.
{
    var media = app.Configuration.GetSection(MediaServerSettings.SectionName).Get<MediaServerSettings>() ?? new MediaServerSettings();
    if (string.IsNullOrWhiteSpace(media.HookSecret))
        app.Logger.LogWarning("MediaServer:HookSecret está vacío: los endpoints /api/media/* solo aceptarán llamadas desde localhost. Configúralo igual que en mediamtx.yml.");
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// MediaMTX llama a /api/media/* por HTTP plano (curl / authHTTPAddress). Si se
// redirigiera a HTTPS (perfil "https" de launchSettings), curl no sigue el 307 y
// el webhook se perdería en silencio. El resto de la app sí fuerza HTTPS.
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/api/media"),
    branch => branch.UseHttpsRedirection());
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapHub<StreamHub>("/streamHub");

app.Run();