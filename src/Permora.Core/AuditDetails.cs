namespace Permora.Core;

/// <summary>
/// Restricts append-only audit metadata to stable identifiers and one-way
/// digests. Free-form explanations are retained by their owning business
/// records where access controls and retention can be governed separately.
/// A SHA-256 value is still pseudonymous, NOT anonymous personal data.
/// </summary>
public static class AuditDetails
{
    public static string Requested(Operation operation) =>
        $"operation={operation.Id:D};kind={operation.Change.Kind};reasonSha256={Wire.Hash(operation.Change.Reason.Trim())}";

    public static string Decision(Guid operationId, string reason) =>
        $"operation={operationId:D};reasonSha256={Wire.Hash(reason.Trim())}";

    public static string IamApproval(string clientId, string evidenceReference) =>
        $"clientIdSha256={Wire.Hash(clientId.Trim())};evidenceSha256={Wire.Hash(evidenceReference.Trim())}";
}
