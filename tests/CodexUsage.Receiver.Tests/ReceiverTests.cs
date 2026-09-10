using System.Net;
using CodexUsage.Core;
using Xunit;

namespace CodexUsage.Receiver.Tests;

public sealed class ReceiverTests
{
    [Theory]
    [InlineData("192.168.1.0/24", "192.168.1.42", true)]
    [InlineData("192.168.1.0/24", "192.168.2.42", false)]
    [InlineData("10.0.0.0/8", "10.22.33.44", true)]
    [InlineData("::1/128", "::1", true)]
    public void SubnetPolicyMatchesExpectedAddresses(string subnet, string address, bool expected) =>
        Assert.Equal(expected, NetworkPolicy.IsAllowed(IPAddress.Parse(address), [subnet]));

    [Fact]
    public async Task ReplacementIsAtomicAndDuplicateRequestsAreIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "codex-usage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "usage.db");
        try
        {
            await UsageDatabase.InitializeAsync(database);
            var start = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
            var first = Payload(start, new TokenCounts(100, 80, 20, 5, 1));
            var accepted = await UsageDatabase.ReplaceAndSummarizeAsync(database, "client-a", first, "request-1");
            Assert.False(accepted.Duplicate);
            Assert.Equal(120, accepted.Combined.Total);
            Assert.Single(accepted.Rows);
            Assert.Equal(120, accepted.Rows[0].Tokens.Total);
            Assert.Equal(120, Assert.Single(accepted.MachineRows["client-a"]).Tokens.Total);

            var duplicate = await UsageDatabase.ReplaceAndSummarizeAsync(database, "client-a", first, "request-1");
            Assert.True(duplicate.Duplicate);
            Assert.Equal(120, duplicate.Combined.Total);

            var replacement = Payload(start, new TokenCounts(250, 200, 50, 10, 2));
            var replaced = await UsageDatabase.ReplaceAndSummarizeAsync(database, "client-a", replacement, "request-2");
            Assert.False(replaced.Duplicate);
            Assert.Equal(300, replaced.Combined.Total);
            Assert.Equal(300, Assert.Single(replaced.Rows).Tokens.Total);
            Assert.Equal(300, Assert.Single(replaced.MachineRows["client-a"]).Tokens.Total);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task NamedProjectRelabelsMatchingAnonymousHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "codex-usage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "usage.db");
        try
        {
            await UsageDatabase.InitializeAsync(database);
            var start = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
            var anonymous = Payload(start, new TokenCounts(100, 80, 20, 5, 1)) with
            {
                Rows = [new AggregateRow(start, "Model", "Project ABCDEF12", new TokenCounts(100, 80, 20, 5, 1))
                    { ProjectId = "Project ABCDEF12" }]
            };
            await UsageDatabase.ReplaceAndSummarizeAsync(database, "client-a", anonymous, "request-anonymous");

            var namedStart = start.AddHours(1);
            var named = new SyncPayload("incremental", "Machine A", namedStart, namedStart.AddHours(1), start, start.AddHours(2),
                [new AggregateRow(namedStart, "Model", "Codex Usage", new TokenCounts(200, 160, 40, 8, 2))
                    { ProjectId = "Project ABCDEF12" }])
            { QueryStartUtc = start, QueryEndUtc = start.AddHours(2) };
            var result = await UsageDatabase.ReplaceAndSummarizeAsync(database, "client-a", named, "request-named");

            Assert.Equal(2, result.Rows.Count);
            Assert.All(result.Rows, row => Assert.Equal("Codex Usage", row.Project));
            Assert.All(result.Rows, row => Assert.Equal("Project ABCDEF12", row.ProjectId));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void OptionalProjectIdIsValidatedWithoutRejectingLegacyRows()
    {
        var start = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
        var legacy = Payload(start, new TokenCounts(1, 0, 2, 0, 1));
        Assert.Null(PayloadValidation.Validate(legacy, 10));

        var invalid = legacy with
        {
            Rows = [legacy.Rows[0] with { ProjectId = "" }]
        };
        Assert.Equal("Invalid project identifier.", PayloadValidation.Validate(invalid, 10));
    }

    private static SyncPayload Payload(DateTime start, TokenCounts counts) => new(
        "incremental", "Machine A", start, start.AddHours(1), start, start.AddHours(1),
        [new AggregateRow(start, "Model", "Project", counts)]);
}
