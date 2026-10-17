using System.Text;
using System.Text.RegularExpressions;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Application.Services.Chat
{
    // Utilidades de texto del chat: enlaces, duraciones y colores.
    public static partial class ChatText
    {
        // http(s)://..., www.algo, o dominio.tld (con TLD de 2+ letras). Evita falsos positivos
        // como "jaja.xd" exigiendo un TLD conocido cuando no hay http ni www.
        [GeneratedRegex(@"(https?://\S+)|(www\.\S+)|(\b[a-z0-9-]+\.(com|net|org|cl|io|gg|tv|me|ly|co|app|dev|xyz|info|es|ar|mx|pe|link|site|online|shop|store)\b)", RegexOptions.IgnoreCase)]
        private static partial Regex LinkRegex();

        public static bool ContainsLink(string text) => LinkRegex().IsMatch(text);

        // ── Fase 4: reglas del score_heuristic del microservicio Python original ──
        //   exceso_mayusculas:          letras > 10  y  mayúsculas / letras > 0.8
        //   spam_caracteres_repetidos:  (.)\1{6,}   → 7 o más caracteres iguales seguidos
        public const int CapsMinLetters = 11;
        public const double CapsMaxUpperRatio = 0.8;
        public const int RepeatedCharsLimit = 7;

        // Solo cuenta lo que la persona escribió: los emotes ("KEKW", "LUL"), las @menciones
        // y los enlaces no suman, porque suelen ir en mayúsculas sin que sea "gritar".
        public static bool HasExcessiveCaps(IEnumerable<ChatFragment> fragments)
        {
            int letters = 0, upper = 0;
            foreach (var fragment in fragments)
            {
                if (fragment.Type != ChatFragment.TextType) continue;
                foreach (var word in fragment.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (word.StartsWith('@') || ContainsLink(word)) continue;
                    foreach (var c in word)
                    {
                        if (char.IsUpper(c)) { letters++; upper++; }
                        else if (char.IsLower(c)) letters++;
                    }
                }
            }
            return letters >= CapsMinLetters && upper > letters * CapsMaxUpperRatio;
        }

        public static bool HasExcessiveCaps(string text) => HasExcessiveCaps(new[] { ChatFragment.FromText(text) });

        // "holaaaaaaa", "!!!!!!!", "😂😂😂😂😂😂😂". Sin distinguir mayúsculas. Los espacios cortan la
        // racha y los dígitos no cuentan ("1000000000 puntos" es un número, no spam).
        // Recorre Runes (no chars) para que un emoji repetido cuente como un solo carácter.
        public static bool HasRepeatedChars(string text)
        {
            var run = 0;
            var previous = default(Rune);
            foreach (var rune in text.EnumerateRunes())
            {
                var current = Rune.ToLowerInvariant(rune);
                if (Rune.IsWhiteSpace(current) || Rune.IsDigit(current))
                {
                    run = 0;
                    continue;
                }
                run = run > 0 && current == previous ? run + 1 : 1;
                previous = current;
                if (run >= RepeatedCharsLimit) return true;
            }
            return false;
        }

        public static bool HasRepeatedChars(IEnumerable<ChatFragment> fragments) =>
            fragments.Any(f => f.Type == ChatFragment.TextType && HasRepeatedChars(f.Text));

        // "90" → 90 s, "10m" → 600, "1h" → 3600, "2d" → 172800. null si no es una duración.
        public static int? ParseDuration(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;
            token = token.Trim().ToLowerInvariant();

            var multiplier = 1;
            var last = token[^1];
            if (last is 's' or 'm' or 'h' or 'd')
            {
                multiplier = last switch { 'm' => 60, 'h' => 3600, 'd' => 86400, _ => 1 };
                token = token[..^1];
            }

            return int.TryParse(token, out var value) && value > 0 && value <= 1_209_600 / multiplier
                ? value * multiplier
                : null;
        }

        public static string FormatDuration(int seconds)
        {
            if (seconds < 60) return $"{seconds} s";
            if (seconds < 3600) return $"{Math.Ceiling(seconds / 60.0)} min";
            if (seconds < 86400) return $"{Math.Round(seconds / 3600.0, 1)} h";
            return $"{Math.Round(seconds / 86400.0, 1)} días";
        }

        // Colores con nombre para /color (pensados para fondo oscuro).
        private static readonly Dictionary<string, string> NamedColors = new(StringComparer.OrdinalIgnoreCase)
        {
            ["rojo"] = "#FF4A4A", ["azul"] = "#4A9EFF", ["verde"] = "#3FCF6A", ["naranjo"] = "#FF8A3D",
            ["naranja"] = "#FF8A3D", ["morado"] = "#B07CFF", ["rosado"] = "#FF5FA2", ["rosa"] = "#FF5FA2",
            ["celeste"] = "#7FD1FF", ["amarillo"] = "#F2C230", ["turquesa"] = "#2EC4B6", ["lima"] = "#9BE15D",
            ["lila"] = "#C792EA", ["coral"] = "#FF7B7B", ["dorado"] = "#E6B422"
        };

        public static IEnumerable<string> ColorNames => NamedColors.Keys;

        // Devuelve el color normalizado (#RRGGBB) o null con un motivo.
        public static (string? Color, string? Error) ParseColor(string input)
        {
            input = (input ?? string.Empty).Trim();
            if (NamedColors.TryGetValue(input, out var named)) return (named, null);

            if (!Regex.IsMatch(input, "^#?[0-9a-fA-F]{6}$"))
                return (null, $"Color inválido. Usa #RRGGBB o uno de: {string.Join(", ", NamedColors.Keys.Distinct())}.");

            var hex = "#" + input.TrimStart('#').ToUpperInvariant();
            var r = Convert.ToInt32(hex.Substring(1, 2), 16) / 255.0;
            var g = Convert.ToInt32(hex.Substring(3, 2), 16) / 255.0;
            var b = Convert.ToInt32(hex.Substring(5, 2), 16) / 255.0;
            var luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            if (luminance < 0.25) return (null, "Ese color es muy oscuro y no se leería sobre el fondo del chat.");

            return (hex, null);
        }
    }
}
