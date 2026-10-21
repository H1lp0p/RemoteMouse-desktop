using System.Text.Json;

namespace RemoteMouse.Core.Protocol;

/// <summary>
/// Current JSON text dialect (websocket-messages.md). Extra fields are ignored.
/// Numbers may arrive as strings. This codec does not emit mouse_move_ack.
/// </summary>
public sealed class JsonV1WireCodec : IWireCodec
{
    public const string CodecId = "json-v1";

    public string Id => CodecId;

    public bool CanDecode(bool isText, ReadOnlySpan<byte> payload) => isText;

    public ClientAction Decode(ReadOnlySpan<byte> payload)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(payload.ToArray());
        }
        catch (JsonException)
        {
            throw new WireFormatException("Invalid JSON");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new WireFormatException("Invalid JSON");

            var root = doc.RootElement;
            var type = ReadString(root, "type");
            return type switch
            {
                "heartbeat" => new ClientAction.Heartbeat(ReadLong(root, "timestamp")),
                "save" => new ClientAction.Save(ReadString(root, "client_name")),
                "mouse_move" => new ClientAction.MouseMove(ReadInt(root, "dx"), ReadInt(root, "dy")),
                "mouse_click" => new ClientAction.MouseClick(
                    ReadString(root, "button") ?? "left",
                    ReadString(root, "action") ?? "click"),
                "mouse_scroll" => new ClientAction.MouseScroll(ReadInt(root, "delta_x"), ReadInt(root, "delta_y")),
                "key_sequence" => new ClientAction.KeySequence(ReadIntList(root, "keys")),
                "text_subscribe" => new ClientAction.TextSubscribe(),
                "text_unsubscribe" => new ClientAction.TextUnsubscribe(),
                "text_set" or "text_input" => new ClientAction.TextSet(ReadString(root, "text") ?? ""),
                "volume_set" => new ClientAction.VolumeSet(ReadInt(root, "level")),
                "volume_step" => new ClientAction.VolumeStep(ReadString(root, "direction") ?? "down"),
                "volume_mute" => new ClientAction.VolumeMute(ReadBool(root, "mute")),
                "media_subscribe" => new ClientAction.MediaSubscribe(),
                "media_unsubscribe" => new ClientAction.MediaUnsubscribe(),
                "media_play_pause" => new ClientAction.MediaPlayPause(),
                "media_next" => new ClientAction.MediaNext(),
                "media_prev" => new ClientAction.MediaPrev(),
                "media_seek" => new ClientAction.MediaSeek(ReadInt(root, "seconds")),
                _ => new ClientAction.Unrecognized(type)
            };
        }
    }

    public byte[] Encode(ServerEvent evt)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            switch (evt)
            {
                case ServerEvent.HeartbeatAck ack:
                    writer.WriteString("type", "heartbeat_ack");
                    WriteNullableLong(writer, "timestamp", ack.Timestamp);
                    break;
                case ServerEvent.SaveSuccess save:
                    writer.WriteString("type", "save-success");
                    writer.WriteString("device_id", save.DeviceId);
                    writer.WriteString("device_name", save.DeviceName);
                    break;
                case ServerEvent.VolumeUpdate volume:
                    writer.WriteString("type", "volume_update");
                    writer.WriteNumber("level", volume.Level);
                    writer.WriteBoolean("muted", volume.Muted);
                    break;
                case ServerEvent.MediaUpdate media:
                    writer.WriteString("type", "media_update");
                    WriteNullableString(writer, "title", media.Title);
                    WriteNullableString(writer, "artist", media.Artist);
                    writer.WriteNumber("position_seconds", media.PositionSeconds);
                    writer.WriteNumber("duration_seconds", media.DurationSeconds);
                    writer.WriteBoolean("is_playing", media.IsPlaying);
                    writer.WriteBoolean("can_seek", media.CanSeek);
                    break;
                case ServerEvent.TextUpdate text:
                    writer.WriteString("type", "text_update");
                    writer.WriteString("text", text.Text);
                    break;
                case ServerEvent.ContextChange context:
                    writer.WriteString("type", "context_change");
                    writer.WriteString("context", context.Context);
                    break;
                case ServerEvent.Error error:
                    writer.WriteString("type", "error");
                    writer.WriteNumber("code", error.Code);
                    writer.WriteString("message", error.Message);
                    break;
                default:
                    throw new InvalidOperationException($"JSON v1 cannot encode {evt.GetType().Name}");
            }
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static string? ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static int ReadInt(JsonElement root, string name)
    {
        var n = ReadLong(root, name);
        return n is null ? 0 : (int)Math.Clamp(n.Value, int.MinValue, int.MaxValue);
    }

    private static long? ReadLong(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number)
        {
            if (el.TryGetInt64(out var asLong)) return asLong;
            if (el.TryGetDouble(out var asDouble)) return (long)asDouble;
        }
        if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out var parsed))
            return parsed;
        return null;
    }

    private static bool ReadBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el)) return false;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(el.GetString(), out var b) && b,
            JsonValueKind.Number => el.TryGetInt32(out var n) && n != 0,
            _ => false
        };
    }

    private static IReadOnlyList<int> ReadIntList(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
            return Array.Empty<int>();
        var list = new List<int>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var n))
                list.Add(n);
            else if (item.ValueKind == JsonValueKind.String && int.TryParse(item.GetString(), out var parsed))
                list.Add(parsed);
        }
        return list;
    }

    private static void WriteNullableLong(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is long n) writer.WriteNumber(name, n);
        else writer.WriteNull(name);
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }
}
