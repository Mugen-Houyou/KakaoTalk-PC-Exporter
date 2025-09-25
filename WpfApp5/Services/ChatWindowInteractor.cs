using System;
using System.Text;
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

        public bool TryCloseChatWindow(ChatEntry entry, out string? warning)
        {
            warning = null;

            if (!NativeMethods.IsWindow(entry.ParentHwnd))
            {
                warning = $"[FLASH] 창을 닫을 수 없습니다(무효): {entry.Title}";
                return false;
            }

            FocusParent(entry.ParentHwnd);
            Thread.Sleep(30);
            PressKey(entry.ParentHwnd, NativeConstants.VK_ESCAPE, false);
            Thread.Sleep(80);
            return true;
        }

        public bool TryReopenChatWindow(string chatTitle, out string? warning)
        {
            warning = null;

            IntPtr kakaoTalk = FindWindowByTitle("KakaoTalk");
            if (kakaoTalk == IntPtr.Zero)
            {
                warning = "[FLASH] 'KakaoTalk' 메인 창을 찾지 못했습니다.";
                return false;
            }

            FocusParent(kakaoTalk);
            Thread.Sleep(30);

            IntPtr edit = FindChildByClass(kakaoTalk, "Edit");
            if (edit == IntPtr.Zero)
            {
                warning = "[FLASH] KakaoTalk 검색창(Edit)을 찾지 못했습니다.";
                return false;
            }

            NativeMethods.SendMessage(edit, NativeConstants.WM_SETTEXT, IntPtr.Zero, string.Empty);
            Thread.Sleep(20);
            NativeMethods.SendMessage(edit, NativeConstants.WM_SETTEXT, IntPtr.Zero, chatTitle);
            Thread.Sleep(80);

            IntPtr list = FindChildByClass(kakaoTalk, "EVA_VH_ListControl_Dblclk");
            if (list == IntPtr.Zero)
            {
                warning = "[FLASH] KakaoTalk 목록 컨트롤을 찾지 못했습니다.";
                return false;
            }

            IntPtr point = NativeMethods.MakeLParam(10, 10);
            NativeMethods.PostMessage(list, NativeConstants.WM_LBUTTONDOWN, (IntPtr)1, point);
            Thread.Sleep(10);
            NativeMethods.PostMessage(list, NativeConstants.WM_LBUTTONUP, IntPtr.Zero, point);
            Thread.Sleep(10);
            NativeMethods.PostMessage(list, NativeConstants.WM_LBUTTONDBLCLK, (IntPtr)1, point);
            Thread.Sleep(10);
            NativeMethods.PostMessage(list, NativeConstants.WM_LBUTTONUP, IntPtr.Zero, point);
            Thread.Sleep(200);

            return true;
        }

        public bool TrySendMessage(ChatEntry entry, string message, string inputClassName, out string? error)
        {
            if (string.IsNullOrEmpty(message))
            {
                error = "[송신] 메시지가 비어 있습니다.";
                return false;
            }

            if (!Validate(entry, out var warning))
            {
                error = warning ?? "[송신] 대상 창이 무효합니다.";
                return false;
            }

            IntPtr input = FindEditableChild(entry.ParentHwnd, inputClassName);
            if (input == IntPtr.Zero)
            {
                error = $"[송신] 입력 컨트롤({inputClassName})을 찾을 수 없습니다: {entry.Title}";
                return false;
            }

            FocusParent(entry.ParentHwnd);
            Thread.Sleep(30);
            ClickTextbox(input);
            Thread.Sleep(10);
            PressKey(input, NativeConstants.VK_C, false);
            Thread.Sleep(10);
            PressKey(input, NativeConstants.VK_BACK, false);
            ClickTextbox(input);
            Thread.Sleep(10);
            PressKey(input, NativeConstants.VK_A, false);
            Thread.Sleep(10);
            PressKey(input, NativeConstants.VK_BACK, false);
            ClickTextbox(input);
            Thread.Sleep(10);

            string normalized = NormalizeLineEndings(message);
            NativeMethods.SendMessage(input, NativeConstants.WM_SETTEXT, IntPtr.Zero, normalized);
            Thread.Sleep(20);

            IntPtr caretEnd = (IntPtr)(-1);
            NativeMethods.SendMessage(input, NativeConstants.EM_SETSEL, caretEnd, caretEnd);
            Thread.Sleep(10);

            ClickTextbox(input);
            Thread.Sleep(10);

            PressEnter(input);

            error = null;
            return true;
        }

        private static void FocusParent(IntPtr parent)
        {
            NativeMethods.SendMessage(parent, NativeConstants.WM_ACTIVATE, (IntPtr)NativeConstants.WA_ACTIVE, IntPtr.Zero);
            NativeMethods.SetForegroundWindow(parent);
        }

        private static void SelectAllAndCopy(IntPtr hwnd)
        {
            Thread.Sleep(30);
            PressKeyCombo(hwnd, NativeConstants.VK_CONTROL, NativeConstants.VK_A, false);
            Thread.Sleep(10);
            PressKeyCombo(hwnd, NativeConstants.VK_CONTROL, NativeConstants.VK_C, false);
        }

        private static void DeselectList(IntPtr hwnd)
        {
            IntPtr point = NativeMethods.MakeLParam(5, 30);
            NativeMethods.PostMessage(hwnd, NativeConstants.WM_LBUTTONDOWN, (IntPtr)1, point);
            NativeMethods.PostMessage(hwnd, NativeConstants.WM_LBUTTONUP, IntPtr.Zero, point);
        }

        private static void ClickTextbox(IntPtr hwnd)
        {
            IntPtr point = NativeMethods.MakeLParam(15, 20);
            NativeMethods.PostMessage(hwnd, NativeConstants.WM_LBUTTONDOWN, (IntPtr)1, point);
            Thread.Sleep(10);
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

        private static IntPtr FindEditableChild(IntPtr parent, string className)
        {
            IntPtr result = IntPtr.Zero;

            NativeMethods.EnumChildWindows(parent, (hWnd, _) =>
            {
                if (!NativeMethods.IsWindow(hWnd))
                    return true;

                var sb = new StringBuilder(256);
                NativeMethods.GetClassName(hWnd, sb, sb.Capacity);
                if (!string.Equals(sb.ToString(), className, StringComparison.Ordinal))
                    return true;

                int style = NativeMethods.GetWindowLong(hWnd, NativeConstants.GWL_STYLE);
                bool isEditable = (style & NativeConstants.ES_READONLY) == 0;
                bool isMultiline = (style & NativeConstants.ES_MULTILINE) != 0;

                if (isEditable && isMultiline)
                {
                    result = hWnd;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            return result;
        }

        private static string NormalizeLineEndings(string message)
        {
            string normalized = message.Replace("\r\n", "\n").Replace('\r', '\n');
            return normalized.Replace("\n", "\r\n");
        }

        private static void PressEnter(IntPtr hwnd)
        {
            if (!NativeMethods.IsWindow(hwnd))
                return;

            uint targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
            uint currentThread = NativeMethods.GetCurrentThreadId();

            NativeMethods.AttachThreadInput(currentThread, targetThread, true);

            IntPtr lparam = NativeMethods.MakeKeyLParam((uint)NativeConstants.VK_RETURN);
            NativeMethods.PostMessage(hwnd, NativeConstants.WM_KEYDOWN, (IntPtr)NativeConstants.VK_RETURN, lparam);
            Thread.Sleep(15);
            NativeMethods.PostMessage(hwnd, NativeConstants.WM_CHAR, (IntPtr)'\r', lparam);
            Thread.Sleep(15);
            NativeMethods.PostMessage(hwnd, NativeConstants.WM_KEYUP, (IntPtr)NativeConstants.VK_RETURN,
                (IntPtr)(lparam.ToInt64() | (1L << 30) | (1L << 31)));

            NativeMethods.AttachThreadInput(currentThread, targetThread, false);
        }
        private static void PressKey(IntPtr hwnd, int keyVk, bool sysKey = false)
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

            newState[keyVk] |= 0x80;
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

        private static IntPtr FindWindowByTitle(string title)
        {
            IntPtr found = IntPtr.Zero;

            NativeMethods.EnumWindows((hWnd, _) =>
            {
                if (!NativeMethods.IsWindow(hWnd))
                    return true;

                var sb = new StringBuilder(512);
                NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
                if (!string.Equals(sb.ToString(), title, StringComparison.OrdinalIgnoreCase))
                    return true;

                found = hWnd;
                return false;
            }, IntPtr.Zero);

            return found;
        }

        private static IntPtr FindChildByClass(IntPtr parent, string className)
        {
            IntPtr result = IntPtr.Zero;

            NativeMethods.EnumChildWindows(parent, (hWnd, _) =>
            {
                if (!NativeMethods.IsWindow(hWnd))
                    return true;

                var sb = new StringBuilder(256);
                NativeMethods.GetClassName(hWnd, sb, sb.Capacity);
                if (!string.Equals(sb.ToString(), className, StringComparison.Ordinal))
                    return true;

                result = hWnd;
                return false;
            }, IntPtr.Zero);

            return result;
        }
    }
}
