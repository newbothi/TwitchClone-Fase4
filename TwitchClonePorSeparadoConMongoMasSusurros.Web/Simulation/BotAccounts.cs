
namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Simulation
{
    public record BotAccount(int Number, string Id, string DisplayName);

    // Cuentas de bots: usuarios reales en la tabla Users (rol Viewer) con email
    // bot0001@bots.twitchclone.local, sin contraseña (no pueden iniciar sesión).
    // Como son usuarios de verdad, pasan por el mismo pipeline del chat que una persona:
    // modo lento, bans, AutoMod, seguidores, puntos...
    public interface IBotAccountService
    {
        Task<IReadOnlyList<BotAccount>> GetBotsAsync();
        // Crea las cuentas que falten hasta llegar a "count". progress recibe cuántas hay.
        Task<IReadOnlyList<BotAccount>> EnsureBotsAsync(int count, IProgress<int>? progress = null, CancellationToken ct = default);
        Task<BotAccount?> FindByNumberAsync(int number);
        bool IsBot(string userId);
    }

    public static class BotAccountNaming
    {
        public const string EmailDomain = "@bots.twitchclone.local";
        public static string EmailFor(int number) => $"bot{number:0000}{EmailDomain}";

        public static int ParseNumber(string email)
        {
            var local = email.Split('@')[0];
            return local.StartsWith("bot", StringComparison.Ordinal) && int.TryParse(local[3..], out var n) ? n : 0;
        }
    }

    // Nombres de usuario creíbles para los bots (estilo Twitch latino/chileno).
    public static class BotNameGenerator
    {
        private static readonly string[] Names =
        {
            "Pancho", "Cami", "Nico", "Vale", "Benja", "Fran", "Seba", "Coni", "Mati", "Javi", "Feña", "Rorro",
            "Tomi", "Dani", "Isi", "Maxi", "Pipe", "Joaco", "Ro", "Anto", "Cata", "Nacho", "Lucho", "Gonza",
            "Flo", "Martu", "Agus", "Juanpi", "Vicho", "Pato", "Chelo", "Kathy", "Romi", "Bastian", "Diego", "Sofi"
        };

        private static readonly string[] Words =
        {
            "Gamer", "Pro", "Noob", "Master", "Plays", "Live", "GG", "Ninja", "Rata", "Kpo", "Bkn", "Weno",
            "Zorro", "Lobo", "Gato", "Panda", "Taco", "Completo", "Sopaipa", "Empanada", "Churrasco", "Pebre",
            "Pixel", "Bot", "Crack", "Leyenda", "Tranqui", "Chill", "Lag", "Tryhard", "Smurf", "Carry"
        };

        private static readonly string[] Styles = { "{0}{1}", "{0}_{1}", "{1}{0}", "x{0}{1}x", "{0}{1}_cl", "el_{1}_{0}", "{0}.{1}", "{1}_de_{0}" };

        public static string Generate(int seed, ISet<string> used)
        {
            var rng = new Random(seed * 7919);
            for (var attempt = 0; attempt < 50; attempt++)
            {
                var name = Names[rng.Next(Names.Length)];
                var word = Words[rng.Next(Words.Length)];
                var style = Styles[rng.Next(Styles.Length)];
                var candidate = string.Format(style, name, word);
                if (rng.NextDouble() < 0.6) candidate += rng.Next(2, 2009).ToString();
                if (rng.NextDouble() < 0.3) candidate = candidate.ToLowerInvariant();
                if (candidate.Length > 25) candidate = candidate[..25];
                if (!used.Contains(candidate)) return candidate;
            }
            return $"viewer{seed}";
        }
    }
}
