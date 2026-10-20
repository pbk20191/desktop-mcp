using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DesktopMcp.Server.Tests.Http;

/// <summary>
/// A self-signed ECDSA P-256 certificate for <c>localhost</c>, <c>127.0.0.1</c> and <c>::1</c>, valid from a day ago to
/// a day from now, written as <c>cert.pem</c> and a PKCS#8 <c>key.pem</c> into a new temp folder that
/// <see cref="Dispose"/> deletes. The key lives only in memory while it is written; no certificate store is opened and
/// no key container is created (Decisions 11; preflight F13). The same helper is in DesktopMcp.Core.Tests.
/// </summary>
internal sealed class TestCertificate : IDisposable
{
    private readonly string _folder;

    private TestCertificate(string folder, string thumbprint)
    {
        _folder = folder;
        Thumbprint = thumbprint;
    }

    public string CertPath => Path.Combine(_folder, "cert.pem");

    public string KeyPath => Path.Combine(_folder, "key.pem");

    /// <summary>The certificate's SHA-1 thumbprint, upper-case hex as <see cref="X509Certificate2.Thumbprint"/>.</summary>
    public string Thumbprint { get; }

    /// <summary>
    /// Creates the pair; with <paramref name="encryptKey"/> the key is written as an encrypted PKCS#8 PEM (password
    /// <c>pw</c>).
    /// </summary>
    public static TestCertificate Create(string commonName = "localhost", bool encryptKey = false)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"dmcp-tls-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            names.AddIpAddress(IPAddress.IPv6Loopback);
            request.CertificateExtensions.Add(names.Build());
            var now = DateTimeOffset.UtcNow;
            using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
            File.WriteAllText(Path.Combine(folder, "cert.pem"), certificate.ExportCertificatePem());
            var keyPem = encryptKey
                ? key.ExportEncryptedPkcs8PrivateKeyPem("pw", new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000))
                : key.ExportPkcs8PrivateKeyPem();
            File.WriteAllText(Path.Combine(folder, "key.pem"), keyPem);
            return new TestCertificate(folder, certificate.Thumbprint);
        }
        catch
        {
            Directory.Delete(folder, recursive: true);
            throw;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}
