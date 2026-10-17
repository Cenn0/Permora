using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Permora.Infrastructure;

public sealed class KeyProtectionOptions
{
    public string CertificatePath { get; init; } = "";
    public string PasswordFile { get; init; } = "";
    public KeyProtectionCertificate[] PreviousCertificates { get; init; } = [];
}
public sealed record KeyProtectionCertificate(string CertificatePath, string PasswordFile);

/// <summary>Shared encrypted sessions are a prerequisite, not proof, of deployment HA.</summary>
public static class HostingSecurity
{
    public static IServiceCollection AddPermoraHostingSecurity(this IServiceCollection services,
        IConfiguration configuration, PortalOptions portal, bool development)
    {
        var settings = configuration.GetSection("KeyProtection").Get<KeyProtectionOptions>() ?? new();
        var protection = services.AddDataProtection().SetApplicationName("Permora")
            .PersistKeysToFileSystem(new DirectoryInfo(portal.DataProtectionPath))
            .SetDefaultKeyLifetime(TimeSpan.FromDays(30));
        if (string.IsNullOrWhiteSpace(settings.CertificatePath))
        {
            if (!development)
                throw new InvalidOperationException("Production requires a key-encryption certificate and protected shared key storage.");
            if (settings.PreviousCertificates.Length != 0 || !string.IsNullOrWhiteSpace(settings.PasswordFile))
                throw new InvalidOperationException("Key protection configuration is incomplete.");
        }
        else
        {
            if (settings.PreviousCertificates.Length > 4)
                throw new InvalidOperationException("At most four previous decryption certificates are allowed.");
            var certificates = new List<X509Certificate2>();
            try
            {
                certificates.Add(LoadCertificate(settings.CertificatePath, settings.PasswordFile, requireCurrent: true));
                foreach (var previous in settings.PreviousCertificates)
                    certificates.Add(LoadCertificate(previous.CertificatePath, previous.PasswordFile, requireCurrent: false));
                protection.ProtectKeysWithCertificate(certificates[0]);
                protection.UnprotectKeysWithAnyCertificate(certificates.ToArray());
                // The provider keeps certificate references. Dispose them with the DI container,
                // not before key-ring encryption/decryption completes.
                services.AddSingleton(_ => new CertificateLifetime(certificates));
                services.AddOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>()
                    .Configure<CertificateLifetime>((_, lifetime) => GC.KeepAlive(lifetime));
            }
            catch
            {
                foreach (var certificate in certificates) certificate.Dispose();
                throw;
            }
        }

        var proxies = configuration.GetSection("Transport:TrustedProxies").Get<string[]>() ?? [];
        var addresses = new List<IPAddress>();
        foreach (var value in proxies)
        {
            if (!IPAddress.TryParse(value, out var address)
                || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
                throw new InvalidOperationException("Each trusted proxy must be an explicit IP address; wildcard trust is forbidden.");
            addresses.Add(address);
        }
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            // When no proxies are configured, do not interpret ANY forwarded headers.
            options.ForwardedHeaders = addresses.Count == 0 ? ForwardedHeaders.None
                : ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var address in addresses.Distinct()) options.KnownProxies.Add(address);
        });
        return services;
    }

    public static bool RequiresHttpsRejection(HttpContext context, bool development) =>
        !development && !context.Request.IsHttps
        && !context.Request.Path.StartsWithSegments("/health");

    private static X509Certificate2 LoadCertificate(string path, string passwordPath, bool requireCurrent)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)
            || !Path.IsPathFullyQualified(passwordPath) || !File.Exists(passwordPath))
            throw new InvalidOperationException("Key protection requires existing, absolute certificate and password-file paths.");
        var password = File.ReadAllText(passwordPath).TrimEnd('\r', '\n');
        if (password.Length < 16)
            throw new InvalidOperationException("The key-encryption certificate password must contain at least 16 characters.");
        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("The key-encryption certificate could not be loaded.");
        }
        using var rsa = certificate.GetRSAPrivateKey();
        var now = DateTime.UtcNow;
        if (!certificate.HasPrivateKey || rsa is null || rsa.KeySize < 2048
            || (requireCurrent && (certificate.NotBefore.ToUniversalTime() > now || certificate.NotAfter.ToUniversalTime() <= now)))
        {
            certificate.Dispose();
            throw new InvalidOperationException("Key protection requires a valid RSA private key of at least 2048 bits; new encryption certificates must be current.");
        }
        // Expired OLD wrapping certificates are intentionally allowed for historical
        // backup decryption. Revocation/compromise is a separate incident decision.
        return certificate;
    }

    private sealed class CertificateLifetime(List<X509Certificate2> certificates) : IDisposable
    {
        public void Dispose()
        {
            foreach (var certificate in certificates) certificate.Dispose();
        }
    }
}
