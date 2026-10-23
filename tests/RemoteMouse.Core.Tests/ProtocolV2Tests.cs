using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using RemoteMouse.Core.Input;
using RemoteMouse.Core.Pairing;
using RemoteMouse.Core.Protocol;
using RemoteMouse.Core.SystemControl;
using Xunit;

namespace RemoteMouse.Core.Tests;

public class V2UdpFrameTests
{
    [Fact]
    public void LittleEndianLayouts_ParseMouseVolumeAndSeek()
    {
        var mouse = new byte[]
        {
            0x01,
            0x34, 0x12,
            0xE8, 0x03, 0x00, 0x00,
            0x05, 0x00,
            0xFE, 0xFF
        };
        var move = Assert.IsType<V2UdpFrame.MouseMove>(Parse(mouse));
        Assert.Equal((ushort)0x1234, move.SessionId);
        Assert.Equal(1000u, move.Timestamp);
        Assert.Equal((short)5, move.Dx);
        Assert.Equal((short)-2, move.Dy);

        var volume = new byte[V2UdpFrame.VolumeLength];
        volume[0] = V2UdpFrame.VolumeOpcode;
        BinaryPrimitives.WriteUInt16LittleEndian(volume.AsSpan(1), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(volume.AsSpan(3), 0xFFFFFFF0);
        volume[7] = 40;
        var level = Assert.IsType<V2UdpFrame.Volume>(Parse(volume));
        Assert.Equal((ushort)7, level.SessionId);
        Assert.Equal(0xFFFFFFF0u, level.Timestamp);
        Assert.Equal((byte)40, level.Level);

        var seek = new byte[V2UdpFrame.SeekLength];
        seek[0] = V2UdpFrame.SeekOpcode;
        BinaryPrimitives.WriteUInt16LittleEndian(seek.AsSpan(1), 9);
        BinaryPrimitives.WriteUInt32LittleEndian(seek.AsSpan(3), 50);
        BinaryPrimitives.WriteUInt16LittleEndian(seek.AsSpan(7), 90);
        var position = Assert.IsType<V2UdpFrame.Seek>(Parse(seek));
        Assert.Equal((ushort)90, position.PositionSeconds);
    }

    [Fact]
    public void WrongLengthOrUnknownOpcode_IsDropped()
    {
        byte[][] payloads =
        [
            [0x01, 0, 0, 0, 0, 0, 0, 0, 0],
            [0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            [0x04, 0, 0, 0, 0, 0, 0],
            [0x02, 0, 0, 0, 0, 0]
        ];
        foreach (var payload in payloads)
        {
            Assert.False(V2UdpFrame.TryParse(payload, out var frame));
            Assert.Null(frame);
        }
    }

    [Fact]
    public void Datagram_MatchesOnlyTheLiveV2Socket()
    {
        Assert.True(V2Session.AcceptsDatagram(4, "10.0.0.5", 4, "10.0.0.5", 2));
        Assert.False(V2Session.AcceptsDatagram(4, "10.0.0.6", 4, "10.0.0.5", 2));
        Assert.False(V2Session.AcceptsDatagram(4, "10.0.0.5", 4, "10.0.0.5", 1));
        Assert.False(V2Session.AcceptsDatagram(4, "10.0.0.5", 0, "10.0.0.5", 2));
        Assert.False(V2Session.AcceptsDatagram(0, "10.0.0.5", 0, "10.0.0.5", 2));
        Assert.False(V2Session.AcceptsDatagram(5, "10.0.0.5", 4, "10.0.0.5", 2));
    }

    private static V2UdpFrame Parse(byte[] payload)
    {
        Assert.True(V2UdpFrame.TryParse(payload, out var frame));
        return frame!;
    }
}

public class PhoneTimeTests
{
    [Fact]
    public void Order_UsesSignedDelta_AcrossTheUint32Wrap()
    {
        uint before = 0xFFFF_FFF0;
        uint after = 0x0000_0010;
        Assert.Equal(32, PhoneTime.Delta(after, before));
        Assert.Equal(-32, PhoneTime.Delta(before, after));
        Assert.True(PhoneTime.IsNewer(after, before));
        Assert.False(PhoneTime.IsNewer(before, after));
        Assert.False(PhoneTime.IsNewer(before, before));
        Assert.Equal(before - 8, PhoneTime.Add(before, -8));
    }
}

public class MouseTimelineTests
{
    [Fact]
    public void Playhead_HoldsTheNominalDelay_AndDropsSamplesBehindIt()
    {
        Assert.Equal(8, MouseTimeline.BaseDelayMs);
        Assert.Equal(20, MouseTimeline.MaxDelayMs);
        var timeline = new MouseTimeline();
        var played = new List<int>();
        timeline.AddSample(1000, 4, 0, 0, (dx, _) => played.Add(dx));
        timeline.Pump(7, (dx, _) => played.Add(dx));
        Assert.Empty(played);
        timeline.Pump(8, (dx, _) => played.Add(dx));
        Assert.Equal(new[] { 4 }, played);
        timeline.AddSample(999, 9, 0, 8, (dx, _) => played.Add(dx));
        timeline.Pump(40, (dx, _) => played.Add(dx));
        Assert.Equal(new[] { 4 }, played);
    }

    [Fact]
    public void ReorderedSample_AheadOfThePlayhead_IsInserted()
    {
        var timeline = new MouseTimeline();
        var played = new List<int>();
        timeline.AddSample(1000, 2, 0, 0, (dx, _) => played.Add(dx));
        timeline.AddSample(1012, 5, 0, 2, (dx, _) => played.Add(dx));
        timeline.AddSample(1006, 3, 0, 3, (dx, _) => played.Add(dx));
        Assert.Empty(played);
        timeline.Pump(20, (dx, _) => played.Add(dx));
        Assert.Equal(new[] { 2, 3, 5 }, played);
    }

    [Fact]
    public void WrappedTimestamps_PlayInSignedOrder()
    {
        var timeline = new MouseTimeline();
        var played = new List<int>();
        timeline.AddSample(0xFFFF_FFFC, 1, 0, 0, (dx, _) => played.Add(dx));
        timeline.AddSample(0x0000_000A, 2, 0, 2, (dx, _) => played.Add(dx));
        timeline.AddSample(0x0000_0002, 3, 0, 3, (dx, _) => played.Add(dx));
        timeline.Pump(22, (dx, _) => played.Add(dx));
        Assert.Equal(new[] { 1, 3, 2 }, played);
    }

    [Fact]
    public async Task ClickBarrier_FlushesUpToInputTime_ThenRuns()
    {
        var timeline = new MouseTimeline();
        long now = 0;
        var log = new List<string>();
        var waits = 0;
        timeline.AddSample(1000, 4, -1, now, (dx, _) => log.Add("m" + dx));
        timeline.AddSample(1010, 7, 0, now, (dx, _) => log.Add("m" + dx));
        await timeline.AwaitClickAsync(
            1005,
            () => now,
            (dx, _) => log.Add("m" + dx),
            () => log.Add("click"),
            CancellationToken.None,
            (_, _) =>
            {
                waits++;
                return Task.CompletedTask;
            });
        Assert.Equal(0, waits);
        Assert.Equal(new[] { "m4", "click" }, log);
        timeline.AddSample(1004, 9, 0, now, (dx, _) => log.Add("m" + dx));
        timeline.Pump(now + 5, (dx, _) => log.Add("m" + dx));
        Assert.Equal(new[] { "m4", "click", "m7" }, log);
    }

    [Fact]
    public async Task ClickBarrier_TimesOutAt30ms_AndDropsLateSamples()
    {
        Assert.Equal(30, MouseTimeline.BarrierTimeoutMs);
        var timeline = new MouseTimeline();
        long now = 0;
        var log = new List<string>();
        timeline.AddSample(100, 4, 0, now, (dx, _) => log.Add("m" + dx));
        await timeline.AwaitClickAsync(
            500,
            () => now,
            (dx, _) => log.Add("m" + dx),
            () => log.Add("click"),
            CancellationToken.None,
            (milliseconds, _) =>
            {
                now += milliseconds;
                return Task.CompletedTask;
            });
        Assert.Equal(30, now);
        Assert.Equal(new[] { "m4", "click" }, log);
        timeline.AddSample(200, 9, 0, now, (dx, _) => log.Add("m" + dx));
        timeline.AddSample(501, 3, 0, now, (dx, _) => log.Add("m" + dx));
        timeline.Pump(now + 1, (dx, _) => log.Add("m" + dx));
        Assert.Equal(new[] { "m4", "click", "m3" }, log);
    }
}

public class TimestampGateTests
{
    [Fact]
    public void VolumeAndSeek_KeepIndependentNewestTimestamps_IncludingWrap()
    {
        var volume = new TimestampGate();
        var seek = new TimestampGate();
        Assert.True(volume.TryAccept(0xFFFF_FFF0, out var first));
        Assert.False(volume.TryAccept(0xFFFF_FFF0, out _));
        Assert.False(volume.TryAccept(0xFFFF_FFE0, out _));
        Assert.True(volume.TryAccept(0x0000_0010, out var wrapped));
        Assert.NotEqual(first, wrapped);
        Assert.True(volume.IsGeneration(wrapped));
        Assert.False(volume.IsGeneration(first));

        Assert.True(seek.TryAccept(40, out _));
        Assert.True(seek.TryAccept(41, out _));
        Assert.False(volume.TryAccept(1, out _));
        Assert.True(volume.TryAccept(0x20, out _));
    }
}

public class ProtocolV2HandlerTests
{
    [Fact]
    public async Task MissingInputTime_RunsImmediately()
    {
        var mouse = new RecordingMouse();
        var v2 = new FakeSession { Mouse = mouse, ProtocolVersion = 2 };
        var started = Environment.TickCount64;
        await new MouseClickHandler().HandleAsync(
            v2,
            new ClientAction.MouseClick("left", "click"),
            CancellationToken.None);
        await new MouseScrollHandler().HandleAsync(
            v2,
            new ClientAction.MouseScroll(0, -120),
            CancellationToken.None);
        Assert.True(Environment.TickCount64 - started < 25);
        Assert.False(v2.PointerSynced);
        Assert.Equal(("left", "click"), mouse.Clicks.Single());
        Assert.Equal((0, -120), mouse.Scrolls.Single());

        var v1 = new FakeSession { Mouse = mouse, ProtocolVersion = 1 };
        await new MouseClickHandler().HandleAsync(
            v1,
            new ClientAction.MouseClick("right", "down", 50),
            CancellationToken.None);
        Assert.False(v1.PointerSynced);
        Assert.Equal(("right", "down"), mouse.Clicks.Last());
    }

    [Fact]
    public async Task V2WebSocket_SkipsMouseMoveAndVolumeSet_KeepsDiscreteCommands()
    {
        var mouse = new RecordingMouse();
        var volume = new MemoryVolumeController();
        var session = new FakeSession { Mouse = mouse, Volume = volume, ProtocolVersion = 2 };
        var interpreter = ActionInterpreter.CreateDefault();
        await interpreter.DispatchAsync(session, new ClientAction.MouseMove(4, 5), CancellationToken.None);
        await interpreter.DispatchAsync(session, new ClientAction.VolumeSet(80), CancellationToken.None);
        Assert.Empty(mouse.Moves);
        Assert.Equal(50, volume.Get().Level);
        Assert.Empty(session.Published);

        await interpreter.DispatchAsync(session, new ClientAction.VolumeStep("up"), CancellationToken.None);
        await interpreter.DispatchAsync(session, new ClientAction.VolumeMute(true), CancellationToken.None);
        await interpreter.DispatchAsync(session, new ClientAction.MediaSeek(-10), CancellationToken.None);
        Assert.Equal(55, volume.Get().Level);
        Assert.True(volume.Get().Muted);
        Assert.Contains(session.Published, item => item.Event is ServerEvent.MediaUpdate);
    }

    [Fact]
    public void JsonClickAndScroll_ReadOptionalInputTime()
    {
        var codec = new JsonV1WireCodec();
        var click = Assert.IsType<ClientAction.MouseClick>(codec.Decode(Encoding.UTF8.GetBytes(
            """{"type":"mouse_click","button":"left","action":"down","input_time":4000000000}""")));
        Assert.Equal(4000000000u, click.InputTime);
        var scroll = Assert.IsType<ClientAction.MouseScroll>(codec.Decode(Encoding.UTF8.GetBytes(
            """{"type":"mouse_scroll","delta_x":0,"delta_y":-120}""")));
        Assert.Null(scroll.InputTime);
        Assert.Equal(-120, scroll.DeltaY);
    }
}

public class ProtocolV2PairingTests
{
    [Fact]
    public void UnsupportedProtocol_DoesNotConsumeThePairingLimit()
    {
        long now = 1_000_000;
        var pairing = new PairingService(() => now) { IsEnabled = true, VerificationCode = "482731" };
        for (var i = 0; i < 6; i++)
        {
            using var doc = JsonDocument.Parse(Body("000000", 9));
            var fail = Assert.IsType<PairDecision.Failure>(pairing.Evaluate("10.0.0.5", doc.RootElement, "pc-1", _ => false));
            Assert.Equal(400, fail.StatusCode);
            Assert.Equal("unsupported_protocol", fail.Error);
            Assert.Null(fail.RetryAfterSeconds);
        }

        using var bad = JsonDocument.Parse(Body("000000", 2));
        var invalid = Assert.IsType<PairDecision.Failure>(pairing.Evaluate("10.0.0.5", bad.RootElement, "pc-1", _ => false));
        Assert.Equal("invalid_code", invalid.Error);

        using var good = JsonDocument.Parse(Body("482731", null));
        var ok = Assert.IsType<PairDecision.Success>(pairing.Evaluate("10.0.0.5", good.RootElement, "pc-1", _ => false));
        Assert.Equal(1, ok.Peer.ProtocolVersion);
        Assert.Equal((ushort)0, ok.Peer.SessionId);
    }

    [Fact]
    public void V2Pair_IssuesANonZeroSessionId_ForNewAndSaved()
    {
        var pairing = new PairingService { IsEnabled = true, VerificationCode = "482731" };
        using var fresh = JsonDocument.Parse(Body("482731", 2));
        var created = Assert.IsType<PairDecision.Success>(pairing.Evaluate("10.0.0.5", fresh.RootElement, "pc-1", _ => false));
        using var saved = JsonDocument.Parse(SavedBody());
        var restored = Assert.IsType<PairDecision.Success>(pairing.Evaluate("10.0.0.6", saved.RootElement, "pc-1", id => id == "phone-1"));

        Assert.Equal(2, created.Peer.ProtocolVersion);
        Assert.Equal(2, restored.Peer.ProtocolVersion);
        Assert.NotEqual((ushort)0, created.Peer.SessionId);
        Assert.NotEqual((ushort)0, restored.Peer.SessionId);
        Assert.NotEqual(created.Peer.SessionId, restored.Peer.SessionId);
        Assert.Equal(created.Peer.SessionId, pairing.LastPaired("10.0.0.5")!.SessionId);
        Assert.Equal(restored.Peer.SessionId, pairing.LastPaired("10.0.0.6")!.SessionId);
    }

    private static string Body(string code, int? protocolVersion)
    {
        var version = protocolVersion is int value ? $",\"protocol_version\":{value}" : "";
        return "{\"pairing_mode\":\"new\",\"device_id\":\"pc-1\",\"client_id\":\"phone-1\",\"client_name\":\"Your phone\",\"verification_code\":\""
            + code + "\"" + version
            + ",\"fingerprint\":{\"android_id\":\"abc\",\"model\":\"S\",\"manufacturer\":\"samsung\",\"os_version\":\"14\"}}";
    }

    private static string SavedBody() => """
        {"pairing_mode":"saved","device_id":"pc-1","client_id":"phone-1","client_name":"Your phone","protocol_version":2,"fingerprint":{"android_id":"abc"}}
        """;
}
