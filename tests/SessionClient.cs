using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NKeeper.Testing;

namespace NKeeper.Plugins.Storage.Tests;

/// <summary>The browser's end of a session.</summary>
internal sealed class SessionClient(WebSocket socket) : IAsyncDisposable
{
    private readonly StringBuilder _output = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static async Task<SessionClient> ConnectAsync(TestServer server, string cookie, string type)
    {
        var client = new ClientWebSocket();
        client.Options.SetRequestHeader("Origin", server.BaseAddress.GetLeftPart(UriPartial.Authority));
        client.Options.SetRequestHeader("Cookie", cookie);
        var address = new UriBuilder(new Uri(server.BaseAddress, $"/api/sessions/{type}")) { Scheme = "ws" }.Uri;
        await client.ConnectAsync(address, Token);
        return new SessionClient(client);
    }

    public Task SendAsync(object message) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, JsonSerializerOptions.Web), WebSocketMessageType.Text, true, Token);

    public Task SendBinaryAsync(byte[] data) => socket.SendAsync(data, WebSocketMessageType.Binary, true, Token);

    /// <summary>Reads until a text message of <paramref name="type"/>, failing on a different control message.</summary>
    public async Task<JsonElement> ExpectAsync(string type)
    {
        while (true)
        {
            var (kind, data) = await ReceiveAsync();
            if (kind == WebSocketMessageType.Close)
            {
                Assert.Fail($"The session closed while waiting for {type}.");
            }

            if (kind != WebSocketMessageType.Text)
            {
                continue;
            }

            var message = JsonDocument.Parse(data).RootElement;
            var actual = message.GetProperty("type").GetString();
            if (actual == type)
            {
                return message;
            }

            Assert.False(actual is "error" or "closed", $"Expected {type}, got {Encoding.UTF8.GetString(data)}");
        }
    }

    /// <summary>The tool's messages that came while waiting for a reply: sizes, entries, acknowledgements.</summary>
    public List<JsonElement> Received { get; } = [];

    private int _nextId;

    /// <summary>Sends a tool request (docs/architecture.md §7.25) and waits for its <c>reply</c> or <c>error</c>.</summary>
    public async Task<JsonElement> RequestAsync(string type, object? body = null, string? id = null)
    {
        id ??= $"t{++_nextId}";
        await SendRequestAsync(type, body, id);
        return await ReplyAsync(id);
    }

    /// <summary>Sends a tool request without waiting for its answer.</summary>
    public Task SendRequestAsync(string type, object? body, string id)
    {
        var message = body is null ? new JsonObject() : JsonSerializer.SerializeToNode(body, JsonSerializerOptions.Web)!.AsObject();
        message["type"] = type;
        message["id"] = id;
        return socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, Token);
    }

    /// <summary>Waits for the reply or error to a request, keeping what else came in <see cref="Received"/>.</summary>
    public async Task<JsonElement> ReplyAsync(string id)
    {
        while (true)
        {
            var message = await NextAsync();
            if (message.TryGetProperty("id", out var of) && of.GetString() == id && message.GetProperty("type").GetString() is "reply" or "error")
            {
                return message;
            }

            Received.Add(message);
        }
    }

    /// <summary>The next text message.</summary>
    public async Task<JsonElement> NextAsync()
    {
        while (true)
        {
            var (kind, data) = await ReceiveAsync();
            Assert.False(kind == WebSocketMessageType.Close, "The session closed.");
            if (kind == WebSocketMessageType.Text)
            {
                return JsonDocument.Parse(data).RootElement.Clone();
            }
        }
    }

    /// <summary>Reads terminal output until <paramref name="text"/> appears.</summary>
    public async Task ReadUntilAsync(string text)
    {
        while (!_output.ToString().Contains(text, StringComparison.Ordinal))
        {
            var (kind, data) = await ReceiveAsync();
            Assert.NotEqual(WebSocketMessageType.Close, kind);
            if (kind == WebSocketMessageType.Binary)
            {
                _output.Append(Encoding.UTF8.GetString(data));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
        }

        socket.Dispose();
    }

    private async Task<(WebSocketMessageType Kind, byte[] Data)> ReceiveAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (WebSocketMessageType.Close, []);
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return (result.MessageType, message.ToArray());
            }
        }
    }
}
