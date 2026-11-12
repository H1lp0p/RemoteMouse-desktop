using System.Windows;
using RemoteMouse.App.Hosting;
using RemoteMouse.App.Desktop;
using RemoteMouse.App.Ui;
using RemoteMouse.Core.Input;
using RemoteMouse.Core.SystemControl;
using Application = System.Windows.Application;
using WinForms = System.Windows.Forms;

namespace RemoteMouse.App;

public partial class App : Application
{
    private Mutex? _mutex;
    private WinForms.NotifyIcon? _tray;
    private FlyoutWindow? _flyout;
    private RemoteMouseServer? _server;
    private SystemVolumeController? _volume;
    private InterpolatingMouse? _mouse;
    private SmtcMediaController? _media;
    private FocusedTextController? _text;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, @"Local\RemoteMouse.Desktop", out var created);
        if (!created)
        {
            Shutdown();
            return;
        }

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteMouse");
        var log = new AppLog(Path.Combine(dir, "server.log"));
        var store = new Core.State.StateStore(Path.Combine(dir, "state.json"));
        var pairing = new Core.Pairing.PairingService();
        var codecs = new Core.Protocol.WireCodecRegistry(new Core.Protocol.JsonV1WireCodec());
        var interpreter = Core.Protocol.ActionInterpreter.CreateDefault();
        _volume = new SystemVolumeController(log.Write);
        _mouse = new InterpolatingMouse();
        _media = new SmtcMediaController(log.Write);
        _text = new FocusedTextController(log.Write);
        _server = new RemoteMouseServer(
            store,
            pairing,
            codecs,
            interpreter,
            _mouse,
            new SendInputKeyboard(),
            _volume,
            _media,
            _text,
            log);

        var model = new FlyoutViewModel(_server);
        _flyout = new FlyoutWindow(model);
        _server.Changed += (_, _) => Dispatcher.BeginInvoke(() => model.Apply(_server.Snapshot()));
        model.Apply(_server.Snapshot());

        _tray = new WinForms.NotifyIcon
        {
            Icon = TrayIcon.Create(),
            Visible = true,
            Text = "Remote Mouse"
        };
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowFlyout());
        var startup = new WinForms.ToolStripMenuItem("Start with Windows")
        {
            Checked = WindowsStartup.IsEnabled()
        };
        startup.Click += (_, _) =>
        {
            WindowsStartup.Set(!WindowsStartup.IsEnabled());
            startup.Checked = WindowsStartup.IsEnabled();
        };
        menu.Items.Add(startup);
        menu.Items.Add("Exit", null, (_, _) => Shutdown());
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == WinForms.MouseButtons.Left)
                ToggleFlyout();
        };

        DispatcherUnhandledException += (_, args) =>
        {
            log.Write(args.Exception.ToString());
            args.Handled = true;
        };

        await _server.StartAsync();
        if (_server.StartError is { } error)
            _tray.ShowBalloonTip(4000, "Remote Mouse", error, WinForms.ToolTipIcon.Warning);

        ShowFlyout();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Drop the SMTC subscription before anything awaits. async OnExit returns at the
        // first await, and the process then exits with the browser session still attached.
        _media?.Dispose();
        _text?.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        if (_server is not null)
        {
            try
            {
                Task.Run(() => _server.DisposeAsync().AsTask()).GetAwaiter().GetResult();
            }
            catch
            {
                // Shutdown already won the race with a socket close.
            }
        }
        _mouse?.Dispose();
        _volume?.Dispose();
        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
            _mutex.Dispose();
        }
        base.OnExit(e);
    }

    private void ToggleFlyout()
    {
        if (_flyout is null) return;
        if (_flyout.IsVisible)
        {
            _flyout.Hide();
            return;
        }
        if (DateTime.UtcNow - _flyout.HiddenAt < TimeSpan.FromMilliseconds(250))
            return;
        ShowFlyout();
    }

    private void ShowFlyout()
    {
        if (_flyout is null || _server is null) return;
        _flyout.ShowAnchored();
    }

}
