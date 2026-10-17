using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models
{
    public class StreamSession
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = null!;
        public string ChannelId { get; set; } = null!;
        public string ChannelName { get; set; } = null!;
        public string Title { get; set; } = string.Empty;
        public string CategoryName { get; set; } = string.Empty;
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
        public DateTime? EndedAt { get; set; }
        public int PeakViewers { get; set; } = 0;
        public int AvgViewers { get; set; } = 0;
        public long DurationSeconds { get; set; } = 0;
        public string? VodUrl { get; set; }
        public List<string> Tags { get; set; } = new();
    }
}
