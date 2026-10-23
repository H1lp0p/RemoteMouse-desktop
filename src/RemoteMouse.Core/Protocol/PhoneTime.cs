namespace RemoteMouse.Core.Protocol;

/// <summary>
/// Phone timestamps are the low 32 bits of <c>SystemClock.elapsedRealtime()</c> in milliseconds.
/// They are not Unix time and are not converted onto the PC clock.
/// Order uses a signed int32 difference so the ~49.7 day uint32 wrap stays ordered inside a ±24.8 day window.
/// </summary>
public static class PhoneTime
{
    public static int Delta(uint value, uint origin) => unchecked((int)(value - origin));

    public static bool IsNewer(uint value, uint origin) => Delta(value, origin) > 0;

    public static uint Add(uint timestamp, int milliseconds)
        => unchecked((uint)((long)timestamp + milliseconds));
}
