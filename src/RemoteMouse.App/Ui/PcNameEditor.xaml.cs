using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RemoteMouse.App.Ui;

public partial class PcNameEditor : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(PcNameEditor),
        new PropertyMetadata(""));

    public static readonly DependencyProperty TextAlignmentProperty = DependencyProperty.Register(
        nameof(TextAlignment),
        typeof(TextAlignment),
        typeof(PcNameEditor),
        new PropertyMetadata(TextAlignment.Left));

    private bool _editing;

    public PcNameEditor()
    {
        InitializeComponent();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public TextAlignment TextAlignment
    {
        get => (TextAlignment)GetValue(TextAlignmentProperty);
        set => SetValue(TextAlignmentProperty, value);
    }

    public event EventHandler<string>? NameCommitted;

    private void OnDisplayClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2) return;
        e.Handled = true;
        _editing = true;
        Editor.Text = Text ?? "";
        Display.Visibility = Visibility.Collapsed;
        Editor.Visibility = Visibility.Visible;
        Editor.Focus();
        Editor.SelectAll();
    }

    private void OnEditorKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Commit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Cancel();
            e.Handled = true;
        }
    }

    private void OnEditorLostFocus(object sender, RoutedEventArgs e)
    {
        if (_editing) Commit();
    }

    private void Commit()
    {
        if (!_editing) return;
        _editing = false;
        Editor.Visibility = Visibility.Collapsed;
        Display.Visibility = Visibility.Visible;
        var name = (Editor.Text ?? "").Trim();
        if (name.Length > 64) name = name[..64];
        if (name.Length > 0 && name != Text)
            NameCommitted?.Invoke(this, name);
    }

    private void Cancel()
    {
        _editing = false;
        Editor.Visibility = Visibility.Collapsed;
        Display.Visibility = Visibility.Visible;
    }
}
