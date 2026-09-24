using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AnyPC.Core.Protocol;

namespace AnyPC.Core.Tests;

/// <summary>A minimal phone-side client, pinning the server certificate like the iOS app does.</summary>
sealed class TestClient : IAsyncDisposable
{
    readonly ClientWebSocket _ws = new();
    public string? SeenFingerprint { get; private set; }

    public static async Task<TestClient> ConnectAsync(int port, string expectedFingerprint)
    {
        var c = new TestClient();
        c._ws.Options.RemoteCertificateValidationCallback = (_, cert, _, _) =>
        {
            if (cert is null) return false;
            c.SeenFingerprint = Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())).ToLowerInvariant();
            return c.SeenFingerprint == expectedFingerprint;
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await c._ws.ConnectAsync(new Uri($"wss://127.0.0.1:{port}{Wire.Path}"), cts.Token);
        return c;
    }

    public WebSocketState State => _ws.State;

    public Task SendAsync(object message) =>
        _ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, CancellationToken.None);

    public Task SendBinaryAsync(byte[] data) =>
        _ws.SendAsync(data, WebSocketMessageType.Binary, true, CancellationToken.None);

    public async Task<(WebSocketMessageType Type, byte[] Data)> ReceiveAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        var ms = new MemoryStream();
        var buf = new byte[64 * 1024];
        WebSocketReceiveResult r;
        do
        {
            r = await _ws.ReceiveAsync(buf, cts.Token);
            ms.Write(buf, 0, r.Count);
        } while (!r.EndOfMessage);
        return (r.MessageType, ms.ToArray());
    }

    /// <summary>Receives until a JSON message of type <paramref name="t"/> arrives, skipping others.</summary>
    public async Task<JsonElement> WaitForAsync(string t, List<byte[]>? binarySink = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var (type, data) = await ReceiveAsync();
            if (type == WebSocketMessageType.Close) throw new InvalidOperationException("closed while waiting for " + t);
            if (type == WebSocketMessageType.Binary) { binarySink?.Add(data); continue; }
            var el = JsonDocument.Parse(Encoding.UTF8.GetString(data)).RootElement.Clone();
            if (el.GetProperty("t").GetString() == t) return el;
            if (el.GetProperty("t").GetString() == "error" && t != "error")
                throw new InvalidOperationException("server error: " + el.GetProperty("msg").GetString());
        }
        throw new TimeoutException("waiting for " + t);
    }

    public async Task<byte[]> WaitForBinaryAsync(byte type)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var (t, data) = await ReceiveAsync();
            if (t == WebSocketMessageType.Binary && data[0] == type) return data;
        }
        throw new TimeoutException("waiting for binary " + type);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", new CancellationTokenSource(1000).Token);
        }
        catch (Exception) { }
        _ws.Dispose();
    }
}
