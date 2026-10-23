using RemoteMouse.Core.SystemControl;

namespace RemoteMouse.Core.Protocol;

public sealed class HeartbeatHandler : ActionHandler<ClientAction.Heartbeat>
{
    protected override Task HandleAsync(ISessionContext context, ClientAction.Heartbeat action, CancellationToken cancellationToken)
        => context.PublishAsync(new ServerEvent.HeartbeatAck(action.Timestamp), Audience.Self, cancellationToken);
}

public sealed class SaveHandler : ActionHandler<ClientAction.Save>
{
    protected override Task HandleAsync(ISessionContext context, ClientAction.Save action, CancellationToken cancellationToken)
    {
        context.Trust(action.ClientName);
        return context.PublishAsync(
            new ServerEvent.SaveSuccess(context.DeviceId, context.DeviceName),
            Audience.Self,
            cancellationToken);
    }
}

public sealed class MouseMoveHandler : ActionHandler<ClientAction.MouseMove>
{
    protected override Task HandleAsync(ISessionContext context, ClientAction.MouseMove action, CancellationToken cancellationToken)
    {
        // v2 motion is the UDP playhead. v1 still applies the JSON deltas here.
        if (context.ProtocolVersion >= 2)
            return Task.CompletedTask;
        // dx/dy are already pixels. Sensitivity lives on the phone.
        context.Mouse.Move(action.Dx, action.Dy);
        return Task.CompletedTask;
    }
}

public sealed class MouseClickHandler : ActionHandler<ClientAction.MouseClick>
{
    protected override Task HandleAsync(ISessionContext context, ClientAction.MouseClick action, CancellationToken cancellationToken)
    {
        void Click() => context.Mouse.Button(action.Button, action.ActionName);
        if (action.InputTime is uint inputTime && context.ProtocolVersion >= 2)
            return context.SyncPointerAsync(inputTime, Click, cancellationToken);
        Click();
        return Task.CompletedTask;
    }
}

public sealed class MouseScrollHandler : ActionHandler<ClientAction.MouseScroll>
{
    protected override Task HandleAsync(ISessionContext context, ClientAction.MouseScroll action, CancellationToken cancellationToken)
    {
        // Scroll stays on WebSocket so a notch is not lost, but it must not wait on the
        // mouse playhead. A gesture is many messages; a 30 ms fence on each one stacks.
        context.Mouse.Scroll(action.DeltaX, action.DeltaY);
        return Task.CompletedTask;
    }
}

public sealed class KeySequenceHandler : ActionHandler<ClientAction.KeySequence>
{
    protected override Task HandleAsync(ISessionContext context, ClientAction.KeySequence action, CancellationToken cancellationToken)
    {
        context.Keyboard.Chord(action.Keys);
        return Task.CompletedTask;
    }
}

public sealed class TextSubscribeHandler : ActionHandler<ClientAction.TextSubscribe>
{
    protected override Task HandleAsync(ISessionContext context, ClientAction.TextSubscribe action, CancellationToken cancellationToken)
    {
        context.TextSubscribed = true;
        return context.PublishAsync(new ServerEvent.TextUpdate(context.Text.Get()), Audience.Self, cancellationToken);
    }
}

public sealed class TextUnsubscribeHandler : ActionHandler<ClientAction.TextUnsubscribe>
{
    protected override Task HandleAsync(ISessionContext context, ClientAction.TextUnsubscribe action, CancellationToken cancellationToken)
    {
        context.TextSubscribed = false;
        return Task.CompletedTask;
    }
}

public sealed class TextSetHandler : ActionHandler<ClientAction.TextSet>
{
    protected override Task HandleAsync(ISessionContext context, ClientAction.TextSet action, CancellationToken cancellationToken)
    {
        var text = context.Text.Set(action.Text);
        return context.PublishAsync(new ServerEvent.TextUpdate(text), Audience.TextSubscribers, cancellationToken);
    }
}

public sealed class VolumeSetHandler : ActionHandler<ClientAction.VolumeSet>
{
    protected override Task HandleAsync(ISessionContext context, ClientAction.VolumeSet action, CancellationToken cancellationToken)
    {
        // v2 slider is a UDP absolute level. Mute and volume_step stay on this socket.
        if (context.ProtocolVersion >= 2)
            return Task.CompletedTask;
        var state = context.Volume.SetLevel(action.Level);
        return Publish(context, state, cancellationToken);
    }

    internal static Task Publish(ISessionContext context, VolumeState state, CancellationToken cancellationToken)
        => context.PublishAsync(new ServerEvent.VolumeUpdate(state.Level, state.Muted), Audience.Everyone, cancellationToken);
}

public sealed class VolumeStepHandler : ActionHandler<ClientAction.VolumeStep>
{
    protected override Task HandleAsync(ISessionContext context, ClientAction.VolumeStep action, CancellationToken cancellationToken)
    {
        var delta = string.Equals(action.Direction, "up", StringComparison.OrdinalIgnoreCase) ? 5 : -5;
        return VolumeSetHandler.Publish(context, context.Volume.Step(delta), cancellationToken);
    }
}

public sealed class VolumeMuteHandler : ActionHandler<ClientAction.VolumeMute>
{
    protected override Task HandleAsync(ISessionContext context, ClientAction.VolumeMute action, CancellationToken cancellationToken)
        => VolumeSetHandler.Publish(context, context.Volume.SetMute(action.Mute), cancellationToken);
}

public sealed class MediaSubscribeHandler : ActionHandler<ClientAction.MediaSubscribe>
{
    protected override Task HandleAsync(ISessionContext context, ClientAction.MediaSubscribe action, CancellationToken cancellationToken)
    {
        context.MediaSubscribed = true;
        return context.PublishAsync(MediaEvents.From(context.Media.Get()), Audience.Self, cancellationToken);
    }
}

public sealed class MediaUnsubscribeHandler : ActionHandler<ClientAction.MediaUnsubscribe>
{
    protected override Task HandleAsync(ISessionContext context, ClientAction.MediaUnsubscribe action, CancellationToken cancellationToken)
    {
        context.MediaSubscribed = false;
        return Task.CompletedTask;
    }
}

public sealed class MediaPlayPauseHandler : ActionHandler<ClientAction.MediaPlayPause>
{
    protected override Task HandleAsync(ISessionContext context, ClientAction.MediaPlayPause action, CancellationToken cancellationToken)
        => MediaEvents.Publish(context, context.Media.PlayPause(), cancellationToken);
}

public sealed class MediaNextHandler : ActionHandler<ClientAction.MediaNext>
{
    protected override Task HandleAsync(ISessionContext context, ClientAction.MediaNext action, CancellationToken cancellationToken)
        => MediaEvents.Publish(context, context.Media.Next(), cancellationToken);
}

public sealed class MediaPrevHandler : ActionHandler<ClientAction.MediaPrev>
{
    protected override Task HandleAsync(ISessionContext context, ClientAction.MediaPrev action, CancellationToken cancellationToken)
        => MediaEvents.Publish(context, context.Media.Previous(), cancellationToken);
}

public sealed class MediaSeekHandler : ActionHandler<ClientAction.MediaSeek>
{
    protected override Task HandleAsync(ISessionContext context, ClientAction.MediaSeek action, CancellationToken cancellationToken)
        => MediaEvents.Publish(context, context.Media.Seek(action.Seconds), cancellationToken);
}

static class MediaEvents
{
    public static ServerEvent.MediaUpdate From(MediaSnapshot snapshot) => new(
        snapshot.Title,
        snapshot.Artist,
        snapshot.PositionSeconds,
        snapshot.DurationSeconds,
        snapshot.IsPlaying,
        snapshot.CanSeek);

    public static Task Publish(ISessionContext context, MediaSnapshot snapshot, CancellationToken cancellationToken)
        => context.PublishAsync(From(snapshot), Audience.MediaSubscribers, cancellationToken);
}
