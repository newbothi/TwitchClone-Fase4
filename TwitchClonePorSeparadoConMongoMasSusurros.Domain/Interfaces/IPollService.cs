using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces
{
    // Encuestas en vivo (una activa por canal). Viven en memoria mientras duran.
    public interface IPollService
    {
        PollDto? GetActive(int channelId);

        // Error != null si no se pudo crear (ya hay una activa, opciones inválidas...).
        (PollDto? Poll, string? Error) Start(int channelId, string question, IReadOnlyList<string> options, TimeSpan duration, string createdBy);

        // Un voto por usuario; se puede cambiar de opción mientras la encuesta siga abierta.
        (PollDto? Poll, string? Error) Vote(int channelId, string pollId, string userId, int optionIndex);

        PollDto? End(int channelId);

        // Encuestas que cambiaron desde la última vez (para difundir como mucho 1 vez por segundo)
        // y las que vencieron (se cierran aquí).
        IReadOnlyList<PollDto> DrainChanged();
    }
}
