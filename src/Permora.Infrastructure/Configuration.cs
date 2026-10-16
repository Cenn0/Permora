using Microsoft.Extensions.Configuration;
using Permora.Core;

namespace Permora.Infrastructure;

public sealed class PortalOptions
{
    public string Authority { get; init; } = "";
    public string ClientId { get; init; } = "permora-portal";
    public string ClientSecretFile { get; init; } = "";
    public string PublicOrigin { get; init; } = "";
    public string DataProtectionPath { get; init; } = "";
    public string[] AdministratorSubjects { get; init; } = [];
    public Dictionary<string, string[]> RealmTeams { get; init; } = [];
    public bool AllowHttpInDevelopment { get; init; }
    public bool EnableDevelopmentLogin { get; init; }
    public string DevelopmentSubject { get; init; } = "demo-owner";
}

public static class ConfigurationChecks
{
    public static void AddLocalFile(ConfigurationManager configuration)
    {
        var path = Environment.GetEnvironmentVariable("PERMORA_CONFIG");
        if (!string.IsNullOrWhiteSpace(path))
            configuration.AddJsonFile(Path.GetFullPath(path), optional: false, reloadOnChange: false).AddEnvironmentVariables();
    }
    public static PortalOptions ReadPortal(IConfiguration configuration, bool development)
    {
        var options = configuration.GetSection("Portal").Get<PortalOptions>() ?? new PortalOptions();
        ValidateUrl(options.Authority, development && options.AllowHttpInDevelopment);
        ValidateUrl(options.PublicOrigin, development && options.AllowHttpInDevelopment);
        if (new Uri(options.PublicOrigin).AbsolutePath != "/") throw new InvalidOperationException("PublicOrigin must not contain a path.");
        if (string.IsNullOrWhiteSpace(options.ClientId) || string.IsNullOrWhiteSpace(options.DataProtectionPath))
            throw new InvalidOperationException("Portal client and persistent data-protection path are required.");
        if (options.EnableDevelopmentLogin && !development)
            throw new InvalidOperationException("Development authentication is forbidden outside Development.");
        if (!options.EnableDevelopmentLogin && !File.Exists(options.ClientSecretFile))
            throw new InvalidOperationException("Portal client secret file is missing.");
        return options;
    }
    public static IReadOnlySet<string> Administrators(PortalOptions options) =>
        options.AdministratorSubjects.Select(s => Wire.Subject(options.Authority, s)).ToHashSet();
    public static void ValidateUrl(string value, bool allowHttp)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.Query)
            || (uri.Scheme != Uri.UriSchemeHttps && !(allowHttp && uri.Scheme == Uri.UriSchemeHttp)))
            throw new InvalidOperationException("A trusted absolute HTTPS URL is required.");
    }
}
