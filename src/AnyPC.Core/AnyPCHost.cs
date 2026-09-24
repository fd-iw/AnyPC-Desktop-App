using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using AnyPC.Core.Abstractions;
using AnyPC.Core.Discovery;
using AnyPC.Core.Pairing;
using AnyPC.Core.Protocol;
using AnyPC.Core.Security;
using AnyPC.Core.Server;

namespace AnyPC.Core;

/// <summary>Wires together certificate, pairing, server and discovery. Used by the tray app.</summary>
public sealed class AnyPCHost : IAsyncDisposable
{
    readonly string _dataDir;
    readonly MdnsAdvertiser _mdns = new();
    readonly Action<string> _log;

    public PairingManager Pairing { get; }
    public ControlServer Server { get; }
    public string ServerId { get; }
    public string ServerName { get; }
    public string Fingerprint => Server.Fingerprint;

    public AnyPCHost(IPlatform platform, string dataDir, Action<string>? log = null, int port = Wire.DefaultPort)
    {
        _dataDir = dataDir;
        _log = log ?? (_ => { });
        Directory.CreateDirectory(dataDir);

        ServerId = LoadOrCreateServerId(Path.Combine(dataDir, "server-id.txt"));
        ServerName = platform.MachineName;
        var cert = CertManager.LoadOrCreate(Path.Combine(dataDir, "server.pfx"));
        Pairing = new PairingManager(new DeviceStore(Path.Combine(dataDir, "devices.json")));
        Server = new ControlServer(new ControlServerOptions { Port = port, ServerId = ServerId, ServerName = ServerName }, platform, Pairing, cert, _log);
    }

    public async Task StartAsync()
    {
        await Server.StartAsync();
        try
        {
            _mdns.Start(ServerName, (ushort)Server.Port, ServerId, Fingerprint);
        }
        catch (Exception ex)
        {
            // Discovery is a convenience; the phone can still connect by IP or QR code.
            _log("Bonjour advertising failed: " + ex.Message);
        }
    }

    static string LoadOrCreateServerId(string path)
    {
        if (File.Exists(path))
        {
            var id = File.ReadAllText(path).Trim();
            if (id.Length == 32) return id;
        }
        var created = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        File.WriteAllText(path, created);
        return created;
    }

    /// <summary>Private IPv4 addresses of active, non-virtual adapters, best candidates first.</summary>
    public static IReadOnlyList<string> LocalAddresses()
    {
        var result = new List<(int Rank, string Ip)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var desc = (nic.Description + " " + nic.Name).ToLowerInvariant();
            var isVirtual = desc.Contains("virtual") || desc.Contains("vmware") || desc.Contains("hyper-v") || desc.Contains("vethernet") || desc.Contains("wsl") || desc.Contains("docker") || desc.Contains("vpn");
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); } catch (NetworkInformationException) { continue; }
            var hasGateway = props.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && g.Address.AddressFamily == AddressFamily.InterNetwork);
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var b = ua.Address.GetAddressBytes();
                var isPrivate = b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
                if (!isPrivate) continue;
                var rank = (isVirtual ? 10 : 0) + (hasGateway ? 0 : 5) + (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1);
                result.Add((rank, ua.Address.ToString()));
            }
        }
        return result.OrderBy(r => r.Rank).Select(r => r.Ip).Distinct().ToList();
    }

    /// <summary>The URL encoded into the pairing QR code.</summary>
    public string PairingUri()
    {
        var hosts = string.Join(",", LocalAddresses().Take(3));
        return $"anypc://pair?h={hosts}&p={Server.Port}&fp={Fingerprint}&id={ServerId}&pin={Pairing.CurrentPin}&n={Uri.EscapeDataString(ServerName)}";
    }

    public async ValueTask DisposeAsync()
    {
        _mdns.Dispose();
        await Server.DisposeAsync();
    }
}
