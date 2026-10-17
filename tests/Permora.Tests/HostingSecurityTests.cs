using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Permora.Infrastructure;

namespace Permora.Tests;

public sealed class HostingSecurityTests
{
    private const string Password = "isolated-certificate-password-only";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (string Pfx, string PasswordFile) Certificate(string root, string name, bool expired = false)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Permora isolated key protection", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-3),
            expired ? DateTimeOffset.UtcNow.AddDays(-1) : DateTimeOffset.UtcNow.AddDays(10));
        var pfx = Path.Combine(root, name + ".pfx");
        var passwordFile = Path.Combine(root, name + ".password");
        File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pfx, Password));
        File.WriteAllText(passwordFile, Password);
        return (pfx, passwordFile);
    }

    private static ServiceProvider Provider(string keyDirectory, (string Pfx, string PasswordFile) certificate,
        (string Pfx, string PasswordFile)? previous = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["KeyProtection:CertificatePath"] = certificate.Pfx,
            ["KeyProtection:PasswordFile"] = certificate.PasswordFile
        };
        if (previous is { } old)
        {
            values["KeyProtection:PreviousCertificates:0:CertificatePath"] = old.Pfx;
            values["KeyProtection:PreviousCertificates:0:PasswordFile"] = old.PasswordFile;
        }
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPermoraHostingSecurity(new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            new PortalOptions { DataProtectionPath = keyDirectory }, development: false);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Encrypted_key_ring_survives_replica_restart_backup_and_wrapping_certificate_rotation()
    {
        var root = Path.Combine(Path.GetTempPath(), "permora-keyring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var a = Certificate(root, "old");
            var b = Certificate(root, "new");
            var directory = Path.Combine(root, "shared-keys");
            string original;
            using (var first = Provider(directory, a))
                original = first.GetRequiredService<IDataProtectionProvider>().CreateProtector("session-test").Protect("before-rotation");
            Assert.All(Directory.GetFiles(directory, "*.xml"), file =>
                Assert.Contains("encryptedSecret", File.ReadAllText(file), StringComparison.Ordinal));

            string rotated;
            using (var second = Provider(directory, b, a))
            {
                second.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30));
                var protector = second.GetRequiredService<IDataProtectionProvider>().CreateProtector("session-test");
                Assert.Equal("before-rotation", protector.Unprotect(original));
                rotated = protector.Protect("after-rotation");
            }
            var restored = Path.Combine(root, "restored-keys");
            Directory.CreateDirectory(restored);
            foreach (var file in Directory.GetFiles(directory, "*.xml"))
                File.Copy(file, Path.Combine(restored, Path.GetFileName(file)));
            using (var replica = Provider(restored, b, a))
            {
                var protector = replica.GetRequiredService<IDataProtectionProvider>().CreateProtector("session-test");
                Assert.Equal("before-rotation", protector.Unprotect(original));
                Assert.Equal("after-rotation", protector.Unprotect(rotated));
                Assert.Throws<CryptographicException>(() => replica.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("another-purpose").Unprotect(original));
            }
            using (var withoutPrevious = Provider(restored, b))
                Assert.Throws<CryptographicException>(() => withoutPrevious.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("session-test").Unprotect(original));
            using (var missingKeys = Provider(Path.Combine(root, "lost-keyring"), b, a))
                Assert.Throws<CryptographicException>(() => missingKeys.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("session-test").Unprotect(rotated));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Production_refuses_missing_or_expired_key_encryption_material()
    {
        var root = Path.Combine(Path.GetTempPath(), "permora-keyring-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddPermoraHostingSecurity(
                new ConfigurationBuilder().Build(), new PortalOptions { DataProtectionPath = root }, development: false));
            var expired = Certificate(root, "expired", expired: true);
            Assert.Throws<InvalidOperationException>(() => Provider(Path.Combine(root, "keys"), expired));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(null, "127.0.0.1", false)]
    [InlineData("192.0.2.9", "192.0.2.8", false)]
    [InlineData("192.0.2.9", "192.0.2.9", true)]
    public async Task Forwarded_https_is_accepted_only_from_the_explicit_trusted_proxy(string? trusted, string remote, bool expectedHttps)
    {
        var values = new Dictionary<string, string?>();
        if (trusted is not null) values["Transport:TrustedProxies:0"] = trusted;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPermoraHostingSecurity(new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            new PortalOptions { DataProtectionPath = Path.GetTempPath() }, development: true);
        using var provider = services.BuildServiceProvider();
        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance,
            provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>());
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Path = "/api/v1/applications";
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.7";
        await middleware.Invoke(context);
        Assert.Equal(expectedHttps, context.Request.IsHttps);
        Assert.Equal(!expectedHttps, HostingSecurity.RequiresHttpsRejection(context, development: false));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Encrypted_postgresql_sessions_are_shared_and_logout_is_seen_by_another_replica()
    {
        var connection = Environment.GetEnvironmentVariable("PERMORA_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection)) Assert.Skip("Requires isolated PostgreSQL integration database.");
        var settings = new NpgsqlConnectionStringBuilder(connection);
        Assert.StartsWith("permora_test_", settings.Database ?? "");
        var schema = "test_" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "permora-session-ha-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var admin = NpgsqlDataSource.Create(connection!);
        await using (var create = admin.CreateCommand($"CREATE SCHEMA {schema}")) await create.ExecuteNonQueryAsync(Ct);
        try
        {
            settings.SearchPath = schema;
            await using var db = NpgsqlDataSource.Create(settings.ConnectionString);
            await new PostgresStore(db).MigrateAsync(Ct);
            var cert = Certificate(root, "session");
            using var a = Provider(Path.Combine(root, "keys"), cert);
            using var b = Provider(Path.Combine(root, "keys"), cert);
            var storeA = new SessionStore(db, a.GetRequiredService<IDataProtectionProvider>());
            var storeB = new SessionStore(db, b.GetRequiredService<IDataProtectionProvider>());
            var identity = new ClaimsIdentity([new Claim("sub", "isolated-user"), new Claim("acr", "2"),
                new Claim("auth_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture))], "test");
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), new AuthenticationProperties
                { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5) }, CookieAuthenticationDefaults.AuthenticationScheme);
            var id = await storeA.StoreAsync(ticket);
            var other = await storeB.RetrieveAsync(id);
            Assert.NotNull(other);
            Assert.True(SessionAssurance.IsPrivileged(other.Principal, new PortalOptions(), DateTimeOffset.UtcNow));
            await storeB.RemoveAsync(id);
            Assert.Null(await storeA.RetrieveAsync(id));
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await drop.ExecuteNonQueryAsync(Ct);
            Directory.Delete(root, recursive: true);
        }
    }
}
