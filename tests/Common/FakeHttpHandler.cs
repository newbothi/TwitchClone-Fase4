using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Common
{
    // Servidor HTTP falso para probar los clientes (AutoMod, feedback) sin red.
    // Anota cada petición y responde lo que la prueba diga.
    public sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<RecordedRequest> _requests = new();

        public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body);

        // Respuesta por defecto: 200 con "{}". Se puede cambiar por prueba.
        public Func<RecordedRequest, HttpResponseMessage> Respond { get; set; } = _ => Json("{}");

        public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

        public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var recorded = new RecordedRequest(request.Method, request.RequestUri!, body);
            _requests.Enqueue(recorded);
            return Respond(recorded);
        }
    }
}
