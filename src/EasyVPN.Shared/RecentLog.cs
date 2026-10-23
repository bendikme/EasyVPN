namespace EasyVPN;

/// <summary>
/// The last lines worth showing someone looking at the app. Everything also goes to the service
/// log; this is just the part a person needs to see why a connection did what it did.
/// </summary>
public sealed class RecentLog(Action<string>? forward = null, int capacity = 60)
{
    private readonly Queue<LogLine> _lines = new();

    public void Add(string text)
    {
        lock (_lines)
        {
            _lines.Enqueue(new LogLine(DateTimeOffset.Now, text));
            while (_lines.Count > capacity) _lines.Dequeue();
        }
        forward?.Invoke(text);
    }

    public LogLine[] Snapshot()
    {
        lock (_lines) return [.. _lines];
    }
}
