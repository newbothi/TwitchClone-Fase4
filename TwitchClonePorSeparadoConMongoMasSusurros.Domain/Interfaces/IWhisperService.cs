using System.Collections.Generic;
using System.Threading.Tasks;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces
{
    public interface IWhisperService
    {
        Task<WhisperMessage> SendAsync(string senderId, string senderUserName, string recipientId, string recipientUserName, string content);
        Task<List<WhisperMessage>> GetConversationAsync(string conversationId, int limit = 100);
        Task<List<WhisperConversationSummary>> GetConversationsForUserAsync(string userId);
        Task MarkConversationAsReadAsync(string conversationId, string readerId);
        Task<int> GetUnreadCountAsync(string userId);
    }

    // Resumen de una conversación para la lista tipo "inbox" — no vive en
    // Mongo como colección propia, se calcula al vuelo a partir de WhisperMessage.
    public class WhisperConversationSummary
    {
        public string ConversationId { get; set; } = string.Empty;
        public string OtherUserId { get; set; } = string.Empty;
        public string OtherUserName { get; set; } = string.Empty;
        public string LastMessage { get; set; } = string.Empty;
        public DateTime LastMessageAt { get; set; }
        public int UnreadCount { get; set; }
    }
}
