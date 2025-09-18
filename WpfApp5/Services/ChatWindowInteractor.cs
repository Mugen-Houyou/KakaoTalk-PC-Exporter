using System;
using System.Threading;
using KakaoPcLogger.Interop;
using KakaoPcLogger.Models;

namespace KakaoPcLogger.Services
{
    public sealed class ChatWindowInteractor
    {
        public bool Validate(ChatEntry entry, out string? warning)
        {
            if (!NativeMethods.IsWindow(entry.ParentHwnd) || !NativeMethods.IsWindow(entry.Hwnd))
            {
                warning = $"[경고] 무효 핸들: {entry.Title} {entry.HwndHex}";
                return false;
            }

            warning = null;
            return true;
        }

        public void ActivateAndCopy(ChatEntry entry)
        {
            FocusParent(entry.ParentHwnd);
            SelectAllAndCopy(entry.Hwnd);
            DeselectList(entry.Hwnd);
        }

        private static void FocusParent(IntPtr parent)
        {
            NativeMethods.SendMessage(parent, NativeConstants.WM_ACTIVATE, (IntPtr)NativeConstants.WA_ACTIVE, IntPtr.Zero);
            NativeMethods.SetForegroundWindow(parent);
        }

        private static void SelectAllAndCopy(IntPtr hwnd)
        {
            PressKeyCombo(hwnd, NativeConstants.VK_CONTROL, NativeConstants.VK_A, false);
            Thread.Sleep(30);
            PressKeyCombo(hwnd, NativeConstants.VK_CONTROL, NativeConstants.VK_C, false);
        }

        private static void DeselectList(IntPtr hwnd)
        {
            IntPtr point = NativeMethods.MakeLParam(30, 3);
            NativeMethods.PostMessage(hwnd, NativeConstants.WM_LBUTTONDOWN, (IntPtr)1, point);
            NativeMethods.PostMessage(hwnd, NativeConstants.WM_LBUTTONUP, IntPtr.Zero, point);
        }

        private static void PressKeyCombo(IntPtr hwnd, int modifierVk, int keyVk, bool sysKey)
        {
            if (!NativeMethods.IsWindow(hwnd))
                return;

            uint targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
            uint currentThread = NativeMethods.GetCurrentThreadId();

            NativeMethods.AttachThreadInput(currentThread, targetThread, true);

            var oldState = new byte[256];
            var newState = new byte[256];
            NativeMethods.GetKeyboardState(oldState);
            Array.Copy(oldState, newState, 256);

            newState[modifierVk] |= 0x80;
            NativeMethods.SetKeyboardState(newState);

            int msgDown = sysKey ? NativeConstants.WM_SYSKEYDOWN : NativeConstants.WM_KEYDOWN;
            int msgUp = sysKey ? NativeConstants.WM_SYSKEYUP : NativeConstants.WM_KEYUP;

            IntPtr lparam = NativeMethods.MakeKeyLParam((uint)keyVk);
            NativeMethods.PostMessage(hwnd, msgDown, (IntPtr)keyVk, lparam);
            Thread.Sleep(10);
            NativeMethods.PostMessage(hwnd, msgUp, (IntPtr)keyVk, (IntPtr)(lparam.ToInt64() | (1L << 30) | (1L << 31)));

            Array.Copy(oldState, newState, 256);
            NativeMethods.SetKeyboardState(newState);

            NativeMethods.AttachThreadInput(currentThread, targetThread, false);
        }
    }
}
