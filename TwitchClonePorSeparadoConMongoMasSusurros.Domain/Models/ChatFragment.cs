using MongoDB.Bson.Serialization.Attributes;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models
{
    // Un trozo de un mensaje de chat: texto plano o un emote.
    // El cliente nunca recibe HTML: arma el mensaje con textContent + <img>
    // a partir de estos fragmentos, así no hay forma de inyectar código.
    [BsonIgnoreExtraElements]
    public class ChatFragment
    {
        public const string TextType = "text";
        public const string EmoteType = "emote";

        public string Type { get; set; } = TextType;

        // Para "text": el texto tal cual. Para "emote": el código escrito (ej. "pepega").
        public string Text { get; set; } = string.Empty;

        // Solo para "emote": URL de la imagen (siempre sale de la tabla Emotes, nunca del usuario).
        public string? EmoteUrl { get; set; }

        public static ChatFragment FromText(string text) => new() { Type = TextType, Text = text };

        public static ChatFragment FromEmote(string code, string url) => new() { Type = EmoteType, Text = code, EmoteUrl = url };
    }
}
