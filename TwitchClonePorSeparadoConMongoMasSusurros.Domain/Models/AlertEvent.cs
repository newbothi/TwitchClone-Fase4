using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models
{
    public class AlertEvent
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = null!;
        public string ChannelId { get; set; } = null!;
        public string Type { get; set; } = null!;
        public string FromUserName { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public int Amount { get; set; } = 0;
        public bool IsShown { get; set; } = false;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
