namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces
{
    // Clasificador de mensajes para AutoMod. Implementaciones (Infrastructure):
    //  • Ollama  — un modelo local (llama3.2, qwen2.5, etc.) decide si el mensaje es ofensivo.
    //  • Http    — cualquier servicio propio (por ejemplo, tu microservicio Python).
    //  • Ninguno — AutoMod con IA desactivado (siguen funcionando las reglas: enlaces, repetidos...).
    public interface IAutoModClassifier
    {
        bool IsAvailable { get; }
        string ProviderName { get; }

        // Debe responder rápido: el chat espera como máximo AutoMod:TimeoutMs y,
        // si no hay respuesta, deja pasar el mensaje (falla "abierto").
        Task<AutoModVerdict> ClassifyAsync(string text, CancellationToken ct);
    }

    public record AutoModVerdict(bool Flagged, string Category, string Reason)
    {
        public static AutoModVerdict Clean { get; } = new(false, "ninguna", string.Empty);
    }
}
