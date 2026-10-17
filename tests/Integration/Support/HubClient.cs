using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR.Client;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.DTOs;
using TwitchClonePorSeparadoConMongoMasSusurros.Domain.Interfaces;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Hubs;
using TwitchClonePorSeparadoConMongoMasSusurros.Web.Realtime;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Tests.Integration.Support
{
    // Una pestaña del navegador conectada al StreamHub. Anota todos los eventos que
    // le llegan para que la prueba pueda esperarlos (WaitForAsync) o comprobar que
    // NO llegaron (después de una "barrera", ver HubChatTests).
    public sealed class HubClient : IAsyncDisposable
    {
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

        private readonly ConcurrentQueue<HubEvent> _events = new();

        public HubClient(HubConnection connection, string? userId)
        {
            Connection = connection;
            UserId = userId;

            // Los mismos eventos que escucha wwwroot/js (ver comentario en StreamHub).
            Record<ChatMessageDto>("ChatMessage");
            Record<string>("MessageDeleted");
            Record<string, string?, int>("UserTimedOut");
            Record<string, string?>("UserBanned");
            Record("ChatCleared");
            Record<int, int>("TimedOut");
            Record<int>("Banned");
            Record<int>("Unbanned");
            Record<int>("ViewerCountUpdated");
            Record("StreamStarted");
            Record("StreamEnded");

            // Fase 2
            Record<ChatSettingsDto>("ChatSettingsChanged");
            Record<AutoModHeldDto>("AutoModHeld");
            Record<string, bool, string>("AutoModResolved");
            Record<int, string>("ChatNotice");
            Record<ChatMessageDto?>("PinChanged");
            Record<string, string, string>("UserRoleChanged");
            Record<int, string>("RoleChanged");
        }

        public HubConnection Connection { get; }
        public string? UserId { get; }

        public sealed record HubEvent(string Name, object?[] Args)
        {
            public T Arg<T>(int index) => (T)Args[index]!;
            public override string ToString() => $"{Name}({string.Join(", ", Args.Select(a => a?.ToString() ?? "null"))})";
        }

        // ── Llamadas al hub ──────────────────────────────────────────────

        public Task<JoinChannelResult> JoinAsync(int channelId) =>
            Connection.InvokeAsync<JoinChannelResult>("JoinChannel", channelId);

        public Task<ChatSendResult> SendAsync(int channelId, string content, string? replyToId = null) =>
            Connection.InvokeAsync<ChatSendResult>("SendMessage", channelId, content, replyToId);

        public Task<ChatActionResult> ModerateAsync(string method, params object?[] args) =>
            Connection.InvokeCoreAsync<ChatActionResult>(method, args);

        public Task<ViewerListDto> GetViewersAsync(int channelId) =>
            Connection.InvokeAsync<ViewerListDto>("GetViewers", channelId);

        // ── Eventos recibidos ────────────────────────────────────────────

        public IReadOnlyList<HubEvent> Received(string name) => _events.Where(e => e.Name == name).ToList();

        public IReadOnlyList<ChatMessageDto> ChatMessages => Received("ChatMessage").Select(e => e.Arg<ChatMessageDto>(0)).ToList();

        public async Task<HubEvent> WaitForAsync(string name, Func<HubEvent, bool>? match = null, TimeSpan? timeout = null)
        {
            var limit = timeout ?? DefaultTimeout;
            var found = await Eventually.FindAsync(
                () => _events.FirstOrDefault(e => e.Name == name && (match == null || match(e))),
                limit);

            if (found == null)
            {
                var received = string.Join(Environment.NewLine + "  ", _events.Select(e => e.ToString()));
                Assert.Fail($"{Who} no recibió '{name}' en {limit.TotalSeconds:0.#} s. Recibió:{Environment.NewLine}  {(received.Length == 0 ? "(nada)" : received)}");
            }
            return found!;
        }

        // Espera un mensaje de chat con ese texto (concatenando sus fragmentos).
        public async Task<ChatMessageDto> WaitForChatAsync(string text, TimeSpan? timeout = null)
        {
            var e = await WaitForAsync("ChatMessage", ev => TextOf(ev.Arg<ChatMessageDto>(0)) == text, timeout);
            return e.Arg<ChatMessageDto>(0);
        }

        public static string TextOf(ChatMessageDto message) => string.Concat(message.Fragments.Select(f => f.Text));

        private string Who => UserId == null ? "El visitante anónimo" : $"El usuario '{UserId}'";

        private void Record(string name) =>
            Connection.On(name, () => _events.Enqueue(new HubEvent(name, Array.Empty<object?>())));

        private void Record<T1>(string name) =>
            Connection.On<T1>(name, a => _events.Enqueue(new HubEvent(name, new object?[] { a })));

        private void Record<T1, T2>(string name) =>
            Connection.On<T1, T2>(name, (a, b) => _events.Enqueue(new HubEvent(name, new object?[] { a, b })));

        private void Record<T1, T2, T3>(string name) =>
            Connection.On<T1, T2, T3>(name, (a, b, c) => _events.Enqueue(new HubEvent(name, new object?[] { a, b, c })));

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }
}
