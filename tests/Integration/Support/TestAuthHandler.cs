using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Integration.Support
{
    // Reemplaza a la cookie de Identity en las pruebas: el "usuario logueado" viaja en
    // un encabezado. Sin encabezado = visitante anónimo (igual que sin cookie).
    //
    //   X-Test-User:  u1
    //   X-Test-Roles: Admin,Streamer
    //
    // Solo existe en el proyecto de pruebas; la app real sigue usando Identity.
    public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "Pruebas";
        public const string UserHeader = "X-Test-User";
        public const string RolesHeader = "X-Test-Roles";

        public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var userId = Request.Headers[UserHeader].ToString();
            if (string.IsNullOrWhiteSpace(userId))
                return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim>
            {
                // NameIdentifier es lo que SignalR usa como Context.UserIdentifier (Clients.User).
                new(ClaimTypes.NameIdentifier, userId),
                new(ClaimTypes.Name, userId)
            };

            var roles = Request.Headers[RolesHeader].ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }
}
