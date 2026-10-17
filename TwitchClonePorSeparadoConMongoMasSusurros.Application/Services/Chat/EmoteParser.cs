using System.Text;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Application.Services.Chat
{
    // Convierte "hola pepega como estan :pepega:" en fragmentos de texto y emote.
    // Igual que en Twitch, un emote se reconoce como palabra completa (separada por espacios).
    public static class EmoteParser
    {
        // emotesByCode: código → URL de la imagen. Debe incluir las variantes "code" y ":code:".
        public static List<ChatFragment> Parse(string content, IReadOnlyDictionary<string, string> emotesByCode)
        {
            var fragments = new List<ChatFragment>();
            if (string.IsNullOrEmpty(content)) return fragments;

            if (emotesByCode.Count == 0)
            {
                fragments.Add(ChatFragment.FromText(content));
                return fragments;
            }

            var text = new StringBuilder();
            var words = content.Split(' ');

            for (var i = 0; i < words.Length; i++)
            {
                var word = words[i];

                if (word.Length > 0 && emotesByCode.TryGetValue(word, out var url))
                {
                    if (text.Length > 0)
                    {
                        fragments.Add(ChatFragment.FromText(text.ToString()));
                        text.Clear();
                    }
                    fragments.Add(ChatFragment.FromEmote(word, url));
                }
                else
                {
                    text.Append(word);
                }

                // Conservamos los espacios tal como venían.
                if (i < words.Length - 1) text.Append(' ');
            }

            if (text.Length > 0) fragments.Add(ChatFragment.FromText(text.ToString()));
            return fragments;
        }

        // Construye el diccionario a partir de la tabla Emotes. Acepta códigos guardados
        // con o sin dos puntos ("pepega" o ":pepega:") y reconoce ambas formas al escribir.
        public static Dictionary<string, string> BuildLookup(IEnumerable<Emote> emotes)
        {
            var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var emote in emotes)
            {
                var bare = emote.Code.Trim().Trim(':');
                if (bare.Length == 0 || string.IsNullOrWhiteSpace(emote.ImageUrl)) continue;

                lookup[bare] = emote.ImageUrl;
                lookup[$":{bare}:"] = emote.ImageUrl;
            }
            return lookup;
        }
    }
}
