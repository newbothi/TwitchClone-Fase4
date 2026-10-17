using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Chat;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Realtime
{
    // Fase 4: cuando un moderador aprueba/rechaza un mensaje de AutoMod o borra uno,
    // ChatService devuelve un AutoModFeedbackEffect. Este observador lo pasa a la cola
    // que lo manda al servicio Python (python/automod), que reentrena el modelo.
    // Funciona igual venga la acción del chat, del Dashboard o de un comando.
    public sealed class AutoModFeedbackObserver : IChatEventObserver
    {
        private readonly IAutoModFeedbackSink _sink;

        public AutoModFeedbackObserver(IAutoModFeedbackSink sink) => _sink = sink;

        public Task OnEffectsAsync(int channelId, IReadOnlyList<ChatEffect> effects)
        {
            if (!_sink.IsEnabled) return Task.CompletedTask;

            foreach (var e in effects.OfType<AutoModFeedbackEffect>())
                _sink.Enqueue(new AutoModFeedback(channelId, e.Text, e.IsToxic, e.Category, e.Source));

            return Task.CompletedTask;
        }
    }
}
