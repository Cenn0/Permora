using Microsoft.Extensions.Configuration;
using Permora.Infrastructure;
namespace Permora.Tests;
public sealed class ConfigurationTests
{
    [Theory]
    [InlineData("http://sso.example.test")][InlineData("https://user:password@example.test")]
    [InlineData("https://example.test/#fragment")][InlineData("https://example.test/?token=private")]
    [InlineData("file:///etc/passwd")][InlineData("not-a-url")]
    public void Production_configuration_rejects_untrusted_urls(string value) => Assert.Throws<InvalidOperationException>(() => ConfigurationChecks.ValidateUrl(value, false));
    [Fact] public void Explicit_development_http_is_allowed() => ConfigurationChecks.ValidateUrl("http://127.0.0.1:8080", true);
    [Theory]
    [InlineData("", 10)]
    [InlineData("2", 0)]
    [InlineData("2", 61)]
    [InlineData("a b", 10)]
    public void Invalid_step_up_configuration_is_rejected(string acr, int maxAge)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["Portal:Authority"] = Fixture.Issuer,
            ["Portal:PublicOrigin"] = "https://portal.example.test",
            ["Portal:DataProtectionPath"] = "/tmp/unused-test-path",
            ["Portal:EnableDevelopmentLogin"] = "true",
            ["Portal:PrivilegedAcr"] = acr,
            ["Portal:PrivilegedAuthenticationMaxAgeMinutes"] = maxAge.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }).Build();
        Assert.Throws<InvalidOperationException>(() => ConfigurationChecks.ReadPortal(configuration, true));
    }

    [Fact] public void Development_authentication_cannot_start_in_production()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        { ["Portal:Authority"] = Fixture.Issuer, ["Portal:PublicOrigin"] = "https://portal.example.test", ["Portal:DataProtectionPath"] = "/tmp/unused-test-path", ["Portal:EnableDevelopmentLogin"] = "true" }).Build();
        Assert.Throws<InvalidOperationException>(() => ConfigurationChecks.ReadPortal(configuration, false));
    }
    [Fact]
    public void File_based_postgresql_connection_string_is_read_without_overriding_inline_values()
    {
        var directory = Path.Combine(Path.GetTempPath(), "permora-connection-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "database.secret");
        try
        {
            File.WriteAllText(file, "Host=127.0.0.1;Database=permora_test_example;Username=reader;Password=isolated" + Environment.NewLine);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:PortalFile"] = file
            }).Build();
            Assert.Equal("Host=127.0.0.1;Database=permora_test_example;Username=reader;Password=isolated",
                ConfigurationChecks.ReadPortalConnectionString(config));

            var conflicting = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:PortalFile"] = file,
                ["ConnectionStrings:Portal"] = "Host=unexpected"
            }).Build();
            var ex = Assert.Throws<InvalidOperationException>(() =>
                ConfigurationChecks.ReadPortalConnectionString(conflicting));
            Assert.DoesNotContain("isolated", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("unexpected", ex.Message, StringComparison.Ordinal);

            File.WriteAllText(file, "   ");
            Assert.Throws<InvalidOperationException>(() => ConfigurationChecks.ReadPortalConnectionString(config));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Invalid_or_missing_database_secret_file_fails_closed()
    {
        static IConfiguration Config(string file) => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:PortalFile"] = file }).Build();

        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationChecks.ReadPortalConnectionString(Config("relative-database.secret")));
        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationChecks.ReadPortalConnectionString(Config(
                Path.Combine(Path.GetTempPath(), "permora-missing-" + Guid.NewGuid().ToString("N")))));
        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationChecks.ReadPortalConnectionString(new ConfigurationBuilder().Build()));
    }

    [Fact]
    public void Existing_inline_connection_string_remains_supported_for_isolated_development()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:Portal"] = "Host=localhost;Database=permora_test_demo" }).Build();
        Assert.Equal("Host=localhost;Database=permora_test_demo",
            ConfigurationChecks.ReadPortalConnectionString(config));
    }

    [Fact]
    public void Directory_retention_requires_an_explicit_privacy_policy()
    {
        Assert.Null(ConfigurationChecks.ReadDirectoryRetentionDays(
            new ConfigurationBuilder().Build()));
        var enabled = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Privacy:UnreferencedDirectoryRetentionDays"] = "180" }).Build();
        Assert.Equal(180, ConfigurationChecks.ReadDirectoryRetentionDays(enabled));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("29")]
    [InlineData("-90")]
    [InlineData("3651")]
    [InlineData("never")]
    [InlineData("90.5")]
    public void Invalid_retention_cannot_silently_delete_personal_directory_data(string days)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Privacy:UnreferencedDirectoryRetentionDays"] = days }).Build();
        Assert.Throws<InvalidOperationException>(() =>
            ConfigurationChecks.ReadDirectoryRetentionDays(config));
    }

}
