using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DesktopMcp.Core.Config;

namespace DesktopMcp.Core.Security;

/// <summary>The HTTPS certificate from <c>--ssl-certfile</c> and <c>--ssl-keyfile</c> PEM files (spec §8 TLS; Decisions 11).</summary>
public static class TlsCertificate
{
    /// <summary>
    /// Loads the certificate at <paramref name="certPath"/> with the unencrypted private key at <paramref name="keyPath"/>
    /// as a certificate Schannel can serve with. Schannel cannot use the ephemeral key a PEM load gives, so the pair is
    /// round-tripped through PKCS#12 with the default storage flags: that creates a temporary key container which is
    /// removed when the returned certificate is disposed. No certificate store is opened. A missing or unreadable file,
    /// bad PEM, an encrypted key or a key that does not match the certificate is a <see cref="ConfigException"/>;
    /// its message never includes key material.
    /// </summary>
    public static X509Certificate2 Load(string certPath, string keyPath)
    {
        ArgumentNullException.ThrowIfNull(certPath);
        ArgumentNullException.ThrowIfNull(keyPath);
        byte[]? pkcs12 = null;
        try
        {
            using (var pem = X509Certificate2.CreateFromPemFile(certPath, keyPath))
                pkcs12 = pem.Export(X509ContentType.Pkcs12);
            return X509CertificateLoader.LoadPkcs12(pkcs12, null);
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new ConfigException($"Could not load the TLS certificate '{certPath}' with key '{keyPath}': {e.Message}");
        }
        finally
        {
            if (pkcs12 is not null) CryptographicOperations.ZeroMemory(pkcs12);
        }
    }
}
