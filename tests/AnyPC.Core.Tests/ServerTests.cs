using System.Net;
using System.Net.WebSockets;
using System.Text;
using AnyPC.Core.Pairing;
using AnyPC.Core.Protocol;
using AnyPC.Core.Security;
using AnyPC.Core.Server;
using Xunit;

namespace AnyPC.Core.Tests;

public sealed class ServerTests : IAsyncLifetime
{
    readonly string _dir = Directory.CreateTempSubdirectory("anypc-test").FullName;
    readonly FakePlatform _platform = new();
    PairingManager _pairing = null!;
    ControlServer _server = null!;

    public async Task InitializeAsync()
    {
        _pairing = new PairingManager(new DeviceStore(Path.Combine(_dir, "devices.json")));
        _server = new ControlServer(
            new ControlServerOptions { Port = 0, BindAddress = IPAddress.Loopback, ServerId = "0123456789abcdef0123456789abcdef", ServerName = "TEST-PC" },
            _platform, _pairing, CertManager.CreateInMemory());
        await _server.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        Directory.Delete(_dir, true);
    }

    async Task<(TestClient Client, string Token)> PairAsync(string deviceId = "phone-1")
    {
        var c = await TestClient.ConnectAsync(_server.Port, _server.Fingerprint);
        await c.SendAsync(new { t = "hello", v = 1, deviceId, deviceName = "iPhone SE" });
        var need = await c.WaitForAsync("need_pair");
        Assert.Equal("TEST-PC", need.GetProperty("serverName").GetString());

        await c.SendAsync(new { t = "pair", deviceId, deviceName = "iPhone SE", pin = _pairing.CurrentPin });
        var paired = await c.WaitForAsync("paired");
        var welcome = await c.WaitForAsync("welcome");
        Assert.Equal(2, welcome.GetProperty("monitors").GetArrayLength());
        return (c, paired.GetProperty("token").GetString()!);
    }

    static async Task EventuallyAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    [Fact]
    public async Task Rejects_wrong_certificate_fingerprint()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => TestClient.ConnectAsync(_server.Port, new string('0', 64)));
    }

    [Fact]
    public async Task Pairs_with_pin_then_reconnects_with_token()
    {
        var (c, token) = await PairAsync();
        await c.DisposeAsync();

        await using var c2 = await TestClient.ConnectAsync(_server.Port, _server.Fingerprint);
        await c2.SendAsync(new { t = "hello", v = 1, deviceId = "phone-1", deviceName = "iPhone SE", token });
        var welcome = await c2.WaitForAsync("welcome");
        Assert.Equal("0123456789abcdef0123456789abcdef", welcome.GetProperty("serverId").GetString());
    }

    [Fact]
    public async Task Token_is_bound_to_device_id()
    {
        var (c, token) = await PairAsync();
        await c.DisposeAsync();

        await using var c2 = await TestClient.ConnectAsync(_server.Port, _server.Fingerprint);
        await c2.SendAsync(new { t = "hello", v = 1, deviceId = "someone-else", deviceName = "x", token });
        await c2.WaitForAsync("need_pair");
    }

    [Fact]
    public async Task Wrong_pin_is_rejected_and_input_is_refused_before_auth()
    {
        await using var c = await TestClient.ConnectAsync(_server.Port, _server.Fingerprint);
        await c.SendAsync(new { t = "hello", v = 1, deviceId = "p", deviceName = "x" });
        await c.WaitForAsync("need_pair");

        var wrong = _pairing.CurrentPin == "000000" ? "000001" : "000000";
        await c.SendAsync(new { t = "pair", deviceId = "p", deviceName = "x", pin = wrong });
        var fail = await c.WaitForAsync("pair_fail");
        Assert.Equal("bad_pin", fail.GetProperty("reason").GetString());

        await c.SendAsync(new { t = "text", s = "rm -rf" });
        var err = await c.WaitForAsync("error");
        Assert.Contains("Not authenticated", err.GetProperty("msg").GetString());
        Assert.Empty(_platform.FakeInput.Events);
    }

    [Fact]
    public async Task Streams_frames_with_flow_control()
    {
        var (c, _) = await PairAsync();
        await using var _c = c;
        await c.SendAsync(new { t = "stream", on = true, monitor = 1, maxWidth = 800, quality = 70, fps = 30 });

        var f1 = Wire.DecodeFrame(await c.WaitForBinaryAsync(Wire.FrameType));
        var f2 = Wire.DecodeFrame(await c.WaitForBinaryAsync(Wire.FrameType));
        Assert.Equal(800, f1.Width);
        Assert.Equal(1, f1.Jpeg.Span[2]); // monitor index encoded by the fake
        Assert.Equal(10, f1.CursorX);
        Assert.Equal(f1.Seq + 1, f2.Seq);

        // Without acks, no third frame may arrive.
        var pending = c.ReceiveAsync(TimeSpan.FromSeconds(10));
        Assert.NotSame(pending, await Task.WhenAny(pending, Task.Delay(400)));

        await c.SendAsync(new { t = "ack", seq = f1.Seq });
        var f3 = Wire.DecodeFrame((await pending).Data);
        Assert.Equal(f2.Seq + 1, f3.Seq);
    }

    [Fact]
    public async Task Dispatches_input_events()
    {
        var (c, _) = await PairAsync();
        await using var _c = c;
        await c.SendAsync(new { t = "stream", on = true, monitor = 0 });
        await c.WaitForBinaryAsync(Wire.FrameType);
        await c.SendAsync(new { t = "stream", on = false });

        await c.SendAsync(new { t = "move", x = 0.25, y = 0.5 });
        await c.SendAsync(new { t = "moverel", dx = 5, dy = -3 });
        await c.SendAsync(new { t = "button", b = "right", a = "click" });
        await c.SendAsync(new { t = "wheel", dx = 0, dy = -240 });
        await c.SendAsync(new { t = "key", vk = 0x43, a = "press", mods = new[] { "ctrl" } });
        await c.SendAsync(new { t = "text", s = "héllo" });
        await c.SendAsync(new { t = "sys", a = "lock" });
        await c.WaitForAsync("sys_ok");

        Assert.Equal(
            ["move 0 0.25 0.5", "moverel 5 -3", "button Right Click", "wheel 0 -240", "key 67 Press Ctrl", "text héllo"],
            _platform.FakeInput.Events.ToArray());
        Assert.Equal(["lock"], _platform.FakeSystem.Actions.ToArray());
    }

    [Fact]
    public async Task Unknown_system_action_returns_error()
    {
        var (c, _) = await PairAsync();
        await using var _c = c;
        await c.SendAsync(new { t = "sys", a = "format_c" });
        var err = await c.WaitForAsync("error");
        Assert.Contains("format_c", err.GetProperty("msg").GetString());
    }

    [Fact]
    public async Task Lists_downloads_and_uploads_files()
    {
        var (c, _) = await PairAsync();
        await using var _c = c;

        var content = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("AnyPC file transfer! ", 10_000)));
        var src = Path.Combine(_dir, "big.txt");
        await File.WriteAllBytesAsync(src, content);

        await c.SendAsync(new { t = "fs_list", id = 1, path = _dir });
        var list = await c.WaitForAsync("fs_list");
        Assert.Contains(list.GetProperty("entries").EnumerateArray(), e => e.GetProperty("n").GetString() == "big.txt" && e.GetProperty("s").GetInt64() == content.Length && e.GetProperty("p").GetString() == src);

        // Download
        await c.SendAsync(new { t = "fs_get", id = 2, path = src });
        var meta = await c.WaitForAsync("fs_meta");
        Assert.Equal(content.Length, meta.GetProperty("size").GetInt64());
        var chunks = new List<byte[]>();
        await c.WaitForAsync("fs_done", chunks);
        var received = chunks.Where(b => b[0] == Wire.DownloadChunkType).SelectMany(b => Wire.DecodeChunk(b).Data.ToArray()).ToArray();
        Assert.Equal(content, received);

        // Upload into a sub folder, with a path-traversal attempt in the name
        var dest = Path.Combine(_dir, "incoming");
        await c.SendAsync(new { t = "fs_put", id = 3, dir = dest, name = "../../evil.txt", size = content.Length });
        var ready = await c.WaitForAsync("fs_ready");
        Assert.Equal("evil.txt", ready.GetProperty("name").GetString());
        for (int off = 0; off < content.Length; off += Wire.ChunkSize)
            await c.SendBinaryAsync(Wire.EncodeChunk(Wire.UploadChunkType, 3, content.AsSpan(off, Math.Min(Wire.ChunkSize, content.Length - off))));
        await c.SendAsync(new { t = "fs_put_end", id = 3 });
        await c.WaitForAsync("fs_done");
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(dest, "evil.txt")));

        // A second upload with the same name must not overwrite.
        await c.SendAsync(new { t = "fs_put", id = 4, dir = dest, name = "evil.txt", size = 1 });
        Assert.Equal("evil (1).txt", (await c.WaitForAsync("fs_ready")).GetProperty("name").GetString());
        await c.SendAsync(new { t = "fs_cancel", id = 4 });
        await c.WaitForAsync("fs_err");
        await EventuallyAsync(() => !File.Exists(Path.Combine(dest, "evil (1).txt")));
    }

    [Fact]
    public async Task Revoked_device_is_disconnected_and_must_re_pair()
    {
        var (c, token) = await PairAsync();
        await using var _c = c;
        _pairing.Devices.Remove("phone-1");
        _server.DisconnectDevice("phone-1");
        await EventuallyAsync(() => c.State != WebSocketState.Open || _server.ActiveSessions.Count == 0);

        await using var c2 = await TestClient.ConnectAsync(_server.Port, _server.Fingerprint);
        await c2.SendAsync(new { t = "hello", v = 1, deviceId = "phone-1", deviceName = "iPhone SE", token });
        await c2.WaitForAsync("need_pair");
    }
}
