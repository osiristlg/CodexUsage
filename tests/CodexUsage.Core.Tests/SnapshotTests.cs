using CodexUsageDashboard;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace CodexUsage.Core.Tests;

public sealed class SnapshotTests(ITestOutputHelper output)
{
    [Fact]
    public void EffortTotalsAreCachedPerHourWithUnknownFallback()
    {
        var day = new DateTime(2026, 9, 15);
        var snapshot = new UsageSnapshot(day, day,
        [
            new UsagePoint(day.AddHours(1), "A", "One", 100, 0, 20, 0, Effort: "High"),
            new UsagePoint(day.AddHours(1), "B", "One", 50, 0, 10, 0, Effort: "Light"),
            new UsagePoint(day.AddHours(2), "A", "Two", 25, 0, 5, 0, Effort: "High"),
            new UsagePoint(day.AddHours(2), "B", "Two", 10, 0, 0, 0, Effort: " ")
        ], 1);
        Assert.Equal(120, snapshot.Aggregates.HourlyEfforts[1]["High"]);
        Assert.Equal(60, snapshot.Aggregates.HourlyEfforts[1]["Light"]);
        Assert.Equal(30, snapshot.Aggregates.HourlyEfforts[2]["High"]);
        Assert.Equal(10, snapshot.Aggregates.HourlyEfforts[2]["Unknown"]);
        Assert.Empty(snapshot.Aggregates.HourlyEfforts[0]);
        Assert.Equal(snapshot.Total, snapshot.Aggregates.HourlyEfforts.Sum(hour => hour.Values.Sum()));
        Assert.Same(snapshot.Aggregates, snapshot.Aggregates);
    }

    [Fact]
    public void LargeSnapshotMatchesPreviousHourlyCalculationAndReusesCache()
    {
        var day = new DateTime(2026, 9, 15);
        var points = Enumerable.Range(0, 100_000).Select(index => new UsagePoint(
            day.AddHours(index % 24), $"Model {index % 5}", $"Project {index % 7}", index % 1000, 0, 10, 2)).ToArray();
        var snapshot = new UsageSnapshot(day, day, points, 1);
        Dictionary<string, long[]> Previous() => points.GroupBy(point => point.Model).ToDictionary(group => group.Key,
            group => Enumerable.Range(0, 24).Select(hour => group.Where(point => point.Time.Hour == hour)
                .Sum(point => point.Total)).ToArray());
        var expected = Previous();
        foreach (var pair in expected) Assert.Equal(pair.Value, snapshot.Aggregates.HourlyModels[pair.Key]);
        var timer = Stopwatch.StartNew();
        for (var iteration = 0; iteration < 5; iteration++) _ = Previous();
        var previousMs = timer.Elapsed.TotalMilliseconds / 5;
        timer.Restart();
        for (var iteration = 0; iteration < 10_000; iteration++)
            Assert.Same(snapshot.Aggregates, snapshot.Aggregates);
        var cachedMs = timer.Elapsed.TotalMilliseconds / 10_000;
        output.WriteLine($"100,000 synthetic points: previous hourly grouping {previousMs:F3} ms/access; cached lookup {cachedMs:F6} ms/access. Excludes chart drawing and initial cache build.");
    }

    [Fact]
    public void CachedTotalsMatchPointsAndSeparateHours()
    {
        var day = new DateTime(2026, 9, 15);
        var points = new[]
        {
            new UsagePoint(day.AddHours(1), "A", "One", 100, 80, 20, 5),
            new UsagePoint(day.AddHours(1), "A", "Two", 50, 40, 10, 3, 2),
            new UsagePoint(day.AddHours(2), "B", "One", 25, 20, 5, 1)
        };
        var snapshot = new UsageSnapshot(day, day, points, 3);
        Assert.Equal(210, snapshot.Total);
        Assert.Equal(175, snapshot.Input);
        Assert.Equal(140, snapshot.Cached);
        Assert.Equal(35, snapshot.Output);
        Assert.Equal(9, snapshot.Reasoning);
        Assert.Equal(4, snapshot.Responses);
        Assert.Equal(180, snapshot.Aggregates.HourlyModels["A"][1]);
        Assert.Equal(30, snapshot.Aggregates.HourlyModels["B"][2]);
        Assert.Equal(150, snapshot.Aggregates.Projects["One"]);
        Assert.Equal(120, snapshot.Aggregates.HourlyProjects[1]["One"]);
        Assert.Empty(snapshot.Aggregates.HourlyProjects[0]);
        Assert.Same(snapshot.Aggregates, snapshot.Aggregates);
    }

    [Fact]
    public void NewPointsInvalidateRecordCopyWithoutChangingOldSnapshot()
    {
        var day = new DateTime(2026, 9, 15);
        var original = new UsageSnapshot(day, day, [], 0);
        var cached = original.Aggregates;
        var updated = original with { Points = [new UsagePoint(day, "B", "New", 10, 0, 2, 0)] };
        Assert.Equal(12, updated.Total);
        Assert.NotSame(cached, updated.Aggregates);
        Assert.Equal(0, original.Total);
        Assert.Same(cached, original.Aggregates);
        Assert.All(updated.Aggregates.HourlyModels["B"].Skip(1), value => Assert.Equal(0, value));
    }
}
