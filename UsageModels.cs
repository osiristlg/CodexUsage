namespace CodexUsageDashboard;

internal sealed record UsagePoint(DateTime Time, string Model, string Project, long Input, long Cached, long Output, long Reasoning,
    long Responses = 1, string Effort = "Unknown")
{
    public long Total => Input + Output;
}

// Published point collections are immutable. Replacing Points invalidates derived
// values, including on record copies; hover and repaint reuse the existing result.
internal sealed record UsageSnapshot(DateTime Day, DateTime RefreshedAt, IReadOnlyList<UsagePoint> Points, int FilesScanned)
{
    private IReadOnlyList<UsagePoint>? aggregatePoints;
    private SnapshotAggregates? aggregateCache;
    [System.Text.Json.Serialization.JsonIgnore]
    public SnapshotAggregates Aggregates
    {
        get
        {
            if (aggregateCache is null || !ReferenceEquals(aggregatePoints, Points))
            {
                aggregateCache = SnapshotAggregates.Build(Points);
                aggregatePoints = Points;
            }
            return aggregateCache;
        }
    }
    public long Total => Aggregates.Input + Aggregates.Output;
    public long Input => Aggregates.Input;
    public long Cached => Aggregates.Cached;
    public long Output => Aggregates.Output;
    public long Reasoning => Aggregates.Reasoning;
    public long Responses => Aggregates.Responses;
}

internal sealed class SnapshotAggregates
{
    public long Input { get; private set; }
    public long Cached { get; private set; }
    public long Output { get; private set; }
    public long Reasoning { get; private set; }
    public long Responses { get; private set; }
    public Dictionary<string, long[]> HourlyModels { get; } = new();
    public Dictionary<string, long> Projects { get; } = new();
    public Dictionary<string, long>[] HourlyProjects { get; } = Enumerable.Range(0, 24)
        .Select(_ => new Dictionary<string, long>()).ToArray();
    public Dictionary<string, long>[] HourlyEfforts { get; } = Enumerable.Range(0, 24)
        .Select(_ => new Dictionary<string, long>()).ToArray();

    public static SnapshotAggregates Build(IReadOnlyList<UsagePoint> points)
    {
        var result = new SnapshotAggregates();
        foreach (var point in points)
        {
            result.Input += point.Input;
            result.Cached += point.Cached;
            result.Output += point.Output;
            result.Reasoning += point.Reasoning;
            result.Responses += point.Responses;
            if (!result.HourlyModels.TryGetValue(point.Model, out var hours))
                result.HourlyModels[point.Model] = hours = new long[24];
            var hour = point.Time.Hour;
            hours[hour] += point.Total;
            result.Projects[point.Project] = result.Projects.GetValueOrDefault(point.Project) + point.Total;
            var projects = result.HourlyProjects[hour];
            projects[point.Project] = projects.GetValueOrDefault(point.Project) + point.Total;
            var effort = string.IsNullOrWhiteSpace(point.Effort) ? "Unknown" : point.Effort.Trim();
            var efforts = result.HourlyEfforts[hour];
            efforts[effort] = efforts.GetValueOrDefault(effort) + point.Total;
        }
        return result;
    }
}
