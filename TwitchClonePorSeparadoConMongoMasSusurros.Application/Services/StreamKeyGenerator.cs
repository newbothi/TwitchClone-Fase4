using System.Security.Cryptography;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Application.Services
{
    // Claves de transmisión criptográficamente aleatorias (antes eran Guid.NewGuid()).
    // Formato "sk_" + 32 caracteres URL-safe, sin caracteres que confundan a OBS.
    // Las claves GUID antiguas siguen funcionando hasta que el streamer regenere la suya.
    public static class StreamKeyGenerator
    {
        public static string NewKey()
        {
            var bytes = RandomNumberGenerator.GetBytes(24);
            var token = Convert.ToBase64String(bytes)
                .Replace('+', 'A')
                .Replace('/', 'z')
                .TrimEnd('=');
            return "sk_" + token;
        }
    }
}
