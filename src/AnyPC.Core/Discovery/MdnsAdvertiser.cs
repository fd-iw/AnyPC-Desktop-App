using System.Text.RegularExpressions;
using AnyPC.Core.Protocol;
using Makaretu.Dns;

namespace AnyPC.Core.Discovery;

/// <summary>Publishes the server as a Bonjour service so the iPhone app can find it.</summary>
public sealed partial class MdnsAdvertiser : IDisposable
{
    ServiceDiscovery? _sd;
    ServiceProfile? _profile;

    public void Start(string instanceName, ushort port, string serverId, string fingerprint)
    {
        Stop();
        var safe = UnsafeChars().Replace(instanceName, "-").Trim('-');
        if (safe.Length == 0) safe = "AnyPC";
        if (safe.Length > 60) safe = safe[..60];

        _profile = new ServiceProfile(safe, Wire.ServiceType, port);
        _profile.AddProperty("id", serverId);
        _profile.AddProperty("fp", fingerprint);
        _profile.AddProperty("v", Wire.Version.ToString());
        _sd = new ServiceDiscovery();
        _sd.Advertise(_profile);
        _sd.Announce(_profile);
    }

    public void Stop()
    {
        if (_sd is null) return;
        try
        {
            if (_profile is not null) _sd.Unadvertise(_profile);
        }
        catch (Exception) { /* network may already be gone */ }
        _sd.Dispose();
        _sd = null;
        _profile = null;
    }

    public void Dispose() => Stop();

    [GeneratedRegex("[^A-Za-z0-9 _-]+")]
    private static partial Regex UnsafeChars();
}
