using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;

using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.Data;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Models;
//using TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MongoServices;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Infrastructure.MongoServices
{
    public class EmailService : IEmailService
    {
        private readonly IConfiguration _configuration;

        public EmailService(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public async Task SendEmailAsync(string email, string subject, string body)
        {
            try
            {
                var smtpServer = _configuration["EmailSettings:SmtpServer"];
                if (string.IsNullOrEmpty(smtpServer))
                    throw new InvalidOperationException("SMTP server no configurado");

                var smtpPortStr = _configuration["EmailSettings:SmtpPort"];
                if (string.IsNullOrEmpty(smtpPortStr) || !int.TryParse(smtpPortStr, out int smtpPort))
                    throw new InvalidOperationException("SMTP port no válido");

                var senderEmail = _configuration["EmailSettings:SenderEmail"];
                if (string.IsNullOrEmpty(senderEmail))
                    throw new InvalidOperationException("Sender email no configurado");

                var senderPassword = _configuration["EmailSettings:SenderPassword"];
                if (string.IsNullOrEmpty(senderPassword))
                    throw new InvalidOperationException("Sender password no configurado");

                using (var client = new SmtpClient(smtpServer, smtpPort))
                {
                    client.EnableSsl = true;
                    client.Credentials = new NetworkCredential(senderEmail, senderPassword);

                    var mailMessage = new MailMessage(senderEmail, email, subject, body)
                    {
                        IsBodyHtml = true
                    };

                    await client.SendMailAsync(mailMessage);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error enviando email: {ex.Message}");
            }
        }
    }
}