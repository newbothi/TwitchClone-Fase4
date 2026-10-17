namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces
{
    // Fase 4: adónde van las decisiones de los moderadores para que AutoMod aprenda.
    // Implementaciones (Infrastructure):
    //  • Http    — POST {AutoMod:FeedbackUrl} (el servicio Python de python/automod).
    //  • Ninguno — no se envía nada (AutoMod con Ollama, sin IA, o SendFeedback = false).
    //
    // Enqueue NO puede bloquear ni lanzar excepciones: se llama desde el chat en vivo.
    public interface IAutoModFeedbackSink
    {
        bool IsEnabled { get; }
        void Enqueue(AutoModFeedback feedback);
    }

    public record AutoModFeedback(int ChannelId, string Text, bool IsToxic, string? Category, string Source);
}
