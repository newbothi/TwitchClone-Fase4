using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Ai
{
    // AutoMod sin IA: solo funcionan las reglas (enlaces, repetidos, palabras bloqueadas...).
    public class NullAutoModClassifier : IAutoModClassifier
    {
        public bool IsAvailable => false;
        public string ProviderName => "Ninguno";
        public Task<AutoModVerdict> ClassifyAsync(string text, CancellationToken ct) => Task.FromResult(AutoModVerdict.Clean);
    }

    // AutoMod con un modelo local de Ollama (POST /api/chat con salida JSON).
    // Probado con el formato de respuesta de Ollama; el modelo se elige en "Ollama:Model".
    public class OllamaAutoModClassifier : IAutoModClassifier
    {
        private const string SystemPrompt =
            "Eres el moderador automático del chat de una plataforma de streaming en español (Chile y Latinoamérica). " +
            "Clasifica el mensaje del usuario. Marca flag=true SOLO si contiene: insultos o acoso dirigidos a una persona, " +
            "discurso de odio, contenido sexual explícito, amenazas, o spam/estafas. " +
            "Las groserías casuales sin destinatario y las bromas normales NO se marcan. " +
            "Responde únicamente con JSON: {\"flag\": true|false, \"category\": \"insulto|acoso|odio|sexual|amenaza|spam|ninguna\", \"reason\": \"explicación breve en español\"}";

        private readonly HttpClient _http;
        private readonly OllamaSettings _settings;
        private readonly ILogger<OllamaAutoModClassifier> _logger;

        public OllamaAutoModClassifier(HttpClient http, IOptions<OllamaSettings> settings, ILogger<OllamaAutoModClassifier> logger)
        {
            _http = http;
            _settings = settings.Value;
            _logger = logger;
        }

        public bool IsAvailable => true;
        public string ProviderName => $"Ollama ({_settings.Model})";

        public async Task<AutoModVerdict> ClassifyAsync(string text, CancellationToken ct)
        {
            var request = new
            {
                model = _settings.Model,
                stream = false,
                format = "json",
                options = new { temperature = 0 },
                messages = new object[]
                {
                    new { role = "system", content = SystemPrompt },
                    new { role = "user", content = text }
                }
            };

            using var response = await _http.PostAsJsonAsync("api/chat", request, ct);
            response.EnsureSuccessStatusCode();
            var content = await OllamaJson.ReadMessageContentAsync(response, ct);

            using var verdict = JsonDocument.Parse(content);
            var root = verdict.RootElement;
            var flagged = root.TryGetProperty("flag", out var f) && f.ValueKind == JsonValueKind.True;
            var category = root.TryGetProperty("category", out var c) ? c.GetString() ?? "ninguna" : "ninguna";
            var reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? string.Empty : string.Empty;

            if (flagged) _logger.LogInformation("AutoMod (Ollama) retuvo un mensaje: {Category} — {Reason}", category, reason);
            return new AutoModVerdict(flagged, category, reason);
        }
    }

    // AutoMod con un servicio HTTP propio (por ejemplo, tu microservicio Python).
    // Contrato:  POST {AutoMod:HttpUrl}   { "text": "..." }
    //            → { "flagged": bool, "category": "insulto", "reason": "..." }
    // También acepta "flag" o "toxic" en vez de "flagged" para adaptarse a servicios existentes.
    public class HttpAutoModClassifier : IAutoModClassifier
    {
        private readonly HttpClient _http;
        private readonly AutoModSettings _settings;

        public HttpAutoModClassifier(HttpClient http, IOptions<AutoModSettings> settings)
        {
            _http = http;
            _settings = settings.Value;
        }

        public bool IsAvailable => !string.IsNullOrWhiteSpace(_settings.HttpUrl);
        public string ProviderName => $"HTTP ({_settings.HttpUrl})";

        public async Task<AutoModVerdict> ClassifyAsync(string text, CancellationToken ct)
        {
            using var response = await _http.PostAsJsonAsync(_settings.HttpUrl, new { text }, ct);
            response.EnsureSuccessStatusCode();

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = doc.RootElement;

            bool IsTrue(string name) => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
            var flagged = IsTrue("flagged") || IsTrue("flag") || IsTrue("toxic");
            var category = root.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : (flagged ? "otro" : "ninguna");
            var reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : string.Empty;

            return new AutoModVerdict(flagged, category, reason);
        }
    }

    internal static class OllamaJson
    {
        // Respuesta de /api/chat sin streaming: { "message": { "role": "assistant", "content": "..." }, ... }
        public static async Task<string> ReadMessageContentAsync(HttpResponseMessage response, CancellationToken ct)
        {
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "{}";
        }
    }
}
