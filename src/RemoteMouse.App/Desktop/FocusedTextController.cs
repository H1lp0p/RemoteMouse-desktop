using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using RemoteMouse.Core.Input;
using RemoteMouse.Core.SystemControl;

namespace RemoteMouse.App.Desktop;

/// <summary>
/// Syncs the phone text panel with the editable control that currently has focus.
/// </summary>
public sealed class FocusedTextController : ITextController, IDisposable
{
    private const int MaxChars = 100_000;
    private readonly Action<string> _log;
    private readonly SendInputKeyboard _keys = new();
    private readonly object _gate = new();
    private AutomationElement? _watched;
    private string _last = "";
    private string? _suppress;
    private long _quietUntil;
    private int _debounce;
    private int _generation;
    private int _pasteGeneration;
    private string? _savedClipboard;
    private bool _clipboardBorrowed;
    private bool _hooked;

    public FocusedTextController(Action<string> log)
    {
        _log = log;
        try
        {
            Automation.AddAutomationFocusChangedEventHandler(OnFocusChanged);
            _hooked = true;
        }
        catch (Exception ex)
        {
            _log("text focus hook: " + ex.Message);
        }
    }

    public event Action<string>? Changed;

    public string Get()
    {
        lock (_gate) return ReadLocked();
    }

    public string Set(string text)
    {
        text ??= "";
        if (text.Length > MaxChars) text = text[..MaxChars];
        int generation;
        lock (_gate)
        {
            if (Same(ReadLocked(), text))
            {
                _suppress = text;
                _last = text;
                return text;
            }
            generation = ++_generation;
            // The control's accessibility value is often stale for a few hundred milliseconds
            // after a write. Echoing that stale read makes the phone wipe the character it just sent.
            _suppress = text;
            _quietUntil = Environment.TickCount64 + 900;
            _last = text;
        }

        try
        {
            Apply(text);
        }
        catch (Exception ex)
        {
            _log("text write: " + ex.Message);
        }

        ScheduleConfirm(generation);
        return text;
    }

    public void Dispose()
    {
        if (!_hooked) return;
        try
        {
            Automation.RemoveAutomationFocusChangedEventHandler(OnFocusChanged);
            Unwatch();
        }
        catch (Exception ex)
        {
            _log("text unhook: " + ex.Message);
        }
    }

    private string ReadLocked()
    {
        if (ForegroundIsOurs()) return _last;
        var text = ReadFocused();
        _last = text;
        return text;
    }

    private void Apply(string text)
    {
        if (ForegroundIsOurs()) return;
        // Edit/RichEdit applies WM_SETTEXT immediately. UIA SetValue on the same control
        // often reports the previous value and then fights the next write.
        if (TryWin32Set(text)) return;
        var element = FocusedElement();
        if (element is not null && !IsOurElement(element) && TryValueSet(element, text)) return;
        PasteReplacement(text);
    }

    private void OnFocusChanged(object sender, AutomationFocusChangedEventArgs e)
    {
        try
        {
            var element = FocusedElement();
            lock (_gate) Watch(element);
            SchedulePush();
        }
        catch (Exception ex)
        {
            _log("text focus: " + ex.Message);
        }
    }

    private void Watch(AutomationElement? element)
    {
        if (element is not null && _watched is not null && Automation.Compare(element, _watched)) return;
        Unwatch();
        _watched = element;
        if (element is null || IsOurElement(element)) return;
        try
        {
            Automation.AddAutomationPropertyChangedEventHandler(
                element,
                TreeScope.Element,
                OnValueChanged,
                ValuePattern.ValueProperty);
        }
        catch
        {
            // The control does not expose a value property. Focus changes still refresh the phone.
        }
        try
        {
            Automation.AddAutomationEventHandler(
                TextPattern.TextChangedEvent,
                element,
                TreeScope.Element,
                OnTextEvent);
        }
        catch
        {
            // Not every field raises text-changed.
        }
    }

    private void Unwatch()
    {
        if (_watched is null) return;
        try
        {
            Automation.RemoveAutomationPropertyChangedEventHandler(_watched, OnValueChanged);
        }
        catch
        {
            // Already gone.
        }
        try
        {
            Automation.RemoveAutomationEventHandler(TextPattern.TextChangedEvent, _watched, OnTextEvent);
        }
        catch
        {
            // Already gone.
        }
        _watched = null;
    }

    private void OnValueChanged(object sender, AutomationPropertyChangedEventArgs e) => SchedulePush();

    private void OnTextEvent(object sender, AutomationEventArgs e) => SchedulePush();

    private void SchedulePush()
    {
        if (Interlocked.Exchange(ref _debounce, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await PublishWhenSettled();
            }
            catch (Exception ex)
            {
                _log("text push: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _debounce, 0);
            }
        });
    }

    private void ScheduleConfirm(int generation)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(900);
                await PublishWhenSettled(generation);
            }
            catch (Exception ex)
            {
                _log("text confirm: " + ex.Message);
            }
        });
    }

    /// <summary>
    /// Pushes the focused text only after it has stopped changing, and never echoes our own write.
    /// </summary>
    private async Task PublishWhenSettled(int? generation = null)
    {
        while (true)
        {
            long quiet;
            lock (_gate) quiet = _quietUntil;
            var wait = quiet - Environment.TickCount64;
            if (wait <= 0) break;
            await Task.Delay((int)Math.Min(wait, 900));
        }

        string? push = null;
        string? previous = null;
        for (var i = 0; i < 8; i++)
        {
            string sample;
            lock (_gate)
            {
                if (generation is int gen && gen != _generation) return;
                if (Environment.TickCount64 < _quietUntil) break;
                sample = ReadLocked();
                if (Same(sample, _suppress)) return;
            }
            var canonical = Canonical(sample);
            if (previous == canonical)
            {
                push = canonical;
                break;
            }
            previous = canonical;
            await Task.Delay(60);
        }

        if (push is null) return;
        // A late accessibility read is often still the pre-write value. Give the control one more moment
        // to show what we wrote before telling the phone to revert.
        await Task.Delay(200);
        lock (_gate)
        {
            if (generation is int gen && gen != _generation) return;
            var again = ReadLocked();
            if (Same(again, _suppress) || !Same(again, push)) return;
            _suppress = null;
            _last = push;
        }
        Changed?.Invoke(push);
    }

    private static bool Same(string? left, string? right) => Canonical(left) == Canonical(right);

    private static string Canonical(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    private static string ReadFocused()
    {
        // Win32 Edit text is synchronous. UIA often still returns the previous value right after a write.
        if (TryWin32Get(out var native)) return Cap(native);
        var element = FocusedElement();
        if (element is not null && !IsOurElement(element))
        {
            if (TryValueGet(element, out var value)) return Cap(value);
            if (TryTextPattern(element, out var document)) return Cap(document);
        }
        return "";
    }

    private static bool TryValueGet(AutomationElement element, out string value)
    {
        value = "";
        try
        {
            if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)) return false;
            value = ((ValuePattern)pattern).Current.Value ?? "";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryValueSet(AutomationElement element, string text)
    {
        try
        {
            if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)) return false;
            var value = (ValuePattern)pattern;
            if (value.Current.IsReadOnly) return false;
            value.SetValue(text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryTextPattern(AutomationElement element, out string text)
    {
        text = "";
        try
        {
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return false;
            text = ((TextPattern)pattern).DocumentRange.GetText(MaxChars) ?? "";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void PasteReplacement(string text)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || ForegroundIsOurs()) return;
        var generation = Interlocked.Increment(ref _pasteGeneration);
        dispatcher.Invoke(() =>
        {
            try
            {
                if (!_clipboardBorrowed)
                {
                    _savedClipboard = System.Windows.Clipboard.ContainsText()
                        ? System.Windows.Clipboard.GetText()
                        : null;
                    _clipboardBorrowed = true;
                }
                System.Windows.Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                _log("text clipboard: " + ex.Message);
            }
        });
        // Select-all, then paste. Restoring the clipboard immediately makes the target app paste the old contents.
        _keys.Chord(new[] { 0x11, 0x41 });
        Thread.Sleep(60);
        _keys.Chord(new[] { 0x11, 0x56 });
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            if (generation != Volatile.Read(ref _pasteGeneration)) return;
            try
            {
                dispatcher.Invoke(() =>
                {
                    if (generation != Volatile.Read(ref _pasteGeneration)) return;
                    if (_savedClipboard is not null) System.Windows.Clipboard.SetText(_savedClipboard);
                    else System.Windows.Clipboard.Clear();
                    _clipboardBorrowed = false;
                    _savedClipboard = null;
                });
            }
            catch (Exception ex)
            {
                _log("text clipboard restore: " + ex.Message);
            }
        });
    }

    private static bool TryWin32Get(out string text)
    {
        text = "";
        var hwnd = FocusedHwnd(out var ours);
        if (hwnd == IntPtr.Zero || ours || !IsEditClass(hwnd)) return false;
        var length = (int)SendMessage(hwnd, WmGetTextLength, IntPtr.Zero, IntPtr.Zero);
        if (length < 0) return false;
        length = Math.Min(length, MaxChars);
        var buffer = new StringBuilder(length + 1);
        SendMessage(hwnd, WmGetText, (IntPtr)(length + 1), buffer);
        text = buffer.ToString();
        return true;
    }

    private static bool TryWin32Set(string text)
    {
        var hwnd = FocusedHwnd(out var ours);
        if (hwnd == IntPtr.Zero || ours || !IsEditClass(hwnd)) return false;
        SendMessage(hwnd, WmSetText, IntPtr.Zero, text);
        return true;
    }

    private static bool ForegroundIsOurs()
    {
        FocusedHwnd(out var ours);
        return ours;
    }

    private static IntPtr FocusedHwnd(out bool ours)
    {
        ours = false;
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return IntPtr.Zero;
        var thread = GetWindowThreadProcessId(foreground, out var pid);
        ours = pid == (uint)Environment.ProcessId;
        var info = new GuiThreadInfo { cbSize = Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(thread, ref info) || info.hwndFocus == IntPtr.Zero)
            return ours ? IntPtr.Zero : foreground;
        return info.hwndFocus;
    }

    private static bool IsOurElement(AutomationElement element)
    {
        try
        {
            return element.Current.ProcessId == Environment.ProcessId;
        }
        catch
        {
            return false;
        }
    }

    private static AutomationElement? FocusedElement()
    {
        try
        {
            return AutomationElement.FocusedElement;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsEditClass(IntPtr hwnd)
    {
        var name = new StringBuilder(256);
        if (GetClassName(hwnd, name, name.Capacity) == 0) return false;
        var cls = name.ToString();
        return cls.Equals("Edit", StringComparison.OrdinalIgnoreCase)
               || cls.Contains("RichEdit", StringComparison.OrdinalIgnoreCase)
               || cls.Contains("EDIT", StringComparison.Ordinal);
    }

    private static string Cap(string text) => text.Length <= MaxChars ? text : text[..MaxChars];

    private const int WmGetText = 0x000D;
    private const int WmGetTextLength = 0x000E;
    private const int WmSetText = 0x000C;

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, StringBuilder lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, string lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
