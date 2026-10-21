namespace RemoteMouse.Core.Protocol;

public interface IActionHandler
{
    Type ActionType { get; }
    Task HandleAsync(ISessionContext context, ClientAction action, CancellationToken cancellationToken);
}

public abstract class ActionHandler<TAction> : IActionHandler where TAction : ClientAction
{
    public Type ActionType => typeof(TAction);

    public Task HandleAsync(ISessionContext context, ClientAction action, CancellationToken cancellationToken)
        => HandleAsync(context, (TAction)action, cancellationToken);

    protected abstract Task HandleAsync(ISessionContext context, TAction action, CancellationToken cancellationToken);
}

/// <summary>
/// Routes a decoded <see cref="ClientAction"/> to a handler.
/// Unknown actions are ignored. Adding a command means a new action type plus a handler,
/// not a change to the socket loop.
/// </summary>
public sealed class ActionInterpreter
{
    private readonly Dictionary<Type, IActionHandler> _handlers = new();

    public ActionInterpreter Register(IActionHandler handler)
    {
        _handlers[handler.ActionType] = handler;
        return this;
    }

    public Task DispatchAsync(ISessionContext context, ClientAction action, CancellationToken cancellationToken)
    {
        if (action is ClientAction.Unrecognized unknown)
        {
            context.Log($"Ignoring unknown action '{unknown.Type ?? "<missing>"}'");
            return Task.CompletedTask;
        }

        if (!_handlers.TryGetValue(action.GetType(), out var handler))
        {
            context.Log($"No handler for {action.GetType().Name}");
            return Task.CompletedTask;
        }

        return handler.HandleAsync(context, action, cancellationToken);
    }

    public static ActionInterpreter CreateDefault()
    {
        return new ActionInterpreter()
            .Register(new HeartbeatHandler())
            .Register(new SaveHandler())
            .Register(new MouseMoveHandler())
            .Register(new MouseClickHandler())
            .Register(new MouseScrollHandler())
            .Register(new KeySequenceHandler())
            .Register(new TextSubscribeHandler())
            .Register(new TextUnsubscribeHandler())
            .Register(new TextSetHandler())
            .Register(new VolumeSetHandler())
            .Register(new VolumeStepHandler())
            .Register(new VolumeMuteHandler())
            .Register(new MediaSubscribeHandler())
            .Register(new MediaUnsubscribeHandler())
            .Register(new MediaPlayPauseHandler())
            .Register(new MediaNextHandler())
            .Register(new MediaPrevHandler())
            .Register(new MediaSeekHandler());
    }
}
