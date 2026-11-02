namespace Overlay.Core;

public enum ArenaPresenceChange { None, NotRunning, AlreadyRunning, Started, Closed, Restarted }

/// <summary>Reports lifecycle transitions once, independently of draft-log availability.</summary>
public sealed class ArenaPresence
{
    private HashSet<int>? previous;
    public ArenaPresenceChange Observe(IEnumerable<int> processIds)
    {
        var current = processIds.ToHashSet();
        var change = previous is null ? (current.Count == 0 ? ArenaPresenceChange.NotRunning : ArenaPresenceChange.AlreadyRunning)
            : current.SetEquals(previous) ? ArenaPresenceChange.None
            : current.Count == 0 ? ArenaPresenceChange.Closed
            : previous.Count == 0 ? ArenaPresenceChange.Started
            : !current.Overlaps(previous) ? ArenaPresenceChange.Restarted
            : ArenaPresenceChange.None;
        previous = current;
        return change;
    }
}
