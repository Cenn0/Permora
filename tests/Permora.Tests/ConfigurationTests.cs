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
}
