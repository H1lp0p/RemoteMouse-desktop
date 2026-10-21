using System.Text.Json;

namespace RemoteMouse.Core.State;

public sealed class TrustedClient
{
    public required string ClientId { get; init; }
    public required string ClientName { get; set; }
    public Dictionary<string, string?> Fingerprint { get; set; } = new();
}

public sealed class StateStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<TrustedClient> _trusted = new();

    public string DeviceId { get; private set; } = "";
    public string DeviceName { get; private set; } = "My desktop";

    public StateStore(string path)
    {
        _path = path;
        Load();
    }

    public IReadOnlyList<TrustedClient> Trusted
    {
        get
        {
            lock (_gate) return _trusted.Select(Clone).ToList();
        }
    }

    public bool IsTrusted(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return false;
        lock (_gate) return _trusted.Any(c => c.ClientId == clientId);
    }

    public void Rename(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return;
        lock (_gate)
        {
            DeviceName = trimmed;
            Save();
        }
    }

    public void Upsert(string clientId, string clientName, IReadOnlyDictionary<string, string?> fingerprint)
    {
        lock (_gate)
        {
            var existing = _trusted.FirstOrDefault(c => c.ClientId == clientId);
            if (existing is null)
            {
                _trusted.Add(new TrustedClient
                {
                    ClientId = clientId,
                    ClientName = clientName,
                    Fingerprint = CopyFingerprint(fingerprint)
                });
            }
            else
            {
                existing.ClientName = clientName;
                existing.Fingerprint = CopyFingerprint(fingerprint);
            }
            Save();
        }
    }

    public bool Forget(string clientId)
    {
        lock (_gate)
        {
            var removed = _trusted.RemoveAll(c => c.ClientId == clientId) > 0;
            if (removed) Save();
            return removed;
        }
    }

    private void Load()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (!File.Exists(_path))
        {
            DeviceId = Guid.NewGuid().ToString();
            DeviceName = "My desktop";
            Save();
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            var root = doc.RootElement;
            DeviceId = root.TryGetProperty("deviceId", out var id) && id.GetString() is { Length: > 0 } stored
                ? stored
                : Guid.NewGuid().ToString();
            DeviceName = root.TryGetProperty("deviceName", out var name) && name.GetString() is { Length: > 0 } storedName
                ? storedName
                : "My desktop";
            if (root.TryGetProperty("trusted", out var trusted) && trusted.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in trusted.EnumerateArray())
                {
                    var clientId = item.TryGetProperty("clientId", out var cid) ? cid.GetString() : null;
                    if (string.IsNullOrWhiteSpace(clientId)) continue;
                    var clientName = item.TryGetProperty("clientName", out var cn) ? cn.GetString() : null;
                    var fingerprint = new Dictionary<string, string?>();
                    if (item.TryGetProperty("fingerprint", out var fp) && fp.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in fp.EnumerateObject())
                            fingerprint[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.ToString();
                    }
                    _trusted.Add(new TrustedClient
                    {
                        ClientId = clientId,
                        ClientName = string.IsNullOrWhiteSpace(clientName) ? "Android" : clientName,
                        Fingerprint = fingerprint
                    });
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            DeviceId = Guid.NewGuid().ToString();
            DeviceName = "My desktop";
            Save();
        }
    }

    private void Save()
    {
        var payload = new
        {
            version = 1,
            deviceId = DeviceId,
            deviceName = DeviceName,
            trusted = _trusted.Select(c => new
            {
                clientId = c.ClientId,
                clientName = c.ClientName,
                fingerprint = c.Fingerprint
            })
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }

    private static TrustedClient Clone(TrustedClient client) => new()
    {
        ClientId = client.ClientId,
        ClientName = client.ClientName,
        Fingerprint = CopyFingerprint(client.Fingerprint)
    };

    private static Dictionary<string, string?> CopyFingerprint(IReadOnlyDictionary<string, string?> fingerprint)
        => fingerprint.ToDictionary(pair => pair.Key, pair => pair.Value);
}
