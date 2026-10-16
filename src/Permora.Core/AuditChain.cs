namespace Permora.Core;

public static class AuditChain
{
    public static AuditEvent Create(Guid appId, string actor, string action, string detail, DateTimeOffset now, AuditEvent? previous)
    {
        var entry = new AuditEvent(Guid.NewGuid(), appId, actor, action, detail, now.ToUniversalTime(),
            (previous?.Sequence ?? 0) + 1, previous?.Hash ?? "GENESIS", "");
        return entry with { Hash = Digest(entry) };
    }
    public static string Digest(AuditEvent entry) => Wire.Hash(Wire.Serialize(entry with { Hash = "" }));
    public static bool Verify(IReadOnlyList<AuditEvent> entries, string previousHash = "GENESIS", long previousSequence = 0)
    {
        Guid? application = null;
        foreach (var entry in entries)
        {
            application ??= entry.ApplicationId;
            if (entry.ApplicationId != application || entry.PreviousHash != previousHash
                || entry.Sequence != previousSequence + 1 || entry.Hash != Digest(entry)) return false;
            previousHash = entry.Hash; previousSequence = entry.Sequence;
        }
        return true;
    }
}
