using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using KakaoPcLogger.Interop;

namespace WpfApp5.Services
{
    /// <summary>
    /// RegisterShellHookWindow 기반으로 HSHELL_FLASH/REDRAW를 감지하여 콜백을 발생.
    /// </summary>
    public sealed class TaskbarFlashWatcher : IDisposable
    {
        private readonly string _procLower;           // "kakaotalk"
        private readonly HashSet<int> _pidFilter;     // 비어있으면 전체 허용
        private readonly TimeSpan _debounce = TimeSpan.FromMilliseconds(500);
        private readonly Dictionary<IntPtr, DateTime> _last = new();

        private HwndSource? _src;
        private IntPtr _hwnd;
        private uint _shellMsgId;

        public event Action<IntPtr /*hwnd*/, int /*hshell code*/>? OnSignal;

        public TaskbarFlashWatcher(string targetProcessLower, IEnumerable<int>? pids = null)
        {
            _procLower = targetProcessLower;
            _pidFilter = pids != null ? new HashSet<int>(pids) : new HashSet<int>();
        }

        public void Start(Window wpfWindow)
        {
            _src = (HwndSource)PresentationSource.FromVisual(wpfWindow)!;
            if (_src == null) 
                throw new InvalidOperationException("No HwndSource");
            _hwnd = _src.Handle;

            _shellMsgId = NativeMethods.RegisterWindowMessage("SHELLHOOK");
            if (!NativeMethods.RegisterShellHookWindow(_hwnd))
                throw new InvalidOperationException("RegisterShellHookWindow failed");

            _src.AddHook(WndProc);
        }

        public void Dispose()
        {
            if (_hwnd != IntPtr.Zero) NativeMethods.DeregisterShellHookWindow(_hwnd);
            _src?.RemoveHook(WndProc);
            _src = null; _hwnd = IntPtr.Zero;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // 우리 훅 메시지 아니면 즉시 종료
            if ((uint)msg != _shellMsgId)
                return IntPtr.Zero;

            // 코드/타겟 추출 + 원시 로그
            int code = wParam.ToInt32();
            IntPtr target = lParam;
            System.Diagnostics.Debug.WriteLine($"[SHELL] code=0x{code:X}, hwnd=0x{target.ToInt64():X}");

            // 관심 코드만
            if (code != NativeConstants.HSHELL_FLASH && code != NativeConstants.HSHELL_REDRAW)
                return IntPtr.Zero;

            // 루트 HWND도 참고용 로그 (매칭 실패시 유용)
            IntPtr root = NativeMethods.GetAncestor(target, NativeConstants.GA_ROOT);
            if (root == IntPtr.Zero) root = target;
            System.Diagnostics.Debug.WriteLine($"[SHELL] root=0x{root.ToInt64():X}");

            // 일단 신호를 무조건 보낸다 (필터 사용 안함)
            OnSignal?.Invoke(target, code);
            return IntPtr.Zero;
        }

    }
}
