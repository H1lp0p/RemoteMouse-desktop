namespace RemoteMouse.Core.Protocol;

/// <summary>
/// Newest phone-timestamp wins. Equal or older samples are dropped. Volume and absolute seek each keep their own gate.
/// </summary>
public sealed class TimestampGate
{
    private readonly object _gate = new();
    private bool _set;
    private uint _last;
    private int _generation;

    public bool TryAccept(uint timestamp, out int generation)
    {
        lock (_gate)
        {
            if (_set && !PhoneTime.IsNewer(timestamp, _last))
            {
                generation = 0;
                return false;
            }

            _set = true;
            _last = timestamp;
            generation = ++_generation;
            return true;
        }
    }

    public bool IsGeneration(int generation)
    {
        lock (_gate) return generation != 0 && _generation == generation;
    }
}
