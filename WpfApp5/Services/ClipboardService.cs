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
        /// 지정한 에디트/텍스트 입력 HWND의 내용을 깨끗이 비웁니다.
        /// 1) WM_SETTEXT("") 시도
        /// 2) 남아 있으면 EM_SETSEL(-1,-1) + WM_CLEAR
        /// </summary>
        public bool ClearText(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
                return false;

            try
            {
                // 1) WM_SETTEXT ""
                NativeMethods.SendMessage(hwnd, NativeConstants.WM_SETTEXT, IntPtr.Zero, string.Empty);

                // 길이 확인
                int len = NativeMethods.SendMessage(hwnd, NativeConstants.WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero).ToInt32();
                if (len <= 0)
                    return true;

                // 2) EM_SETSEL(-1,-1) + WM_CLEAR
                IntPtr minusOne = (IntPtr)(-1);
                NativeMethods.SendMessage(hwnd, NativeConstants.EM_SETSEL, minusOne, minusOne);
                NativeMethods.SendMessage(hwnd, NativeConstants.WM_CLEAR, IntPtr.Zero, IntPtr.Zero);

                // 최종 확인
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
