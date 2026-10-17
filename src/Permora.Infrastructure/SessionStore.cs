using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using System.Security.Cryptography;

namespace Permora.Infrastructure;

public sealed class SessionStore(NpgsqlDataSource source, IDataProtectionProvider protection, Microsoft.Extensions.Logging.ILogger<SessionStore> logger) : ITicketStore
{
    private readonly IDataProtector protector = protection.CreateProtector("Permora.ServerSession.v1");
    private void Diagnostic(string action, AuthenticationTicket ticket)
    {
        if (Environment.GetEnvironmentVariable("PERMORA_CI_DIAG_ASSURANCE") != "1") return;
        logger.LogInformation("PERMORA-CI-ASSURANCE: ticket_action={Action} ticket_authenticated={Authenticated} ticket_acr_2={Acr2} ticket_auth_time_count={AuthTimeCount}",
            action, ticket.Principal.Identity?.IsAuthenticated == true,
            ticket.Principal.FindAll("acr").Any(c => c.Value == "2"),
            ticket.Principal.FindAll("auth_time").Count());
    }

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await RenewAsync(key, ticket);
        Diagnostic("store", ticket);
        return key;
    }
    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        Diagnostic("renew", ticket);
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
        try
        {
            var ticket = TicketSerializer.Default.Deserialize(protector.Unprotect(bytes));
            if (ticket is not null) Diagnostic("retrieve", ticket);
            return ticket;
        }
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
