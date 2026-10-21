using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using RemoteMouse.Core.Input;

namespace RemoteMouse.App.Desktop;

/// <summary>
/// YouTube in Chrome/Edge ignores SMTC seek. The page itself seeks 10 seconds on J and L
/// when the player, not a text field, has focus.
/// </summary>
static class YoutubeKeySeek
{
    public static bool Try(int seconds, out string detail)
    {
        detail = "no foreground window";
        if (seconds == 0) return false;
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        var title = WindowTitle(hwnd);
        if (title.Contains("YouTube", StringComparison.OrdinalIgnoreCase) == false)
        {
            detail = "foreground is not a YouTube tab";
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        string process;
        try
        {
            process = Process.GetProcessById((int)pid).ProcessName;
        }
        catch
        {
            detail = "browser process is gone";
            return false;
        }
        if (!IsBrowser(process))
        {
            detail = "foreground process is " + process;
            return false;
        }
        if (FocusIsTextEntry())
        {
            detail = "a text field is focused";
            return false;
        }

        var steps = Math.Clamp((Math.Abs(seconds) + 5) / 10, 1, 30);
        var vk = seconds > 0 ? 0x4C : 0x4A;
        var keys = new SendInputKeyboard();
        for (var i = 0; i < steps; i++)
        {
            keys.Chord(new[] { vk });
            if (i + 1 < steps) Thread.Sleep(70);
        }
        detail = steps + (seconds > 0 ? "×+10s" : "×-10s");
        return true;
    }

    private static bool IsBrowser(string process)
        => process.Equals("chrome", StringComparison.OrdinalIgnoreCase)
           || process.Equals("msedge", StringComparison.OrdinalIgnoreCase)
           || process.Equals("firefox", StringComparison.OrdinalIgnoreCase)
           || process.Equals("brave", StringComparison.OrdinalIgnoreCase)
           || process.Equals("opera", StringComparison.OrdinalIgnoreCase)
           || process.Equals("vivaldi", StringComparison.OrdinalIgnoreCase)
           || process.Equals("chromium", StringComparison.OrdinalIgnoreCase);

    private static bool FocusIsTextEntry()
    {
        try
        {
            var element = AutomationElement.FocusedElement;
            if (element is null) return false;
            if (element.Current.ControlType == ControlType.Edit) return true;
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var raw)
                && raw is ValuePattern value
                && !value.Current.IsReadOnly)
                return true;
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static string WindowTitle(IntPtr hwnd)
    {
        var buffer = new StringBuilder(1024);
        _ = GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
