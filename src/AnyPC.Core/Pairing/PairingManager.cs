using System.Security.Cryptography;

namespace AnyPC.Core.Pairing;

public enum PairResult { Ok, BadPin, Locked }

/// <summary>
/// Owns the 6-digit pairing PIN and brute-force protection. The PIN rotates every
/// <see cref="PinLifetime"/> and after every successful pairing.
/// </summary>
public sealed class PairingManager
{
    public static readonly TimeSpan PinLifetime = TimeSpan.FromMinutes(5);
    public const int MaxFailures = 5;
    static readonly TimeSpan BaseLockout = TimeSpan.FromSeconds(60);

    readonly object _lock = new();
    readonly Func<DateTimeOffset> _now;
    string _pin = "";
    DateTimeOffset _pinIssued;
    int _failures;
    int _lockouts;
    DateTimeOffset _lockedUntil = DateTimeOffset.MinValue;

    public DeviceStore Devices { get; }

    /// <summary>Raised (on an arbitrary thread) whenever the PIN changes.</summary>
    public event Action<string>? PinChanged;

    public PairingManager(DeviceStore devices, Func<DateTimeOffset>? clock = null)
    {
        Devices = devices;
        _now = clock ?? (() => DateTimeOffset.UtcNow);
        RotatePin();
    }

    public string CurrentPin
    {
        get
        {
            bool rotated = false;
            string pin;
            lock (_lock)
            {
                if (_now() - _pinIssued >= PinLifetime) { RotatePinLocked(); rotated = true; }
                pin = _pin;
            }
            if (rotated) PinChanged?.Invoke(pin);
            return pin;
        }
    }

    public TimeSpan PinRemaining
    {
        get { lock (_lock) return PinLifetime - (_now() - _pinIssued); }
    }

    public void RotatePin()
    {
        string pin;
        lock (_lock) { RotatePinLocked(); pin = _pin; }
        PinChanged?.Invoke(pin);
    }

    void RotatePinLocked()
    {
        _pin = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        _pinIssued = _now();
    }

    public PairResult TryPair(string pin, string deviceId, string deviceName, out string token, out TimeSpan retryAfter)
    {
        token = "";
        retryAfter = TimeSpan.Zero;
        var current = CurrentPin; // rotates if expired
        lock (_lock)
        {
            var now = _now();
            if (now < _lockedUntil)
            {
                retryAfter = _lockedUntil - now;
                return PairResult.Locked;
            }

            var ok = pin.Length == current.Length &&
                CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(pin), System.Text.Encoding.ASCII.GetBytes(current));
            if (!ok)
            {
                if (++_failures >= MaxFailures)
                {
                    _failures = 0;
                    var lockout = BaseLockout * Math.Pow(2, Math.Min(_lockouts++, 6));
                    _lockedUntil = now + lockout;
                    RotatePinLocked();
                    retryAfter = lockout;
                    return PairResult.Locked;
                }
                return PairResult.BadPin;
            }

            _failures = 0;
            _lockouts = 0;
            token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            RotatePinLocked();
        }
        Devices.Upsert(deviceId, deviceName, token);
        PinChanged?.Invoke(CurrentPin);
        return PairResult.Ok;
    }
}
