using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace StreamlinkVlcStudio.Infrastructure.LocalWeb;

public sealed class LocalWebCertificatePair(X509Certificate2 server, X509Certificate2 authority) : IDisposable
{
    public X509Certificate2 Server { get; } = server;
    public X509Certificate2 Authority { get; } = authority;

    public void Dispose()
    {
        Server.Dispose();
        Authority.Dispose();
    }
}

public static class LocalWebCertificate
{
    // Private keys stay in the current Windows user's personal certificate store. Only
    // the public CA certificate is exported for clients to trust explicitly.
    public static LocalWebCertificatePair GetOrCreate(IPAddress address)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);

        using var authority = Find(store, AuthorityName(address), IsUsableAuthority)
            ?? CreateAuthority(address, store);
        var server = Find(store, ServerName(address), certificate => IsUsableServer(certificate, authority, address))
            ?? CreateServer(address, authority, store);
        try
        {
            var publicAuthority = X509CertificateLoader.LoadCertificate(authority.Export(X509ContentType.Cert));
            return new LocalWebCertificatePair(server, publicAuthority);
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    internal static LocalWebCertificatePair CreateTemporary(IPAddress address)
    {
        using var authority = CreateAuthority(address, null);
        var server = CreateServer(address, authority, null);
        try
        {
            return new LocalWebCertificatePair(server,
                X509CertificateLoader.LoadCertificate(authority.Export(X509ContentType.Cert)));
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    public static string Fingerprint(X509Certificate2 certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);

    private static string AuthorityName(IPAddress address) => $"CN=Stream Studio LAN CA {address}";
    private static string ServerName(IPAddress address) => $"CN=Stream Studio LAN {address}";

    private static X509Certificate2? Find(X509Store store, string subject, Func<X509Certificate2, bool> usable)
    {
        X509Certificate2? selected = null;
        foreach (var certificate in store.Certificates.Find(X509FindType.FindBySubjectDistinguishedName, subject, false))
        {
            if (selected is null && certificate.Subject == subject && usable(certificate)) selected = certificate;
            else certificate.Dispose();
        }
        return selected;
    }

    private static bool IsUsableAuthority(X509Certificate2 certificate) =>
        certificate.HasPrivateKey &&
        certificate.Subject == certificate.Issuer &&
        certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow &&
        certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddYears(1).AddDays(7) &&
        certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(extension => extension.CertificateAuthority) &&
        certificate.Extensions.OfType<X509KeyUsageExtension>().Any(extension =>
            (extension.KeyUsages & X509KeyUsageFlags.KeyCertSign) != 0);

    private static bool IsUsableServer(X509Certificate2 certificate, X509Certificate2 authority, IPAddress address)
    {
        if (!certificate.HasPrivateKey || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddDays(7) ||
            certificate.Issuer != authority.Subject ||
            !certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Any(extension =>
                extension.EnumerateIPAddresses().Contains(address)) ||
            !certificate.Extensions.OfType<X509AuthorityKeyIdentifierExtension>().Any()) return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate) && chain.ChainElements.Count == 2 &&
            chain.ChainElements[1].Certificate.Thumbprint == authority.Thumbprint;
    }

    private static X509Certificate2 CreateAuthority(IPAddress address, X509Store? store)
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest(AuthorityName(address), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
        return ImportAndStore(generated, store);
    }

    private static X509Certificate2 CreateServer(IPAddress address, X509Certificate2 authority, X509Store? store)
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest(ServerName(address), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(address);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, true, false));
        using var signed = request.Create(authority, DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(1), RandomNumberGenerator.GetBytes(16));
        using var generated = signed.CopyWithPrivateKey(key);
        return ImportAndStore(generated, store);
    }

    private static X509Certificate2 ImportAndStore(X509Certificate2 generated, X509Store? store)
    {
        var flags = X509KeyStorageFlags.UserKeySet;
        if (store is not null) flags |= X509KeyStorageFlags.PersistKeySet;
        var imported = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null, flags);
        try { store?.Add(imported); }
        catch { imported.Dispose(); throw; }
        return imported;
    }
}
