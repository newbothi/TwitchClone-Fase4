using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Controllers;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Integration.Routing
{
    // Qué controlador atiende cada dirección, con las MISMAS rutas que Program.cs:
    // todos los controladores de Web + la ruta convencional {controller=Home}/{action=Index}/{id?}.
    // No ejecuta los controladores (no hace falta SQL ni Identity): solo pregunta al
    // enrutador qué acción eligió.
    //
    // Nació de un error real: ProfileController tenía [HttpGet("{username}")] sin prefijo,
    // y como las rutas por atributo le ganan a la convencional, /Admin, /Simulator, /Whisper...
    // se trataban como "el perfil del usuario Admin" → 404.
    [TestClass]
    public class RutasTests
    {
        private static WebApplication? _app;
        private static HttpClient? _http;

        [ClassInitialize]
        public static async Task Start(TestContext _)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Pruebas",
                ContentRootPath = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "TwitchClone.Rutas")).FullName
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddControllersWithViews().ConfigureApplicationPartManager(parts =>
            {
                parts.ApplicationParts.Clear();
                parts.ApplicationParts.Add(new AssemblyPart(typeof(AdminController).Assembly));
            });

            _app = builder.Build();
            _app.UseRouting();
            // Responde con la acción elegida en vez de ejecutarla.
            _app.Use((HttpContext context, Func<Task> next) =>
            {
                var action = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
                return context.Response.WriteAsync(action == null ? "(ninguna)" : $"{action.ControllerName}.{action.ActionName}");
            });
            _app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");
            await _app.StartAsync();

            var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            _http = new HttpClient { BaseAddress = new Uri(address) };
        }

        [ClassCleanup]
        public static async Task Stop()
        {
            _http?.Dispose();
            if (_app != null) await _app.DisposeAsync();
        }

        private static Task<string> ActionFor(string path) => _http!.GetStringAsync(path);

        [TestMethod]
        [DataRow("/", "Home.Index")]
        [DataRow("/Admin", "Admin.Index")]
        [DataRow("/Admin/Users", "Admin.Users")]
        [DataRow("/Simulator", "Simulator.Index")]
        [DataRow("/Whisper", "Whisper.Index")]
        [DataRow("/Search", "Search.Index")]
        [DataRow("/Category", "Category.Index")]
        [DataRow("/Channel/Dashboard", "Channel.Dashboard")]
        [DataRow("/Account/Login", "Account.Login")]
        [DataRow("/User/Profile/PixelQueen", "User.Profile")]
        [DataRow("/Profile/PixelQueen", "Profile.Index")]
        [DataRow("/Profile/My", "Profile.MyProfile")]
        public async Task CadaDireccion_LaAtiendeSuControlador(string path, string expected)
        {
            Assert.AreEqual(expected, await ActionFor(path), path);
        }

        // Regla general (cubre también controladores futuros): /{Controlador} siempre llega a
        // su Index. Si alguien vuelve a dejar una ruta "{algo}" en la raíz, esto falla.
        [TestMethod]
        public async Task NingunaRutaPorAtributo_SeTragaLasDireccionesDeLosControladores()
        {
            var indexes = _app!.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
                .OfType<ControllerActionDescriptor>()
                .Where(a => a.ActionName == "Index" && a.AttributeRouteInfo == null)
                .Where(a => a.ActionConstraints?.OfType<HttpMethodActionConstraint>()
                    .All(c => c.HttpMethods.Contains("GET")) ?? true)
                .Select(a => a.ControllerName)
                .Distinct()
                .ToList();

            Assert.IsTrue(indexes.Count >= 5, "Debería haber varios controladores con Index: " + string.Join(", ", indexes));
            var wrong = new List<string>();
            foreach (var controller in indexes)
            {
                var actual = await ActionFor("/" + controller);
                if (actual != controller + ".Index") wrong.Add($"/{controller} → {actual}");
            }
            Assert.AreEqual(0, wrong.Count, "Direcciones capturadas por otra ruta: " + string.Join("; ", wrong));
        }
    }
}
