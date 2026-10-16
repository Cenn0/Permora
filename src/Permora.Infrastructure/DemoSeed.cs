using Permora.Core;

namespace Permora.Infrastructure;

public static class DemoSeed
{
    public static readonly Guid TeamId = Guid.Parse("10000000-0000-4000-8000-000000000001");
    public static async Task RunAsync(IPortalStore store, string issuer, CancellationToken ct)
    {
        var owner = new Actor(Wire.Subject(issuer, "demo-owner"), "Demo-Team");
        var existingTeam = (await store.TeamsAsync(ct)).SingleOrDefault(t => t.Id == TeamId);
        var team = new Team(TeamId, "Produktion IT", 1,
        [new(owner.Key, owner.Name, ManagementRole.Owner), new(Wire.Subject(issuer, "demo-approver"), "Freigabe-Team", ManagementRole.Approver)]);
        if (existingTeam is null) await store.SaveTeamAsync(team, null, owner, ct);
        var existingApps = await store.ApplicationsAsync([TeamId], false, ct);
        var names = new[] { ("production-planning", "Produktionsplanung", "Planung, Steuerung und Freigabe von Produktionsaufträgen."),
            ("warehouse", "Lagerverwaltung", "Bestände und Warenbewegungen im Blick."),
            ("quality", "Qualitätsmanagement", "Prüfungen, Nachweise und Freigaben."),
            ("maintenance", "Wartungsportal", "Wartungsaufträge und technische Dokumentation.") };
        foreach (var (slug, name, description) in names)
        {
            if (existingApps.Any(a => a.RealmKey == "enterprise" && a.Slug == slug && a.Environment == "Test")) continue;
            var app = new ApplicationDocument(Guid.NewGuid(), TeamId, "enterprise", slug, name, description,
                "Test", ApplicationStatus.AwaitingIam, 1, null, null, null, AppPolicy.Default, [], [], []);
            await store.CreateApplicationAsync(app, owner, ct);
        }
        // Demo catalog entries deliberately have no fake applied Keycloak grants or successful operations.
    }
}
