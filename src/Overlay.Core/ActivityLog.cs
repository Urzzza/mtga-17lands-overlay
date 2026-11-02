namespace Overlay.Core;

public sealed record ActivityEntry(long Sequence, DateTimeOffset Time, string Message);
public sealed record ActivitySnapshot(long Version, ActivityEntry[] Entries);

/// <summary>Bounded, session-only activity summaries; never records raw Arena payloads.</summary>
public sealed class ActivityLog(int capacity = 500, TimeProvider? timeProvider = null)
{
    private readonly object gate = new();
    private readonly Queue<ActivityEntry> entries = new();
    private readonly Dictionary<string, DateTimeOffset> progressTimes = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly int limit = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private long version;
    public void Write(string message)
    {
        lock (gate) Add(message);
    }
    public void Progress(string channel, string message)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            if (progressTimes.TryGetValue(channel, out var last) && now - last < TimeSpan.FromSeconds(2)) return;
            progressTimes[channel] = now; Add(message);
        }
    }
    private void Add(string message)
    {
        string clean = new(message.Take(1000).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        entries.Enqueue(new(++version, clock.GetUtcNow(), clean));
        while (entries.Count > limit) entries.Dequeue();
    }
    public ActivitySnapshot Snapshot() { lock (gate) return new(version, entries.ToArray()); }
    public void Clear() { lock (gate) { entries.Clear(); progressTimes.Clear(); version++; } }
}
