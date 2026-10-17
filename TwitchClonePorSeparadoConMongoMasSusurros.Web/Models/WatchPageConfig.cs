using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Models
{
    // Datos que la página Watch le pasa a wwwroot/js/watch.js como JSON
    // (<script type="application/json" id="watch-config">).
    public class WatchPageConfig
    {
        public int ChannelId { get; set; }
        public string HlsUrl { get; set; } = string.Empty;
        public bool IsLive { get; set; }
        public bool IsAuthenticated { get; set; }
        public string? CurrentUserId { get; set; }
        public string? CurrentDisplayName { get; set; }  // para resaltar menciones (@nombre)
        public bool CanModerate { get; set; }
        public bool ChatUnavailable { get; set; }
        public List<ChatMessageDto> History { get; set; } = new();
    }
}
