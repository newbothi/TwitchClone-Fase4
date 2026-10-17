using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Settings;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Models
{
    public class SimulatorPageModel
    {
        public SimulationSettings Settings { get; set; } = new();
        public string IngestUrl { get; set; } = string.Empty;
        public int MaxBots { get; set; }
        public List<ChannelSummary> Channels { get; set; } = new();
    }
}
