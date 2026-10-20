using DesktopMcp.Core.Config;
using DesktopMcp.Core.Security;

namespace DesktopMcp.Core.Tests.Security;

/// <summary>The PEM certificate and key for HTTPS (spec §8 TLS; Decisions 11).</summary>
public class TlsCertificateTests
{
    [Fact]
    public void APemPairLoadsWithAUsablePrivateKey()
    {
        using var pem = TestCertificate.Create();
        using var certificate = TlsCertificate.Load(pem.CertPath, pem.KeyPath);
        Assert.True(certificate.HasPrivateKey);
        Assert.Equal(pem.Thumbprint, certificate.Thumbprint);
    }

    [Fact]
    public void AMismatchedKeyIsAConfigError()
    {
        using var a = TestCertificate.Create();
        using var b = TestCertificate.Create();
        var error = Assert.Throws<ConfigException>(() => TlsCertificate.Load(a.CertPath, b.KeyPath));
        Assert.StartsWith($"Could not load the TLS certificate '{a.CertPath}' with key '{b.KeyPath}': ", error.Message);
    }

    [Fact]
    public void AMissingFileIsAConfigError()
    {
        using var pem = TestCertificate.Create();
        var missing = Path.Combine(Path.GetDirectoryName(pem.KeyPath)!, "nope.pem");
        Assert.StartsWith($"Could not load the TLS certificate '{pem.CertPath}' with key '{missing}': ",
            Assert.Throws<ConfigException>(() => TlsCertificate.Load(pem.CertPath, missing)).Message);
    }

    [Fact]
    public void AnEncryptedKeyIsAConfigError()
    {
        using var pem = TestCertificate.Create(encryptKey: true);
        Assert.Throws<ConfigException>(() => TlsCertificate.Load(pem.CertPath, pem.KeyPath));
    }
}
