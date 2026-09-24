using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AnyPC.Core.Security;

/// <summary>Creates and persists the self-signed TLS certificate the server presents.</summary>
public static class CertManager
{
    public static X509Certificate2 LoadOrCreate(string pfxPath)
    {
        if (File.Exists(pfxPath))
        {
            try
            {
                var existing = Load(File.ReadAllBytes(pfxPath));
                if (existing.NotAfter > DateTime.Now.AddDays(30)) return existing;
            }
            catch (CryptographicException) { }
        }

        var pfx = CreatePfx();
        Directory.CreateDirectory(Path.GetDirectoryName(pfxPath)!);
        File.WriteAllBytes(pfxPath, pfx);
        return Load(pfx);
    }

    public static X509Certificate2 CreateInMemory() => Load(CreatePfx());

    static byte[] CreatePfx()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN=AnyPC {Environment.MachineName}", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("anypc.local");
        req.CertificateExtensions.Add(san.Build());
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        return cert.Export(X509ContentType.Pfx);
    }

    static X509Certificate2 Load(byte[] pfx) =>
        // Round-tripping through PFX gives SChannel (Windows) a key it can use for TLS.
        new(pfx, (string?)null, OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);

    /// <summary>SHA-256 over the certificate DER, lower-case hex. This is what clients pin.</summary>
    public static string Fingerprint(X509Certificate2 cert) =>
        Convert.ToHexString(SHA256.HashData(cert.RawData)).ToLowerInvariant();
}
