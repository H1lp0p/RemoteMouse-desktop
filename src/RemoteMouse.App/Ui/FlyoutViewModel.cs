using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;
using RemoteMouse.App.Hosting;

namespace RemoteMouse.App.Ui;

public sealed class FlyoutViewModel : INotifyPropertyChanged
{
    private string _desktopName = "My desktop";
    private bool _pairingOpen;
    private string _codeFormatted = "";
    private string? _qrPayload;
    private ImageSource? _qrImage;
    private string _activeName = "";
    private bool _activeSaved;
    private bool _hasActive;

    public FlyoutViewModel(RemoteMouseServer server)
    {
        Server = server;
        OpenConnectionCommand = new RelayCommand(_ => server.OpenPairing());
        CloseConnectionCommand = new RelayCommand(_ => server.ClosePairing());
        DisconnectCommand = new RelayCommand(_ => server.DisconnectActive());
        ForgetCommand = new RelayCommand(p =>
        {
            if (p is string id) server.Forget(id);
        });
    }

    public RemoteMouseServer Server { get; }

    public ObservableCollection<DeviceRow> Devices { get; } = new();

    public string DesktopName
    {
        get => _desktopName;
        private set => Set(ref _desktopName, value);
    }

    public bool PairingOpen
    {
        get => _pairingOpen;
        private set => Set(ref _pairingOpen, value);
    }

    public string CodeFormatted
    {
        get => _codeFormatted;
        private set => Set(ref _codeFormatted, value);
    }

    public ImageSource? QrImage
    {
        get => _qrImage;
        private set => Set(ref _qrImage, value);
    }

    public string ActiveName
    {
        get => _activeName;
        private set => Set(ref _activeName, value);
    }

    public bool ActiveSaved
    {
        get => _activeSaved;
        private set => Set(ref _activeSaved, value);
    }

    public bool HasActive
    {
        get => _hasActive;
        private set
        {
            if (!Set(ref _hasActive, value)) return;
            OnPropertyChanged(nameof(ShowIdle));
            OnPropertyChanged(nameof(ShowPairing));
            OnPropertyChanged(nameof(ShowSession));
        }
    }

    public bool ShowIdle => !HasActive && !PairingOpen;
    public bool ShowPairing => !HasActive && PairingOpen;
    public bool ShowSession => HasActive;
    public bool HasDevices => Devices.Count > 0;

    public ICommand OpenConnectionCommand { get; }
    public ICommand CloseConnectionCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ForgetCommand { get; }

    public void Rename(string name) => Server.Rename(name);

    public void Apply(UiSnapshot snapshot)
    {
        DesktopName = snapshot.DesktopName;
        var pairingChanged = _pairingOpen != snapshot.PairingOpen;
        PairingOpen = snapshot.PairingOpen;
        CodeFormatted = snapshot.CodeFormatted;
        if (snapshot.QrPayload != _qrPayload)
        {
            _qrPayload = snapshot.QrPayload;
            QrImage = snapshot.QrPayload is null ? null : QrImages.Create(snapshot.QrPayload);
        }

        HasActive = snapshot.Active is not null;
        ActiveName = snapshot.Active?.Name ?? "";
        ActiveSaved = snapshot.Active?.IsSaved ?? false;

        Devices.Clear();
        foreach (var device in snapshot.Devices)
        {
            Devices.Add(new DeviceRow
            {
                ClientId = device.ClientId,
                Name = device.Name,
                IsConnected = device.IsConnected
            });
        }

        OnPropertyChanged(nameof(ShowIdle));
        OnPropertyChanged(nameof(ShowPairing));
        OnPropertyChanged(nameof(ShowSession));
        OnPropertyChanged(nameof(HasDevices));
        if (pairingChanged) OnPropertyChanged(nameof(PairingOpen));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class DeviceRow
{
    public required string ClientId { get; init; }
    public required string Name { get; init; }
    public bool IsConnected { get; init; }
    public bool ShowForget => !IsConnected;
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;

    public RelayCommand(Action<object?> execute) => _execute = execute;

    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute(parameter);
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}

public sealed class BoolToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is true ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

static class QrImages
{
    public static ImageSource Create(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        var image = new BitmapImage();
        using var stream = new MemoryStream(png);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
