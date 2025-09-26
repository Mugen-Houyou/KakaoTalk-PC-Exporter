using System;
using System.Runtime.InteropServices;
using System.Threading;

internal static class FocusHelper
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    public static IntPtr GetFocusedHandle()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return IntPtr.Zero;

        uint targetTid = GetWindowThreadProcessId(fg, out _);
        uint thisTid = GetCurrentThreadId();

        if (thisTid != targetTid)
        {
            AttachThreadInput(thisTid, targetTid, true);
            try { return GetFocus(); }
            finally { AttachThreadInput(thisTid, targetTid, false); }
        }
        return GetFocus();
    }
}
