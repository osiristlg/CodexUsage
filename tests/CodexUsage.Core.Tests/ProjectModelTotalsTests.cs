using CodexUsageDashboard;
using Xunit;

public class ProjectModelTotalsTests
{
    [Fact]
    public void KeepsProjectsAndModelsSeparateAndCountsInputPlusOutput()
    {
        var totals = ProjectModelTotals.Build([
            new UsagePoint(DateTime.Today, "Model A", "Project A", 3_000_000_000, 2_000_000_000, 20, 10),
            new UsagePoint(DateTime.Today, "Model A", "Project A", 100, 50, 30, 10),
            new UsagePoint(DateTime.Today, "Model B", "Project A", 200, 100, 40, 20),
            new UsagePoint(DateTime.Today, "Model A", "Project B", 500, 400, 60, 30)
        ]);

        Assert.Equal(3_000_000_150, totals["Project A"]["Model A"]);
        Assert.Equal(240, totals["Project A"]["Model B"]);
        Assert.Equal(560, totals["Project B"]["Model A"]);
        Assert.Equal(2, totals.Count);
    }

    [Fact]
    public void EmptyHistoryProducesNoProjects() => Assert.Empty(ProjectModelTotals.Build([]));
}
