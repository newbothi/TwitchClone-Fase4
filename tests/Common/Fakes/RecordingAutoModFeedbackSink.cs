using System.Collections.Concurrent;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Common.Fakes
{
    // Fase 4: en vez de mandar el feedback al servicio Python, lo anota para la prueba.
    public sealed class RecordingAutoModFeedbackSink : IAutoModFeedbackSink
    {
        private readonly ConcurrentQueue<AutoModFeedback> _items = new();

        public bool IsEnabled { get; set; } = true;
        public IReadOnlyList<AutoModFeedback> Items => _items.ToArray();

        public void Enqueue(AutoModFeedback feedback) => _items.Enqueue(feedback);
    }
}
