using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Ai
{
    // Mensajes de chat generados por un LLM local (Ollama) para el simulador de bots.
    // Se piden en tandas (no uno por mensaje) y el simulador los va gastando: así un
    // modelo de 3B parámetros alcanza para cientos de bots.
    public class OllamaChatGenerator : IAiChatGenerator
    {
        private readonly HttpClient _http;
        private readonly OllamaSettings _ollama;
        private readonly SimulationSettings _simulation;
        private readonly ILogger<OllamaChatGenerator> _logger;

        public OllamaChatGenerator(HttpClient http, IOptions<OllamaSettings> ollama, IOptions<SimulationSettings> simulation, ILogger<OllamaChatGenerator> logger)
        {
            _http = http;
            _ollama = ollama.Value;
            _simulation = simulation.Value;
            _logger = logger;
        }

        public bool IsAvailable => _simulation.UseAi;

        public async Task<IReadOnlyList<string>> GenerateAsync(string channelTitle, string category, IReadOnlyList<string> recentChat, int count, CancellationToken ct)
        {
            var prompt = new StringBuilder();
            prompt.AppendLine($"Genera {count} mensajes distintos que escribirían espectadores en el chat de un stream en vivo.");
            prompt.AppendLine($"Título del stream: \"{channelTitle}\". Categoría: \"{category}\".");
            if (recentChat.Count > 0)
            {
                prompt.AppendLine("Últimos mensajes del chat (reacciona a algunos):");
                foreach (var line in recentChat.TakeLast(12)) prompt.AppendLine("- " + line);
            }
            prompt.AppendLine("Reglas: español chileno informal (\"wena\", \"po\", \"bkn\", \"jajaja\", \"xd\"), entre 1 y 12 palabras, " +
                              "mezcla de reacciones, preguntas al streamer, bromas y saludos. Sin insultos, sin contenido sexual, sin enlaces, " +
                              "sin hashtags. No numeres los mensajes.");
            prompt.AppendLine("Responde solo con JSON: {\"mensajes\": [\"...\", \"...\"]}");

            var request = new
            {
                model = _ollama.Model,
                stream = false,
                format = "json",
                options = new { temperature = 0.95 },
                messages = new object[] { new { role = "user", content = prompt.ToString() } }
            };

            try
            {
                using var response = await _http.PostAsJsonAsync("api/chat", request, ct);
                response.EnsureSuccessStatusCode();
                var content = await OllamaJson.ReadMessageContentAsync(response, ct);

                using var doc = JsonDocument.Parse(content);
                if (!doc.RootElement.TryGetProperty("mensajes", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    return Array.Empty<string>();

                return arr.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!.Trim().Trim('"'))
                    .Where(s => s.Length is > 0 and <= 200)
                    .ToList();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                _logger.LogDebug(ex, "Ollama no generó mensajes para el simulador; se usan plantillas.");
                return Array.Empty<string>();
            }
        }
    }
}
