using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using System.Security.Cryptography;

namespace Permora.Infrastructure;

public sealed class SessionStore(NpgsqlDataSource source, IDataProtectionProvider protection) : ITicketStore
{
    private readonly IDataProtector protector = protection.CreateProtector("Permora.ServerSession.v1");
    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await RenewAsync(key, ticket);
        return key;
    }
    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO sessions(id, ticket, expires_at) VALUES (@id, @ticket, @expires)
            ON CONFLICT(id) DO UPDATE SET ticket=@ticket, expires_at=@expires
            """, connection);
        command.Parameters.AddWithValue("id", key);
        command.Parameters.AddWithValue("ticket", protector.Protect(TicketSerializer.Default.Serialize(ticket)));
        command.Parameters.AddWithValue("expires", ticket.Properties.ExpiresUtc ?? DateTimeOffset.UtcNow.AddMinutes(30));
        await command.ExecuteNonQueryAsync();
    }
    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT ticket FROM sessions WHERE id=@id AND expires_at>now()", connection);
        command.Parameters.AddWithValue("id", key);
        var bytes = await command.ExecuteScalarAsync() as byte[];
        if (bytes is null) return null;
        try { return TicketSerializer.Default.Deserialize(protector.Unprotect(bytes)); }
        catch (CryptographicException) { await RemoveAsync(key); return null; }
    }
    public async Task RemoveAsync(string key)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("DELETE FROM sessions WHERE id=@id", connection);
        command.Parameters.AddWithValue("id", key);
        await command.ExecuteNonQueryAsync();
    }
}
