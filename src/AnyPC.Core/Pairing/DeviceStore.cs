using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AnyPC.Core.Pairing;

public sealed class PairedDevice
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public DateTimeOffset PairedAt { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}

/// <summary>Persists paired devices as JSON. Only a SHA-256 hash of each token is stored.</summary>
public sealed class DeviceStore
{
    readonly string _path;
    readonly object _lock = new();
    List<PairedDevice> _devices;

    public event Action? Changed;

    public DeviceStore(string path)
    {
        _path = path;
        _devices = Load(path);
    }

    static List<PairedDevice> Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<List<PairedDevice>>(File.ReadAllText(path)) ?? [];
        }
        catch (JsonException) { }
        return [];
    }

    public IReadOnlyList<PairedDevice> All
    {
        get { lock (_lock) return _devices.ToList(); }
    }

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public void Upsert(string deviceId, string name, string token)
    {
        lock (_lock)
        {
            _devices.RemoveAll(d => d.DeviceId == deviceId);
            var now = DateTimeOffset.UtcNow;
            _devices.Add(new PairedDevice { DeviceId = deviceId, Name = name, TokenHash = HashToken(token), PairedAt = now, LastSeen = now });
            Save();
        }
        Changed?.Invoke();
    }

    public bool Validate(string deviceId, string token)
    {
        PairedDevice? d;
        lock (_lock) d = _devices.FirstOrDefault(x => x.DeviceId == deviceId);
        if (d is null) return false;
        var expected = Convert.FromHexString(d.TokenHash);
        var actual = Convert.FromHexString(HashToken(token));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public void Touch(string deviceId)
    {
        lock (_lock)
        {
            var d = _devices.FirstOrDefault(x => x.DeviceId == deviceId);
            if (d is null) return;
            d.LastSeen = DateTimeOffset.UtcNow;
            Save();
        }
        Changed?.Invoke();
    }

    public void Remove(string deviceId)
    {
        lock (_lock)
        {
            _devices.RemoveAll(d => d.DeviceId == deviceId);
            Save();
        }
        Changed?.Invoke();
    }

    void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_devices, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }
}
