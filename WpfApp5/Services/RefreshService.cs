using KakaoPcLogger.Interop;
using KakaoPcLogger.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;

namespace KakaoPcLogger.Services
{
    /// <summary>
    /// KakaoTalk 채팅방 리프레시(닫고-다시열기) 전담 서비스.
    /// - GUI 비의존.
    /// - FLASH 기반 동작과 충돌 방지를 위해 IsRunning 플래그 제공.
    /// - 스케줄(매일 HH:mm) 지원.
    /// </summary>
    public sealed class RefreshService
    {
        private readonly string _processName;
        private readonly ChatWindowScanner _scanner;
        private readonly ClipboardService _clipboard;


        // 좌표: KakaoTalk 메인창 클라이언트 기준
        private readonly int _openClickX = 355;
        private readonly int _openClickY = 140;

        private readonly TimeSpan _scanTimeout = TimeSpan.FromSeconds(5);
        private readonly TimeSpan _betweenStepsDelay = TimeSpan.FromMilliseconds(120);

        private volatile bool _inProgress = false;

        private IntPtr _lastFocusedTextbox = IntPtr.Zero;

        public RefreshService(string processName, ChatWindowScanner scanner, ClipboardService clipboard)
        {
            _processName = processName;
            _scanner = scanner;
            _clipboard = clipboard;

            ScheduledTime = new TimeSpan(4, 0, 0); // 04:00 기본
        }

        /// <summary> 리프레시 진행 중 여부(외부에서 FLASH 가드 등에 사용). </summary>
        public bool IsRunning => _inProgress;

        /// <summary> 매일 실행 시각(HH:mm). </summary>
        public TimeSpan ScheduledTime { get; private set; }

        /// <summary> 마지막 실행 '날짜'(중복 방지용). </summary>
        public DateTime LastRunDate { get; private set; } = DateTime.MinValue;

        /// <summary> 진행 로그 이벤트. </summary>
        public event Action<string>? Log;

        /// <summary>
        /// 채팅방 재오픈 성공 시 알림. (oldTitle, reopenedEntry)
        /// 외부에서 동일 타이틀 엔트리들의 HWND 교체 등에 사용.
        /// </summary>
        public event Action<string, ChatEntry>? OnReopened;

        /// <summary>
        /// 스케줄 시각 파싱. "HH:mm" / "H:mm" 허용.
        /// </summary>
        public bool TryParseScheduledTime(string text)
        {
            text = (text ?? string.Empty).Trim();
            if (TimeSpan.TryParseExact(text,
                                       new[] { @"hh\:mm", @"h\:mm" },
                                       System.Globalization.CultureInfo.InvariantCulture,
                                       out var ts))
            {
                ScheduledTime = ts;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 스케줄 틱(외부 타이머에서 30초 등 간격으로 호출). 반환값: 실행 트리거했으면 true.
        /// </summary>
        public async Task<bool> TickScheduleAsync(Func<IEnumerable<string>> resolveTitles, CancellationToken ct = default)
        {
            if (_inProgress) return false;

            var now = DateTime.Now;
            if (LastRunDate.Date == now.Date) return false;

            var delta = now.TimeOfDay - ScheduledTime;
            if (Math.Abs(delta.TotalSeconds) <= 30) // ±30s 허용
            {
                var titles = resolveTitles?.Invoke() ?? Enumerable.Empty<string>();
                await RunAsync(titles, "스케줄 실행", ct).ConfigureAwait(false);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 즉시 리프레시 실행. titles는 "대상 채팅방 타이틀 목록"(중복 제거 권장).
        /// </summary>
        public async Task RunAsync(IEnumerable<string> titles, string reason, CancellationToken ct = default)
        {
            if (_inProgress) return;
            _inProgress = true;

            try
            {
                Log?.Invoke($"[리프레시] 시작: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({reason})");

                // 1) 메인창 찾기
                var main = FindKakaoMainWindow();
                if (main == IntPtr.Zero)
                {
                    Log?.Invoke("[리프레시] 메인 창을 찾지 못했습니다. 취소합니다.");
                    return;
                }

                // 2) 타이틀 목록 정리
                var titleList = titles?
                                .Where(t => !string.IsNullOrWhiteSpace(t))
                                .Distinct(StringComparer.Ordinal)
                                .ToList() ?? new List<string>();
                if (titleList.Count == 0)
                {
                    Log?.Invoke("[리프레시] 대상 채팅방이 없습니다.");
                    return;
                }

                // 3) 각 타이틀 처리
                foreach (var title in titleList)
                {
                    ct.ThrowIfCancellationRequested();

                    Log?.Invoke($"[리프레시] 처리: \"{title}\"");

                    // 3-1) 해당 채팅방 윈도우 닫기 (동일 타이틀 모두 닫음)
                    var snapshot = _scanner.Scan(autoInclude: false)
                                           .Where(e => string.Equals(e.Title, title, StringComparison.Ordinal))
                                           .ToList();

                    foreach (var e in snapshot)
                    {
                        if (NativeMethods.IsWindow(e.ParentHwnd))
                        {
                            // WM_CLOSE
                            Win.PostMessage(e.ParentHwnd, Win.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                            await Task.Delay(150, ct).ConfigureAwait(false);
                        }
                    }

                    // 3-2) 메인 창 활성/복원
                    Win.ShowWindow(main, Win.SW_RESTORE);
                    Win.SetForegroundWindow(main);
                    await Task.Delay(_betweenStepsDelay, ct).ConfigureAwait(false);

                    // 3-3) Ctrl+F
                    SendCtrlF();
                    await Task.Delay(_betweenStepsDelay, ct).ConfigureAwait(false);
                    // 현재 focus된 textbox의 핸들을 저장 - 이는 _lastFocusedTextbox에 저장됨
                    CatchFocusedHwnd();

                    // 3-4) 텍스트 입력(클립보드→Ctrl+V)
                    //System.Windows.Clipboard.SetText(title);
                    TryWriteText(title);
                    SendCtrlV();
                    await Task.Delay(1000, ct).ConfigureAwait(false);

                    // 3-5) (_openClickX, _openClickY) 더블클릭
                    await DoubleClickClientAsync(main, _openClickX, _openClickY, ct).ConfigureAwait(false);

                    // 3-6) 새 창 타이틀 확인
                    var reopened = await WaitChatByTitleAsync(title, _scanTimeout, ct).ConfigureAwait(false);
                    if (reopened is null)
                    {
                        Log?.Invoke($"[리프레시] 재오픈 실패: \"{title}\" (타임아웃)");
                        continue;
                    }

                    // 저장했던 textbox 핸들의 내용을 완전히 비움
                    if (_lastFocusedTextbox != IntPtr.Zero)
                    {
                        _clipboard.ClearText(_lastFocusedTextbox);
                        await Task.Delay(100, ct).ConfigureAwait(false);
                    }

                    // 3-7) 알림(외부에서 HWND 교체)
                    OnReopened?.Invoke(title, reopened);
                    Log?.Invoke($"[리프레시] 완료: \"{title}\" → HWND {reopened.HwndHex}");

                    await Task.Delay(_betweenStepsDelay, ct).ConfigureAwait(false);
                }

                LastRunDate = DateTime.Now.Date;
                Log?.Invoke("[리프레시] 전체 완료");
            }
            catch (OperationCanceledException)
            {
                Log?.Invoke("[리프레시] 취소됨");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[리프레시 오류] {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _inProgress = false;
            }
        }

        // ============= 내부 유틸 =============
        private bool CatchFocusedHwnd()
        {
            try
            {
                IntPtr focused = IntPtr.Zero;
                var t = new Thread(() =>
                {
                    // STA 스레드에서 Win32 방식으로 실제 포커스 HWND 조회
                    focused = FocusHelper.GetFocusedHandle();
                });
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                t.Join();

                // 조인 후 가시성 보장 → 필드에 기록
                _lastFocusedTextbox = focused;
                return focused != IntPtr.Zero;
            }
            catch
            {
                return false;
            }
        }

        private IntPtr FindKakaoMainWindow()
        {
            IntPtr found = IntPtr.Zero;

            Win.EnumWindows((h, _) =>
            {
                if (!NativeMethods.IsWindow(h) || !NativeMethods.IsWindowVisible(h)) return true;

                // 프로세스 이름 필터
                NativeMethods.GetWindowThreadProcessId(h, out uint pid);
                try
                {
                    using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                    if (!string.Equals(p.ProcessName, _processName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { return true; }

                var cls = GetClassName(h);
                if (!string.Equals(cls, "EVA_Window_Dblclk", StringComparison.Ordinal))
                    return true;

                var title = GetWindowText(h);
                if (!string.Equals(title, "KakaoTalk", StringComparison.Ordinal))
                    return true;

                found = h;
                return false;
            }, IntPtr.Zero);

            return found;
        }

        private static string GetWindowText(IntPtr h)
        {
            var sb = new StringBuilder(512);
            Win.GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string GetClassName(IntPtr h)
        {
            var sb = new StringBuilder(256);
            Win.GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static void SendCtrlF()
        {
            var inputs = new[]
            {
                Win.KeyboardDown(Win.VK_CONTROL),
                Win.KeyboardDown(Win.VK_F),
                Win.KeyboardUp(Win.VK_F),
                Win.KeyboardUp(Win.VK_CONTROL),
            };
            Win.SendInputBatch(inputs);
        }

        private static void SendCtrlV()
        {
            const ushort VK_V = 0x56;
            var inputs = new[]
            {
                Win.KeyboardDown(Win.VK_CONTROL),
                Win.KeyboardDown(VK_V),
                Win.KeyboardUp(VK_V),
                Win.KeyboardUp(Win.VK_CONTROL),
            };
            Win.SendInputBatch(inputs);
        }

        private static async Task DoubleClickClientAsync(IntPtr hWnd, int clientX, int clientY, CancellationToken ct)
        {
            if (!Win.GetWindowRect(hWnd, out var r)) return;
            int x = r.Left + clientX;
            int y = r.Top + clientY;

            Win.SetCursorPos(x, y);
            await Task.Delay(20, ct).ConfigureAwait(false);

            var seq = new[]
            {
                Win.MouseDown(), Win.MouseUp(),
                Win.MouseDown(), Win.MouseUp()
            };
            Win.SendInputBatch(seq);
        }

        private async Task<ChatEntry?> WaitChatByTitleAsync(string title, TimeSpan timeout, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                ct.ThrowIfCancellationRequested();

                var list = _scanner.Scan(autoInclude: false);
                var e = list.FirstOrDefault(x => string.Equals(x.Title, title, StringComparison.Ordinal));
                if (e != null) return e;

                await Task.Delay(120, ct).ConfigureAwait(false);
            }
            return null;
        }

        // Clipboard.SetText()를 STA 스레드에서 안전하게 호출
        // 이 함수는 UI 스레드에서 호출하지 말 것.
        public bool TryWriteText(string text)
        {
            try
            {
                bool ok = false;
                var t = new Thread(() =>
                {
                    try { System.Windows.Clipboard.SetText(text); ok = true; }
                    catch { ok = false; }
                });
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                t.Join();
                return ok;
            }
            catch { return false; }
        }

        // ============= P/Invoke 래퍼 =============
        private static class Win
        {
            // user32
            [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
            [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
            [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
            [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
            [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] internal static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
            [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] internal static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
            [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hWnd);
            [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern bool SetCursorPos(int X, int Y);
            [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
            [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

            internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

            internal const int WM_CLOSE = 0x0010;
            internal const int SW_RESTORE = 9;

            internal const int INPUT_MOUSE = 0;
            internal const int INPUT_KEYBOARD = 1;

            internal const ushort VK_CONTROL = 0x11;
            internal const ushort VK_F = 0x46;

            internal const uint KEYEVENTF_KEYUP = 0x0002;
            internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
            internal const uint MOUSEEVENTF_LEFTUP = 0x0004;

            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
            internal struct RECT { public int Left, Top, Right, Bottom; }

            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
            internal struct INPUT
            {
                public int type;
                public INPUTUNION U;
            }

            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
            internal struct INPUTUNION
            {
                [System.Runtime.InteropServices.FieldOffset(0)] public MOUSEINPUT mi;
                [System.Runtime.InteropServices.FieldOffset(0)] public KEYBDINPUT ki;
            }

            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
            internal struct MOUSEINPUT
            {
                public int dx, dy;
                public uint mouseData, dwFlags, time;
                public IntPtr dwExtraInfo;
            }

            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
            internal struct KEYBDINPUT
            {
                public ushort wVk, wScan;
                public uint dwFlags, time;
                public IntPtr dwExtraInfo;
            }

            internal static INPUT KeyboardDown(ushort vk) => new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk } }
            };

            internal static INPUT KeyboardUp(ushort vk) => new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } }
            };

            internal static INPUT MouseDown() => new INPUT
            {
                type = INPUT_MOUSE,
                U = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN } }
            };

            internal static INPUT MouseUp() => new INPUT
            {
                type = INPUT_MOUSE,
                U = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP } }
            };

            internal static void SendInputBatch(INPUT[] arr)
            {
                SendInput((uint)arr.Length, arr, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
            }
        }
    }
}
