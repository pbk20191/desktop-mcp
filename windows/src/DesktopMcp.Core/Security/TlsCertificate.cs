using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DesktopMcp.Core.Config;

namespace DesktopMcp.Core.Security;

/// <summary>The HTTPS certificate from <c>--ssl-certfile</c> and <c>--ssl-keyfile</c> PEM files (spec §8 TLS; Decisions 11).</summary>
public static class TlsCertificate
{
    /// <summary>The Server Authentication purpose an Extended Key Usage extension must list (RFC 5280 id-kp-serverAuth).</summary>
    public const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";

    /// <summary>
    /// Loads the certificate at <paramref name="certPath"/> with the unencrypted private key at <paramref name="keyPath"/>
    /// as a certificate Schannel can serve with. Schannel cannot use the ephemeral key a PEM load gives, so the pair is
    /// round-tripped through PKCS#12 with the default storage flags: that creates a temporary key container which is
    /// removed when the returned certificate is disposed. No certificate store is opened. A missing or unreadable file,
    /// bad PEM, an encrypted key, a key that does not match the certificate, or a certificate whose Extended Key Usage
    /// leaves out Server Authentication (which Kestrel would refuse at start) is a <see cref="ConfigException"/>; its
    /// message never includes key material.
    /// </summary>
    public static X509Certificate2 Load(string certPath, string keyPath)
    {
        ArgumentNullException.ThrowIfNull(certPath);
        ArgumentNullException.ThrowIfNull(keyPath);
        var prefix = $"Could not load the TLS certificate '{certPath}' with key '{keyPath}': ";
        X509Certificate2 certificate;
        byte[]? pkcs12 = null;
        try
        {
            using (var pem = X509Certificate2.CreateFromPemFile(certPath, keyPath))
                pkcs12 = pem.Export(X509ContentType.Pkcs12);
            certificate = X509CertificateLoader.LoadPkcs12(pkcs12, null);
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new ConfigException(prefix + (IsEncryptedKey(keyPath)
                ? "encrypted private keys are not supported; decrypt the key first."
                : e.Message));
        }
        finally
        {
            if (pkcs12 is not null) CryptographicOperations.ZeroMemory(pkcs12);
        }

        if (!AllowsServerAuthentication(certificate))
        {
            certificate.Dispose();
            throw new ConfigException(prefix + $"the certificate is not valid for server authentication (its Extended Key Usage lacks {ServerAuthenticationOid}).");
        }
        return certificate;
    }

    /// <summary>No Extended Key Usage extension allows every purpose; one that is present must list Server Authentication.</summary>
    private static bool AllowsServerAuthentication(X509Certificate2 certificate)
    {
        var restricted = false;
        foreach (var extension in certificate.Extensions)
        {
            if (extension is not X509EnhancedKeyUsageExtension usage) continue;
            restricted = true;
            foreach (var oid in usage.EnhancedKeyUsages)
                if (oid.Value == ServerAuthenticationOid) return true;
        }
        return !restricted;
    }

    /// <summary>Whether the key file holds an encrypted PKCS#8 PEM (false when it cannot be read).</summary>
    private static bool IsEncryptedKey(string keyPath)
    {
        try
        {
            return File.ReadAllText(keyPath).Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
