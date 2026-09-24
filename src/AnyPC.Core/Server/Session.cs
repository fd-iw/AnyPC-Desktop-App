using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using AnyPC.Core.Abstractions;
using AnyPC.Core.Pairing;
using AnyPC.Core.Protocol;

namespace AnyPC.Core.Server;

public sealed record ServerIdentity(string ServerId, string ServerName, string Fingerprint);

public sealed class SessionInfo
{
    public required string Remote { get; init; }
    public string DeviceId { get; internal set; } = "";
    public string DeviceName { get; internal set; } = "";
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
}

/// <summary>One connected phone. Owns the receive loop, screen stream and file transfers.</summary>
internal sealed class Session
{
    static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(30);
    const int MaxInFlightFrames = 2;
    const int MinQuality = 30;

    readonly WebSocket _ws;
    readonly IPlatform _platform;
    readonly PairingManager _pairing;
    readonly ServerIdentity _identity;
    readonly Action<string> _log;
    readonly SemaphoreSlim _sendLock = new(1, 1);
    readonly ConcurrentDictionary<uint, CancellationTokenSource> _downloads = new();
    readonly Dictionary<uint, Upload> _uploads = new();

    readonly CancellationTokenSource _sessionCts = new();
    CancellationToken _ct;
    IReadOnlyList<MonitorInfo> _monitors = [];

    // Streaming state
    readonly object _streamLock = new();
    CancellationTokenSource? _streamCts;
    StreamSettings _stream = new(0, 1136, 60, 20);
    int _quality = 60;
    int _inFlight;
    long _lastAckTicks;
    readonly ConcurrentDictionary<uint, long> _sentAt = new();
    uint _seq;
    MonitorInfo? _streamMonitor;

    public SessionInfo Info { get; }
    public bool Authenticated { get; private set; }

    public event Action<Session>? AuthenticatedChanged;

    sealed record StreamSettings(int Monitor, int MaxWidth, int Quality, int Fps);

    sealed class Upload(Stream stream, string directory, string name, long size)
    {
        public Stream Stream { get; } = stream;
        public string Path { get; } = System.IO.Path.Combine(directory, name);
        public string Name { get; } = name;
        public long Size { get; } = size;
        public long Received { get; set; }
    }

    public Session(WebSocket ws, string remote, IPlatform platform, PairingManager pairing, ServerIdentity identity, Action<string> log)
    {
        _ws = ws;
        _platform = platform;
        _pairing = pairing;
        _identity = identity;
        _log = log;
        Info = new SessionInfo { Remote = remote };
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _sessionCts.Token);
        _ct = cts.Token;
        _ = Task.Delay(AuthTimeout, _ct).ContinueWith(_ => { if (!Authenticated) cts.Cancel(); }, TaskScheduler.Default);

        var buffer = new ArrayBufferWriter<byte>(64 * 1024);
        try
        {
            while (_ws.State == WebSocketState.Open && !_ct.IsCancellationRequested)
            {
                buffer.ResetWrittenCount();
                ValueWebSocketReceiveResult result;
                do
                {
                    var mem = buffer.GetMemory(16 * 1024);
                    result = await _ws.ReceiveAsync(mem, _ct);
                    buffer.Advance(result.Count);
                    if (buffer.WrittenCount > Wire.MaxMessageSize)
                    {
                        await CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too big");
                        return;
                    }
                } while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);

                if (result.MessageType == WebSocketMessageType.Close) break;

                try
                {
                    if (result.MessageType == WebSocketMessageType.Text)
                        await HandleTextAsync(buffer.WrittenMemory);
                    else
                        await HandleBinaryAsync(buffer.WrittenMemory);
                }
                catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
                {
                    await SendAsync(new { t = "error", msg = "Malformed message: " + ex.Message });
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            StopStream();
            foreach (var d in _downloads.Values) d.Cancel();
            foreach (var u in _uploads.Values) AbortUpload(u);
            _uploads.Clear();
            if (_ws.State == WebSocketState.Open)
                await CloseAsync(WebSocketCloseStatus.NormalClosure, Authenticated ? "bye" : "authentication timeout");
        }
    }

    /// <summary>Ends the session from outside (pause, device revoked, shutdown).</summary>
    public void Disconnect()
    {
        try { _sessionCts.Cancel(); } catch (ObjectDisposedException) { }
    }

    async Task CloseAsync(WebSocketCloseStatus status, string reason)
    {
        try
        {
            using var t = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _ws.CloseOutputAsync(status, reason, t.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    // ---------------------------------------------------------------- sending

    public async Task SendAsync(object message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await SendRawAsync(bytes, WebSocketMessageType.Text);
    }

    async Task SendRawAsync(ReadOnlyMemory<byte> bytes, WebSocketMessageType type)
    {
        await _sendLock.WaitAsync(_ct);
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.SendAsync(bytes, type, true, _ct);
        }
        finally { _sendLock.Release(); }
    }

    // ---------------------------------------------------------------- dispatch

    async Task HandleTextAsync(ReadOnlyMemory<byte> data)
    {
        using var doc = JsonDocument.Parse(data);
        var m = doc.RootElement;
        var type = m.GetProperty("t").GetString();

        switch (type)
        {
            case "ping":
                await SendAsync(new { t = "pong", ts = m.TryGetProperty("ts", out var ts) ? ts.GetDouble() : 0 });
                return;
            case "hello":
                await HandleHelloAsync(m);
                return;
            case "pair":
                await HandlePairAsync(m);
                return;
        }

        if (!Authenticated)
        {
            await SendAsync(new { t = "error", msg = "Not authenticated" });
            return;
        }

        switch (type)
        {
            case "stream": HandleStream(m); break;
            case "ack": HandleAck(m.GetProperty("seq").GetUInt32()); break;
            case "move":
                if (_streamMonitor is { } mon)
                    _platform.Input.MoveAbsolute(mon, Clamp01(m.GetProperty("x").GetDouble()), Clamp01(m.GetProperty("y").GetDouble()));
                break;
            case "moverel":
                _platform.Input.MoveRelative(Math.Clamp(m.GetProperty("dx").GetInt32(), -5000, 5000), Math.Clamp(m.GetProperty("dy").GetInt32(), -5000, 5000));
                break;
            case "button":
                {
                    var b = Wire.ParseButton(Str(m, "b") ?? "left") ?? throw new FormatException("bad button");
                    var a = Wire.ParseButtonAction(Str(m, "a")) ?? throw new FormatException("bad action");
                    _platform.Input.Button(b, a);
                    break;
                }
            case "wheel":
                _platform.Input.Wheel(Int(m, "dx"), Int(m, "dy"));
                break;
            case "key":
                {
                    var vk = m.GetProperty("vk").GetInt32();
                    if (vk is <= 0 or > 254) throw new FormatException("bad vk");
                    var a = Wire.ParseKeyAction(Str(m, "a")) ?? throw new FormatException("bad key action");
                    var mods = m.TryGetProperty("mods", out var mp) && mp.ValueKind == JsonValueKind.Array
                        ? Wire.ParseModifiers(mp.EnumerateArray().Select(x => x.GetString() ?? ""))
                        : Modifiers.None;
                    _platform.Input.Key((ushort)vk, a, mods);
                    break;
                }
            case "text":
                {
                    var s = Str(m, "s") ?? "";
                    if (s.Length > 4096) s = s[..4096];
                    _platform.Input.Text(s);
                    break;
                }
            case "fs_list": await HandleFsListAsync(m); break;
            case "fs_get": StartDownload(m); break;
            case "fs_put": await HandleFsPutAsync(m); break;
            case "fs_put_end": await HandleFsPutEndAsync(m.GetProperty("id").GetUInt32()); break;
            case "fs_cancel": await HandleFsCancelAsync(m.GetProperty("id").GetUInt32()); break;
            case "sys":
                {
                    var a = Str(m, "a") ?? "";
                    if (_platform.System.Execute(a)) await SendAsync(new { t = "sys_ok", a });
                    else await SendAsync(new { t = "error", msg = $"Unknown action '{a}'" });
                    break;
                }
            default:
                await SendAsync(new { t = "error", msg = $"Unknown message '{type}'" });
                break;
        }
    }

    static string? Str(JsonElement m, string name) =>
        m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static int Int(JsonElement m, string name) =>
        m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)Math.Clamp(v.GetDouble(), -100_000, 100_000) : 0;

    static double Clamp01(double v) => double.IsNaN(v) ? 0 : Math.Clamp(v, 0, 1);

    async Task HandleBinaryAsync(ReadOnlyMemory<byte> data)
    {
        if (!Authenticated || data.Length == 0) return;
        if (data.Span[0] != Wire.UploadChunkType) return;

        var (id, chunk) = Wire.DecodeChunk(data);
        if (!_uploads.TryGetValue(id, out var up)) return;
        if (up.Received + chunk.Length > up.Size)
        {
            AbortUpload(up);
            _uploads.Remove(id);
            await SendAsync(new { t = "fs_err", id, msg = "Upload is larger than announced" });
            return;
        }
        await up.Stream.WriteAsync(chunk, _ct);
        up.Received += chunk.Length;
    }

    // ---------------------------------------------------------------- auth

    async Task HandleHelloAsync(JsonElement m)
    {
        var deviceId = Str(m, "deviceId") ?? "";
        var deviceName = Str(m, "deviceName") ?? "iPhone";
        var token = Str(m, "token");
        Info.DeviceId = deviceId;
        Info.DeviceName = deviceName;

        if (deviceId.Length is > 0 and <= 128 && token is not null && _pairing.Devices.Validate(deviceId, token))
        {
            _pairing.Devices.Touch(deviceId);
            await BecomeAuthenticatedAsync();
            return;
        }
        await SendAsync(new { t = "need_pair", serverId = _identity.ServerId, serverName = _identity.ServerName });
    }

    async Task HandlePairAsync(JsonElement m)
    {
        var deviceId = Str(m, "deviceId") ?? "";
        var deviceName = Str(m, "deviceName") ?? "iPhone";
        var pin = Str(m, "pin") ?? "";
        if (deviceId.Length is 0 or > 128)
        {
            await SendAsync(new { t = "error", msg = "Invalid device id" });
            return;
        }
        if (deviceName.Length > 64) deviceName = deviceName[..64];

        switch (_pairing.TryPair(pin, deviceId, deviceName, out var token, out var retryAfter))
        {
            case PairResult.Ok:
                Info.DeviceId = deviceId;
                Info.DeviceName = deviceName;
                _log($"Paired new device '{deviceName}' from {Info.Remote}");
                await SendAsync(new { t = "paired", token });
                await BecomeAuthenticatedAsync();
                break;
            case PairResult.Locked:
                await SendAsync(new { t = "pair_fail", reason = "locked", retryAfter = (int)Math.Ceiling(retryAfter.TotalSeconds) });
                break;
            default:
                await SendAsync(new { t = "pair_fail", reason = "bad_pin", retryAfter = 0 });
                break;
        }
    }

    async Task BecomeAuthenticatedAsync()
    {
        _monitors = _platform.Screen.GetMonitors();
        _streamMonitor = _monitors.FirstOrDefault(m => m.Primary) ?? _monitors.FirstOrDefault();
        Authenticated = true;
        AuthenticatedChanged?.Invoke(this);
        await SendAsync(new
        {
            t = "welcome",
            v = Wire.Version,
            serverId = _identity.ServerId,
            serverName = _identity.ServerName,
            monitors = _monitors.Select(x => new { i = x.Index, name = x.Name, x = x.X, y = x.Y, w = x.Width, h = x.Height, primary = x.Primary }),
        });
    }

    // ---------------------------------------------------------------- screen

    void HandleStream(JsonElement m)
    {
        var on = !m.TryGetProperty("on", out var o) || o.GetBoolean();
        if (!on) { StopStream(); return; }

        _monitors = _platform.Screen.GetMonitors();
        var monitorIndex = Math.Clamp(Int(m, "monitor"), 0, Math.Max(0, _monitors.Count - 1));
        var settings = new StreamSettings(
            monitorIndex,
            Math.Clamp(m.TryGetProperty("maxWidth", out _) ? Int(m, "maxWidth") : 1136, 320, 3840),
            Math.Clamp(m.TryGetProperty("quality", out _) ? Int(m, "quality") : 60, MinQuality, 95),
            Math.Clamp(m.TryGetProperty("fps", out _) ? Int(m, "fps") : 20, 1, 60));

        lock (_streamLock)
        {
            _streamCts?.Cancel();
            _stream = settings;
            _quality = settings.Quality;
            _streamMonitor = _monitors.Count > 0 ? _monitors[monitorIndex] : null;
            _inFlight = 0;
            _sentAt.Clear();
            _streamCts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
            var token = _streamCts.Token;
            _ = Task.Run(() => StreamLoopAsync(settings, token), token);
        }
    }

    void StopStream()
    {
        lock (_streamLock)
        {
            _streamCts?.Cancel();
            _streamCts = null;
        }
    }

    void HandleAck(uint seq)
    {
        Interlocked.Exchange(ref _lastAckTicks, Stopwatch.GetTimestamp());
        if (Interlocked.Decrement(ref _inFlight) < 0) Interlocked.Exchange(ref _inFlight, 0);
        if (!_sentAt.TryRemove(seq, out var sent)) return;

        // Adapt JPEG quality to round-trip latency so a slow phone or Wi-Fi isn't flooded.
        var rtt = Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
        var target = _stream.Quality;
        if (rtt > 250) _quality = Math.Max(MinQuality, _quality - 10);
        else if (rtt > 120) _quality = Math.Max(MinQuality, _quality - 3);
        else if (rtt < 60) _quality = Math.Min(target, _quality + 2);
    }

    async Task StreamLoopAsync(StreamSettings s, CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(1000.0 / s.Fps);
        var keepAlive = TimeSpan.FromSeconds(2);
        long lastSent = 0;
        Interlocked.Exchange(ref _lastAckTicks, Stopwatch.GetTimestamp());
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var start = Stopwatch.GetTimestamp();

                if (Volatile.Read(ref _inFlight) >= MaxInFlightFrames)
                {
                    // Recover if acks were lost.
                    if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastAckTicks)) > TimeSpan.FromSeconds(3))
                        Interlocked.Exchange(ref _inFlight, 0);
                    await Task.Delay(10, ct);
                    continue;
                }

                var force = lastSent == 0 || Stopwatch.GetElapsedTime(lastSent) > keepAlive;
                EncodedFrame? frame = null;
                try
                {
                    frame = _platform.Screen.Capture(s.Monitor, s.MaxWidth, _quality, force);
                }
                catch (Exception ex)
                {
                    _log("Capture failed: " + ex.Message);
                }

                if (frame is not null)
                {
                    var seq = ++_seq;
                    _sentAt[seq] = Stopwatch.GetTimestamp();
                    Interlocked.Increment(ref _inFlight);
                    await SendRawAsync(Wire.EncodeFrame(seq, frame), WebSocketMessageType.Binary);
                    lastSent = Stopwatch.GetTimestamp();
                    if (_sentAt.Count > 32)
                        foreach (var k in _sentAt.Keys.Where(k => k + 32 < seq)) _sentAt.TryRemove(k, out _);
                }

                var remaining = interval - Stopwatch.GetElapsedTime(start);
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
    }

    // ---------------------------------------------------------------- files

    async Task HandleFsListAsync(JsonElement m)
    {
        var id = m.GetProperty("id").GetUInt32();
        try
        {
            var listing = await Task.Run(() => _platform.Files.List(Str(m, "path") ?? ""), _ct);
            await SendAsync(new
            {
                t = "fs_list",
                id,
                path = listing.Path,
                parent = listing.Parent,
                entries = listing.Entries.Select(e => new { n = e.Name, d = e.IsDirectory, s = e.Size, m = e.ModifiedUnix }),
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            await SendAsync(new { t = "fs_err", id, msg = ex.Message });
        }
    }

    void StartDownload(JsonElement m)
    {
        var id = m.GetProperty("id").GetUInt32();
        var path = Str(m, "path") ?? "";
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        if (!_downloads.TryAdd(id, cts)) { cts.Dispose(); return; }

        _ = Task.Run(async () =>
        {
            try
            {
                await using var stream = _platform.Files.OpenRead(path, out var name, out var size);
                await SendAsync(new { t = "fs_meta", id, name, size });
                var buf = new byte[Wire.ChunkSize];
                int n;
                while ((n = await stream.ReadAsync(buf, cts.Token)) > 0)
                    await SendRawAsync(Wire.EncodeChunk(Wire.DownloadChunkType, id, buf.AsSpan(0, n)), WebSocketMessageType.Binary);
                await SendAsync(new { t = "fs_done", id, name });
            }
            catch (OperationCanceledException) when (!_ct.IsCancellationRequested)
            {
                await SendAsync(new { t = "fs_err", id, msg = "Cancelled" });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                await SendAsync(new { t = "fs_err", id, msg = ex.Message });
            }
            catch (Exception) { /* session is closing */ }
            finally
            {
                _downloads.TryRemove(id, out _);
                cts.Dispose();
            }
        });
    }

    async Task HandleFsPutAsync(JsonElement m)
    {
        var id = m.GetProperty("id").GetUInt32();
        var size = m.GetProperty("size").GetInt64();
        try
        {
            if (size < 0) throw new ArgumentException("Invalid size");
            if (_uploads.ContainsKey(id)) throw new ArgumentException("Duplicate upload id");
            var dir = Str(m, "dir") ?? "";
            var stream = _platform.Files.Create(dir, Str(m, "name") ?? "upload", out var finalName);
            var fullDir = System.IO.Path.GetDirectoryName((stream as FileStream)?.Name ?? System.IO.Path.Combine(dir, finalName)) ?? dir;
            _uploads[id] = new Upload(stream, fullDir, finalName, size);
            await SendAsync(new { t = "fs_ready", id, name = finalName });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            await SendAsync(new { t = "fs_err", id, msg = ex.Message });
        }
    }

    async Task HandleFsPutEndAsync(uint id)
    {
        if (!_uploads.Remove(id, out var up)) return;
        if (up.Received != up.Size)
        {
            AbortUpload(up);
            await SendAsync(new { t = "fs_err", id, msg = $"Upload incomplete ({up.Received} of {up.Size} bytes)" });
            return;
        }
        await up.Stream.DisposeAsync();
        _log($"Received file {up.Path}");
        await SendAsync(new { t = "fs_done", id, name = up.Name });
    }

    async Task HandleFsCancelAsync(uint id)
    {
        if (_downloads.TryGetValue(id, out var cts)) cts.Cancel();
        if (_uploads.Remove(id, out var up))
        {
            AbortUpload(up);
            await SendAsync(new { t = "fs_err", id, msg = "Cancelled" });
        }
    }

    static void AbortUpload(Upload up)
    {
        try
        {
            up.Stream.Dispose();
            File.Delete(up.Path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
