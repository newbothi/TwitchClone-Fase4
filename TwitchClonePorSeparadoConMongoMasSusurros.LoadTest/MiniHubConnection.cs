using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TwitchClonePorSeparadoConMongoMasSusurros.LoadTest
{
    // Cliente mínimo de SignalR (protocolo JSON, transporte WebSocket, sin negociación).
    // Lo justo para la prueba de carga: conectarse, invocar métodos del hub y recibir
    // eventos. Así la prueba no depende de Microsoft.AspNetCore.SignalR.Client.
    //
    // Formato: cada mensaje es un JSON terminado en el carácter 0x1E.
    //   handshake  {"protocol":"json","version":1}
    //   1 = invocación  {"type":1,"invocationId":"7","target":"SendMessage","arguments":[...]}
    //   3 = respuesta   {"type":3,"invocationId":"7","result":...}  o  "error":"..."
    //   6 = ping        7 = cierre
    public sealed class MiniHubConnection : IAsyncDisposable
    {
        private const byte RecordSeparator = 0x1E;

        private readonly ClientWebSocket _ws = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
        private readonly CancellationTokenSource _cts = new();
        private Task _receiveLoop = Task.CompletedTask;
        private Task _pingLoop = Task.CompletedTask;
        private int _nextId;

        // (target, arguments) — se llama desde el hilo de recepción de esta conexión.
        // El JsonElement solo es válido durante la llamada (no guardarlo).
        public Action<string, JsonElement>? OnInvocation { get; set; }
        public string? CloseReason { get; private set; }
        public bool IsOpen => _ws.State == WebSocketState.Open;

        public MiniHubConnection(bool acceptAnyCertificate)
        {
            if (acceptAnyCertificate)
                _ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true; // certificado de desarrollo
            _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        }

        public async Task ConnectAsync(Uri hubUri, CancellationToken ct)
        {
            await _ws.ConnectAsync(hubUri, ct);
            await SendRawAsync("{\"protocol\":\"json\",\"version\":1}", ct);

            // La respuesta del handshake es "{}" (o {"error":"..."}).
            var handshake = await ReceiveOneRecordAsync(ct);
            using (var doc = JsonDocument.Parse(handshake))
            {
                if (doc.RootElement.TryGetProperty("error", out var error))
                    throw new InvalidOperationException("Handshake rechazado: " + error.GetString());
            }

            _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            _pingLoop = Task.Run(() => PingLoopAsync(_cts.Token));
        }

        public async Task<JsonElement> InvokeAsync(string target, object?[] arguments, CancellationToken ct)
        {
            var id = Interlocked.Increment(ref _nextId).ToString();
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            var payload = JsonSerializer.Serialize(new { type = 1, invocationId = id, target, arguments });
            try
            {
                await SendRawAsync(payload, ct);
                using var reg = ct.Register(() => tcs.TrySetCanceled());
                return await tcs.Task;
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        private async Task SendRawAsync(string json, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(json + (char)RecordSeparator);
            await _sendLock.WaitAsync(ct);
            try
            {
                await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task PingLoopAsync(CancellationToken ct)
        {
            // El servidor corta a los clientes que no mandan nada en 30 s.
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            try
            {
                while (await timer.WaitForNextTickAsync(ct) && IsOpen)
                    await SendRawAsync("{\"type\":6}", ct);
            }
            catch { /* conexión cerrada */ }
        }

        private readonly ArrayBufferWriter<byte> _buffer = new(4096);
        private readonly byte[] _chunk = new byte[8192];

        private async Task<string> ReceiveOneRecordAsync(CancellationToken ct)
        {
            var chunk = _chunk;
            while (true)
            {
                var span = _buffer.WrittenSpan;
                var end = span.IndexOf(RecordSeparator);
                if (end >= 0)
                {
                    var record = Encoding.UTF8.GetString(span[..end]);
                    var rest = span[(end + 1)..].ToArray();
                    _buffer.Clear();
                    _buffer.Write(rest);
                    return record;
                }

                var result = await _ws.ReceiveAsync(chunk, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new WebSocketException("El servidor cerró la conexión: " + result.CloseStatusDescription);
                _buffer.Write(chunk.AsSpan(0, result.Count));
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var record = await ReceiveOneRecordAsync(ct);
                    Handle(record);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
                CloseReason ??= ex.Message;
            }
            finally
            {
                foreach (var pending in _pending.Values) pending.TrySetException(new WebSocketException(CloseReason ?? "Conexión cerrada."));
            }
        }

        private void Handle(string record)
        {
            using var doc = JsonDocument.Parse(record);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetInt32();
            switch (type)
            {
                case 1:
                    var target = root.GetProperty("target").GetString() ?? string.Empty;
                    OnInvocation?.Invoke(target, root.GetProperty("arguments"));
                    break;

                case 3:
                    var id = root.GetProperty("invocationId").GetString();
                    if (id != null && _pending.TryGetValue(id, out var tcs))
                    {
                        if (root.TryGetProperty("error", out var error))
                            tcs.TrySetException(new InvalidOperationException(error.GetString()));
                        else
                            tcs.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
                    }
                    break;

                case 7:
                    CloseReason = root.TryGetProperty("error", out var reason) ? reason.GetString() : "Cerrada por el servidor.";
                    _cts.Cancel();
                    break;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try
            {
                if (_ws.State == WebSocketState.Open)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "fin de la prueba", timeout.Token);
                }
            }
            catch { /* ya estaba cerrada */ }
            try { await Task.WhenAll(_receiveLoop, _pingLoop).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            _ws.Dispose();
        }
    }
}
