using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using AnyPC.Core.Abstractions;
using AnyPC.Core.Pairing;
using AnyPC.Core.Protocol;
using AnyPC.Core.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AnyPC.Core.Server;

public sealed class ControlServerOptions
{
    public int Port { get; init; } = Wire.DefaultPort;

    /// <summary>Null listens on all interfaces (IPv4 and IPv6).</summary>
    public IPAddress? BindAddress { get; init; }

    public required string ServerId { get; init; }
    public required string ServerName { get; init; }
}

/// <summary>TLS WebSocket server that phones connect to.</summary>
public sealed class ControlServer : IAsyncDisposable
{
    readonly ControlServerOptions _options;
    readonly IPlatform _platform;
    readonly PairingManager _pairing;
    readonly X509Certificate2 _cert;
    readonly Action<string> _log;
    readonly ConcurrentDictionary<Session, byte> _sessions = new();
    WebApplication? _app;

    public int Port { get; private set; }
    public string Fingerprint { get; }

    /// <summary>When paused, new connections are refused and existing sessions are closed.</summary>
    public bool Paused { get; private set; }

    public event Action? SessionsChanged;

    public ControlServer(ControlServerOptions options, IPlatform platform, PairingManager pairing, X509Certificate2 cert, Action<string>? log = null)
    {
        _options = options;
        _platform = platform;
        _pairing = pairing;
        _cert = cert;
        _log = log ?? (_ => { });
        Fingerprint = CertManager.Fingerprint(cert);
    }

    public IReadOnlyList<SessionInfo> ActiveSessions =>
        _sessions.Keys.Where(s => s.Authenticated).Select(s => s.Info).ToList();

    public async Task StartAsync(CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = Wire.MaxMessageSize;
            void Configure(ListenOptions lo)
            {
                lo.Protocols = HttpProtocols.Http1;
                lo.UseHttps(_cert);
            }
            if (_options.BindAddress is null) k.ListenAnyIP(_options.Port, Configure);
            else k.Listen(_options.BindAddress, _options.Port, Configure);
        });

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        app.Run(HandleRequestAsync);
        await app.StartAsync(ct);
        _app = app;

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        Port = addresses?.Select(a => new Uri(a.Replace("*", "localhost").Replace("+", "localhost")).Port).FirstOrDefault() ?? _options.Port;
        _log($"Listening on port {Port}");
    }

    async Task HandleRequestAsync(HttpContext ctx)
    {
        if (ctx.Request.Path != Wire.Path)
        {
            ctx.Response.ContentType = "text/plain";
            await ctx.Response.WriteAsync($"AnyPC {Wire.Version}");
            return;
        }
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (Paused)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var remote = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
        var identity = new ServerIdentity(_options.ServerId, _options.ServerName, Fingerprint);
        var session = new Session(ws, remote, _platform, _pairing, identity, _log);
        session.AuthenticatedChanged += s =>
        {
            _log($"'{s.Info.DeviceName}' connected from {s.Info.Remote}");
            SessionsChanged?.Invoke();
        };
        _sessions[session] = 0;
        try
        {
            await session.RunAsync(ctx.RequestAborted);
        }
        finally
        {
            _sessions.TryRemove(session, out _);
            if (session.Authenticated)
            {
                _log($"'{session.Info.DeviceName}' disconnected");
                SessionsChanged?.Invoke();
            }
        }
    }

    public void SetPaused(bool paused)
    {
        Paused = paused;
        if (paused) DisconnectAll();
        SessionsChanged?.Invoke();
    }

    /// <summary>Drops every live session.</summary>
    public void DisconnectAll()
    {
        foreach (var s in _sessions.Keys) s.Disconnect();
    }

    /// <summary>Drops the sessions of one device (used when it is un-paired).</summary>
    public void DisconnectDevice(string deviceId)
    {
        foreach (var s in _sessions.Keys.Where(s => s.Info.DeviceId == deviceId)) s.Disconnect();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null) return;
        DisconnectAll();
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            await _app.StopAsync(cts.Token);
        await _app.DisposeAsync();
        _app = null;
    }
}
