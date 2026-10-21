using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace RemoteMouse.App.Ui;

public partial class FlyoutWindow : Window
{
    private readonly FlyoutViewModel _model;
    private DateTime _shownAt = DateTime.MinValue;
    private DateTime _hiddenAt = DateTime.MinValue;
    private bool _positioned;
    private bool _placing;

    public FlyoutWindow(FlyoutViewModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;
        IdleName.NameCommitted += OnNameCommitted;
        PairingName.NameCommitted += OnNameCommitted;
        SessionName.NameCommitted += OnNameCommitted;
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(FlyoutViewModel.PairingOpen) or nameof(FlyoutViewModel.ShowSession))
                LayoutSessionButtons();
            if (IsVisible)
                Dispatcher.BeginInvoke(FitToContent, DispatcherPriority.Render);
        };
        SourceInitialized += (_, _) => ApplyToolWindow();
    }

    public DateTime HiddenAt => _hiddenAt;

    public void ShowAnchored()
    {
        _positioned = true;
        LayoutSessionButtons();
        _shownAt = DateTime.UtcNow;
        Show();
        FitToContent();
        Activate();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (DateTime.UtcNow - _shownAt < TimeSpan.FromMilliseconds(200)) return;
        HideFlyout();
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            HideFlyout();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_positioned) FitToContent();
    }

    private void OnNameCommitted(object? sender, string name) => _model.Rename(name);

    private void HideFlyout()
    {
        if (!IsVisible) return;
        Hide();
        _hiddenAt = DateTime.UtcNow;
    }

    private void LayoutSessionButtons()
    {
        if (CloseSessionButton is null || DisconnectButton is null) return;
        if (_model.PairingOpen)
        {
            CloseSessionButton.Visibility = Visibility.Visible;
            Grid.SetColumn(DisconnectButton, 2);
            Grid.SetColumnSpan(DisconnectButton, 1);
        }
        else
        {
            CloseSessionButton.Visibility = Visibility.Collapsed;
            Grid.SetColumn(DisconnectButton, 0);
            Grid.SetColumnSpan(DisconnectButton, 3);
        }
    }

    private void FitToContent()
    {
        if (_placing || !_positioned) return;
        _placing = true;
        try
        {
            ContentScroller.MaxHeight = double.PositiveInfinity;
            ContentPanel.InvalidateMeasure();
            var innerWidth = Math.Max(
                1,
                RootCard.Width - RootCard.Padding.Left - RootCard.Padding.Right
                - RootCard.BorderThickness.Left - RootCard.BorderThickness.Right);
            ContentPanel.Measure(new System.Windows.Size(innerWidth, double.PositiveInfinity));
            var contentHeight = ContentPanel.DesiredSize.Height;
            if (contentHeight < 1) return;

            var chrome = RootCard.Margin.Top + RootCard.Margin.Bottom
                + RootCard.Padding.Top + RootCard.Padding.Bottom
                + RootCard.BorderThickness.Top + RootCard.BorderThickness.Bottom;
            var height = contentHeight + chrome;
            var width = RootCard.Width + RootCard.Margin.Left + RootCard.Margin.Right;

            var area = SystemParameters.WorkArea;
            var maxHeight = Math.Max(240, area.Height - 8);
            if (height > maxHeight)
            {
                ContentScroller.MaxHeight = Math.Max(160, maxHeight - chrome);
                height = maxHeight;
            }

            Width = Math.Ceiling(width);
            Height = Math.Ceiling(height);
            Left = area.Right - Width;
            Top = Math.Max(area.Top, area.Bottom - Height);
        }
        finally
        {
            _placing = false;
        }
    }

    private void ApplyToolWindow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(hwnd, GwlExStyle, style | WsExToolWindow);
    }

    const int GwlExStyle = -20;
    const int WsExToolWindow = 0x00000080;

    [DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
