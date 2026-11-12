using Microsoft.Win32;

namespace RemoteMouse.App.Ui;

/// <summary>
/// Current-user autostart. The Run key stores the path of this executable.
/// </summary>
static class WindowsStartup
{
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Remote Mouse";

    public static bool IsEnabled()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path)) return false;
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return string.Equals(Unquote(key?.GetValue(ValueName) as string), path, StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        key.SetValue(ValueName, path.Contains(' ') ? $"\"{path}\"" : path);
    }

    static string? Unquote(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            return trimmed[1..^1];
        return trimmed;
    }
}
