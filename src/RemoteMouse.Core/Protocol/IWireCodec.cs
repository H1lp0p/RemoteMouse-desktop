namespace RemoteMouse.Core.Protocol;

/// <summary>
/// One wire dialect. Register specific dialects before the JSON v1 fallback:
/// the registry returns the first codec whose <see cref="CanDecode"/> is true.
/// </summary>
public interface IWireCodec
{
    string Id { get; }

    bool CanDecode(bool isText, ReadOnlySpan<byte> payload);

    /// <exception cref="WireFormatException">The frame belongs to this dialect but is malformed.</exception>
    ClientAction Decode(ReadOnlySpan<byte> payload);

    byte[] Encode(ServerEvent evt);
}

public sealed class WireCodecRegistry
{
    private readonly List<IWireCodec> _codecs = new();

    public IWireCodec OutboundDefault { get; set; }

    public WireCodecRegistry(IWireCodec outboundDefault)
    {
        OutboundDefault = outboundDefault;
        _codecs.Add(outboundDefault);
    }

    /// <summary>Insert ahead of the current fallback so a newer dialect wins when it recognises the frame.</summary>
    public void RegisterBeforeFallback(IWireCodec codec) => _codecs.Insert(0, codec);

    public IWireCodec? TryResolve(bool isText, ReadOnlySpan<byte> payload)
    {
        foreach (var codec in _codecs)
        {
            if (codec.CanDecode(isText, payload))
                return codec;
        }
        return null;
    }
}
