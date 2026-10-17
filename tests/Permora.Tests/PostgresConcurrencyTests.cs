using Npgsql;
using Permora.Infrastructure;

namespace Permora.Tests;

/// <summary>Real PostgreSQL row-lock serialization and cross-application progress.</summary>
public sealed class PostgresConcurrencyTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Simultaneous_writers_serialize_per_application_without_blocking_unrelated_apps()
    {
        var value = Environment.GetEnvironmentVariable("PERMORA_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(value))
            Assert.Skip("Set PERMORA_TEST_POSTGRES to an isolated permora_test_* PostgreSQL database.");
        var options = new NpgsqlConnectionStringBuilder(value);
        Assert.StartsWith("permora_test_", options.Database ?? "");
        var schema = "test_" + Guid.NewGuid().ToString("N");
        var ct = TestContext.Current.CancellationToken;

        await using var admin = NpgsqlDataSource.Create(value!);
        await using (var create = admin.CreateCommand($"CREATE SCHEMA {schema}"))
            await create.ExecuteNonQueryAsync(ct);
        try
        {
            options.SearchPath = schema;
            await using var source = NpgsqlDataSource.Create(options.ConnectionString);
            var store = new PostgresStore(source);
            var fixture = new Fixture();
            await store.MigrateAsync(ct);
            await store.SaveTeamAsync(fixture.Team, null, fixture.Owner, ct);
            await store.CreateApplicationAsync(fixture.App, fixture.Owner, ct);
            var another = fixture.App with
            {
                Id = Guid.NewGuid(),
                Slug = "independent-concurrency-test",
                Name = "Independent app"
            };
            await store.CreateApplicationAsync(another, fixture.Owner, ct);

            var enteredFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var enteredSecond = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var first = Task.Run(() => store.InApplicationAsync(fixture.App.Id, async tx =>
            {
                enteredFirst.TrySetResult(true);
                await releaseFirst.Task.WaitAsync(ct);
                tx.Application = tx.Application with
                {
                    Name = "serialized-first", Revision = tx.Application.Revision + 1
                };
                return true;
            }, ct), ct);
            Task<bool>? second = null;
            try
            {
                await enteredFirst.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                second = Task.Run(() => store.InApplicationAsync(fixture.App.Id, async tx =>
                {
                    enteredSecond.TrySetResult(true);
                    tx.Application = tx.Application with
                    {
                        Name = "serialized-second", Revision = tx.Application.Revision + 1
                    };
                    return true;
                }, ct), ct);

                // With app A locked, an unrelated app B must remain independently writable.
                await store.InApplicationAsync(another.Id, async tx =>
                {
                    tx.Application = tx.Application with { Name = "independent-progress", Revision = tx.Application.Revision + 1 };
                    return true;
                }, ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
                Assert.Equal("independent-progress", (await store.ApplicationAsync(another.Id, ct))!.Name);

                // The same-app writer must NOT enter the transaction while writer A owns the lock.
                await Task.Delay(TimeSpan.FromMilliseconds(400), ct);
                Assert.False(enteredSecond.Task.IsCompleted);
            }
            finally
            {
                releaseFirst.TrySetResult(true);
            }
            await first.WaitAsync(TimeSpan.FromSeconds(15), ct);
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(15), ct);
            var final = (await store.ApplicationAsync(fixture.App.Id, ct))!;
            Assert.Equal("serialized-second", final.Name);
            Assert.Equal(fixture.App.Revision + 2, final.Revision);
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await drop.ExecuteNonQueryAsync(ct);
        }
    }
}
