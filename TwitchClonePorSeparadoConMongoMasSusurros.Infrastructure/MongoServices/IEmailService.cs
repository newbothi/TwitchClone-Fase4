
using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Data;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
//using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MongoServices;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MongoServices
{
    public interface IEmailService
    {
        Task SendEmailAsync(string email, string subject, string body);
    }
}