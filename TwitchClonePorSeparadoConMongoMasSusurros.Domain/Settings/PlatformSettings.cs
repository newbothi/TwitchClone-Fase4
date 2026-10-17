namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings
{
    // Sección "AutoMod" de appsettings.json.
    public class AutoModSettings
    {
        public const string SectionName = "AutoMod";

        // "None" (solo reglas), "Ollama" o "Http".
        public string Provider { get; set; } = "None";

        // Para Provider = "Http": POST {HttpUrl} con { text } → { flagged, category, reason }.
        public string HttpUrl { get; set; } = "http://localhost:8001/moderate";

        // Si el clasificador tarda más, el mensaje pasa igual (no se congela el chat).
        public int TimeoutMs { get; set; } = 2500;

        // Fase 4 (solo con Provider = "Http"): las decisiones de los moderadores
        // (aprobar/rechazar en AutoMod, borrar mensajes) se envían aquí para que el
        // modelo de python/automod aprenda de ellas.
        public bool SendFeedback { get; set; } = true;
        public string FeedbackUrl { get; set; } = "http://localhost:8001/feedback";
    }

    // Sección "Ollama": LLM local usado por AutoMod y por el simulador de bots.
    public class OllamaSettings
    {
        public const string SectionName = "Ollama";
        public string Url { get; set; } = "http://localhost:11434";
        public string Model { get; set; } = "llama3.2";
    }

    // Sección "Points": puntos del canal por ver streams en vivo.
    public class PointsSettings
    {
        public const string SectionName = "Points";
        public int IntervalMinutes { get; set; } = 5;
        public int AmountPerInterval { get; set; } = 10;
    }

    // Sección "Simulation" (Fase 3).
    public class SimulationSettings
    {
        public const string SectionName = "Simulation";

        // Clave para que la prueba de carga (proyecto LoadTest) se conecte como bots.
        // Vacía = desactivado. Solo funciona en entorno Development.
        public string BotApiKey { get; set; } = string.Empty;

        // Ruta a ffmpeg (para streams simulados y miniaturas). "ffmpeg" si está en el PATH.
        public string FfmpegPath { get; set; } = "ffmpeg";

        // Carpeta opcional con .mp4: los streams simulados los transmiten en bucle.
        public string VideosFolder { get; set; } = string.Empty;

        // Usar Ollama para que los bots escriban mensajes con contexto.
        public bool UseAi { get; set; } = false;

        // Cada cuántos segundos se regeneran las miniaturas de los canales en vivo (0 = desactivado).
        public int ThumbnailIntervalSeconds { get; set; } = 45;

        // Calidad de los streams simulados. 1280x720 a 30 fps y 2500 kbps es lo que
        // recomienda Twitch para 720p; bájalo (854x480, 1200) si vas a simular muchos canales.
        public string StreamResolution { get; set; } = "1280x720";
        public int StreamFps { get; set; } = 30;
        public int StreamBitrateKbps { get; set; } = 2500;

        // Tope de cuentas de bots que se pueden crear.
        public int MaxBots { get; set; } = 2000;
    }
}
