using System.Text;

namespace CodexUsage.Core;

// Retains parser state and a byte checkpoint, never the raw log. Only complete
// lines are committed; an unfinished tail is retried on the next refresh.
public sealed class IncrementalJsonlFile<T>(Func<T> createState, Action<T, string> parseLine)
{
    public T State { get; private set; } = createState();
    public long BytesRead { get; private set; }
    private long offset;
    private long observedLength;
    private DateTime observedWrite;
    private DateTime creation;
    private byte[] boundary = [];
    private bool initialized;

    public void Update(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var info = new FileInfo(path);
        if (initialized && info.Length == observedLength && info.LastWriteTimeUtc == observedWrite &&
            info.CreationTimeUtc == creation) return;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        var reset = !initialized || info.CreationTimeUtc != creation || stream.Length < offset ||
            (info.Length <= observedLength && info.LastWriteTimeUtc != observedWrite);
        if (!reset && boundary.Length > 0)
        {
            stream.Position = offset - boundary.Length;
            var check = new byte[boundary.Length];
            var read = stream.Read(check);
            BytesRead += read;
            reset = read != boundary.Length || !check.AsSpan().SequenceEqual(boundary);
        }
        if (reset)
        {
            State = createState();
            offset = 0;
            boundary = [];
        }
        initialized = true;
        creation = info.CreationTimeUtc;
        // Capture before reading. Concurrent appends must trigger another update.
        observedLength = info.Length;
        observedWrite = info.LastWriteTimeUtc;
        stream.Position = offset;
        using var line = new MemoryStream();
        var buffer = new byte[64 * 1024];
        long position = offset;
        try
        {
            int count;
            while ((count = stream.Read(buffer)) > 0)
            {
                BytesRead += count;
                token.ThrowIfCancellationRequested();
                var start = 0;
                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] != (byte)'\n') continue;
                    line.Write(buffer, start, i - start);
                    var text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r');
                    if (position == 0) text = text.TrimStart('\uFEFF');
                    parseLine(State, text);
                    position += line.Length + 1;
                    offset = position;
                    line.SetLength(0);
                    start = i + 1;
                }
                line.Write(buffer, start, count - start);
            }
        }
        catch
        {
            // Retry uncommitted bytes after a failed/cancelled read.
            observedWrite = default;
            throw;
        }
        finally
        {
            var length = (int)Math.Min(64, offset);
            boundary = new byte[length];
            stream.Position = offset - length;
            var read = stream.Read(boundary);
            BytesRead += read;
            if (read != length) { initialized = false; }
        }
    }
}
