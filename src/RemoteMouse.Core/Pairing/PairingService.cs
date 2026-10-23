using System.Text.Json;

namespace RemoteMouse.Core.Pairing;

public sealed record PairedPeer(
    string ClientId,
    string ClientName,
    IReadOnlyDictionary<string, string?> Fingerprint,
    int ProtocolVersion = 1,
    ushort SessionId = 0);

public abstract record PairDecision
{
    public sealed record Success(PairedPeer Peer) : PairDecision;
    public sealed record Failure(int StatusCode, string Error, string Message, int? RetryAfterSeconds) : PairDecision;
}

/// <summary>
/// POST /pair rules from the REST spec. Rate limit is 5 failures / 30s per IP + fingerprint.
/// An unsupported protocol_version is not a bad code and does not consume that limit.
/// Missing protocol_version is v1.
/// </summary>
public sealed class PairingService
{
    public const int MaxFailedAttempts = 5;
    public const int WindowMs = 30_000;
    public const int ProtocolV1 = 1;
    public const int ProtocolV2 = 2;

    private readonly object _gate = new();
    private readonly Dictionary<string, List<long>> _failures = new();
    private readonly Dictionary<string, PairedPeer> _pairedByIp = new();
    private readonly Func<long> _clock;
    private ushort _nextSessionId = 1;

    public bool IsEnabled { get; set; }
    public string VerificationCode { get; set; } = "000000";

    public PairingService(Func<long>? clockMs = null)
    {
        _clock = clockMs ?? (() => Environment.TickCount64);
    }

    public PairedPeer? LastPaired(string peerIp)
    {
        lock (_gate)
            return _pairedByIp.TryGetValue(peerIp, out var peer) ? peer : null;
    }

    public PairDecision Evaluate(string peerIp, JsonElement body, string expectedDeviceId, Func<string, bool> isTrusted)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return Fail(400, "bad_request", "Expected a JSON object");

        var mode = ReadString(body, "pairing_mode");
        var deviceId = ReadString(body, "device_id");
        var clientId = ReadString(body, "client_id");
        var clientName = ReadString(body, "client_name");
        if (string.IsNullOrWhiteSpace(clientName)) clientName = "Android";
        var fingerprint = ReadFingerprint(body);
        var rateKey = peerIp + "|" + FingerprintKey(fingerprint);

        lock (_gate)
        {
            if (!TryReadProtocolVersion(body, out var protocolVersion))
                return Fail(400, "unsupported_protocol", "Unsupported protocol_version");

            if (!IsEnabled && mode == "new")
                return Fail(403, "pairing_disabled", "New connections are blocked");

            if (IsRateLimited(rateKey, out var retryAfter))
                return Fail(429, "rate_limited", "Too many failed attempts", retryAfter);

            if (!string.Equals(deviceId, expectedDeviceId, StringComparison.Ordinal))
                return Fail(401, "unknown_device", "device_id mismatch");

            if (mode == "new")
            {
                var code = ReadString(body, "verification_code") ?? "";
                if (!string.Equals(code, VerificationCode, StringComparison.Ordinal))
                {
                    RecordFailure(rateKey);
                    return Fail(401, "invalid_code", "Invalid verification code");
                }

                return new PairDecision.Success(Remember(peerIp, clientId ?? "", clientName, fingerprint, protocolVersion));
            }

            if (mode == "saved")
            {
                if (string.IsNullOrWhiteSpace(clientId) || !isTrusted(clientId))
                    return Fail(401, "device_not_trusted", "This client_id is not in the trusted devices list");

                return new PairDecision.Success(Remember(peerIp, clientId, clientName, fingerprint, protocolVersion));
            }

            return Fail(400, "bad_request", $"Unknown pairing_mode: {mode}");
        }
    }

    private bool IsRateLimited(string key, out int retryAfter)
    {
        var now = _clock();
        var window = _failures.TryGetValue(key, out var stamps)
            ? stamps.Where(t => now - t < WindowMs).ToList()
            : new List<long>();
        _failures[key] = window;
        if (window.Count >= MaxFailedAttempts)
        {
            retryAfter = (int)(WindowMs - (now - window[0])) / 1000;
            if (retryAfter < 1) retryAfter = 1;
            return true;
        }
        retryAfter = 0;
        return false;
    }

    private void RecordFailure(string key)
    {
        if (!_failures.TryGetValue(key, out var stamps))
        {
            stamps = new List<long>();
            _failures[key] = stamps;
        }
        stamps.Add(_clock());
    }

    private PairedPeer Remember(string peerIp, string clientId, string clientName, IReadOnlyDictionary<string, string?> fingerprint, int protocolVersion)
    {
        var sessionId = protocolVersion >= ProtocolV2 ? AllocateSessionId() : (ushort)0;
        var peer = new PairedPeer(clientId, clientName, fingerprint, protocolVersion, sessionId);
        _pairedByIp[peerIp] = peer;
        return peer;
    }

    private ushort AllocateSessionId()
    {
        var id = _nextSessionId;
        _nextSessionId++;
        if (_nextSessionId == 0) _nextSessionId = 1;
        return id;
    }

    private static bool TryReadProtocolVersion(JsonElement body, out int version)
    {
        version = ProtocolV1;
        if (!body.TryGetProperty("protocol_version", out var el) || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return true;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var number))
        {
            version = number;
            return number is ProtocolV1 or ProtocolV2;
        }
        if (el.ValueKind == JsonValueKind.String
            && int.TryParse(el.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            version = parsed;
            return parsed is ProtocolV1 or ProtocolV2;
        }
        return false;
    }

    private static PairDecision Fail(int status, string error, string message, int? retryAfter = null)
        => new PairDecision.Failure(status, error, message, retryAfter);

    private static string FingerprintKey(IReadOnlyDictionary<string, string?> fingerprint)
    {
        static string Part(IReadOnlyDictionary<string, string?> map, string key)
            => map.TryGetValue(key, out var value) ? value ?? "" : "";
        return string.Join("|", Part(fingerprint, "android_id"), Part(fingerprint, "model"), Part(fingerprint, "manufacturer"), Part(fingerprint, "os_version"));
    }

    private static string? ReadString(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
    }

    private static Dictionary<string, string?> ReadFingerprint(JsonElement body)
    {
        var map = new Dictionary<string, string?>();
        if (!body.TryGetProperty("fingerprint", out var fp) || fp.ValueKind != JsonValueKind.Object)
            return map;
        foreach (var prop in fp.EnumerateObject())
        {
            map[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Null => null,
                _ => prop.Value.ToString()
            };
        }
        return map;
    }
}
