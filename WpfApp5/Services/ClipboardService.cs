using KakaoPcLogger.Interop;
using System.Threading;
using System.Windows;

namespace KakaoPcLogger.Services
{
    public sealed class ClipboardService
    {
        public string? TryReadText(int retries = 6, int delayMs = 30)
        {
            for (int i = 0; i < retries; i++)
            {
                try
                {
                    if (Clipboard.ContainsText())
                        return Clipboard.GetText();
                    return string.Empty;
                }
                catch
                {
                    Thread.Sleep(delayMs);
                }
            }

            return null;
        }

        /// <summary>
        /// ������ ����Ʈ/�ؽ�Ʈ �Է� HWND�� ������ ������ ���ϴ�.
        /// 1) WM_SETTEXT("") �õ�
        /// 2) ���� ������ EM_SETSEL(-1,-1) + WM_CLEAR
        /// </summary>
        public bool ClearText(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
                return false;

            try
            {
                // 1) WM_SETTEXT ""
                NativeMethods.SendMessage(hwnd, NativeConstants.WM_SETTEXT, IntPtr.Zero, string.Empty);

                // ���� Ȯ��
                int len = NativeMethods.SendMessage(hwnd, NativeConstants.WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero).ToInt32();
                if (len <= 0)
                    return true;

                // 2) EM_SETSEL(-1,-1) + WM_CLEAR
                IntPtr minusOne = (IntPtr)(-1);
                NativeMethods.SendMessage(hwnd, NativeConstants.EM_SETSEL, minusOne, minusOne);
                NativeMethods.SendMessage(hwnd, NativeConstants.WM_CLEAR, IntPtr.Zero, IntPtr.Zero);

                // ���� Ȯ��
                len = NativeMethods.SendMessage(hwnd, NativeConstants.WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero).ToInt32();
                return len == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
