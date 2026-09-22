using System.Threading.Channels;

namespace RemoteDebugger.Core;

/// <summary>Coalesce pending pointer positions without reordering clicks or keys.</summary>
public sealed class RemoteInputQueue<T>(int capacity = 128)
{
    private readonly object sync = new();
    private readonly LinkedList<(string Kind, T Value)> pending = new();
    private readonly Channel<bool> ready = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });

    public bool TryWrite(string kind, T value)
    {
        lock (sync)
        {
            if (kind == "move" && pending.Last is { Value.Kind: "move" } last)
                last.Value = (kind, value);
            else
            {
                if (pending.Count >= capacity) return false;
                pending.AddLast((kind, value));
            }
            ready.Writer.TryWrite(true);
            return true;
        }
    }

    // A release goes through the same dispatcher as input already in flight.
    // Dropping pending input prevents a delayed key-down after focus is lost.
    public void Reset(T release)
    {
        lock (sync)
        {
            pending.Clear();
            pending.AddLast(("release", release));
            ready.Writer.TryWrite(true);
        }
    }

    public async IAsyncEnumerable<T> ReadAllAsync()
    {
        await foreach (bool _ in ready.Reader.ReadAllAsync())
        {
            T value;
            lock (sync)
            {
                if (pending.First is not { } first) continue;
                value = first.Value.Value;
                pending.RemoveFirst();
                if (pending.Count > 0) ready.Writer.TryWrite(true);
            }
            yield return value;
        }
    }

    public void Complete() => ready.Writer.TryComplete();

    public IReadOnlyList<T> TakePending(int maximum, Func<T, bool> eligible)
    {
        var values = new List<T>();
        lock (sync)
            while (values.Count < Math.Clamp(maximum, 0, 15) && pending.First is { } first &&
                first.Value.Kind is not ("release" or "secureAttention") && eligible(first.Value.Value))
            { values.Add(first.Value.Value); pending.RemoveFirst(); }
        return values;
    }
}

/// <summary>User preference is independent from temporary transport availability.</summary>
public sealed class RemoteInputState
{
    public bool Enabled { get; set; } = true;
    public bool Suspended { get; private set; }
    public void Suspend() => Suspended = true;
    public void Released() => Suspended = false;
    public bool CanSend(bool connected, bool fresh, bool focused) => Enabled && !Suspended && connected && fresh && focused;
}
