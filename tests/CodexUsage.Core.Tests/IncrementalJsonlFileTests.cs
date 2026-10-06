using CodexUsage.Core;
using Xunit;

public sealed class IncrementalJsonlFileTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), "usage-tail-" + Guid.NewGuid() + ".jsonl");
    private static IncrementalJsonlFile<List<string>> Reader() => new(() => [], (state, line) => state.Add(line));

    [Fact]
    public void UnchangedFilesHaveNoReadsAndAppendRetainsContext()
    {
        File.WriteAllText(path, "context\r\nfirst\n");
        var reader = Reader();
        reader.Update(path, default);
        var bytes = reader.BytesRead;
        reader.Update(path, default);
        Assert.Equal(bytes, reader.BytesRead);
        File.AppendAllText(path, "second\n");
        reader.Update(path, default);
        Assert.Equal(new[] { "context", "first", "second" }, reader.State);
    }

    [Fact]
    public void PartialUtf8TailIsRetriedWithoutDuplicatingCompletedLines()
    {
        File.WriteAllText(path, "first\npartial é");
        var reader = Reader();
        reader.Update(path, default);
        Assert.Single(reader.State);
        File.AppendAllText(path, " completed\n");
        reader.Update(path, default);
        Assert.Equal(new[] { "first", "partial é completed" }, reader.State);
    }

    [Fact]
    public void TruncationAndLargerReplacementResetState()
    {
        File.WriteAllText(path, "original line\n");
        var reader = Reader();
        reader.Update(path, default);
        File.WriteAllText(path, "short\n");
        reader.Update(path, default);
        Assert.Equal(new[] { "short" }, reader.State);
        File.WriteAllText(path, "replacement much larger\nnext\n");
        reader.Update(path, default);
        Assert.Equal(new[] { "replacement much larger", "next" }, reader.State);
    }

    [Fact]
    public void AppendReadsOnlyTailNotLargeExistingPrefix()
    {
        File.WriteAllText(path, new string('x', 2_000_000) + "\n");
        var reader = Reader();
        reader.Update(path, default);
        var before = reader.BytesRead;
        File.AppendAllText(path, "new\n");
        reader.Update(path, default);
        Assert.InRange(reader.BytesRead - before, 4, 132);
        Assert.Equal(2, reader.State.Count);
    }

    [Fact]
    public void CancellationDoesNotCommitUnreadData()
    {
        File.WriteAllText(path, "first\n");
        var reader = Reader();
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => reader.Update(path, source.Token));
        reader.Update(path, default);
        Assert.Equal(new[] { "first" }, reader.State);
    }

    public void Dispose() => File.Delete(path);
}
