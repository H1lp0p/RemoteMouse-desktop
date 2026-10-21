using System.Runtime.InteropServices;

namespace RemoteMouse.Core.Input;

public sealed class SendInputMouse : SystemControl.IMouseController
{
    /// <summary>
    /// A physical mouse or a 1 px absolute-mapping error can disagree with the position we last set.
    /// Anything larger is the user moving the local pointer, so the next remote step starts from there.
    /// </summary>
    private const int ResyncPixels = 3;

    private static readonly Dictionary<string, (uint Down, uint Up)> Buttons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["left"] = (NativeInput.MouseLeftDown, NativeInput.MouseLeftUp),
        ["right"] = (NativeInput.MouseRightDown, NativeInput.MouseRightUp),
        ["middle"] = (NativeInput.MouseMiddleDown, NativeInput.MouseMiddleUp)
    };

    private readonly object _gate = new();
    private int _x;
    private int _y;
    private bool _has;

    public void Move(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        lock (_gate)
        {
            var screen = NativeInput.VirtualScreen();
            if (screen.Width < 1 || screen.Height < 1) return;
            var here = NativeInput.CursorPos();
            if (!_has || Math.Abs(here.X - _x) > ResyncPixels || Math.Abs(here.Y - _y) > ResyncPixels)
            {
                _x = here.X;
                _y = here.Y;
                _has = true;
            }

            _x = Math.Clamp(_x + dx, screen.Left, screen.Right);
            _y = Math.Clamp(_y + dy, screen.Top, screen.Bottom);
            var abs = AbsolutePointer.Normalize(_x, _y, screen.Left, screen.Top, screen.Width, screen.Height);
            NativeInput.Send(NativeInput.Mouse(
                NativeInput.MouseMove | NativeInput.MouseAbsolute | NativeInput.MouseVirtualDesk,
                abs.X,
                abs.Y,
                0));
        }
    }

    public void Button(string button, string action)
    {
        if (!Buttons.TryGetValue(button ?? "", out var flags)) return;
        switch ((action ?? "").ToLowerInvariant())
        {
            case "down":
                NativeInput.Send(NativeInput.Mouse(flags.Down, 0, 0, 0));
                break;
            case "up":
                NativeInput.Send(NativeInput.Mouse(flags.Up, 0, 0, 0));
                break;
            case "click":
                NativeInput.Send(NativeInput.Mouse(flags.Down, 0, 0, 0), NativeInput.Mouse(flags.Up, 0, 0, 0));
                break;
            case "double_click":
                NativeInput.Send(NativeInput.Mouse(flags.Down, 0, 0, 0), NativeInput.Mouse(flags.Up, 0, 0, 0));
                Thread.Sleep(40);
                NativeInput.Send(NativeInput.Mouse(flags.Down, 0, 0, 0), NativeInput.Mouse(flags.Up, 0, 0, 0));
                break;
        }
    }

    public void Scroll(int deltaX, int deltaY)
    {
        var events = new List<NativeInput.INPUT>(2);
        if (deltaY != 0) events.Add(NativeInput.Mouse(NativeInput.MouseWheel, 0, 0, deltaY));
        if (deltaX != 0) events.Add(NativeInput.Mouse(NativeInput.MouseHWheel, 0, 0, deltaX));
        if (events.Count > 0) NativeInput.Send(events.ToArray());
    }
}

public sealed class SendInputKeyboard : SystemControl.IKeyboardController
{
    public void Chord(IReadOnlyList<int> virtualKeys)
    {
        var keys = virtualKeys.Where(vk => vk is > 0 and < 255).Select(vk => (ushort)vk).ToArray();
        if (keys.Length == 0) return;
        var events = new NativeInput.INPUT[keys.Length * 2];
        for (var i = 0; i < keys.Length; i++)
            events[i] = NativeInput.Key(keys[i], keyUp: false);
        for (var i = 0; i < keys.Length; i++)
            events[keys.Length + i] = NativeInput.Key(keys[keys.Length - 1 - i], keyUp: true);
        NativeInput.Send(events);
    }
}

static class NativeInput
{
    public const uint MouseMove = 0x0001;
    public const uint MouseVirtualDesk = 0x4000;
    public const uint MouseAbsolute = 0x8000;
    public const uint MouseLeftDown = 0x0002;
    public const uint MouseLeftUp = 0x0004;
    public const uint MouseRightDown = 0x0008;
    public const uint MouseRightUp = 0x0010;
    public const uint MouseMiddleDown = 0x0020;
    public const uint MouseMiddleUp = 0x0040;
    public const uint MouseWheel = 0x0800;
    public const uint MouseHWheel = 0x1000;
    const uint KeyUp = 0x0002;
    const uint ExtendedKey = 0x0001;
    const uint InputMouse = 0;
    const uint InputKeyboard = 1;

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, INPUT[] inputs, int cbSize);

    public static void Send(params INPUT[] inputs)
    {
        if (inputs.Length == 0) return;
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            throw new InvalidOperationException($"SendInput sent {sent}/{inputs.Length}");
    }

    public static INPUT Mouse(uint flags, int dx, int dy, int data) => new()
    {
        type = InputMouse,
        U = new InputUnion
        {
            mi = new MOUSEINPUT
            {
                dx = dx,
                dy = dy,
                mouseData = unchecked((uint)data),
                dwFlags = flags
            }
        }
    };

    public static INPUT Key(ushort vk, bool keyUp)
    {
        var flags = keyUp ? KeyUp : 0u;
        if (IsExtended(vk)) flags |= ExtendedKey;
        return new INPUT
        {
            type = InputKeyboard,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    dwFlags = flags
                }
            }
        };
    }

    public static VirtualScreenBounds VirtualScreen()
    {
        return new VirtualScreenBounds(
            GetSystemMetrics(SmXVirtualScreen),
            GetSystemMetrics(SmYVirtualScreen),
            GetSystemMetrics(SmCxVirtualScreen),
            GetSystemMetrics(SmCyVirtualScreen));
    }

    public static (int X, int Y) CursorPos()
    {
        if (!GetCursorPos(out var point)) return (0, 0);
        return (point.X, point.Y);
    }

    const int SmXVirtualScreen = 76;
    const int SmYVirtualScreen = 77;
    const int SmCxVirtualScreen = 78;
    const int SmCyVirtualScreen = 79;

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);

    static bool IsExtended(ushort vk) => vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28
        or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D or 0x6F or 0xA3 or 0xA5
        or (>= 0xB0 and <= 0xB3);

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }
}

/// <summary>Pixel rectangle of every monitor together. Left and top are negative when a display sits beside the primary.</summary>
internal readonly record struct VirtualScreenBounds(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Math.Max(Width, 1) - 1;
    public int Bottom => Top + Math.Max(Height, 1) - 1;
}

/// <summary>
/// Maps a pixel on the virtual desktop to the 0..65535 space SendInput uses with MOUSEEVENTF_VIRTUALDESK.
/// user32 turns that value back into a pixel with (absolute * size) / 65536.
/// </summary>
internal static class AbsolutePointer
{
    public static (int X, int Y) Normalize(int pixelX, int pixelY, int originX, int originY, int width, int height)
    {
        if (width < 1) width = 1;
        if (height < 1) height = 1;
        var relX = Math.Clamp(pixelX - originX, 0, width - 1);
        var relY = Math.Clamp(pixelY - originY, 0, height - 1);
        return (Axis(relX, width), Axis(relY, height));
    }

    public static (int X, int Y) ToPixel(int absoluteX, int absoluteY, int originX, int originY, int width, int height)
    {
        if (width < 1) width = 1;
        if (height < 1) height = 1;
        var x = originX + (int)((long)absoluteX * width >> 16);
        var y = originY + (int)((long)absoluteY * height >> 16);
        return (x, y);
    }

    private static int Axis(int relative, int size)
    {
        var absolute = (int)(((long)relative << 16) / size);
        if (absolute < 65535 && (int)((long)absolute * size >> 16) < relative)
            absolute++;
        if (absolute > 65535) absolute = 65535;
        return absolute;
    }
}
