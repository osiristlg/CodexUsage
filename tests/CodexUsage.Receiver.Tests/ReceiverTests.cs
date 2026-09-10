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

            var duplicate = await UsageDatabase.ReplaceAndSummarizeAsync(database, "client-a", first, "request-1");
            Assert.True(duplicate.Duplicate);
            Assert.Equal(120, duplicate.Combined.Total);

            var replacement = Payload(start, new TokenCounts(250, 200, 50, 10, 2));
            var replaced = await UsageDatabase.ReplaceAndSummarizeAsync(database, "client-a", replacement, "request-2");
            Assert.False(replaced.Duplicate);
            Assert.Equal(300, replaced.Combined.Total);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static SyncPayload Payload(DateTime start, TokenCounts counts) => new(
        "incremental", "Machine A", start, start.AddHours(1), start, start.AddHours(1),
        [new AggregateRow(start, "Model", "Project", counts)]);
}
