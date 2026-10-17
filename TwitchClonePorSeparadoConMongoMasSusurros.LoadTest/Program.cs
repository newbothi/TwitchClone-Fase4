using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using TwitchClonePorSeparadoConMongoMasSusurros.LoadTest;

// ═══════════════════════════════════════════════════════════════════════════
//  Prueba de carga del chat (Fase 3)
//
//  Abre N conexiones de SignalR (cada una es un bot distinto), las une a un canal,
//  hace que algunas escriban a un ritmo fijo y mide:
//    • cuánto tarda el hub en aceptar cada mensaje (ack de SendMessage),
//    • cuánto tarda cada mensaje en llegarle a TODOS los demás (fan-out),
//    • cuántas entregas se perdieron.
//
//  Requisitos: la app corriendo en Development con Simulation:BotApiKey configurada.
//
//  Ejemplo:
//    dotnet run -c Release --project TwitchClonePorSeparadoConMongoMasSusurros.LoadTest -- ^
//        --url http://localhost:5145 --key dev-bots-XXXX --channel 1 --clients 500 --senders 50 --rate 10 --duration 60
// ═══════════════════════════════════════════════════════════════════════════

var options = LoadTestOptions.Parse(args);
if (options == null) return 1;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var baseUri = new Uri(options.Url.TrimEnd('/') + "/");
var acceptAnyCert = baseUri.IsLoopback; // certificado de desarrollo de ASP.NET Core

using var http = new HttpClient(new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = acceptAnyCert ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator : null
})
{ BaseAddress = baseUri, Timeout = TimeSpan.FromMinutes(5) };
http.DefaultRequestHeaders.Add("X-Bot-Key", options.Key);

// ── 1) Cuentas de bots ────────────────────────────────────────────────────
Console.WriteLine($"Preparando {options.Clients} cuentas de bots en {baseUri} ...");
List<int> botNumbers;
try
{
    var response = await http.PostAsync($"api/simulation/bots?count={options.Clients}", null);
    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        Console.Error.WriteLine("La API de simulación respondió 404: la app no está en Development o falta Simulation:BotApiKey (16+ caracteres).");
        return 2;
    }
    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
    {
        Console.Error.WriteLine("Clave de bots incorrecta (--key debe ser igual a Simulation:BotApiKey).");
        return 2;
    }
    response.EnsureSuccessStatusCode();
    var bots = await response.Content.ReadFromJsonAsync<BotsResponse>();
    botNumbers = bots?.Numbers?.ToList() ?? new List<int>();
}
catch (Exception ex)
{
    Console.Error.WriteLine("No se pudo contactar a la app: " + ex.Message);
    return 2;
}

if (botNumbers.Count < options.Clients)
{
    Console.WriteLine($"Solo hay {botNumbers.Count} bots disponibles (Simulation:MaxBots). Se usarán esos.");
    options.Clients = botNumbers.Count;
    options.Senders = Math.Min(options.Senders, options.Clients);
}
if (options.Clients == 0) { Console.Error.WriteLine("No hay bots."); return 2; }

// ── 2) Conexiones ─────────────────────────────────────────────────────────
var wsBase = new UriBuilder(baseUri) { Scheme = baseUri.Scheme == "https" ? "wss" : "ws" }.Uri;
var stats = new Stats();
var sentAt = new ConcurrentDictionary<long, long>();      // id del mensaje → Stopwatch timestamp
var connections = new MiniHubConnection?[options.Clients];
var deliveries = new List<double>[options.Clients];
for (var i = 0; i < options.Clients; i++) deliveries[i] = new List<double>();
var stop = new CancellationTokenSource();

Console.WriteLine($"Abriendo {options.Clients} conexiones en ~{options.RampSeconds} s ...");
var rampDelay = TimeSpan.FromSeconds(options.RampSeconds / (double)options.Clients);
var connectTasks = new List<Task>();
var rampWatch = Stopwatch.StartNew();
for (var i = 0; i < options.Clients; i++)
{
    var index = i;
    connectTasks.Add(Task.Run(async () =>
    {
        var conn = new MiniHubConnection(acceptAnyCert);
        var myDeliveries = deliveries[index];
        conn.OnInvocation = (target, arguments) =>
        {
            if (target != "ChatMessage") return;
            var received = Stopwatch.GetTimestamp();
            var message = arguments[0];
            var text = string.Concat(message.GetProperty("fragments").EnumerateArray().Select(f => f.GetProperty("text").GetString()));
            var marker = text.LastIndexOf("#lt", StringComparison.Ordinal);
            if (marker < 0 || !long.TryParse(text.AsSpan(marker + 3), NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return;
            if (sentAt.TryGetValue(id, out var sent))
            {
                lock (myDeliveries) myDeliveries.Add(Stopwatch.GetElapsedTime(sent, received).TotalMilliseconds);
            }
        };

        var started = Stopwatch.GetTimestamp();
        try
        {
            var uri = new Uri(wsBase, $"streamHub?bot_key={Uri.EscapeDataString(options.Key)}&bot_user={botNumbers[index]}");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await conn.ConnectAsync(uri, timeout.Token);
            await conn.InvokeAsync("JoinChannel", new object?[] { options.ChannelId }, timeout.Token);
            stats.AddConnect(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            connections[index] = conn;
        }
        catch (Exception ex)
        {
            stats.AddConnectError(ex.Message);
            await conn.DisposeAsync();
        }
    }));
    await Task.Delay(rampDelay);
}
await Task.WhenAll(connectTasks);
Console.WriteLine($"Conectadas {stats.Connected} de {options.Clients} en {rampWatch.Elapsed.TotalSeconds:0.0} s ({stats.ConnectErrors} fallidas).");
if (stats.Connected == 0)
{
    Console.Error.WriteLine("Ninguna conexión funcionó. Primer error: " + stats.FirstConnectError);
    return 3;
}

// ── 3) Mensajes ───────────────────────────────────────────────────────────
var senders = Enumerable.Range(0, options.Clients).Where(i => connections[i] != null).Take(options.Senders).ToList();
Console.WriteLine($"Enviando {options.Rate:0.##} mensajes/s desde {senders.Count} bots durante {options.DurationSeconds} s ...");

var receivers = connections.Count(c => c != null);
var sendTasks = new ConcurrentBag<Task>();
var interval = TimeSpan.FromSeconds(1.0 / options.Rate);
var sendWatch = Stopwatch.StartNew();
long nextId = 0;
var nextSend = TimeSpan.Zero;
var lastReport = TimeSpan.Zero;

while (sendWatch.Elapsed < TimeSpan.FromSeconds(options.DurationSeconds))
{
    var wait = nextSend - sendWatch.Elapsed;
    if (wait > TimeSpan.Zero) await Task.Delay(wait);
    nextSend += interval;

    var id = Interlocked.Increment(ref nextId);
    var conn = connections[senders[(int)(id % senders.Count)]]!;
    var content = $"{Phrases.Pick((int)id)} #lt{id}";

    sendTasks.Add(Task.Run(async () =>
    {
        var start = Stopwatch.GetTimestamp();
        sentAt[id] = start;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var result = await conn.InvokeAsync("SendMessage", new object?[] { options.ChannelId, content, null }, timeout.Token);
            var ackMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var ok = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
            var held = ok && result.TryGetProperty("heldForReview", out var heldProp) && heldProp.GetBoolean();
            if (held)
            {
                sentAt.TryRemove(id, out _);
                stats.AddRejected("automod"); // retenido para revisión: no se difunde
            }
            else if (ok) stats.AddAck(ackMs);
            else
            {
                sentAt.TryRemove(id, out _);
                var code = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("errorCode", out var c) ? c.GetString() ?? "?" : "?";
                stats.AddRejected(code);
            }
        }
        catch (Exception ex)
        {
            sentAt.TryRemove(id, out _);
            stats.AddRejected(ex is OperationCanceledException ? "timeout" : "error: " + ex.Message);
        }
    }));

    if (sendWatch.Elapsed - lastReport >= TimeSpan.FromSeconds(5))
    {
        lastReport = sendWatch.Elapsed;
        Console.WriteLine($"  {sendWatch.Elapsed.TotalSeconds,5:0}s  enviados {stats.Acked + stats.Rejected,6}  ok {stats.Acked,6}  rechazados {stats.Rejected,5}  conexiones abiertas {connections.Count(c => c?.IsOpen == true),5}");
    }
}

await Task.WhenAll(sendTasks);
Console.WriteLine("Esperando entregas pendientes ...");
await Task.Delay(TimeSpan.FromSeconds(3));
var closedDuringTest = connections.Count(c => c != null && !c.IsOpen);

// ── 4) Informe ────────────────────────────────────────────────────────────
var allDeliveries = deliveries.SelectMany(d => { lock (d) return d.ToArray(); }).ToArray();
Array.Sort(allDeliveries);
var expected = (long)stats.Acked * receivers;

Console.WriteLine();
Console.WriteLine("════════════════ RESULTADO ════════════════");
Console.WriteLine($"Canal:                {options.ChannelId}");
Console.WriteLine($"Conexiones:           {stats.Connected} abiertas, {stats.ConnectErrors} fallidas, {closedDuringTest} se cayeron durante la prueba");
Console.WriteLine($"Conectar + unirse:    {Stats.Describe(stats.ConnectTimes())}");
if (stats.FirstConnectError != null) Console.WriteLine($"Primer error:         {stats.FirstConnectError}");
Console.WriteLine($"Mensajes:             {stats.Acked + stats.Rejected} enviados · {stats.Acked} aceptados · {stats.Rejected} rechazados {stats.DescribeRejections()}");
Console.WriteLine($"Ack del hub:          {Stats.Describe(stats.AckTimes())}");
Console.WriteLine($"Entregas:             {allDeliveries.Length:N0} de {expected:N0} esperadas ({(expected == 0 ? 0 : 100.0 * allDeliveries.Length / expected):0.0} %)");
Console.WriteLine($"Latencia de entrega:  {Stats.Describe(allDeliveries)}");
Console.WriteLine($"Fan-out:              {allDeliveries.Length / Math.Max(1, options.DurationSeconds):N0} mensajes entregados por segundo");
Console.WriteLine("═══════════════════════════════════════════");

stop.Cancel();
await Task.WhenAll(connections.Where(c => c != null).Select(c => c!.DisposeAsync().AsTask()));
return 0;

// ═══════════════════════════════════════════════════════════════════════════

sealed class LoadTestOptions
{
    public string Url { get; set; } = "http://localhost:5145";
    public string Key { get; set; } = Environment.GetEnvironmentVariable("TWITCHCLONE_BOT_KEY") ?? string.Empty;
    public int ChannelId { get; set; }
    public int Clients { get; set; } = 200;
    public int Senders { get; set; } = 20;
    public double Rate { get; set; } = 5;
    public int DurationSeconds { get; set; } = 30;
    public int RampSeconds { get; set; } = 10;

    public static LoadTestOptions? Parse(string[] args)
    {
        var o = new LoadTestOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Falta el valor de {args[i]}");
            switch (args[i])
            {
                case "--url": o.Url = Next(); break;
                case "--key": o.Key = Next(); break;
                case "--channel": o.ChannelId = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--clients": o.Clients = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--senders": o.Senders = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--rate": o.Rate = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--duration": o.DurationSeconds = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--ramp": o.RampSeconds = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "-h":
                case "--help":
                    PrintHelp();
                    return null;
                default:
                    Console.Error.WriteLine($"Opción desconocida: {args[i]}");
                    PrintHelp();
                    return null;
            }
        }

        if (o.ChannelId <= 0 || string.IsNullOrWhiteSpace(o.Key))
        {
            PrintHelp();
            return null;
        }

        o.Clients = Math.Clamp(o.Clients, 1, 10_000);
        o.Senders = Math.Clamp(o.Senders, 1, o.Clients);
        o.Rate = Math.Clamp(o.Rate, 0.1, 1000);
        o.DurationSeconds = Math.Clamp(o.DurationSeconds, 5, 3600);
        o.RampSeconds = Math.Clamp(o.RampSeconds, 0, 600);
        return o;
    }

    static void PrintHelp()
    {
        Console.WriteLine("""
            Prueba de carga del chat de TwitchClone.

            Uso:
              dotnet run -c Release -- --channel 1 --key <Simulation:BotApiKey> [opciones]

            Opciones:
              --url URL        Dirección de la app (por defecto http://localhost:5145)
              --key CLAVE      Simulation:BotApiKey de appsettings.Development.json (o variable TWITCHCLONE_BOT_KEY)
              --channel ID     Canal donde se conectan los bots (obligatorio)
              --clients N      Conexiones simultáneas, cada una un bot distinto (por defecto 200)
              --senders N      Cuántas de ellas escriben (por defecto 20)
              --rate R         Mensajes por segundo en total (por defecto 5)
              --duration S     Segundos enviando mensajes (por defecto 30)
              --ramp S         Segundos para abrir todas las conexiones (por defecto 10)

            Ojo: cada bot puede mandar como máximo 20 mensajes cada 30 s (el mismo límite
            que una persona). Si rate / senders > 0,66 verás rechazos "rate_limited":
            sube --senders. El modo lento y "solo seguidores" del canal también aplican.
            """);
    }
}

sealed record BotsResponse(int Total, List<int>? Numbers);

static class Phrases
{
    private static readonly string[] Items =
    {
        "jajaja buena", "qué jugada", "hola chat", "GG", "KEKW", "vamos!!", "saludos desde Santiago",
        "se ve bien la calidad", "a qué hora termina?", "PogChamp", "buenísimo", "+1", "xd", "esa estuvo cerca"
    };

    public static string Pick(int n) => Items[n % Items.Length];
}

sealed class Stats
{
    private readonly ConcurrentBag<double> _connect = new();
    private readonly ConcurrentBag<double> _ack = new();
    private readonly ConcurrentDictionary<string, int> _rejections = new();
    private int _connectErrors, _acked, _rejected;

    public int Connected => _connect.Count;
    public int ConnectErrors => _connectErrors;
    public int Acked => _acked;
    public int Rejected => _rejected;
    public string? FirstConnectError { get; private set; }

    public void AddConnect(double ms) => _connect.Add(ms);
    public void AddConnectError(string error)
    {
        Interlocked.Increment(ref _connectErrors);
        FirstConnectError ??= error;
    }
    public void AddAck(double ms) { _ack.Add(ms); Interlocked.Increment(ref _acked); }
    public void AddRejected(string code) { _rejections.AddOrUpdate(code, 1, (_, n) => n + 1); Interlocked.Increment(ref _rejected); }

    public double[] ConnectTimes() { var a = _connect.ToArray(); Array.Sort(a); return a; }
    public double[] AckTimes() { var a = _ack.ToArray(); Array.Sort(a); return a; }

    public string DescribeRejections() =>
        _rejections.IsEmpty ? string.Empty : "(" + string.Join(", ", _rejections.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")) + ")";

    // Recibe un arreglo ORDENADO.
    public static string Describe(double[] sorted)
    {
        if (sorted.Length == 0) return "sin datos";
        double P(double p) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];
        return $"p50 {P(0.50):0} ms · p95 {P(0.95):0} ms · p99 {P(0.99):0} ms · máx {sorted[^1]:0} ms";
    }
}
