namespace Permora.Tests;

public sealed class AuditPrivacyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Request_free_text_is_not_stored_in_append_only_audit()
    {
        var f = new Fixture();
        var store = new MemoryStore(f);
        var service = new ChangeService(store, f.Admins, f.Clock);
        var reason = "Temporary access for sensitive.person@permora.invalid";
        var operation = await service.RequestAsync(f.App.Id, f.Owner, f.Grant() with
        {
            Reason = reason
        }, f.App.Revision, "audit-privacy-req-0001", Ct);

        var entry = Assert.Single(store.Audit);
        Assert.Equal("change.requested", entry.Action);
        Assert.Contains(operation.Id.ToString("D"), entry.Detail);
        Assert.Contains("reasonSha256=" + Wire.Hash(reason), entry.Detail);
        Assert.DoesNotContain(reason, entry.Detail);
        Assert.DoesNotContain("sensitive.person", entry.Detail);
        Assert.True(AuditChain.Verify(store.Audit));
        Assert.Equal(reason, store.Operations[operation.Id].Change.Reason);
    }

    [Fact]
    public async Task Approval_reason_is_digest_only_in_audit_and_still_enforces_four_eyes()
    {
        var f = new Fixture();
        var store = new MemoryStore(f);
        var service = new ChangeService(store, f.Admins, f.Clock);
        var operation = await service.RequestAsync(f.App.Id, f.Owner, f.Grant(),
            f.App.Revision, "audit-privacy-req-0002", Ct);
        var reason = "Private review by analyst@permora.invalid";
        await service.DecideAsync(f.App.Id, operation.Id, f.Approver, true, reason, Ct);

        var entry = Assert.Single(store.Audit.Where(a => a.Action == "change.approved"));
        Assert.DoesNotContain(reason, entry.Detail);
        Assert.Contains("reasonSha256=" + Wire.Hash(reason), entry.Detail);
        Assert.Contains(operation.Id.ToString("D"), entry.Detail);
        Assert.Equal(OperationStatus.Queued, store.Operations[operation.Id].Status);
        Assert.Equal(reason, store.Operations[operation.Id].DecisionReason);
        Assert.True(AuditChain.Verify(store.Audit));
    }

    [Fact]
    public void Existing_operations_without_new_optional_decision_reason_remain_deserializable()
    {
        var f = new Fixture();
        var operation = new Operation(Guid.NewGuid(), f.App.Id, f.Owner, f.Grant(),
            f.App.Revision, "legacy-idempotency", "original-hash",
            OperationStatus.AwaitingApproval, f.Clock.Now, f.Clock.Now, 0);
        var data = System.Text.Json.Nodes.JsonNode.Parse(Wire.Serialize(operation))!.AsObject();
        data.Remove("decisionReason");
        var restored = Wire.Deserialize<Operation>(data.ToJsonString());
        Assert.Null(restored.DecisionReason);
        Assert.Equal(operation.Id, restored.Id);
        Assert.Equal(operation.Status, restored.Status);
    }

    [Fact]
    public void Central_iam_evidence_is_not_embedded_verbatim_in_immutable_audit_metadata()
    {
        var value = "SEC-1234 / alice.personal@permora.invalid / internal case";
        var detail = AuditDetails.IamApproval("internal-client-name", value);
        Assert.Contains("evidenceSha256=" + Wire.Hash(value), detail);
        Assert.Contains("clientIdSha256=" + Wire.Hash("internal-client-name"), detail);
        Assert.DoesNotContain("alice.personal", detail);
        Assert.DoesNotContain(value, detail);
        Assert.DoesNotContain("internal-client-name", detail);
        Assert.NotEqual(detail, AuditDetails.IamApproval("internal-client-name", "another evidence"));
    }
}
