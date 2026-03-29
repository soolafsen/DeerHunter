using DeerHunter.Configuration;
using DeerHunter.Models;
using Microsoft.Extensions.Options;

namespace DeerHunter.Services;

public sealed class EventStore
{
    private readonly int _capacity;
    private readonly LinkedList<SupervisorEvent> _events = new();
    private readonly Dictionary<string, ManagedProcessSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private long _droppedEvents;
    private long _nextSequence;

    public EventStore(IOptions<DeerHunterOptions> options)
    {
        _capacity = options.Value.EventBufferSize;
    }

    public SupervisorEvent Record(string eventType, string? processName, string source, string message, IReadOnlyDictionary<string, string?>? details = null)
    {
        lock (_gate)
        {
            var eventDetails = details is null
                ? new Dictionary<string, string?>(StringComparer.Ordinal)
                : new Dictionary<string, string?>(details, StringComparer.Ordinal);

            var next = new SupervisorEvent(
                Sequence: ++_nextSequence,
                TimestampUtc: DateTimeOffset.UtcNow,
                EventType: eventType,
                ProcessName: processName,
                Source: source,
                Message: message,
                Details: eventDetails);

            _events.AddLast(next);
            while (_events.Count > _capacity)
            {
                _events.RemoveFirst();
                _droppedEvents++;
            }

            eventDetails["retentionCapacity"] = _capacity.ToString();
            eventDetails["retainedEvents"] = _events.Count.ToString();
            eventDetails["droppedEvents"] = _droppedEvents.ToString();

            return next;
        }
    }

    public void UpsertSnapshot(ManagedProcessSnapshot snapshot)
    {
        lock (_gate)
        {
            _snapshots[snapshot.Name] = snapshot;
        }
    }

    public ManagedProcessSnapshot? GetSnapshot(string name)
    {
        lock (_gate)
        {
            return _snapshots.TryGetValue(name, out var snapshot) ? snapshot : null;
        }
    }

    public IReadOnlyList<ManagedProcessSnapshot> GetSnapshots()
    {
        lock (_gate)
        {
            return _snapshots.Values.OrderBy(static snapshot => snapshot.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public IReadOnlyList<SupervisorEvent> GetRecent(int take)
    {
        lock (_gate)
        {
            return _events.Reverse().Take(take).Reverse().ToArray();
        }
    }
}
