using System.Text;
using Microsoft.Extensions.Configuration;
using Permora.Infrastructure;

namespace Permora.Tests;

public sealed class KeycloakConfigurationTests
{
    [Fact]
    public void Empty_protected_subject_list_does_not_silently_drop_a_configured_realm()
    {
        const string json = """
        {"Keycloak":{"AllowHttpInDevelopment":true,"Realms":{"test-realm":{
          "BaseUrl":"http://127.0.0.1:19085","Realm":"test-realm",
          "Issuer":"http://127.0.0.1:18085/realms/test-realm",
          "Roles":{"ClientId":"role-reader","SecretFile":"/run/secrets/roles"},
          "Assignments":{"ClientId":"role-mapper","SecretFile":"/run/secrets/mappings"},
          "Directory":{"ClientId":"directory-reader","SecretFile":"/run/secrets/directory"},
          "ProtectedSubjectIds":[]
        }}}}
        """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var options = KeycloakConfiguration.Read(configuration);
        var realm = Assert.Single(options.Realms).Value;
        Assert.True(options.AllowHttpInDevelopment);
        Assert.Equal("test-realm", realm.Realm);
        Assert.Equal("role-mapper", realm.Assignments.ClientId);
        Assert.Empty(realm.ProtectedSubjectIds);
    }

    [Theory]
    [InlineData("BaseUrl")]
    [InlineData("Realm")]
    [InlineData("Issuer")]
    [InlineData("Roles:SecretFile")]
    [InlineData("Assignments:ClientId")]
    [InlineData("Directory:SecretFile")]
    public void Incomplete_configured_realms_fail_instead_of_becoming_an_empty_dictionary(string missing)
    {
        var values = new Dictionary<string, string?>
        {
            ["BaseUrl"]="https://sso.example.test",["Realm"]="test",["Issuer"]="https://sso.example.test/realms/test",
            ["Roles:ClientId"]="roles",["Roles:SecretFile"]="/run/secrets/roles",
            ["Assignments:ClientId"]="assignments",["Assignments:SecretFile"]="/run/secrets/assignments",
            ["Directory:ClientId"]="directory",["Directory:SecretFile"]="/run/secrets/directory"
        };
        values.Remove(missing);
        var config = new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(
            pair => "Keycloak:Realms:test:" + pair.Key, pair => pair.Value)).Build();
        Assert.Throws<InvalidOperationException>(() => KeycloakConfiguration.Read(config));
    }
}
