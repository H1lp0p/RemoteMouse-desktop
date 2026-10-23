using System.Text;
using System.Text.Json;
using RemoteMouse.Core.Input;
using RemoteMouse.Core.Pairing;
using RemoteMouse.Core.Protocol;
using RemoteMouse.Core.SystemControl;
using Xunit;

namespace RemoteMouse.Core.Tests;

public class JsonV1WireCodecTests
{
    private readonly JsonV1WireCodec _codec = new();

    [Fact]
    public void MouseMove_TrustsDeltas_AndDropsDebugTimestamp()
    {
        var action = Decode("""{"type":"mouse_move","dx":7,"dy":-3,"timestamp":1700000000000,"prev_rtt_ms":14}""");
        var move = Assert.IsType<ClientAction.MouseMove>(action);
        Assert.Equal(7, move.Dx);
        Assert.Equal(-3, move.Dy);
    }

    [Fact]
    public void UnknownType_IsUnrecognized()
    {
        var action = Decode("""{"type":"future_gesture","amount":1}""");
        var unknown = Assert.IsType<ClientAction.Unrecognized>(action);
        Assert.Equal("future_gesture", unknown.Type);
    }

    [Fact]
    public void InvalidJson_Throws()
    {
        Assert.Throws<WireFormatException>(() => Decode("{"));
    }

    [Fact]
    public void KeySequence_ReadsVkList()
    {
        var action = Decode("""{"type":"key_sequence","keys":[17,67]}""");
        var keys = Assert.IsType<ClientAction.KeySequence>(action);
        Assert.Equal(new[] { 17, 67 }, keys.Keys);
    }

    [Fact]
    public void TextInput_IsTheSameActionAsTextSet()
    {
        var action = Decode("""{"type":"text_input","text":"Hello"}""");
        var text = Assert.IsType<ClientAction.TextSet>(action);
        Assert.Equal("Hello", text.Text);
    }

    [Fact]
    public void Encode_VolumeAndSave_UseContractNames()
    {
        var volume = Encoding.UTF8.GetString(_codec.Encode(new ServerEvent.VolumeUpdate(50, false)));
        Assert.Contains("\"type\":\"volume_update\"", volume);
        Assert.Contains("\"level\":50", volume);
        Assert.Contains("\"muted\":false", volume);
        Assert.DoesNotContain("mouse_move_ack", volume);

        var save = Encoding.UTF8.GetString(_codec.Encode(new ServerEvent.SaveSuccess("pc-1", "My desktop")));
        Assert.Contains("\"type\":\"save-success\"", save);
        Assert.Contains("\"device_id\":\"pc-1\"", save);
        Assert.Contains("\"device_name\":\"My desktop\"", save);
    }

    [Fact]
    public void NumbersAsStrings_StillDecode()
    {
        var action = Decode("""{"type":"mouse_scroll","delta_x":"2","delta_y":"-120"}""");
        var scroll = Assert.IsType<ClientAction.MouseScroll>(action);
        Assert.Equal(2, scroll.DeltaX);
        Assert.Equal(-120, scroll.DeltaY);
    }

    private ClientAction Decode(string json) => _codec.Decode(Encoding.UTF8.GetBytes(json));
}

public class ActionInterpreterTests
{
    [Fact]
    public async Task MouseMove_IsNotScaled()
    {
        var mouse = new RecordingMouse();
        var session = new FakeSession { Mouse = mouse };
        var interpreter = new ActionInterpreter().Register(new MouseMoveHandler());
        await interpreter.DispatchAsync(session, new ClientAction.MouseMove(7, -4), CancellationToken.None);
        Assert.Equal((7, -4), mouse.Moves.Single());
        Assert.Empty(session.Published);
    }

    [Fact]
    public async Task UnknownAction_DoesNotThrow()
    {
        var session = new FakeSession();
        var interpreter = ActionInterpreter.CreateDefault();
        await interpreter.DispatchAsync(session, new ClientAction.Unrecognized("future_gesture"), CancellationToken.None);
        Assert.Empty(session.Published);
    }

    [Fact]
    public void NewerCodec_WinsWhenRegisteredFirst()
    {
        var registry = new WireCodecRegistry(new JsonV1WireCodec());
        registry.RegisterBeforeFallback(new Proto2Codec());

        var modern = registry.TryResolve(true, Encoding.UTF8.GetBytes("""{"proto":2,"type":"mouse_move"}"""));
        var current = registry.TryResolve(true, Encoding.UTF8.GetBytes("""{"type":"mouse_move","dx":1,"dy":2}"""));

        Assert.Equal("json-v2", modern!.Id);
        Assert.Equal(JsonV1WireCodec.CodecId, current!.Id);
        var move = Assert.IsType<ClientAction.MouseMove>(modern.Decode(Encoding.UTF8.GetBytes("""{"proto":2}""")));
        Assert.Equal(9, move.Dx);
    }
}

public class PairingServiceTests
{
    [Fact]
    public void NewPairing_AcceptsTheCurrentCode()
    {
        var pairing = new PairingService { IsEnabled = true, VerificationCode = "482731" };
        using var doc = JsonDocument.Parse(NewBody("482731"));
        var decision = pairing.Evaluate("10.0.0.5", doc.RootElement, "pc-1", _ => false);
        var ok = Assert.IsType<PairDecision.Success>(decision);
        Assert.Equal("phone-1", ok.Peer.ClientId);
    }

    [Fact]
    public void WrongCode_ThenRateLimit_BlocksEvenTheRightCode()
    {
        long now = 1_000_000;
        var pairing = new PairingService(() => now) { IsEnabled = true, VerificationCode = "482731" };
        for (var i = 0; i < 5; i++)
        {
            using var doc = JsonDocument.Parse(NewBody("000000"));
            var fail = pairing.Evaluate("10.0.0.5", doc.RootElement, "pc-1", _ => false);
            Assert.Equal("invalid_code", Assert.IsType<PairDecision.Failure>(fail).Error);
        }

        using var good = JsonDocument.Parse(NewBody("482731"));
        var blocked = Assert.IsType<PairDecision.Failure>(
            pairing.Evaluate("10.0.0.5", good.RootElement, "pc-1", _ => false));
        Assert.Equal(429, blocked.StatusCode);
        Assert.Equal("rate_limited", blocked.Error);
    }

    [Fact]
    public void ClosedPairing_RejectsNew_AndAllowsSaved()
    {
        var pairing = new PairingService { IsEnabled = false, VerificationCode = "482731" };
        using var fresh = JsonDocument.Parse(NewBody("482731"));
        var closed = Assert.IsType<PairDecision.Failure>(
            pairing.Evaluate("10.0.0.5", fresh.RootElement, "pc-1", _ => true));
        Assert.Equal("pairing_disabled", closed.Error);

        using var savedDoc = JsonDocument.Parse(SavedBody());
        var saved = pairing.Evaluate("10.0.0.5", savedDoc.RootElement, "pc-1", id => id == "phone-1");
        Assert.IsType<PairDecision.Success>(saved);
    }

    [Fact]
    public void UnknownSavedClient_IsNotTrusted()
    {
        var pairing = new PairingService { IsEnabled = true };
        using var doc = JsonDocument.Parse(SavedBody());
        var fail = Assert.IsType<PairDecision.Failure>(
            pairing.Evaluate("10.0.0.5", doc.RootElement, "pc-1", _ => false));
        Assert.Equal("device_not_trusted", fail.Error);
    }

    [Fact]
    public void Format_SplitsSixDigits()
    {
        Assert.Equal("123 - 456", VerificationCode.Format("123456"));
    }

    private static string NewBody(string code) =>
        "{\"pairing_mode\":\"new\",\"device_id\":\"pc-1\",\"client_id\":\"phone-1\",\"client_name\":\"Your phone\",\"verification_code\":\""
        + code
        + "\",\"fingerprint\":{\"android_id\":\"abc\",\"model\":\"S\",\"manufacturer\":\"samsung\",\"os_version\":\"14\"}}";

    private static string SavedBody() => """
        {"pairing_mode":"saved","device_id":"pc-1","client_id":"phone-1","client_name":"Your phone","fingerprint":{"android_id":"abc"}}
        """;
}

public class VolumeControllerTests
{
    [Fact]
    public void SetLevel_ClearsMute_StepDoesNot()
    {
        var volume = new MemoryVolumeController();
        volume.SetMute(true);
        var stepped = volume.Step(-5);
        Assert.True(stepped.Muted);
        Assert.Equal(45, stepped.Level);
        var set = volume.SetLevel(12);
        Assert.False(set.Muted);
        Assert.Equal(12, set.Level);
        Assert.Equal(0, volume.SetLevel(-10).Level);
        Assert.Equal(100, volume.SetLevel(140).Level);
    }
}

public class CursorInterpolatorTests
{
    [Fact]
    public void Delta_IsSpreadAndStillLandsOnTheSamePoint()
    {
        var path = new CursorInterpolator();
        path.Add(16, -8, 0);
        var steps = new List<(int X, int Y)>();
        for (var t = 4d; t <= 16; t += 4)
            steps.Add(path.Advance(t));
        var rest = path.Flush();

        Assert.Equal(16, steps.Sum(step => step.X) + rest.X);
        Assert.Equal(-8, steps.Sum(step => step.Y) + rest.Y);
        Assert.Contains(steps, step => step.X is > 0 and < 16);
        Assert.All(steps, step => Assert.True(Math.Abs(step.X) < 16));
    }

    [Fact]
    public void Flush_EmitsWhateverIsLeft()
    {
        var path = new CursorInterpolator();
        path.Add(5, 1, 0);
        var first = path.Advance(4);
        var rest = path.Flush();
        Assert.Equal(5, first.X + rest.X);
        Assert.Equal(1, first.Y + rest.Y);
        Assert.False(path.IsActive);
    }

    [Fact]
    public void LateTick_DoesNotDumpTheWholeQueue()
    {
        var path = new CursorInterpolator();
        path.Add(40, -20, 0);
        var jumped = path.Advance(30);
        Assert.NotEqual(0, jumped.X);
        Assert.True(Math.Abs(jumped.X) < 20);
        Assert.True(Math.Abs(jumped.Y) < 10);

        var sumX = jumped.X;
        var sumY = jumped.Y;
        for (var t = 31d; t < 400; t++)
        {
            var step = path.Advance(t);
            sumX += step.X;
            sumY += step.Y;
        }
        var rest = path.Flush();
        Assert.Equal(40, sumX + rest.X);
        Assert.Equal(-20, sumY + rest.Y);
    }

    [Fact]
    public void SteadyPackets_StayBelowAFullBatch()
    {
        var path = new CursorInterpolator();
        var sum = 0;
        var max = 0;
        for (var packet = 0; packet < 8; packet++)
        {
            path.Add(24, 0, packet * 16d);
            for (var t = 1; t <= 16; t++)
            {
                var step = path.Advance(packet * 16d + t);
                sum += step.X;
                if (Math.Abs(step.X) > max) max = Math.Abs(step.X);
            }
        }
        sum += path.Flush().X;
        Assert.Equal(24 * 8, sum);
        Assert.True(max <= 6, $"largest step was {max}px");
    }
}

public class SendInputLayoutTests
{
    [Fact]
    public void InputStruct_MatchesWin32Size()
    {
        var size = System.Runtime.InteropServices.Marshal.SizeOf<RemoteMouse.Core.Input.NativeInput.INPUT>();
        Assert.Equal(Environment.Is64BitProcess ? 40 : 28, size);
    }
}

sealed class Proto2Codec : IWireCodec
{
    public string Id => "json-v2";
    public bool CanDecode(bool isText, ReadOnlySpan<byte> payload)
        => isText && Encoding.UTF8.GetString(payload).Contains("\"proto\":2");
    public ClientAction Decode(ReadOnlySpan<byte> payload) => new ClientAction.MouseMove(9, 8);
    public byte[] Encode(ServerEvent evt) => Encoding.UTF8.GetBytes("""{"type":"v2"}""");
}

sealed class RecordingMouse : IMouseController
{
    public List<(int Dx, int Dy)> Moves { get; } = new();
    public List<(string Button, string Action)> Clicks { get; } = new();
    public List<(int X, int Y)> Scrolls { get; } = new();
    public void Move(int dx, int dy) => Moves.Add((dx, dy));
    public void Button(string button, string action) => Clicks.Add((button, action));
    public void Scroll(int deltaX, int deltaY) => Scrolls.Add((deltaX, deltaY));
}

sealed class FakeSession : ISessionContext
{
    public List<(ServerEvent Event, Audience Audience)> Published { get; } = new();
    public string DeviceId => "pc-1";
    public string DeviceName => "My desktop";
    public bool MediaSubscribed { get; set; }
    public bool TextSubscribed { get; set; }
    public IMouseController Mouse { get; set; } = new RecordingMouse();
    public IKeyboardController Keyboard { get; set; } = new RecordingKeyboard();
    public IVolumeController Volume { get; set; } = new MemoryVolumeController();
    public IMediaController Media { get; set; } = new MemoryMediaController();
    public ITextController Text { get; set; } = new MemoryTextController();
    public int ProtocolVersion { get; set; } = 1;
    public bool PointerSynced { get; private set; }
    public Task SyncPointerAsync(uint inputTime, Action command, CancellationToken cancellationToken)
    {
        PointerSynced = true;
        command();
        return Task.CompletedTask;
    }
    public void Trust(string? clientName) { }
    public void Log(string message) { }
    public Task PublishAsync(ServerEvent evt, Audience audience, CancellationToken cancellationToken)
    {
        Published.Add((evt, audience));
        return Task.CompletedTask;
    }
}

sealed class RecordingKeyboard : IKeyboardController
{
    public void Chord(IReadOnlyList<int> virtualKeys) { }
}
