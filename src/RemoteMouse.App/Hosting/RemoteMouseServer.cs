using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RemoteMouse.Core.Input;
using RemoteMouse.Core.Pairing;
using RemoteMouse.Core.Protocol;
using RemoteMouse.Core.State;
using RemoteMouse.Core.SystemControl;

namespace RemoteMouse.App.Hosting;

public sealed record UiDevice(string ClientId, string Name, bool IsConnected, bool IsSaved);

public sealed record UiActive(string SessionId, string Name, bool IsSaved);

public sealed record UiSnapshot(
    string DesktopName,
    bool PairingOpen,
    string CodeFormatted,
    string? QrPayload,
    UiActive? Active,
    IReadOnlyList<UiDevice> Devices);

public sealed class RemoteMouseServer : IAsyncDisposable
{
    public const int Port = 12345;
    public const int RealtimePort = 12346;
    public const int MaxClients = 5;
    public const string Version = "1.0.0";

    private readonly StateStore _store;
    private readonly PairingService _pairing;
    private readonly WireCodecRegistry _codecs;
    private readonly ActionInterpreter _interpreter;
    private readonly IMouseController _mouse;
    private readonly IKeyboardController _keyboard;
    private readonly IVolumeController _volume;
    private readonly IMediaController _media;
    private readonly ITextController _text;
    private readonly AppLog _log;
    private readonly object _gate = new();
    private readonly List<ClientConnection> _sessions = new();
    private readonly string _deviceId;
    private readonly CancellationTokenSource _stopping = new();

    private WebApplication? _app;
    private UdpClient? _udp;
    private UdpClient? _realtime;
    private string? _activeId;
    private string? _qrPayload;
    private readonly DateTime _started = DateTime.UtcNow;

    public RemoteMouseServer(
        StateStore store,
        PairingService pairing,
        WireCodecRegistry codecs,
        ActionInterpreter interpreter,
        IMouseController mouse,
        IKeyboardController keyboard,
        IVolumeController volume,
        IMediaController media,
        ITextController text,
        AppLog log)
    {
        _store = store;
        _pairing = pairing;
        _codecs = codecs;
        _interpreter = interpreter;
        _mouse = mouse;
        _keyboard = keyboard;
        _volume = volume;
        _media = media;
        _text = text;
        _log = log;
        _deviceId = store.DeviceId;
        _media.Changed += OnMediaChanged;
        _text.Changed += OnTextChanged;
    }

    public event EventHandler? Changed;

    public string? StartError { get; private set; }

    public async Task StartAsync()
    {
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = Array.Empty<string>(),
                ContentRootPath = AppContext.BaseDirectory,
                ApplicationName = "RemoteMouse"
            });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls($"http://0.0.0.0:{Port}");
            var app = builder.Build();
            app.UseWebSockets();
            app.MapGet("/status", HandleStatus);
            app.MapPost("/pair", HandlePair);
            app.Map("/ws", HandleSocket);
            await app.StartAsync(_stopping.Token);
            _app = app;
            _log.Write($"REST/WS listening on {Port}, device {_deviceId}");
        }
        catch (Exception ex)
        {
            StartError = "Port 12345 is busy. Close the other Remote Mouse or the Python stub.";
            _log.Write(ex.ToString());
            return;
        }

        try
        {
            StartUdp();
            _log.Write($"UDP discovery on {Port}");
        }
        catch (Exception ex)
        {
            _log.Write("UDP discovery failed: " + ex.Message);
        }

        try
        {
            StartRealtimeUdp();
            _log.Write($"UDP realtime on {RealtimePort}");
        }
        catch (Exception ex)
        {
            _log.Write("UDP realtime failed: " + ex.Message);
        }

        _ = Task.Run(PlayheadLoop);
    }

    public void Rename(string name)
    {
        _store.Rename(name);
        lock (_gate)
        {
            if (_pairing.IsEnabled)
                _qrPayload = QrPayload(_pairing.VerificationCode);
        }
        _log.Write("PC renamed to " + _store.DeviceName);
        Notify();
    }

    public void OpenPairing()
    {
        var code = VerificationCode.Create();
        var payload = QrPayload(code);
        lock (_gate)
        {
            _pairing.VerificationCode = code;
            _pairing.IsEnabled = true;
            _qrPayload = payload;
        }
        _log.Write($"Pairing open, code {code}");
        Notify();
    }

    public void ClosePairing()
    {
        lock (_gate)
        {
            _pairing.IsEnabled = false;
            _qrPayload = null;
        }
        _log.Write("Pairing closed");
        Notify();
    }

    public void DisconnectActive()
    {
        ClientConnection? session;
        lock (_gate)
            session = ActiveSession();
        if (session is { Socket.State: WebSocketState.Open })
            _ = session.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "disconnect", CancellationToken.None);
    }

    public void Forget(string clientId)
    {
        if (_store.Forget(clientId))
        {
            _log.Write($"Forgot {clientId}");
            Notify();
        }
    }

    public UiSnapshot Snapshot()
    {
        List<SessionView> sessions;
        string? activeId;
        bool pairing;
        string code;
        string? qr;
        lock (_gate)
        {
            sessions = _sessions.Select(SessionView.From).ToList();
            activeId = _activeId;
            pairing = _pairing.IsEnabled;
            code = _pairing.VerificationCode;
            qr = pairing ? _qrPayload : null;
        }

        var trusted = _store.Trusted;
        var trustedIds = trusted.Select(t => t.ClientId).ToHashSet(StringComparer.Ordinal);
        var rows = new List<UiDevice>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in sessions.OrderByDescending(s => s.ConnectedAt))
        {
            var id = string.IsNullOrWhiteSpace(session.ClientId) ? session.SessionId : session.ClientId;
            if (!seen.Add(id)) continue;
            rows.Add(new UiDevice(id, session.ClientName, true, trustedIds.Contains(session.ClientId ?? "")));
        }
        foreach (var client in trusted.OrderBy(c => c.ClientName, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!seen.Add(client.ClientId)) continue;
            rows.Add(new UiDevice(client.ClientId, client.ClientName, false, true));
        }

        var active = sessions.FirstOrDefault(s => s.SessionId == activeId) ?? sessions.FirstOrDefault();
        UiActive? header = active is null
            ? null
            : new UiActive(active.SessionId, active.ClientName, trustedIds.Contains(active.ClientId ?? ""));

        return new UiSnapshot(
            _store.DeviceName,
            pairing,
            VerificationCode.Format(code),
            qr,
            header,
            rows);
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        List<ClientConnection> sessions;
        lock (_gate) sessions = _sessions.ToList();
        foreach (var session in sessions)
        {
            try
            {
                if (session.Socket.State == WebSocketState.Open)
                    await session.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None);
            }
            catch
            {
                // The socket is going away with the process.
            }
        }
        _udp?.Dispose();
        _realtime?.Dispose();
        if (_app is not null)
            await _app.StopAsync(CancellationToken.None);
    }

    private async Task HandleStatus(HttpContext context)
    {
        int connected;
        bool pairing;
        lock (_gate)
        {
            connected = _sessions.Count;
            pairing = _pairing.IsEnabled;
        }
        var body = new
        {
            version = Version,
            uptime_seconds = (int)(DateTime.UtcNow - _started).TotalSeconds,
            connected_clients = connected,
            max_clients = MaxClients,
            is_pairing_enabled = pairing
        };
        await WriteJson(context, 200, body);
    }

    private async Task HandlePair(HttpContext context)
    {
        var ip = PeerAddress(context.Connection.RemoteIpAddress);
        JsonDocument? doc = null;
        try
        {
            doc = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        }
        catch (JsonException)
        {
            await WriteJson(context, 400, new { error = "bad_request", message = "Invalid JSON" });
            return;
        }

        using (doc)
        {
            var decision = _pairing.Evaluate(ip, doc.RootElement, _deviceId, _store.IsTrusted);
            if (decision is PairDecision.Failure failure)
            {
                _log.Write($"PAIR {ip} {failure.Error}");
                if (failure.RetryAfterSeconds is int retry)
                    context.Response.Headers.RetryAfter = retry.ToString();
                var payload = new Dictionary<string, object?>
                {
                    ["error"] = failure.Error,
                    ["message"] = failure.Message
                };
                if (failure.RetryAfterSeconds is int seconds)
                    payload["retry_after_seconds"] = seconds;
                await WriteJson(context, failure.StatusCode, payload);
                return;
            }

            var success = (PairDecision.Success)decision;
            _log.Write($"PAIR {ip} ok {success.Peer.ClientId} {success.Peer.ClientName} v{success.Peer.ProtocolVersion}");
            if (success.Peer.ProtocolVersion >= PairingService.ProtocolV2)
            {
                await WriteJson(context, 200, new
                {
                    paired = true,
                    server_info = new { name = _store.DeviceName, version = Version },
                    protocol_version = success.Peer.ProtocolVersion,
                    session_id = success.Peer.SessionId,
                    udp_port = RealtimePort
                });
                return;
            }

            await WriteJson(context, 200, new
            {
                paired = true,
                server_info = new { name = _store.DeviceName, version = Version }
            });
        }
    }

    private async Task HandleSocket(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var socket = await context.WebSockets.AcceptWebSocketAsync();
        var ip = PeerAddress(context.Connection.RemoteIpAddress);
        var paired = _pairing.LastPaired(ip);
        var protocolVersion = paired is { ProtocolVersion: >= PairingService.ProtocolV2 } ? paired.ProtocolVersion : 1;
        var session = new ClientConnection(this, socket, ip)
        {
            Codec = _codecs.OutboundDefault,
            ClientId = string.IsNullOrWhiteSpace(paired?.ClientId) ? null : paired!.ClientId,
            ClientName = string.IsNullOrWhiteSpace(paired?.ClientName) ? "Android" : paired!.ClientName,
            Fingerprint = paired?.Fingerprint.ToDictionary(p => p.Key, p => p.Value) ?? new Dictionary<string, string?>(),
            ProtocolVersion = protocolVersion,
            ProtocolSessionId = protocolVersion >= PairingService.ProtocolV2 ? paired!.SessionId : (ushort)0
        };

        var accepted = false;
        lock (_gate)
        {
            if (_sessions.Count < MaxClients)
            {
                _sessions.Add(session);
                _activeId = session.SessionId;
                accepted = true;
            }
        }
        if (!accepted)
        {
            await RejectFull(socket);
            return;
        }
        _log.Write($"WS connect {ip} client={session.ClientId ?? "-"} proto={session.ProtocolVersion}");
        Notify();

        try
        {
            await OnConnectedAsync(session, _stopping.Token);
            await ReceiveLoop(session, _stopping.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        catch (Exception ex)
        {
            _log.Write($"WS error {ip}: {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                _sessions.Remove(session);
                if (_activeId == session.SessionId)
                    _activeId = _sessions.OrderByDescending(s => s.ConnectedAt).FirstOrDefault()?.SessionId;
            }
            _log.Write($"WS disconnect {ip}");
            Notify();
        }
    }

    private async Task OnConnectedAsync(ClientConnection session, CancellationToken cancellationToken)
    {
        // Phones expect a JSON volume_update before they send anything.
        // v2 keeps that push; the slider itself then arrives on UDP.
        var state = _volume.Get();
        await session.PublishAsync(new ServerEvent.VolumeUpdate(state.Level, state.Muted), Audience.Self, cancellationToken);
    }

    private async Task ReceiveLoop(ClientConnection session, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (session.Socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await session.Socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                if (message.Length + result.Count > 256 * 1024)
                {
                    await session.Socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too big", CancellationToken.None);
                    return;
                }
                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var payload = message.ToArray();
            var isText = result.MessageType == WebSocketMessageType.Text;
            ClientAction action;
            try
            {
                action = session.Codec.Decode(payload);
            }
            catch (WireFormatException)
            {
                _log.Write($"Bad frame from {session.PeerIp}");
                await session.PublishAsync(new ServerEvent.Error(1001, "Invalid JSON"), Audience.Self, cancellationToken);
                continue;
            }

            if (action is not ClientAction.MouseMove and not ClientAction.MouseScroll and not ClientAction.Heartbeat)
                _log.Write($"WS {session.PeerIp} {action.GetType().Name}");

            await _interpreter.DispatchAsync(session, action, cancellationToken);
            if (action is ClientAction.Save)
                Notify();
        }
    }

    private async Task RejectFull(WebSocket socket)
    {
        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "max clients", CancellationToken.None);
        }
        catch
        {
            // The client may already be gone.
        }
    }

    private void StartUdp()
    {
        var client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
        client.EnableBroadcast = true;
        _udp = client;
        _ = Task.Run(UdpLoop);
    }

    private async Task UdpLoop()
    {
        var udp = _udp;
        if (udp is null) return;
        while (!_stopping.IsCancellationRequested)
        {
            UdpReceiveResult datagram;
            try
            {
                datagram = await udp.ReceiveAsync(_stopping.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Write("UDP recv: " + ex.Message);
                continue;
            }

            JsonDocument? doc;
            try
            {
                doc = JsonDocument.Parse(datagram.Buffer);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (root.TryGetProperty("type", out var type) && type.GetString() != "discovery") continue;
                var target = root.TryGetProperty("target_device_id", out var targetEl) ? targetEl.GetString() : null;
                if (!string.IsNullOrEmpty(target) && !string.Equals(target, _deviceId, StringComparison.Ordinal))
                    continue;
                var clientId = root.TryGetProperty("client_id", out var clientEl) ? clientEl.GetString() : null;
                var trusted = _store.IsTrusted(clientId);
                var response = JsonSerializer.Serialize(new
                {
                    type = "discovery_response",
                    device_id = _deviceId,
                    device_name = _store.DeviceName,
                    ip = LanIp(),
                    port = Port,
                    is_trusted = trusted,
                    requires_pairing = !trusted
                });
                var bytes = System.Text.Encoding.UTF8.GetBytes(response);
                try
                {
                    await udp.SendAsync(bytes.AsMemory(), datagram.RemoteEndPoint, _stopping.Token);
                }
                catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log.Write("UDP send: " + ex.Message);
                }
            }
        }
    }

    private void StartRealtimeUdp()
    {
        var client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.Client.Bind(new IPEndPoint(IPAddress.Any, RealtimePort));
        _realtime = client;
        _ = Task.Run(RealtimeLoop);
    }

    private async Task RealtimeLoop()
    {
        var udp = _realtime;
        if (udp is null) return;
        while (!_stopping.IsCancellationRequested)
        {
            UdpReceiveResult datagram;
            try
            {
                datagram = await udp.ReceiveAsync(_stopping.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Write("UDP realtime recv: " + ex.Message);
                continue;
            }

            if (!V2UdpFrame.TryParse(datagram.Buffer, out var frame) || frame is null)
                continue;
            var session = FindV2Session(frame.SessionId, PeerAddress(datagram.RemoteEndPoint.Address));
            if (session is null) continue;
            ApplyRealtime(session, frame);
        }
    }

    private async Task PlayheadLoop()
    {
        while (!_stopping.IsCancellationRequested)
        {
            List<ClientConnection> sessions;
            lock (_gate) sessions = _sessions.Where(s => s.ProtocolVersion >= PairingService.ProtocolV2).ToList();
            var now = Environment.TickCount64;
            foreach (var session in sessions)
            {
                try
                {
                    session.Timeline.Pump(now, ApplyImmediateStep);
                }
                catch (Exception ex)
                {
                    _log.Write("playhead: " + ex.Message);
                }
            }

            try
            {
                await Task.Delay(2, _stopping.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private ClientConnection? FindV2Session(ushort sessionId, string ip)
    {
        lock (_gate)
        {
            ClientConnection? found = null;
            foreach (var session in _sessions)
            {
                if (V2Session.AcceptsDatagram(sessionId, ip, session.ProtocolSessionId, session.PeerIp, session.ProtocolVersion))
                    found = session;
            }
            return found;
        }
    }

    private void ApplyRealtime(ClientConnection session, V2UdpFrame frame)
    {
        var now = Environment.TickCount64;
        switch (frame)
        {
            case V2UdpFrame.MouseMove move:
                session.Timeline.AddSample(move.Timestamp, move.Dx, move.Dy, now, ApplyImmediateStep);
                break;
            case V2UdpFrame.Volume volume when session.VolumeGate.TryAccept(volume.Timestamp, out var volumeGeneration):
                ApplyVolumeLater(session, volumeGeneration, Math.Clamp((int)volume.Level, 0, 100));
                break;
            case V2UdpFrame.Seek seek when session.SeekGate.TryAccept(seek.Timestamp, out var seekGeneration):
                ApplySeekLater(session, seekGeneration, seek.PositionSeconds);
                break;
        }
    }

    private void ApplyVolumeLater(ClientConnection session, int generation, int level)
    {
        _ = Task.Run(() =>
        {
            try
            {
                VolumeState state;
                lock (session.VolumeApply)
                {
                    if (!session.VolumeGate.IsGeneration(generation)) return;
                    state = _volume.SetLevel(level);
                    if (!session.VolumeGate.IsGeneration(generation)) return;
                    session.PublishAsync(new ServerEvent.VolumeUpdate(state.Level, state.Muted), Audience.Everyone, CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                _log.Write("udp volume: " + ex.Message);
            }
        });
    }

    private void ApplySeekLater(ClientConnection session, int generation, int positionSeconds)
    {
        _ = Task.Run(() =>
        {
            try
            {
                MediaSnapshot snap;
                lock (session.SeekApply)
                {
                    if (!session.SeekGate.IsGeneration(generation)) return;
                    snap = _media.SeekAbsolute(positionSeconds);
                    if (!session.SeekGate.IsGeneration(generation)) return;
                    session.PublishAsync(MediaEvent(snap), Audience.MediaSubscribers, CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                _log.Write("udp seek: " + ex.Message);
            }
        });
    }

    private void ApplyImmediateStep(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        if (_mouse is InterpolatingMouse smooth)
            smooth.MoveImmediate(dx, dy);
        else
            _mouse.Move(dx, dy);
    }

    private static ServerEvent.MediaUpdate MediaEvent(MediaSnapshot snap) => new(
        snap.Title,
        snap.Artist,
        snap.PositionSeconds,
        snap.DurationSeconds,
        snap.IsPlaying,
        snap.CanSeek);

    private void Trust(ClientConnection session, string? requestedName)
    {
        if (!string.IsNullOrWhiteSpace(requestedName))
            session.ClientName = requestedName.Trim();
        if (string.IsNullOrWhiteSpace(session.ClientId))
            session.ClientId = Guid.NewGuid().ToString();
        _store.Upsert(session.ClientId, session.ClientName, session.Fingerprint);
        _log.Write($"Trusted {session.ClientId} {session.ClientName}");
    }

    private async Task PublishAsync(ClientConnection origin, ServerEvent evt, Audience audience, CancellationToken cancellationToken)
    {
        List<ClientConnection> targets;
        lock (_gate)
        {
            targets = audience switch
            {
                Audience.Self => new List<ClientConnection> { origin },
                Audience.Everyone => _sessions.ToList(),
                Audience.MediaSubscribers => _sessions.Where(s => s.MediaSubscribed || ReferenceEquals(s, origin)).ToList(),
                Audience.TextSubscribers => _sessions.Where(s => s.TextSubscribed || ReferenceEquals(s, origin)).ToList(),
                _ => new List<ClientConnection>()
            };
        }

        foreach (var target in targets)
        {
            byte[] bytes;
            try
            {
                bytes = target.Codec.Encode(evt);
            }
            catch (Exception ex)
            {
                _log.Write("Encode failed: " + ex.Message);
                continue;
            }
            try
            {
                if (target.Socket.State == WebSocketState.Open)
                    await target.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The receive loop drops the socket.
            }
        }
    }

    private ClientConnection? ActiveSession()
        => _sessions.FirstOrDefault(s => s.SessionId == _activeId) ?? _sessions.FirstOrDefault();

    private string QrPayload(string code)
    {
        return JsonSerializer.Serialize(new
        {
            device_id = _deviceId,
            device_name = _store.DeviceName,
            ip = LanIp(),
            port = Port,
            verification_code = code
        });
    }

    private void OnMediaChanged()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var snap = _media.Get();
                await BroadcastAsync(new ServerEvent.MediaUpdate(
                    snap.Title,
                    snap.Artist,
                    snap.PositionSeconds,
                    snap.DurationSeconds,
                    snap.IsPlaying,
                    snap.CanSeek), session => session.MediaSubscribed);
            }
            catch (Exception ex)
            {
                _log.Write("media push: " + ex.Message);
            }
        });
    }

    private void OnTextChanged(string text)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await BroadcastAsync(new ServerEvent.TextUpdate(text), session => session.TextSubscribed);
            }
            catch (Exception ex)
            {
                _log.Write("text push: " + ex.Message);
            }
        });
    }

    private async Task BroadcastAsync(ServerEvent evt, Func<ClientConnection, bool> include)
    {
        List<ClientConnection> targets;
        lock (_gate) targets = _sessions.Where(include).ToList();
        foreach (var target in targets)
        {
            try
            {
                if (target.Socket.State != WebSocketState.Open) continue;
                var bytes = target.Codec.Encode(evt);
                await target.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
            {
                // The receive loop drops the socket.
            }
        }
    }

    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);

    private static async Task WriteJson(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(JsonSerializer.Serialize(body));
    }

    private static string PeerAddress(IPAddress? address)
    {
        if (address is null) return "?";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.ToString();
    }

    private static string LanIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 80);
            return ((IPEndPoint)socket.LocalEndPoint!).Address.ToString();
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    private sealed class SessionView
    {
        public required string SessionId { get; init; }
        public required string? ClientId { get; init; }
        public required string ClientName { get; init; }
        public required DateTime ConnectedAt { get; init; }

        public static SessionView From(ClientConnection session) => new()
        {
            SessionId = session.SessionId,
            ClientId = session.ClientId,
            ClientName = session.ClientName,
            ConnectedAt = session.ConnectedAt
        };
    }

    private sealed class ClientConnection : ISessionContext
    {
        private readonly RemoteMouseServer _server;

        public ClientConnection(RemoteMouseServer server, WebSocket socket, string peerIp)
        {
            _server = server;
            Socket = socket;
            PeerIp = peerIp;
            SessionId = Guid.NewGuid().ToString();
        }

        public WebSocket Socket { get; }
        public string SessionId { get; }
        public string PeerIp { get; }
        public int ProtocolVersion { get; init; } = 1;
        public ushort ProtocolSessionId { get; init; }
        public MouseTimeline Timeline { get; } = new();
        public TimestampGate VolumeGate { get; } = new();
        public TimestampGate SeekGate { get; } = new();
        public object VolumeApply { get; } = new();
        public object SeekApply { get; } = new();
        public string? ClientId { get; set; }
        public string ClientName { get; set; } = "Android";
        public Dictionary<string, string?> Fingerprint { get; set; } = new();
        public IWireCodec Codec { get; set; } = null!;
        public bool MediaSubscribed { get; set; }
        public bool TextSubscribed { get; set; }
        public DateTime ConnectedAt { get; } = DateTime.UtcNow;

        public string DeviceId => _server._deviceId;
        public string DeviceName => _server._store.DeviceName;
        public IMouseController Mouse => _server._mouse;
        public IKeyboardController Keyboard => _server._keyboard;
        public IVolumeController Volume => _server._volume;
        public IMediaController Media => _server._media;
        public ITextController Text => _server._text;

        public Task SyncPointerAsync(uint inputTime, Action command, CancellationToken cancellationToken)
            => Timeline.AwaitClickAsync(inputTime, () => Environment.TickCount64, _server.ApplyImmediateStep, command, cancellationToken);

        public void Trust(string? clientName) => _server.Trust(this, clientName);
        public void Log(string message) => _server._log.Write(message);
        public Task PublishAsync(ServerEvent evt, Audience audience, CancellationToken cancellationToken)
            => _server.PublishAsync(this, evt, audience, cancellationToken);
    }
}

public sealed class AppLog
{
    private readonly string _path;
    private readonly object _gate = new();

    public AppLog(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}";
        lock (_gate)
        {
            try
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > 1024 * 1024)
                    File.Move(_path, _path + ".old", overwrite: true);
                File.AppendAllText(_path, line);
            }
            catch
            {
                // Logging must not take down the tray app.
            }
        }
    }
}
